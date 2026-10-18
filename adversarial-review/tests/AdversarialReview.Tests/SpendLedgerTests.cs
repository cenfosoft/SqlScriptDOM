using Microsoft.Data.Sqlite;
using Microsoft.VisualStudio.TestTools.UnitTesting;

namespace AdversarialReview.Tests;

[TestClass]
public sealed class SpendLedgerTests
{
    private sealed class Clock(DateTimeOffset now) : TimeProvider
    {
        public DateTimeOffset Now { get; set; } = now;
        public override DateTimeOffset GetUtcNow() => Now;
    }

    private string path = null!;
    private Clock clock = null!;
    private SqliteReviewSpendLedger Ledger => new(path, clock);
    private static readonly CancellationToken Token = CancellationToken.None;

    [TestInitialize]
    public void Initialize()
    {
        // A unique exact path per test, outside OS temporary directories; no shared DB or wildcard deletion.
        path = Path.GetFullPath($".review-spend-{Guid.NewGuid():N}.db");
        clock = new(new DateTimeOffset(2026, 1, 15, 12, 0, 0, TimeSpan.Zero));
    }

    [TestCleanup]
    public void Cleanup()
    {
        foreach (string suffix in new[] { "", "-journal", "-wal", "-shm" })
            File.Delete(path + suffix);
    }

    private static CycleClaim Claim(string authorization = "label-event-1", int pr = 1,
        decimal maximum = 1m, decimal monthly = 2m, string repository = "owner/repo", int rounds = 3) =>
        new(repository, pr, FoundationTests.Sha, authorization, maximum, monthly, rounds);

    private static ReviewRound Round(int number = 1, decimal? cost = 0.1m, string outcome = "completed") =>
        new(number, "github-models", "first", "First eligible configured model.", "1.0",
            cost.HasValue ? 10 : null, cost.HasValue ? 5 : null, 100, cost, outcome);

    private async Task<object?> ReadAsync(string sql)
    {
        using SqliteConnection connection = new(new SqliteConnectionStringBuilder
        {
            DataSource = path, Mode = SqliteOpenMode.ReadWrite, Pooling = false
        }.ToString());
        await connection.OpenAsync();
        using SqliteCommand command = connection.CreateCommand();
        command.CommandText = sql;
        return await command.ExecuteScalarAsync();
    }

    [TestMethod]
    public async Task ReopenRetainsFullReservationAndReplayAcrossPrShaAndMonth()
    {
        CycleClaim claim = Claim();
        await Ledger.ClaimAsync(claim, Token);
        await Ledger.RecordAsync(claim, Round(cost: 0m), Token);
        Assert.AreEqual(1_000_000L, await ReadAsync("SELECT reserved_micro_usd FROM monthly_budgets;"));
        foreach (CycleClaim replay in new[]
        {
            claim, claim with { PullRequest = 99 }, claim with { HeadSha = FoundationTests.OtherSha },
            claim with { Repository = "OWNER/REPO" }
        })
            await Assert.ThrowsExceptionAsync<ContractException>(() => Ledger.ClaimAsync(replay, Token));
        clock.Now = clock.Now.AddMonths(1);
        await Assert.ThrowsExceptionAsync<ContractException>(() => Ledger.ClaimAsync(claim, Token));
        Assert.AreEqual(1L, await ReadAsync("SELECT count(*) FROM claims;"));
    }

    [TestMethod]
    public async Task ConcurrentClaimsAcrossPrsCannotExceedRepositoryMonthlyLimit()
    {
        Task<bool>[] tasks = Enumerable.Range(1, 12).Select(index => Task.Run(async () =>
        {
            try
            {
                await Ledger.ClaimAsync(Claim($"event-{index}", index), Token);
                return true;
            }
            catch (ContractException)
            {
                return false;
            }
        })).ToArray();
        bool[] admitted = await Task.WhenAll(tasks);
        Assert.AreEqual(2, admitted.Count(value => value));
        Assert.AreEqual(2_000_000L, await ReadAsync("SELECT reserved_micro_usd FROM monthly_budgets;"));
        await Ledger.ClaimAsync(Claim(repository: "other/repo"), Token);
        Assert.AreEqual(3L, await ReadAsync("SELECT count(*) FROM claims;"));
    }

    [TestMethod]
    public async Task ConcurrentSameAuthorizationAdmitsExactlyOnce()
    {
        Task<int>[] tasks = Enumerable.Range(1, 8).Select(index => Task.Run(async () =>
        {
            try
            {
                await Ledger.ClaimAsync(Claim(pr: index, monthly: 20), Token);
                return 1;
            }
            catch (ContractException)
            {
                return 0;
            }
        })).ToArray();
        Assert.AreEqual(1, (await Task.WhenAll(tasks)).Sum());
        Assert.AreEqual(1_000_000L, await ReadAsync("SELECT reserved_micro_usd FROM monthly_budgets;"));
    }

    [TestMethod]
    public async Task LowerLimitIsStickyEvenWhenExistingReservationsExceedIt()
    {
        await Ledger.ClaimAsync(Claim(maximum: 2, monthly: 4), Token);
        await Assert.ThrowsExceptionAsync<ContractException>(() =>
            Ledger.ClaimAsync(Claim("lower", maximum: 0.1m, monthly: 1), Token));
        await Assert.ThrowsExceptionAsync<ContractException>(() =>
            Ledger.ClaimAsync(Claim("raise", maximum: 1, monthly: 10), Token));
        Assert.AreEqual(1_000_000L, await ReadAsync("SELECT limit_micro_usd FROM monthly_budgets;"));
        Assert.AreEqual(2_000_000L, await ReadAsync("SELECT reserved_micro_usd FROM monthly_budgets;"));
    }

    [TestMethod]
    public async Task RecordsRequireMatchingImmutableClaim()
    {
        CycleClaim claim = Claim();
        await Assert.ThrowsExceptionAsync<ContractException>(() => Ledger.RecordAsync(claim, Round(), Token));
        await Ledger.ClaimAsync(claim, Token);
        foreach (CycleClaim altered in new[]
        {
            claim with { Repository = "other/repo" }, claim with { PullRequest = 2 },
            claim with { HeadSha = FoundationTests.OtherSha }, claim with { AuthorizationId = "other" },
            claim with { MaximumCostUsd = 0.5m }, claim with { MonthlyLimitUsd = 3 },
            claim with { MaximumRounds = 2 }
        })
            await Assert.ThrowsExceptionAsync<ContractException>(() => Ledger.RecordAsync(altered, Round(), Token));
        Assert.AreEqual(0L, await ReadAsync("SELECT count(*) FROM rounds;"));
    }

    [TestMethod]
    public async Task OrderedRecordsAreIdempotentButConflictingDuplicatesAreRejected()
    {
        CycleClaim claim = Claim(rounds: 2);
        await Ledger.ClaimAsync(claim, Token);
        await Assert.ThrowsExceptionAsync<ContractException>(() => Ledger.RecordAsync(claim, Round(2), Token));
        await Ledger.RecordAsync(claim, Round(), Token);
        await Ledger.RecordAsync(claim, Round(cost: 0.100000m), Token);
        foreach (ReviewRound conflict in new[]
        {
            Round(cost: 0.2m), Round() with { Model = "second" },
            Round() with { SelectionReason = "Different selection." }, Round() with { InputTokens = 11 },
            Round() with { PromptVersion = "2.0" }, Round() with { LatencyMilliseconds = 101 },
            Round(outcome: "malformed")
        })
            await Assert.ThrowsExceptionAsync<ContractException>(() => Ledger.RecordAsync(claim, conflict, Token));
        await Ledger.RecordAsync(claim, Round(2), Token);
        await Ledger.RecordAsync(claim, Round(2), Token);
        await Assert.ThrowsExceptionAsync<ContractException>(() => Ledger.RecordAsync(claim, Round(3), Token));
        await Assert.ThrowsExceptionAsync<ContractException>(() => Ledger.EnsureActiveAsync(claim, Token));
        Assert.AreEqual(2L, await ReadAsync("SELECT count(*) FROM rounds;"));
    }

    [TestMethod]
    public async Task UnknownBillingStaysNullAndStopsFurtherRoundsWithoutReleasingReservation()
    {
        CycleClaim claim = Claim(monthly: 1);
        ReviewRound unknown = Round(cost: null, outcome: "failed");
        await Ledger.ClaimAsync(claim, Token);
        await Ledger.RecordAsync(claim, unknown, Token);
        await Ledger.RecordAsync(claim, unknown, Token);
        Assert.AreEqual(DBNull.Value, await ReadAsync("SELECT cost_micro_usd FROM rounds;"));
        Assert.AreEqual(DBNull.Value, await ReadAsync("SELECT input_tokens FROM rounds;"));
        await Assert.ThrowsExceptionAsync<ContractException>(() => Ledger.RecordAsync(claim, Round(2), Token));
        await Assert.ThrowsExceptionAsync<ContractException>(() => Ledger.EnsureActiveAsync(claim, Token));
        await Assert.ThrowsExceptionAsync<ContractException>(() => Ledger.ClaimAsync(Claim("another", monthly: 1), Token));
    }

    [DataTestMethod]
    [DataRow("failed")]
    [DataRow("malformed")]
    [DataRow("cancelled")]
    public async Task TerminalRoundStopsCycleEvenWithKnownZeroCost(string outcome)
    {
        CycleClaim claim = Claim();
        await Ledger.ClaimAsync(claim, Token);
        await Ledger.RecordAsync(claim, Round(cost: 0, outcome: outcome), Token);
        await Assert.ThrowsExceptionAsync<ContractException>(() => Ledger.RecordAsync(claim, Round(2), Token));
        await Assert.ThrowsExceptionAsync<ContractException>(() => Ledger.EnsureActiveAsync(claim, Token));
        Assert.AreEqual(1_000_000L, await ReadAsync("SELECT reserved_micro_usd FROM monthly_budgets;"));
    }

    [TestMethod]
    public async Task RejectsOverspendingAndPreservesValidPriorRecords()
    {
        CycleClaim claim = Claim();
        await Ledger.ClaimAsync(claim, Token);
        await Ledger.RecordAsync(claim, Round(cost: 0.7m), Token);
        await Assert.ThrowsExceptionAsync<ContractException>(() => Ledger.RecordAsync(claim, Round(2, 0.300001m), Token));
        await Ledger.RecordAsync(claim, Round(2, 0.3m), Token);
        await Assert.ThrowsExceptionAsync<ContractException>(() => Ledger.RecordAsync(claim, Round(3, 0m), Token));
        Assert.AreEqual(1_000_000L, await ReadAsync("SELECT sum(cost_micro_usd) FROM rounds;"));
    }

    [TestMethod]
    public async Task StrictUsageAndRoundValidationRejectsInvalidMetadata()
    {
        CycleClaim claim = Claim();
        await Ledger.ClaimAsync(claim, Token);
        foreach (ReviewRound invalid in new[]
        {
            Round(0), Round(4), Round(cost: -1), Round(cost: 0.0000001m),
            Round(cost: decimal.MaxValue), Round() with { InputTokens = -1 },
            Round() with { OutputTokens = -1 }, Round() with { InputTokens = null },
            Round() with { InputTokens = null, OutputTokens = null },
            Round(cost: null), Round() with { LatencyMilliseconds = -1 },
            Round() with { Outcome = "unknown" }, Round() with { Model = "" },
            Round() with { PromptVersion = null! }, Round() with { SelectionReason = "" }
        })
            await Assert.ThrowsExceptionAsync<ContractException>(() => Ledger.RecordAsync(claim, invalid, Token));
        await Ledger.RecordAsync(claim, Round(), Token);
        Assert.AreEqual(1L, await ReadAsync("SELECT count(*) FROM rounds;"));
    }

    [TestMethod]
    public async Task ValidatesBudgetPrecisionOverflowAndIdentitiesBeforeOpeningStore()
    {
        foreach (CycleClaim invalid in new[]
        {
            Claim(maximum: 0), Claim(maximum: -1), Claim(monthly: 0), Claim(maximum: 3, monthly: 2),
            Claim(maximum: 0.0000001m), Claim(monthly: 2.0000001m),
            Claim(maximum: decimal.MaxValue, monthly: decimal.MaxValue), Claim(monthly: 9223372036854.775808m),
            Claim(rounds: 0), Claim(rounds: 101), Claim(pr: 0), Claim(repository: "owner"),
            Claim(repository: "../repo"), Claim(repository: "owner/repo/extra"), Claim(repository: "o/r\n"),
            Claim(repository: "o/r?"), Claim(authorization: ""), Claim() with { HeadSha = "invalid" },
            Claim() with { HeadSha = FoundationTests.Sha.ToUpperInvariant() }
        })
            await Assert.ThrowsExceptionAsync<ContractException>(() => Ledger.ClaimAsync(invalid, Token));
        Assert.IsFalse(File.Exists(path));
        Assert.ThrowsException<ArgumentException>(() => new SqliteReviewSpendLedger(":memory:"));
        Assert.ThrowsException<ArgumentException>(() => new SqliteReviewSpendLedger("relative.db"));
        await Ledger.ClaimAsync(Claim(maximum: 0.000001m, monthly: 0.000001m), Token);
        Assert.AreEqual(1L, await ReadAsync("SELECT reserved_micro_usd FROM monthly_budgets;"));
    }

    [TestMethod]
    public async Task MaximumIntegerMicroUsdBudgetIsExactAndCannotOverflowOnAdmission()
    {
        decimal maximum = 9223372036854.775807m;
        await Ledger.ClaimAsync(Claim(maximum: maximum, monthly: maximum), Token);
        await Assert.ThrowsExceptionAsync<ContractException>(() =>
            Ledger.ClaimAsync(Claim("more", maximum: 0.000001m, monthly: maximum), Token));
        Assert.AreEqual(long.MaxValue, await ReadAsync("SELECT reserved_micro_usd FROM monthly_budgets;"));
    }

    [TestMethod]
    public async Task UtcMonthBoundaryExpiresAdmissionButAllowsLateSettlement()
    {
        clock.Now = new DateTimeOffset(2026, 2, 1, 1, 59, 59, TimeSpan.FromHours(2));
        CycleClaim first = Claim(monthly: 1);
        await Ledger.ClaimAsync(first, Token);
        await Ledger.EnsureActiveAsync(first, Token);
        Assert.AreEqual("2026-01", await ReadAsync("SELECT month FROM claims;"));
        clock.Now = clock.Now.AddSeconds(1);
        await Assert.ThrowsExceptionAsync<ContractException>(() => Ledger.EnsureActiveAsync(first, Token));
        await Ledger.RecordAsync(first, Round(), Token);
        CycleClaim next = Claim("february", monthly: 1);
        await Ledger.ClaimAsync(next, Token);
        await Ledger.EnsureActiveAsync(next, Token);
        Assert.AreEqual(2L, await ReadAsync("SELECT count(*) FROM monthly_budgets;"));
        Assert.AreEqual(2_000_000L, await ReadAsync("SELECT sum(reserved_micro_usd) FROM monthly_budgets;"));
    }

    [TestMethod]
    public async Task UnsupportedSchemaAndCorruptStoreFailClosedWithoutReset()
    {
        await Ledger.ClaimAsync(Claim(), Token);
        await ReadAsync("PRAGMA user_version = 99;");
        await Assert.ThrowsExceptionAsync<ContractException>(() => Ledger.ClaimAsync(Claim("next"), Token));
        Assert.AreEqual(1L, await ReadAsync("SELECT count(*) FROM claims;"));
        File.Delete(path);
        await File.WriteAllTextAsync(path, "not a SQLite database");
        await Assert.ThrowsExceptionAsync<SqliteException>(() => Ledger.ClaimAsync(Claim(), Token));
        Assert.AreEqual("not a SQLite database", await File.ReadAllTextAsync(path));
    }

    [TestMethod]
    public async Task ExistingUnversionedDatabaseIsNotAdoptedOrReset()
    {
        using (SqliteConnection connection = new($"Data Source={path};Pooling=False"))
        {
            await connection.OpenAsync();
            using SqliteCommand command = connection.CreateCommand();
            command.CommandText = "CREATE TABLE unrelated(value INTEGER);";
            await command.ExecuteNonQueryAsync();
        }
        await Assert.ThrowsExceptionAsync<ContractException>(() => Ledger.ClaimAsync(Claim(), Token));
        Assert.AreEqual(0L, await ReadAsync("PRAGMA user_version;"));
        Assert.AreEqual(1L, await ReadAsync("SELECT count(*) FROM sqlite_master WHERE name = 'unrelated';"));
    }

    [TestMethod]
    public async Task LockedStoreReportsSqliteFailureAndNeverAdmitsUnreservedClaim()
    {
        await Ledger.ClaimAsync(Claim(), Token);
        using SqliteConnection connection = new($"Data Source={path};Pooling=False");
        await connection.OpenAsync();
        using SqliteTransaction transaction = connection.BeginTransaction(deferred: false);
        SqliteException error = await Assert.ThrowsExceptionAsync<SqliteException>(() => Task.Run(() =>
            Ledger.ClaimAsync(Claim("locked"), Token)));
        Assert.AreEqual(5, error.SqliteErrorCode);
        transaction.Rollback();
        Assert.AreEqual(1L, await ReadAsync("SELECT count(*) FROM claims;"));
    }

    [TestMethod]
    public async Task RecordsPersistOnlyUsageAndFingerprintsNotFreeformPromptOrResponseText()
    {
        CycleClaim claim = Claim();
        await Ledger.ClaimAsync(claim, Token);
        await Ledger.RecordAsync(claim, Round() with { SelectionReason = "sensitive-freeform-sentinel" }, Token);
        byte[] content = await File.ReadAllBytesAsync(path);
        Assert.IsFalse(System.Text.Encoding.UTF8.GetString(content).Contains("sensitive-freeform-sentinel", StringComparison.Ordinal));
    }

    [TestMethod]
    public async Task VersionOneMigrationPreservesReservationsAndRejectsUnrecoverableLegacyHistory()
    {
        CycleClaim claim = Claim();
        await Ledger.ClaimAsync(claim, Token);
        await Ledger.RecordAsync(claim, Round(), Token);
        await ReadAsync("""
            ALTER TABLE rounds DROP COLUMN provider;
            ALTER TABLE rounds DROP COLUMN model;
            ALTER TABLE rounds DROP COLUMN selection_reason;
            ALTER TABLE rounds DROP COLUMN prompt_version;
            PRAGMA user_version = 1;
            """);
        await Assert.ThrowsExceptionAsync<ContractException>(() =>
            Ledger.ReadHistoryAsync("owner/repo", 1, FoundationTests.Sha, Token));
        // Failed history reads roll back migration; a normal ledger operation can migrate without erasing data.
        await Assert.ThrowsExceptionAsync<ContractException>(() => Ledger.ClaimAsync(claim, Token));
        Assert.AreEqual(1_000_000L, await ReadAsync("SELECT reserved_micro_usd FROM monthly_budgets;"));
        Assert.AreEqual(1L, await ReadAsync("SELECT count(*) FROM rounds;"));
        await Ledger.EnsureActiveAsync(claim, Token);
        Assert.AreEqual(2L, await ReadAsync("PRAGMA user_version;"));
    }
}

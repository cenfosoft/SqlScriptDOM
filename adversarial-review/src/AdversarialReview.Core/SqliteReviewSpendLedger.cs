using System.Globalization;
using System.Security.Cryptography;
using System.Text.Json;
using Microsoft.Data.Sqlite;

namespace AdversarialReview;

/// <summary>
/// Reserves microUSD in one host-supplied, trusted persistent SQLite store shared by all workers.
/// A runner-local database is not a durable, global spend authority. Reservations are never released.
/// Database I/O, locking, and corruption errors propagate; an unavailable store must stop execution.
/// </summary>
public sealed class SqliteReviewSpendLedger : IReviewSpendLedger
{
    private const decimal Scale = 1_000_000m;
    private readonly string connectionString;
    private readonly TimeProvider timeProvider;

    public SqliteReviewSpendLedger(string databasePath, TimeProvider? timeProvider = null)
    {
        if (string.IsNullOrWhiteSpace(databasePath) || !Path.IsPathFullyQualified(databasePath)
            || databasePath.Contains('\0'))
            throw new ArgumentException("An absolute persistent database file path is required.", nameof(databasePath));
        connectionString = new SqliteConnectionStringBuilder
        {
            DataSource = databasePath,
            Mode = SqliteOpenMode.ReadWriteCreate,
            Pooling = false,
            DefaultTimeout = 5
        }.ToString();
        this.timeProvider = timeProvider ?? TimeProvider.System;
    }

    public async Task ClaimAsync(CycleClaim claim, CancellationToken cancellationToken)
    {
        (string repository, long maximum, long limit) = ValidateClaim(claim);
        using SqliteConnection connection = new(connectionString);
        await OpenAsync(connection, cancellationToken);
        // IMMEDIATE takes the writer lock before reading budget/replay state, including across processes.
        using SqliteTransaction transaction = connection.BeginTransaction(deferred: false);
        await InitializeAsync(connection, transaction, cancellationToken);
        if (await ScalarAsync(connection, transaction,
            "SELECT 1 FROM claims WHERE repository = $repo AND authorization_id = $auth;",
            cancellationToken, ("$repo", repository), ("$auth", claim.AuthorizationId)) is not null)
            throw new ContractException("Authorization event has already been consumed for this repository.");

        DateTimeOffset now = timeProvider.GetUtcNow().ToUniversalTime();
        string month = now.ToString("yyyy-MM", CultureInfo.InvariantCulture);
        DateTimeOffset expires = new DateTimeOffset(now.Year, now.Month, 1, 0, 0, 0, TimeSpan.Zero).AddMonths(1);
        await ExecuteAsync(connection, transaction, """
            INSERT INTO monthly_budgets(repository, month, reserved_micro_usd, limit_micro_usd)
            VALUES($repo, $month, 0, $limit)
            ON CONFLICT(repository, month) DO UPDATE
            SET limit_micro_usd = min(limit_micro_usd, excluded.limit_micro_usd);
            """, cancellationToken, ("$repo", repository), ("$month", month), ("$limit", limit));
        long effectiveLimit = (long)(await ScalarAsync(connection, transaction, """
            SELECT limit_micro_usd FROM monthly_budgets WHERE repository = $repo AND month = $month;
            """, cancellationToken, ("$repo", repository), ("$month", month)))!;
        long reserved = (long)(await ScalarAsync(connection, transaction, """
            SELECT reserved_micro_usd FROM monthly_budgets WHERE repository = $repo AND month = $month;
            """, cancellationToken, ("$repo", repository), ("$month", month)))!;
        // Subtraction avoids overflow; a lowered limit can be below previously reserved spending.
        if (reserved > effectiveLimit || maximum > effectiveLimit - reserved)
        {
            // Persist a trusted lower limit even when it prevents this admission.
            await transaction.CommitAsync(cancellationToken);
            throw new ContractException("Monthly repository reservation limit would be exceeded.");
        }
        await ExecuteAsync(connection, transaction, """
            INSERT INTO claims(repository, authorization_id, pull_request, head_sha, authorized_actor, authorization_label,
                maximum_micro_usd, monthly_limit_micro_usd, maximum_rounds, month, expires_utc_ticks)
            VALUES($repo, $auth, $pr, $sha, $actor, $label, $maximum, $limit, $rounds, $month, $expires);
            UPDATE monthly_budgets SET reserved_micro_usd = reserved_micro_usd + $maximum
            WHERE repository = $repo AND month = $month;
            """, cancellationToken, ("$repo", repository), ("$auth", claim.AuthorizationId),
            ("$pr", claim.PullRequest), ("$sha", claim.HeadSha), ("$actor", claim.AuthorizedActor),
            ("$label", claim.AuthorizationLabel), ("$maximum", maximum),
            ("$limit", limit), ("$rounds", claim.MaximumRounds), ("$month", month),
            ("$expires", expires.UtcTicks));
        await transaction.CommitAsync(cancellationToken);
    }

    /// <summary>
    /// Revalidates the durable reservation before every request. The caller must bound
    /// in-flight request lifetime to the returned UTC expiry.
    /// </summary>
    public async Task<DateTimeOffset> EnsureActiveAsync(CycleClaim claim, CancellationToken cancellationToken)
    {
        (string repository, long maximum, long limit) = ValidateClaim(claim);
        using SqliteConnection connection = new(connectionString);
        await OpenAsync(connection, cancellationToken);
        using SqliteTransaction transaction = connection.BeginTransaction(deferred: false);
        await InitializeAsync(connection, transaction, cancellationToken);
        long expires = await VerifyClaimAsync(connection, transaction, claim, repository, maximum, limit, cancellationToken);
        if (timeProvider.GetUtcNow().UtcTicks >= expires)
            throw new ContractException("The cycle reservation lease has expired at the UTC month boundary.");
        await VerifyRemainingAsync(connection, transaction, repository, claim.AuthorizationId,
            maximum, claim.MaximumRounds, cancellationToken);
        await transaction.CommitAsync(cancellationToken);
        return new DateTimeOffset(expires, TimeSpan.Zero);
    }

    public async Task RecordAsync(CycleClaim claim, ReviewRound round, CancellationToken cancellationToken)
    {
        (string repository, long maximum, long limit) = ValidateClaim(claim);
        long? cost = ValidateRound(round, claim.MaximumRounds);
        // Fingerprints cover every field, without persisting arbitrary explanation text or prompts.
        ReviewRound normalized = round with { EstimatedCostUsd = cost is long known ? known / Scale : null };
        string fingerprint = Convert.ToHexString(SHA256.HashData(JsonSerializer.SerializeToUtf8Bytes(normalized)));
        using SqliteConnection connection = new(connectionString);
        await OpenAsync(connection, cancellationToken);
        using SqliteTransaction transaction = connection.BeginTransaction(deferred: false);
        await InitializeAsync(connection, transaction, cancellationToken);
        await VerifyClaimAsync(connection, transaction, claim, repository, maximum, limit, cancellationToken);
        object? existing = await ScalarAsync(connection, transaction, """
            SELECT fingerprint FROM rounds WHERE repository = $repo AND authorization_id = $auth AND number = $number;
            """, cancellationToken, ("$repo", repository), ("$auth", claim.AuthorizationId), ("$number", round.Number));
        if (existing is not null)
        {
            if ((string)existing != fingerprint)
                throw new ContractException("The round was already recorded with conflicting data.");
            await transaction.CommitAsync(cancellationToken);
            return;
        }
        (int count, long spent) = await VerifyRemainingAsync(connection, transaction, repository,
            claim.AuthorizationId, maximum, claim.MaximumRounds, cancellationToken);
        if (round.Number != count + 1)
            throw new ContractException("Rounds must be recorded in contiguous order.");
        if (cost.HasValue && cost.Value > maximum - spent)
            throw new ContractException("Recorded costs exceed the reserved cycle budget.");
        // Late settlement is allowed after expiry; it never grants admission in the new month.
        await ExecuteAsync(connection, transaction, """
            INSERT INTO rounds(repository, authorization_id, number, fingerprint,
                input_tokens, output_tokens, latency_ms, cost_micro_usd, outcome,
                provider, model, selection_reason, prompt_version)
            VALUES($repo, $auth, $number, $fingerprint, $input, $output, $latency, $cost, $outcome,
                $provider, $model, $reason, $prompt);
            """, cancellationToken, ("$repo", repository), ("$auth", claim.AuthorizationId),
            ("$number", round.Number), ("$fingerprint", fingerprint), ("$input", round.InputTokens),
            ("$output", round.OutputTokens), ("$latency", round.LatencyMilliseconds),
            ("$cost", cost), ("$outcome", round.Outcome), ("$provider", round.Provider),
            ("$model", round.Model), ("$reason", "Recovered from durable model history."), ("$prompt", round.PromptVersion));
        await transaction.CommitAsync(cancellationToken);
    }

    public async Task RecordFinalStatusAsync(CycleClaim claim, string status, CancellationToken cancellationToken)
    {
        (string repository, long maximum, long limit) = ValidateClaim(claim);
        if (status is not ("complete" or "incomplete" or "stale" or "human-review-required"))
            throw new ContractException("Invalid final review status.");
        using SqliteConnection connection = new(connectionString);
        await OpenAsync(connection, cancellationToken);
        using SqliteTransaction transaction = connection.BeginTransaction(deferred: false);
        await InitializeAsync(connection, transaction, cancellationToken);
        await VerifyClaimAsync(connection, transaction, claim, repository, maximum, limit, cancellationToken);
        object? existing = await ScalarAsync(connection, transaction, """
            SELECT final_status FROM claims WHERE repository = $repo AND authorization_id = $auth;
            """, cancellationToken, ("$repo", repository), ("$auth", claim.AuthorizationId));
        if (existing is string existingStatus)
        {
            if (existingStatus != status)
                throw new ContractException("The cycle already has a conflicting final status.");
            await transaction.CommitAsync(cancellationToken);
            return;
        }
        await ExecuteAsync(connection, transaction, """
            UPDATE claims SET final_status = $status
            WHERE repository = $repo AND authorization_id = $auth AND final_status IS NULL;
            """, cancellationToken, ("$status", status), ("$repo", repository), ("$auth", claim.AuthorizationId));
        await transaction.CommitAsync(cancellationToken);
    }

    private static async Task OpenAsync(SqliteConnection connection, CancellationToken cancellationToken)
    {
        await connection.OpenAsync(cancellationToken);
        using SqliteCommand command = connection.CreateCommand();
        command.CommandText = "PRAGMA foreign_keys = ON; PRAGMA synchronous = FULL;";
        await command.ExecuteNonQueryAsync(cancellationToken);
    }

    private static async Task InitializeAsync(SqliteConnection connection, SqliteTransaction transaction,
        CancellationToken cancellationToken)
    {
        long version = (long)(await ScalarAsync(connection, transaction, "PRAGMA user_version;", cancellationToken))!;
        if (version == 3) return;
        if (version == 1)
        {
            await ExecuteAsync(connection, transaction, """
                ALTER TABLE rounds ADD COLUMN provider TEXT;
                ALTER TABLE rounds ADD COLUMN model TEXT;
                ALTER TABLE rounds ADD COLUMN selection_reason TEXT;
                ALTER TABLE rounds ADD COLUMN prompt_version TEXT;
                PRAGMA user_version = 2;
                """, cancellationToken);
            version = 2;
        }
        if (version == 2)
        {
            await ExecuteAsync(connection, transaction, """
                ALTER TABLE claims ADD COLUMN authorized_actor TEXT;
                ALTER TABLE claims ADD COLUMN authorization_label TEXT;
                ALTER TABLE claims ADD COLUMN final_status TEXT
                    CHECK(final_status IS NULL OR final_status IN ('complete', 'incomplete', 'stale', 'human-review-required'));
                PRAGMA user_version = 3;
                """, cancellationToken);
            return;
        }
        if (version != 0 || (long)(await ScalarAsync(connection, transaction,
            "SELECT count(*) FROM sqlite_master WHERE name NOT LIKE 'sqlite_%';", cancellationToken))! != 0)
            throw new ContractException("Unsupported or unversioned spend ledger schema; no reset was performed.");
        await ExecuteAsync(connection, transaction, """
            CREATE TABLE monthly_budgets(
                repository TEXT NOT NULL, month TEXT NOT NULL,
                reserved_micro_usd INTEGER NOT NULL CHECK(reserved_micro_usd >= 0),
                limit_micro_usd INTEGER NOT NULL CHECK(limit_micro_usd > 0),
                PRIMARY KEY(repository, month)
            ) STRICT;
            CREATE TABLE claims(
                repository TEXT NOT NULL, authorization_id TEXT NOT NULL,
                pull_request INTEGER NOT NULL CHECK(pull_request > 0), head_sha TEXT NOT NULL,
                authorized_actor TEXT NOT NULL, authorization_label TEXT NOT NULL,
                maximum_micro_usd INTEGER NOT NULL CHECK(maximum_micro_usd > 0),
                monthly_limit_micro_usd INTEGER NOT NULL CHECK(monthly_limit_micro_usd > 0),
                maximum_rounds INTEGER NOT NULL CHECK(maximum_rounds BETWEEN 1 AND 3),
                month TEXT NOT NULL, expires_utc_ticks INTEGER NOT NULL,
                final_status TEXT CHECK(final_status IS NULL OR final_status IN ('complete', 'incomplete', 'stale', 'human-review-required')),
                PRIMARY KEY(repository, authorization_id),
                FOREIGN KEY(repository, month) REFERENCES monthly_budgets(repository, month)
            ) STRICT;
            CREATE TABLE rounds(
                repository TEXT NOT NULL, authorization_id TEXT NOT NULL,
                number INTEGER NOT NULL CHECK(number BETWEEN 1 AND 3), fingerprint TEXT NOT NULL,
                input_tokens INTEGER CHECK(input_tokens >= 0), output_tokens INTEGER CHECK(output_tokens >= 0),
                latency_ms INTEGER NOT NULL CHECK(latency_ms >= 0),
                cost_micro_usd INTEGER CHECK(cost_micro_usd >= 0),
                outcome TEXT NOT NULL CHECK(outcome IN ('completed', 'malformed', 'failed', 'cancelled')),
                provider TEXT NOT NULL, model TEXT NOT NULL, selection_reason TEXT NOT NULL, prompt_version TEXT NOT NULL,
                PRIMARY KEY(repository, authorization_id, number),
                FOREIGN KEY(repository, authorization_id) REFERENCES claims(repository, authorization_id)
            ) STRICT;
            PRAGMA user_version = 3;
            """, cancellationToken);
    }

    public async Task<ReviewRound[]> ReadHistoryAsync(string repository, int pullRequest, string headSha,
        CancellationToken cancellationToken, string? authorizationId = null)
    {
        // Reuse claim identity validation without making or releasing any reservation.
        ValidateClaim(new(repository, pullRequest, headSha, "history-read", 1, 1, 1, "history-reader", "history-read"));
        using SqliteConnection connection = new(connectionString);
        await OpenAsync(connection, cancellationToken);
        using SqliteTransaction transaction = connection.BeginTransaction(deferred: false);
        await InitializeAsync(connection, transaction, cancellationToken);
        using SqliteCommand command = Command(connection, transaction, """
            SELECT r.provider, r.model, r.selection_reason, r.prompt_version,
                r.input_tokens, r.output_tokens, r.latency_ms, r.cost_micro_usd, r.outcome
            FROM rounds r JOIN claims c ON c.repository = r.repository AND c.authorization_id = r.authorization_id
            WHERE c.repository = $repo AND c.pull_request = $pr AND c.head_sha = $sha
                AND ($auth IS NULL OR c.authorization_id = $auth)
            ORDER BY c.rowid, r.number LIMIT 101;
            """, ("$repo", repository.ToLowerInvariant()), ("$pr", pullRequest), ("$sha", headSha), ("$auth", authorizationId));
        List<ReviewRound> rounds = [];
        using (SqliteDataReader reader = await command.ExecuteReaderAsync(cancellationToken))
        {
            while (await reader.ReadAsync(cancellationToken))
            {
                if (rounds.Count == 100 || Enumerable.Range(0, 4).Any(reader.IsDBNull))
                    throw new ContractException("Durable model history exceeds its ceiling or requires legacy metadata reconciliation.");
                rounds.Add(new(rounds.Count + 1, reader.GetString(0), reader.GetString(1), reader.GetString(2), reader.GetString(3),
                    reader.IsDBNull(4) ? null : reader.GetInt32(4), reader.IsDBNull(5) ? null : reader.GetInt32(5),
                    reader.GetInt64(6), reader.IsDBNull(7) ? null : reader.GetInt64(7) / Scale, reader.GetString(8)));
            }
        }
        await transaction.CommitAsync(cancellationToken);
        return rounds.ToArray();
    }

    private static async Task<long> VerifyClaimAsync(SqliteConnection connection, SqliteTransaction transaction,
        CycleClaim claim, string repository, long maximum, long limit, CancellationToken cancellationToken)
    {
        object? expires = await ScalarAsync(connection, transaction, """
            SELECT expires_utc_ticks FROM claims
            WHERE repository = $repo AND authorization_id = $auth AND pull_request = $pr AND head_sha = $sha
                AND (authorized_actor IS NULL OR authorized_actor = $actor)
                AND (authorization_label IS NULL OR authorization_label = $label)
                AND maximum_micro_usd = $maximum AND monthly_limit_micro_usd = $limit AND maximum_rounds = $rounds;
            """, cancellationToken, ("$repo", repository), ("$auth", claim.AuthorizationId),
            ("$pr", claim.PullRequest), ("$sha", claim.HeadSha), ("$actor", claim.AuthorizedActor),
            ("$label", claim.AuthorizationLabel), ("$maximum", maximum),
            ("$limit", limit), ("$rounds", claim.MaximumRounds));
        return expires is long value ? value
            : throw new ContractException("Unknown claim or immutable claim data does not match.");
    }

    private static async Task<(int Count, long Spent)> VerifyRemainingAsync(SqliteConnection connection,
        SqliteTransaction transaction, string repository, string authorizationId, long maximum,
        int maximumRounds, CancellationToken cancellationToken)
    {
        using SqliteCommand command = Command(connection, transaction, """
            SELECT count(*), coalesce(sum(cost_micro_usd), 0),
                coalesce(sum(CASE WHEN cost_micro_usd IS NULL OR outcome != 'completed' THEN 1 ELSE 0 END), 0)
            FROM rounds WHERE repository = $repo AND authorization_id = $auth;
            """, ("$repo", repository), ("$auth", authorizationId));
        using SqliteDataReader reader = await command.ExecuteReaderAsync(cancellationToken);
        await reader.ReadAsync(cancellationToken);
        int count = reader.GetInt32(0);
        long spent = reader.GetInt64(1);
        if (count >= maximumRounds || spent >= maximum || reader.GetInt64(2) != 0)
            throw new ContractException("Cycle is exhausted or stopped; unknown billing requires reconciliation.");
        return (count, spent);
    }

    private static (string Repository, long Maximum, long Limit) ValidateClaim(CycleClaim claim)
    {
        ArgumentNullException.ThrowIfNull(claim);
        string[] parts = (claim.Repository ?? "").Split('/');
        if (parts.Length != 2 || parts.Any(p => p.Length is < 1 or > 100 || p is "." or ".."
            || !p.All(c => char.IsAsciiLetterOrDigit(c) || c is '_' or '.' or '-'))
            || claim.PullRequest <= 0 || claim.HeadSha is null || !Contracts.IsSha(claim.HeadSha)
            || !ValidText(claim.AuthorizationId)
            || claim.AuthorizedActor is null || !System.Text.RegularExpressions.Regex.IsMatch(
                claim.AuthorizedActor, "^[A-Za-z0-9-]{1,39}$", System.Text.RegularExpressions.RegexOptions.CultureInvariant)
            || !ValidText(claim.AuthorizationLabel) || claim.MaximumRounds is < 1 or > ReviewConfiguration.MaxRounds)
            throw new ContractException("Invalid cycle claim identity or round limit.");
        long maximum = MicroUsd(claim.MaximumCostUsd);
        long limit = MicroUsd(claim.MonthlyLimitUsd);
        if (maximum == 0 || limit == 0 || maximum > limit)
            throw new ContractException("Cycle and monthly budgets must be positive and ordered.");
        return (claim.Repository!.ToLowerInvariant(), maximum, limit);
    }

    private static long? ValidateRound(ReviewRound round, int maximumRounds)
    {
        ArgumentNullException.ThrowIfNull(round);
        if (round.Number < 1 || round.Number > maximumRounds
            || !ValidText(round.Provider) || !ValidText(round.Model) || !ValidText(round.SelectionReason)
            || !ValidText(round.PromptVersion) || round.InputTokens is < 0 || round.OutputTokens is < 0
            || round.LatencyMilliseconds < 0 || round.Outcome is not ("completed" or "malformed" or "failed" or "cancelled")
            || round.InputTokens.HasValue != round.OutputTokens.HasValue
            || (round.Outcome is "completed" or "malformed"
                && (round.InputTokens is null || round.OutputTokens is null || round.EstimatedCostUsd is null)))
            throw new ContractException("Invalid round metadata or usage.");
        return round.EstimatedCostUsd is decimal cost ? MicroUsd(cost) : null;
    }

    private static bool ValidText(string? value) =>
        !string.IsNullOrWhiteSpace(value) && value.Length <= 8000 && !value.Any(char.IsControl);

    private static long MicroUsd(decimal amount)
    {
        if (amount < 0 || amount > long.MaxValue / Scale)
            throw new ContractException("USD amount is negative or exceeds the microUSD integer range.");
        decimal scaled = amount * Scale;
        if (scaled != decimal.Truncate(scaled))
            throw new ContractException("USD amounts require exact microUSD precision (at most six fractional digits).");
        return (long)scaled;
    }

    private static SqliteCommand Command(SqliteConnection connection, SqliteTransaction transaction,
        string sql, params (string Name, object? Value)[] parameters)
    {
        SqliteCommand command = connection.CreateCommand();
        command.Transaction = transaction;
        command.CommandText = sql;
        foreach ((string name, object? value) in parameters)
            command.Parameters.AddWithValue(name, value ?? DBNull.Value);
        return command;
    }

    private static async Task<object?> ScalarAsync(SqliteConnection connection, SqliteTransaction transaction,
        string sql, CancellationToken cancellationToken, params (string Name, object? Value)[] parameters)
    {
        using SqliteCommand command = Command(connection, transaction, sql, parameters);
        return await command.ExecuteScalarAsync(cancellationToken);
    }

    private static async Task ExecuteAsync(SqliteConnection connection, SqliteTransaction transaction,
        string sql, CancellationToken cancellationToken, params (string Name, object? Value)[] parameters)
    {
        using SqliteCommand command = Command(connection, transaction, sql, parameters);
        await command.ExecuteNonQueryAsync(cancellationToken);
    }
}

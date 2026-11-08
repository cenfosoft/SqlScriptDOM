using Microsoft.VisualStudio.TestTools.UnitTesting;

namespace AdversarialReview.Tests;

[TestClass]
public sealed class CycleTests
{
    private sealed class Provider : IReviewProvider
    {
        public List<ModelRequest> Requests { get; } = [];
        public Func<ModelRequest, CancellationToken, Task<ModelResponse>> Respond { get; set; } =
            (_, _) => Task.FromResult(new ModelResponse("[]", 10, 5, 1, 0.1m));
        public bool IsAvailable(ModelEntry model) => true;
        public Task<ModelResponse> ReviewAsync(ModelRequest request, CancellationToken cancellationToken)
        {
            Requests.Add(request);
            return Respond(request, cancellationToken);
        }
    }

    // Test-only ledger. It deliberately does not implement production durability.
    private sealed class Ledger : IReviewSpendLedger
    {
        public bool RejectClaim { get; set; }
        public bool Claimed { get; private set; }
        public string? FinalStatus { get; private set; }
        public DateTimeOffset Expires { get; set; } = DateTimeOffset.UtcNow.AddMinutes(1);
        public List<ReviewRound> Records { get; } = [];
        public Task ClaimAsync(CycleClaim claim, CancellationToken cancellationToken)
        {
            if (Claimed || RejectClaim) throw new ContractException("Claim unavailable.");
            Claimed = true;
            return Task.CompletedTask;
        }
        public Task RecordAsync(CycleClaim claim, ReviewRound round, CancellationToken cancellationToken)
        {
            Records.Add(round);
            return Task.CompletedTask;
        }
        public Task<DateTimeOffset> EnsureActiveAsync(CycleClaim claim, CancellationToken cancellationToken) =>
            Task.FromResult(Expires);
        public Task RecordFinalStatusAsync(CycleClaim claim, string status, CancellationToken cancellationToken)
        {
            FinalStatus = status;
            return Task.CompletedTask;
        }
    }

    private static Task<CycleResult> Run(
        Provider provider, Ledger ledger, ReviewConfiguration? config = null,
        Func<CancellationToken, Task<PullRequestState>>? readState = null,
        CheckResult[]? checks = null, ReviewRound[]? history = null)
    {
        var context = new ReviewContext(FoundationTests.Sha, FoundationTests.OtherSha,
            "Trusted guidance", "Untrusted diff", [], [], []);
        return new ReviewCycle(provider, ledger).RunAsync(config ?? FoundationTests.Config(),
            FoundationTests.Run(), new("allancascante", "adversarial-review:run", FoundationTests.Sha,
                "label-event-1", true, false), context, checks ?? FoundationTests.Checks(), history ?? [],
            readState ?? (_ => Task.FromResult(new PullRequestState(FoundationTests.Sha, true, false))),
            10000, [], CancellationToken.None);
    }

    [TestMethod]
    public async Task CompleteCycleRotatesModelsAndPersistsEachRound()
    {
        var provider = new Provider();
        var ledger = new Ledger();
        CycleResult result = await Run(provider, ledger);
        Assert.AreEqual("complete", result.Report.Status);
        CollectionAssert.AreEqual(new[] { "first", "second", "first" },
            provider.Requests.Select(r => r.Model.Model).ToArray());
        Assert.AreEqual(3, ledger.Records.Count);
        Assert.AreEqual(3, provider.Requests.Select(r => r.RequestIdentity).Distinct().Count());
        Assert.AreEqual(0.8m, provider.Requests[2].MaximumCostUsd);
        CollectionAssert.AreEqual(new[] { 1000, 990, 980 }, provider.Requests.Select(r => r.MaxInputTokens).ToArray());
        CollectionAssert.AreEqual(new[] { 500, 495, 490 }, provider.Requests.Select(r => r.MaxOutputTokens).ToArray());
        Assert.AreEqual("complete", ledger.FinalStatus);
        await Assert.ThrowsExceptionAsync<ContractException>(() => Run(provider, ledger));
        Assert.AreEqual(3, provider.Requests.Count);
    }

    [TestMethod]
    public async Task DisabledFailedChecksAndDeniedClaimNeverCallProvider()
    {
        foreach (var situation in new[] { "disabled", "checks", "claim" })
        {
            var provider = new Provider();
            var ledger = new Ledger { RejectClaim = situation == "claim" };
            await Assert.ThrowsExceptionAsync<ContractException>(() => Run(provider, ledger,
                FoundationTests.Config() with { Enabled = situation != "disabled" },
                checks: situation == "checks" ? [] : null));
            Assert.AreEqual(0, provider.Requests.Count);
        }
    }

    [TestMethod]
    public async Task NewCommitBetweenRoundsStopsCallsAndMarksStale()
    {
        int reads = 0;
        var provider = new Provider();
        var ledger = new Ledger();
        CycleResult result = await Run(provider, ledger, readState: _ =>
            Task.FromResult(new PullRequestState(++reads > 2 ? FoundationTests.OtherSha : FoundationTests.Sha, true, false)));
        Assert.AreEqual(1, provider.Requests.Count);
        Assert.AreEqual("stale", result.Report.Status);
    }

    [TestMethod]
    public async Task PriorHistoryRotatesAcrossAuthorizedCycles()
    {
        var provider = new Provider();
        await Run(provider, new Ledger(), history: [FoundationTests.Round(1, "first")]);
        Assert.AreEqual("second", provider.Requests[0].Model.Model);
        var malformed = new Provider();
        await Assert.ThrowsExceptionAsync<ContractException>(() =>
            Run(malformed, new Ledger(), history: [FoundationTests.Round(2, "first")]));
        Assert.AreEqual(0, malformed.Requests.Count);
    }

    [TestMethod]
    public async Task MalformedOutputIsRecordedAndNotRetried()
    {
        var provider = new Provider { Respond = (_, _) => Task.FromResult(new ModelResponse("```[]```", 10, 5, 1, 0.1m)) };
        var ledger = new Ledger();
        CycleResult result = await Run(provider, ledger);
        Assert.AreEqual("incomplete", result.Report.Status);
        Assert.IsTrue(AuditEngine.RequiresHumanReview(result.Report));
        Assert.AreEqual("malformed", ledger.Records.Single().Outcome);
        Assert.AreEqual(1, provider.Requests.Count);
    }

    [TestMethod]
    public async Task ProviderFailureRetainsUnknownUsageAndDoesNotRetry()
    {
        var provider = new Provider { Respond = (_, _) => throw new HttpRequestException("Do not log provider payload") };
        var ledger = new Ledger();
        CycleResult result = await Run(provider, ledger);
        Assert.AreEqual("incomplete", result.Report.Status);
        Assert.IsTrue(AuditEngine.RequiresHumanReview(result.Report));
        Assert.IsNull(ledger.Records.Single().EstimatedCostUsd);
        Assert.AreEqual("failed", ledger.Records.Single().Outcome);
        Assert.IsFalse(string.Join("", result.Diagnostics).Contains("provider payload", StringComparison.Ordinal));
        Assert.AreEqual(1, provider.Requests.Count);
    }

    [TestMethod]
    public async Task ExhaustedBudgetStopsSubsequentRounds()
    {
        var provider = new Provider { Respond = (_, _) => Task.FromResult(new ModelResponse("[]", 10, 5, 1, 1m)) };
        CycleResult result = await Run(provider, new Ledger());
        Assert.AreEqual(1, provider.Requests.Count);
        Assert.AreEqual("incomplete", result.Report.Status);
        Assert.IsTrue(AuditEngine.RequiresHumanReview(result.Report));
        StringAssert.Contains(string.Join("", result.Diagnostics), "budget exhausted");
    }

    [TestMethod]
    public async Task SessionTokenBudgetsAccumulateAcrossRoundsAndStopInferenceAtTheLimit()
    {
        var provider = new Provider
        {
            Respond = (request, _) => Task.FromResult(new ModelResponse("[]",
                Math.Min(10, request.MaxInputTokens), Math.Min(5, request.MaxOutputTokens), 1, 0.1m))
        };
        ReviewConfiguration config = FoundationTests.Config();
        config = config with
        {
            Limits = config.Limits with { MaxInputTokens = 25, MaxOutputTokens = 7 }
        };
        var ledger = new Ledger();

        CycleResult result = await Run(provider, ledger, config);

        Assert.AreEqual(2, provider.Requests.Count);
        CollectionAssert.AreEqual(new[] { 25, 15 }, provider.Requests.Select(r => r.MaxInputTokens).ToArray());
        CollectionAssert.AreEqual(new[] { 7, 2 }, provider.Requests.Select(r => r.MaxOutputTokens).ToArray());
        Assert.AreEqual("human-review-required", ledger.FinalStatus);
        StringAssert.Contains(string.Join("", result.Diagnostics), "token budget exhausted");
    }

    [TestMethod]
    public async Task ThirdRoundUnresolvedCriticalFindingEscalatesToHumanReview()
    {
        Finding critical = new("1.0", "CRITICAL-1", FoundationTests.Sha, "reviewer", "security",
            "critical", 0.95m, "Critical issue", "Unresolved critical issue.",
            [new("file", "src/example.cs", "Relevant source evidence.", 1, 2)],
            "Review and remediate.", "unreviewed");
        string findings = System.Text.Json.JsonSerializer.Serialize(new[] { critical }, Contracts.JsonOptions);
        var provider = new Provider
        {
            Respond = (request, _) => Task.FromResult(new ModelResponse(
                request.RequestIdentity.EndsWith(":3", StringComparison.Ordinal) ? findings : "[]",
                10, 5, 1, 0.1m))
        };
        var ledger = new Ledger();

        CycleResult result = await Run(provider, ledger);

        Assert.AreEqual(3, provider.Requests.Count);
        Assert.AreEqual("incomplete", result.Report.Status);
        Assert.IsTrue(AuditEngine.RequiresHumanReview(result.Report));
        Assert.AreEqual("human-review-required", ledger.FinalStatus);
        StringAssert.Contains(ReportRenderer.Render(result.Report), "Human Review Required");
    }

    [TestMethod]
    public async Task ElevatedRiskStopsFurtherModelCallsImmediately()
    {
        Finding high = new("1.0", "HIGH-1", FoundationTests.Sha, "reviewer", "security",
            "high", 0.9m, "High issue", "Unresolved high issue.",
            [new("file", "src/example.cs", "Relevant source evidence.", 1, 2)],
            "Review and remediate.", "confirmed");
        string findings = System.Text.Json.JsonSerializer.Serialize(new[] { high }, Contracts.JsonOptions);
        var provider = new Provider
        {
            Respond = (_, _) => Task.FromResult(new ModelResponse(findings, 10, 5, 1, 0.1m))
        };

        CycleResult result = await Run(provider, new Ledger());

        Assert.AreEqual(1, provider.Requests.Count);
        Assert.AreEqual("incomplete", result.Report.Status);
        Assert.IsTrue(AuditEngine.RequiresHumanReview(result.Report));
    }

    [TestMethod]
    public async Task ConfigurationCannotRaiseTheThreeRoundHardLimit()
    {
        var config = FoundationTests.Config() with { Rounds = ReviewConfiguration.MaxRounds + 1 };
        var provider = new Provider();

        await Assert.ThrowsExceptionAsync<ContractException>(() => Run(provider, new Ledger(), config));

        Assert.AreEqual(0, provider.Requests.Count);
    }

    [TestMethod]
    public async Task InvalidUsageBecomesUnknownBillingFailure()
    {
        var provider = new Provider { Respond = (_, _) => Task.FromResult(new ModelResponse("[]", 2000, 5, 1, 0.1m)) };
        var ledger = new Ledger();
        await Run(provider, ledger);
        Assert.AreEqual(1, provider.Requests.Count);
        Assert.AreEqual("failed", ledger.Records.Single().Outcome);
        Assert.IsNull(ledger.Records[0].EstimatedCostUsd);
    }

    [TestMethod]
    public async Task TimedOutRequestDoesNotRetry()
    {
        var provider = new Provider
        {
            Respond = async (_, token) =>
            {
                await Task.Delay(Timeout.InfiniteTimeSpan, token);
                return new("[]", 0, 0, 0, 0);
            }
        };
        var config = FoundationTests.Config();
        CycleResult result = await Run(provider, new Ledger(),
            config with { Limits = config.Limits with { TimeoutSeconds = 1 } });
        Assert.AreEqual(1, provider.Requests.Count);
        Assert.AreEqual("incomplete", result.Report.Status);
        Assert.IsTrue(AuditEngine.RequiresHumanReview(result.Report));
    }

    [TestMethod]
    public async Task ExpiredLeasePreventsProviderCall()
    {
        var provider = new Provider();
        CycleResult result = await Run(provider, new Ledger { Expires = DateTimeOffset.UtcNow.AddMinutes(-1) });
        Assert.AreEqual(0, provider.Requests.Count);
        Assert.AreEqual("incomplete", result.Report.Status);
    }

    [TestMethod]
    public async Task CompleteCycleWithSqlitePreventsReplayAcrossInstances()
    {
        string path = Path.Combine(Path.GetTempPath(), $"scriptdom-cycle-{Guid.NewGuid():N}.db");
        try
        {
            var provider = new Provider();
            var config = FoundationTests.Config();
            var context = new ReviewContext(FoundationTests.Sha, FoundationTests.OtherSha,
                "Trusted instructions", "Untrusted diff", [], [], []);
            var authorization = new AuthorizationContext("allancascante", "adversarial-review:run",
                FoundationTests.Sha, "label-event-1", true, false);
            Task<CycleResult> Invoke(IReviewSpendLedger ledger) =>
                new ReviewCycle(provider, ledger).RunAsync(config, FoundationTests.Run(), authorization,
                    context, FoundationTests.Checks(), [],
                    _ => Task.FromResult(new PullRequestState(FoundationTests.Sha, true, false)),
                    10000, [], CancellationToken.None);
            CycleResult result = await Invoke(new SqliteReviewSpendLedger(path));
            Assert.AreEqual("complete", result.Report.Status);
            Assert.AreEqual(3, provider.Requests.Count);
            using (var connection = new Microsoft.Data.Sqlite.SqliteConnection($"Data Source={path};Pooling=False"))
            {
                await connection.OpenAsync();
                using var command = connection.CreateCommand();
                command.CommandText = "SELECT final_status, head_sha, authorized_actor, authorization_label FROM claims;";
                using var reader = await command.ExecuteReaderAsync();
                Assert.IsTrue(await reader.ReadAsync());
                Assert.AreEqual("complete", reader.GetString(0));
                Assert.AreEqual(FoundationTests.Sha, reader.GetString(1));
                Assert.AreEqual("allancascante", reader.GetString(2));
                Assert.AreEqual("adversarial-review:run", reader.GetString(3));
            }
            await Assert.ThrowsExceptionAsync<ContractException>(() => Invoke(new SqliteReviewSpendLedger(path)));
            Assert.AreEqual(3, provider.Requests.Count);
        }
        finally
        {
            File.Delete(path);
            File.Delete(path + "-wal");
            File.Delete(path + "-shm");
        }
    }
}

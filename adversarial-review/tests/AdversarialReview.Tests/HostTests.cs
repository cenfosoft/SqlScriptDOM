using System.Net;
using System.Text;
using System.Text.Json;
using System.Text.Json.Nodes;
using Microsoft.Data.Sqlite;
using Microsoft.VisualStudio.TestTools.UnitTesting;

namespace AdversarialReview.Tests;

[TestClass]
public sealed class HostTests
{
    private sealed class Provider : IReviewProvider
    {
        public List<string> Models { get; } = [];
        public bool Fail { get; set; }
        public Action? AfterReview { get; set; }
        public bool IsAvailable(ModelEntry model) => true;
        public Task<ModelResponse> ReviewAsync(ModelRequest request, CancellationToken cancellationToken)
        {
            Models.Add(request.Model.Model);
            if (Fail) throw new HttpRequestException("private provider response");
            AfterReview?.Invoke();
            return Task.FromResult(new ModelResponse("[]", 10, 5, 1, 0.01m));
        }
    }

    internal sealed class Router(AdmissionTests.Handler admission, IngestionTests.EvidenceHandler evidence) : HttpMessageHandler
    {
        private readonly HttpMessageInvoker admissionHttp = new(admission, disposeHandler: false);
        private readonly HttpMessageInvoker evidenceHttp = new(evidence, disposeHandler: false);
        protected override Task<HttpResponseMessage> SendAsync(HttpRequestMessage request, CancellationToken cancellationToken)
        {
            string path = request.RequestUri!.AbsolutePath;
            return (path.EndsWith("/artifacts", StringComparison.Ordinal) || path.EndsWith("/jobs", StringComparison.Ordinal)
                || path.EndsWith("/zip", StringComparison.Ordinal) ? evidenceHttp : admissionHttp).SendAsync(request, cancellationToken);
        }
        protected override void Dispose(bool disposing)
        {
            if (disposing) { admissionHttp.Dispose(); evidenceHttp.Dispose(); }
            base.Dispose(disposing);
        }
    }

    private string root = null!;
    [TestInitialize]
    public void Initialize() => root = Directory.CreateTempSubdirectory("scriptdom-host-tests-").FullName;
    [TestCleanup]
    public void Cleanup() => Directory.Delete(root, recursive: true);

    private static Task<ReviewContext> Context(AdmittedReview admission, CancellationToken _) => Task.FromResult(
        new ReviewContext(admission.HeadSha, admission.ConfigurationSha, "Trusted", "Diff", [],
            CheckPolicy.Required.Select(c => c.Name + "/" + c.Platform).ToArray(),
            Enumerable.Range(1, 6).Select(i => $"actions-artifact:{i}").ToArray()));

    private static async Task<string?> ReadFinalStatusAsync(string databasePath)
    {
        await using SqliteConnection connection = new(new SqliteConnectionStringBuilder
        {
            DataSource = databasePath,
            Mode = SqliteOpenMode.ReadOnly,
            Pooling = false
        }.ToString());
        await connection.OpenAsync();
        await using SqliteCommand command = connection.CreateCommand();
        command.CommandText = """
            SELECT final_status FROM claims
            WHERE repository = 'owner/repo' AND pull_request = 1;
            """;
        object? status = await command.ExecuteScalarAsync();
        return status is string value ? value : null;
    }

    [TestMethod]
    public async Task HostPersistsReportHistoryRotationAndPreventsPaidReplay()
    {
        using var admission = new AdmissionTests.Handler();
        using var evidence = new IngestionTests.EvidenceHandler();
        using HttpClient http = new(new Router(admission, evidence));
        var ledger = new SqliteReviewSpendLedger(Path.Combine(root, "spend.db"));
        var provider = new Provider();
        var storage = new ReviewHostStorage(root);
        var host = new TrustedReviewHost(new(AdmissionTests.Api(http)), new(AdmissionTests.Api(http), http), ledger, provider, storage);
        ReviewConfiguration config = FoundationTests.Config() with { Rounds = 1 };
        Task<AuditReport> Run(AdmissionRequest request, long runId) => host.RunAsync(config, request, runId, 1, true, Context,
            _ => Task.FromResult(new PullRequestState(FoundationTests.Sha, true, false)), [], CancellationToken.None);
        AuditReport first = await Run(AdmissionTests.Request(), 100);
        Assert.AreEqual("complete", first.Status);
        Assert.AreEqual(first.Run, (await storage.ReadAsync(first.Run, CancellationToken.None)).Run);
        await Assert.ThrowsExceptionAsync<ContractException>(() => Run(AdmissionTests.Request(), 100));
        Assert.AreEqual(1, provider.Models.Count);
        admission.Responses["/repos/owner/repo/issues/1/events?per_page=100&page=1"][0]!["id"] = 51;
        AuditReport second = await Run(AdmissionTests.Request() with { LabelEventId = 51 }, 101);
        Assert.AreEqual("complete", second.Status);
        CollectionAssert.AreEqual(new[] { "first", "second" }, provider.Models);
        ReviewRound[] history = await new SqliteReviewSpendLedger(Path.Combine(root, "spend.db"))
            .ReadHistoryAsync("owner/repo", 1, FoundationTests.Sha, CancellationToken.None);
        CollectionAssert.AreEqual(new[] { "first", "second" }, history.Select(r => r.Model).ToArray());
        Assert.AreEqual(0, (await ledger.ReadHistoryAsync("owner/repo", 1, FoundationTests.OtherSha, CancellationToken.None)).Length);
    }

    [TestMethod]
    public async Task DisabledUnapprovedAndMissingAnalyzerCasesNeverBuildContextOrInvokeModel()
    {
        foreach (string reason in new[] { "disabled", "environment", "analyzers" })
        {
            using var admission = new AdmissionTests.Handler();
            using var evidence = new IngestionTests.EvidenceHandler(complete: reason != "analyzers");
            using HttpClient http = new(new Router(admission, evidence));
            var provider = new Provider();
            var storage = new ReviewHostStorage(root);
            var host = new TrustedReviewHost(new(AdmissionTests.Api(http)), new(AdmissionTests.Api(http), http),
                new(Path.Combine(root, reason + ".db")), provider, storage);
            AuditReport report = await host.RunAsync(FoundationTests.Config() with { Enabled = reason != "disabled" },
                AdmissionTests.Request(), reason == "disabled" ? 100 : reason == "environment" ? 101 : 102, 1, reason != "environment",
                (_, _) => throw new AssertFailedException("Blocked host must not build source context."),
                _ => throw new AssertFailedException("Blocked host must not invoke cycle."), [], CancellationToken.None);
            Assert.AreEqual("incomplete", report.Status);
            Assert.AreEqual(0, provider.Models.Count);
            Assert.IsTrue(report.Blockers.Length > 0);
            Assert.AreEqual(report.Run, (await storage.ReadAsync(report.Run, CancellationToken.None)).Run);
        }
    }

    [TestMethod]
    public async Task ProviderFailurePersistsSanitizedAuditAndUnknownBillingHistory()
    {
        using var admission = new AdmissionTests.Handler();
        using var evidence = new IngestionTests.EvidenceHandler();
        using HttpClient http = new(new Router(admission, evidence));
        var provider = new Provider { Fail = true };
        var ledger = new SqliteReviewSpendLedger(Path.Combine(root, "spend.db"));
        var storage = new ReviewHostStorage(root);
        var host = new TrustedReviewHost(new(AdmissionTests.Api(http)), new(AdmissionTests.Api(http), http), ledger, provider, storage);
        AuditReport report = await host.RunAsync(FoundationTests.Config(), AdmissionTests.Request(), 100, 1, true, Context,
            _ => Task.FromResult(new PullRequestState(FoundationTests.Sha, true, false)), [], CancellationToken.None);
        Assert.AreEqual("incomplete", report.Status);
        Assert.IsNull(report.Rounds.Single().EstimatedCostUsd);
        Assert.IsFalse(Contracts.Write(report, "audit-report").Contains("private provider response", StringComparison.Ordinal));
        Assert.AreEqual("failed", (await ledger.ReadHistoryAsync("owner/repo", 1, FoundationTests.Sha, CancellationToken.None)).Single().Outcome);
    }

    [TestMethod]
    public async Task RevokedAuthorizationBetweenRoundsStopsAndPersistsRecoveryAccounting()
    {
        using var admission = new AdmissionTests.Handler();
        using var evidence = new IngestionTests.EvidenceHandler();
        using HttpClient http = new(new Router(admission, evidence));
        var provider = new Provider
        {
            AfterReview = () => admission.Responses["/repos/owner/repo/issues/1/events?per_page=100&page=1"][0]!["event"] = "unlabeled"
        };
        var store = new ReviewHostStorage(root);
        string databasePath = Path.Combine(root, "spend.db");
        var host = new TrustedReviewHost(new(AdmissionTests.Api(http)), new(AdmissionTests.Api(http), http),
            new(databasePath), provider, store);
        AuditReport report = await host.RunAsync(FoundationTests.Config(), AdmissionTests.Request(), 100, 1, true, Context,
            _ => Task.FromResult(new PullRequestState(FoundationTests.Sha, true, false)), [], CancellationToken.None);
        Assert.AreEqual("incomplete", report.Status);
        Assert.AreEqual(1, provider.Models.Count);
        Assert.AreEqual(0.01m, report.Rounds.Single().EstimatedCostUsd);
        StringAssert.Contains(report.Limitations.Single(), "Recovery");
        Assert.AreEqual(report.Run, (await store.ReadAsync(report.Run, CancellationToken.None)).Run);
        Assert.AreEqual("human-review-required", await ReadFinalStatusAsync(databasePath));
    }

    [DataTestMethod]
    [DataRow("budget")]
    [DataRow("state-query-between-rounds")]
    [DataRow("final-state-query")]
    public async Task InterruptedCyclePersistsHumanReviewStatus(string failure)
    {
        using var admission = new AdmissionTests.Handler();
        using var evidence = new IngestionTests.EvidenceHandler();
        using HttpClient http = new(new Router(admission, evidence));
        var provider = new Provider();
        string databasePath = Path.Combine(root, "spend-" + failure + ".db");
        var store = new ReviewHostStorage(root);
        var host = new TrustedReviewHost(new(AdmissionTests.Api(http)), new(AdmissionTests.Api(http), http),
            new(databasePath), provider, store);
        ReviewConfiguration config = FoundationTests.Config();
        if (failure == "budget")
            config = config with { Limits = config.Limits with { PerAuthorizationUsd = 0.01m } };
        int stateReads = 0;

        AuditReport report = await host.RunAsync(config, AdmissionTests.Request(), 100, 1, true, Context,
            _ =>
            {
                int failureRead = failure switch
                {
                    "state-query-between-rounds" => 3,
                    "final-state-query" => 5,
                    _ => -1
                };
                if (++stateReads == failureRead)
                    throw new HttpRequestException("PR state unavailable.");
                return Task.FromResult(new PullRequestState(FoundationTests.Sha, true, false));
            }, [], CancellationToken.None);

        Assert.AreEqual("incomplete", report.Status);
        int expectedRounds = failure switch { "final-state-query" => 3, "budget" => 1, _ => 1 };
        Assert.AreEqual(expectedRounds, provider.Models.Count);
        Assert.AreEqual(expectedRounds, report.Rounds.Length);
        Assert.AreEqual("human-review-required", await ReadFinalStatusAsync(databasePath));
    }

    [TestMethod]
    public async Task HostLockAndImmutableReportsPreventConcurrentOrConflictingWrites()
    {
        var store = new ReviewHostStorage(root);
        using (store.Acquire("owner/repo", 1))
        {
            Assert.ThrowsException<IOException>(() => new ReviewHostStorage(root).Acquire("OWNER/REPO", 1));
            using FileStream otherPr = store.Acquire("owner/repo", 2);
        }
        using FileStream released = store.Acquire("owner/repo", 1);
        AuditReport report = AuditEngine.Create(FoundationTests.Run(), FoundationTests.Sha, [], [], [], 1);
        await store.SaveAsync(report, [], CancellationToken.None);
        await store.SaveAsync(report, [], CancellationToken.None);
        await Assert.ThrowsExceptionAsync<ContractException>(() =>
            store.SaveAsync(report with { Blockers = ["different"] }, [], CancellationToken.None));
        Assert.AreEqual(0, Directory.GetFiles(root, "*.tmp").Length);
    }

    [TestMethod]
    public async Task GitHubContextVerifiesBlobContentCountsAndRejectsTruncatedDiffs()
    {
        using var handler = new AdmissionTests.Handler();
        byte[] content = Encoding.UTF8.GetBytes("new line\n");
        string blobSha = Convert.ToHexString(System.Security.Cryptography.SHA1.HashData(
            Encoding.UTF8.GetBytes($"blob {content.Length}\0new line\n"))).ToLowerInvariant();
        handler.Responses["/repos/owner/repo/pulls/1/files?per_page=100&page=1"] = new JsonArray(new JsonObject
        {
            ["filename"] = "source.cs", ["sha"] = blobSha, ["status"] = "modified",
            ["patch"] = "@@ -1 +1 @@\n-old line\n+new line", ["additions"] = 1, ["deletions"] = 1
        });
        handler.Responses[$"/repos/owner/repo/git/blobs/{blobSha}"] = new JsonObject
        {
            ["sha"] = blobSha, ["size"] = content.Length, ["encoding"] = "base64", ["content"] = Convert.ToBase64String(content)
        };
        using HttpClient http = new(handler);
        var builder = new GitHubReviewContext(AdmissionTests.Api(http));
        ReviewContext context = await builder.BuildAsync(IngestionTests.Admission(), "trusted", FoundationTests.Checks(), CancellationToken.None);
        Assert.AreEqual(1, context.Files.Single().LineCount);
        StringAssert.Contains(context.UntrustedDiff, "new line");
        handler.Responses["/repos/owner/repo/pulls/1/files?per_page=100&page=1"][0]!["additions"] = 2;
        await Assert.ThrowsExceptionAsync<ContractException>(() =>
            builder.BuildAsync(IngestionTests.Admission(), "trusted", [], CancellationToken.None));
        handler.Responses["/repos/owner/repo/pulls/1/files?per_page=100&page=1"][0]!["additions"] = 1;
        handler.Responses[$"/repos/owner/repo/git/blobs/{blobSha}"]["content"] = Convert.ToBase64String(Encoding.UTF8.GetBytes("bad line\n"));
        await Assert.ThrowsExceptionAsync<ContractException>(() =>
            builder.BuildAsync(IngestionTests.Admission(), "trusted", [], CancellationToken.None));
    }

    private sealed class PublisherHandler : HttpMessageHandler
    {
        public int Writes { get; private set; }
        private string? body;
        protected override async Task<HttpResponseMessage> SendAsync(HttpRequestMessage request, CancellationToken cancellationToken)
        {
            string path = request.RequestUri!.AbsolutePath;
            object result;
            if (path.EndsWith("/pulls/1", StringComparison.Ordinal))
                result = new { state = "open", draft = false, head = new { sha = FoundationTests.Sha } };
            else if (request.Method == HttpMethod.Get)
                result = body is null ? Array.Empty<object>() : new object[] { new { id = 55, body, user = new { type = "Bot", login = "github-actions[bot]" } } };
            else
            {
                Writes++;
                using JsonDocument json = JsonDocument.Parse(await request.Content!.ReadAsStringAsync(cancellationToken));
                body = json.RootElement.GetProperty("body").GetString();
                result = new { id = 55 };
            }
            return new(HttpStatusCode.OK) { Content = new StringContent(JsonSerializer.Serialize(result)) };
        }
    }

    [TestMethod]
    public async Task PublicationRetryReadsStoredReportWithoutProviderCalls()
    {
        using var admission = new AdmissionTests.Handler();
        using var evidence = new IngestionTests.EvidenceHandler();
        using HttpClient http = new(new Router(admission, evidence));
        var provider = new Provider();
        var store = new ReviewHostStorage(root);
        var host = new TrustedReviewHost(new(AdmissionTests.Api(http)), new(AdmissionTests.Api(http), http),
            new(Path.Combine(root, "spend.db")), provider, store);
        AuditReport report = AuditEngine.Create(FoundationTests.Run(), FoundationTests.Sha, [], [], [], 1);
        await store.SaveAsync(report, [], CancellationToken.None);
        using var publication = new PublisherHandler();
        using HttpClient publishingHttp = new(publication);
        var publisher = new GitHubAuditPublisher(publishingHttp, "test-token");
        await host.PublishStoredAsync(report.Run, publisher, [], CancellationToken.None);
        await host.PublishStoredAsync(report.Run, publisher, [], CancellationToken.None);
        Assert.AreEqual(1, publication.Writes);
        Assert.AreEqual(0, provider.Models.Count);
    }
}

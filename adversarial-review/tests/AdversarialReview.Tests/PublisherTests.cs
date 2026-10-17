using System.Net;
using System.Text;
using System.Text.Json;
using Microsoft.VisualStudio.TestTools.UnitTesting;

namespace AdversarialReview.Tests;

[TestClass]
public sealed class PublisherTests
{
    private static AuditReport Report() => AuditEngine.Create(
        FoundationTests.Run(), FoundationTests.Sha, FoundationTests.Checks(), [],
        [FoundationTests.Round(1, "first")], 1);

    private static string Pr(string sha = FoundationTests.Sha, bool draft = false, string state = "open") =>
        JsonSerializer.Serialize(new { state, draft, head = new { sha } });

    private sealed class GitHubHandler : HttpMessageHandler
    {
        public string? Comment { get; set; }
        public string BotLogin { get; set; } = "github-actions[bot]";
        public int Mutations { get; private set; }
        public int Reads { get; private set; }
        public int ChangeAfterReads { get; set; } = int.MaxValue;
        public HttpStatusCode Status { get; set; } = HttpStatusCode.OK;
        public string? CurrentPr { get; set; }
        public bool DuplicateComments { get; set; }
        public bool FullPages { get; set; }

        protected override async Task<HttpResponseMessage> SendAsync(HttpRequestMessage request, CancellationToken cancellationToken)
        {
            Assert.AreEqual("api.github.com", request.RequestUri!.Host);
            Assert.AreEqual("https", request.RequestUri.Scheme);
            string json;
            if (request.Method != HttpMethod.Get)
            {
                Mutations++;
                using var body = JsonDocument.Parse(await request.Content!.ReadAsStringAsync(cancellationToken));
                Comment = body.RootElement.GetProperty("body").GetString();
                json = "{\"id\":7}";
            }
            else if (request.RequestUri.AbsolutePath.Contains("/pulls/", StringComparison.Ordinal))
            {
                Reads++;
                json = CurrentPr ?? Pr(Reads > ChangeAfterReads ? FoundationTests.OtherSha : FoundationTests.Sha);
            }
            else
            {
                var comment = new { id = 7, body = Comment, user = new { type = "Bot", login = BotLogin } };
                json = FullPages ? JsonSerializer.Serialize(Enumerable.Repeat(
                    new { id = 8, body = "unrelated", user = new { type = "User", login = "contributor" } }, 100))
                    : Comment is null ? "[]" : JsonSerializer.Serialize(DuplicateComments ? new[] { comment, comment } : [comment]);
            }
            return new HttpResponseMessage(Status) { Content = new StringContent(json, Encoding.UTF8, "application/json") };
        }
    }

    [TestMethod]
    public async Task CreatesOneCommentAndRerunIsIdempotent()
    {
        using var handler = new GitHubHandler();
        using var http = new HttpClient(handler);
        var publisher = new GitHubAuditPublisher(http, "test-token");
        PublishedComment first = await publisher.PublishAsync(Report(), [], CancellationToken.None);
        PublishedComment second = await publisher.PublishAsync(Report(), [], CancellationToken.None);
        Assert.IsTrue(first.Created);
        Assert.IsFalse(second.Created);
        Assert.AreEqual(7L, first.CommentId);
        Assert.AreEqual(1, handler.Mutations);
    }

    [TestMethod]
    public async Task UpdatesExistingBotCommentButRejectsNewerAttempt()
    {
        using var handler = new GitHubHandler();
        using var http = new HttpClient(handler);
        var publisher = new GitHubAuditPublisher(http, "test-token");
        await publisher.PublishAsync(Report(), [], CancellationToken.None);
        var newer = Report() with { Run = FoundationTests.Run() with { RunAttempt = 2 } };
        await publisher.PublishAsync(newer, [], CancellationToken.None);
        Assert.AreEqual(2, handler.Mutations);
        await Assert.ThrowsExceptionAsync<ContractException>(() => publisher.PublishAsync(Report(), [], CancellationToken.None));
        Assert.AreEqual(2, handler.Mutations);
    }

    [DataTestMethod]
    [DataRow("closed", false)]
    [DataRow("open", true)]
    public async Task ClosedAndDraftPrsAreNotPublished(string state, bool draft)
    {
        using var handler = new GitHubHandler { CurrentPr = Pr(state: state, draft: draft) };
        using var http = new HttpClient(handler);
        var publisher = new GitHubAuditPublisher(http, "test-token");
        await Assert.ThrowsExceptionAsync<ContractException>(() => publisher.PublishAsync(Report(), [], CancellationToken.None));
        Assert.AreEqual(0, handler.Mutations);
    }

    [TestMethod]
    public async Task StaleBeforeWriteDoesNotMutateButRaceAfterWriteIsMarkedStale()
    {
        using var before = new GitHubHandler { ChangeAfterReads = 1 };
        using var beforeHttp = new HttpClient(before);
        await Assert.ThrowsExceptionAsync<ContractException>(() =>
            new GitHubAuditPublisher(beforeHttp, "test-token").PublishAsync(Report(), [], CancellationToken.None));
        Assert.AreEqual(0, before.Mutations);
        using var after = new GitHubHandler { ChangeAfterReads = 2 };
        using var afterHttp = new HttpClient(after);
        var result = await new GitHubAuditPublisher(afterHttp, "test-token").PublishAsync(Report(), [], CancellationToken.None);
        Assert.IsTrue(result.MarkedStale);
        Assert.AreEqual(2, after.Mutations);
        StringAssert.Contains(after.Comment!, "**stale**");
    }

    [TestMethod]
    public async Task HumanOrOtherBotMarkerIsNotAnUpdateTarget()
    {
        using var handler = new GitHubHandler { Comment = ReportRenderer.Render(Report()), BotLogin = "another-bot[bot]" };
        using var http = new HttpClient(handler);
        var result = await new GitHubAuditPublisher(http, "test-token").PublishAsync(Report(), [], CancellationToken.None);
        Assert.IsTrue(result.Created);
    }

    [TestMethod]
    public async Task PermissionFailureIsExplicitAndNeverConsumesModelTokens()
    {
        using var handler = new GitHubHandler { Status = HttpStatusCode.Forbidden };
        using var http = new HttpClient(handler);
        await Assert.ThrowsExceptionAsync<HttpRequestException>(() =>
            new GitHubAuditPublisher(http, "test-token").PublishAsync(Report(), [], CancellationToken.None));
        Assert.AreEqual(0, handler.Mutations);
    }

    [TestMethod]
    public async Task DuplicateCommentsAndMissingMetadataRequireReconciliation()
    {
        using var handler = new GitHubHandler();
        using var http = new HttpClient(handler);
        var publisher = new GitHubAuditPublisher(http, "test-token");
        await publisher.PublishAsync(Report(), [], CancellationToken.None);
        handler.DuplicateComments = true;
        await Assert.ThrowsExceptionAsync<ContractException>(() => publisher.PublishAsync(Report(), [], CancellationToken.None));
        Assert.AreEqual(1, handler.Mutations);
        handler.DuplicateComments = false;
        handler.Comment = ReportRenderer.Render(Report());
        await Assert.ThrowsExceptionAsync<ContractException>(() => publisher.PublishAsync(Report(), [], CancellationToken.None));
        Assert.AreEqual(1, handler.Mutations);
    }

    [TestMethod]
    public async Task PaginationCapCannotCreateADuplicate()
    {
        using var handler = new GitHubHandler { FullPages = true };
        using var http = new HttpClient(handler);
        var publisher = new GitHubAuditPublisher(http, "test-token", maximumPages: 1);
        await Assert.ThrowsExceptionAsync<ContractException>(() => publisher.PublishAsync(Report(), [], CancellationToken.None));
        Assert.AreEqual(0, handler.Mutations);
    }
}

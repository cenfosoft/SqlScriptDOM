using System.Net;
using System.Text;
using System.Text.Json.Nodes;
using Microsoft.VisualStudio.TestTools.UnitTesting;

namespace AdversarialReview.Tests;

[TestClass]
public sealed class AdmissionTests
{
    internal sealed class Handler : HttpMessageHandler
    {
        public Dictionary<string, JsonNode> Responses { get; } = Fixtures();
        public List<string> Requests { get; } = [];
        public Func<HttpRequestMessage, HttpResponseMessage>? Respond { get; set; }
        protected override Task<HttpResponseMessage> SendAsync(HttpRequestMessage request, CancellationToken cancellationToken)
        {
            cancellationToken.ThrowIfCancellationRequested();
            Requests.Add(request.RequestUri!.PathAndQuery);
            Assert.AreEqual("api.github.com", request.RequestUri.Host);
            Assert.AreEqual(HttpMethod.Get, request.Method);
            Assert.AreEqual("test-token", request.Headers.Authorization!.Parameter);
            if (Respond is not null) return Task.FromResult(Respond(request));
            if (!Responses.TryGetValue(request.RequestUri.PathAndQuery, out JsonNode? data))
                throw new AssertFailedException("Unexpected route: " + request.RequestUri.PathAndQuery);
            return Task.FromResult(new HttpResponseMessage(HttpStatusCode.OK)
            { Content = new StringContent(data.ToJsonString(), Encoding.UTF8, "application/json") });
        }
    }

    internal static AdmissionRequest Request() => new(1, FoundationTests.Sha, "allancascante", 50, 70, 2, FoundationTests.OtherSha);
    internal static GitHubReviewApi Api(HttpClient http) => new(http, "test-token", "owner/repo");

    internal static Dictionary<string, JsonNode> Fixtures() => new()
    {
        ["/repos/owner/repo/actions/workflows/pr-validation.yml"] = JsonNode.Parse("""
            {"id":10,"state":"active","path":".github/workflows/pr-validation.yml"}
            """)!,
        ["/repos/owner/repo/pulls/1"] = JsonNode.Parse($$$$"""
            {"number":1,"state":"open","draft":false,"labels":[{"name":"adversarial-review:run"}],
             "head":{"sha":"{{{{FoundationTests.Sha}}}}","repo":{"id":2}},
             "base":{"sha":"{{{{FoundationTests.OtherSha}}}}","repo":{"id":1,"full_name":"owner/repo"}}}
            """)!,
        ["/repos/owner/repo/issues/1/events?per_page=100&page=1"] = JsonNode.Parse("""
            [{"id":50,"event":"labeled","label":{"name":"adversarial-review:run"},
              "actor":{"login":"allancascante","type":"User"},"created_at":"2026-10-17T00:00:00Z"}]
            """)!,
        ["/repos/owner/repo/actions/runs/70/attempts/2"] = JsonNode.Parse($$$"""
            {"id":70,"run_attempt":2,"workflow_id":10,"repository":{"id":1},"head_repository":{"id":2},
             "event":"pull_request","status":"completed","conclusion":"success","head_sha":"{{{FoundationTests.Sha}}}",
             "pull_requests":[{"number":1,"head":{"sha":"{{{FoundationTests.Sha}}}"}}]}
            """)!
    };

    [TestMethod]
    public async Task ForkAndSameRepositoryAdmissionUseAuthenticatedLabelAndExactRunAttempt()
    {
        foreach (int headRepository in new[] { 1, 2 })
        {
            using var handler = new Handler();
            handler.Responses["/repos/owner/repo/pulls/1"]["head"]!["repo"]!["id"] = headRepository;
            handler.Responses["/repos/owner/repo/actions/runs/70/attempts/2"]["head_repository"]!["id"] = headRepository;
            using HttpClient http = new(handler);
            AdmittedReview result = await new TrustedAdmission(Api(http)).VerifyAsync(FoundationTests.Config(), Request(), CancellationToken.None);
            Assert.AreEqual(FoundationTests.Sha, result.HeadSha);
            Assert.AreEqual("github-label-1-50", TrustedAdmission.AuthorizationId(result));
            Assert.AreEqual(headRepository, result.HeadRepositoryId);
            Assert.AreEqual(4, handler.Requests.Count);
        }
    }

    [DataTestMethod]
    [DataRow("draft")]
    [DataRow("closed")]
    [DataRow("sha")]
    [DataRow("label")]
    [DataRow("base")]
    [DataRow("actor")]
    [DataRow("bot")]
    [DataRow("removed")]
    [DataRow("event")]
    [DataRow("workflow")]
    [DataRow("attempt")]
    [DataRow("failed")]
    [DataRow("run-sha")]
    [DataRow("run-repo")]
    [DataRow("run-head-repo")]
    [DataRow("run-pr")]
    public async Task UntrustedStaleOrUnsuccessfulMetadataIsRejected(string mutation)
    {
        using var handler = new Handler();
        JsonNode pr = handler.Responses["/repos/owner/repo/pulls/1"];
        JsonNode label = handler.Responses["/repos/owner/repo/issues/1/events?per_page=100&page=1"][0]!;
        JsonNode run = handler.Responses["/repos/owner/repo/actions/runs/70/attempts/2"];
        switch (mutation)
        {
            case "draft": pr["draft"] = true; break;
            case "closed": pr["state"] = "closed"; break;
            case "sha": pr["head"]!["sha"] = FoundationTests.OtherSha; break;
            case "label": pr["labels"] = new JsonArray(); break;
            case "base": pr["base"]!["repo"]!["full_name"] = "other/repo"; break;
            case "actor": label["actor"]!["login"] = "attacker"; break;
            case "bot": label["actor"]!["type"] = "Bot"; break;
            case "removed": label["event"] = "unlabeled"; break;
            case "event": label["id"] = 51; break;
            case "workflow": run["workflow_id"] = 11; break;
            case "attempt": run["run_attempt"] = 3; break;
            case "failed": run["conclusion"] = "failure"; break;
            case "run-sha": run["head_sha"] = FoundationTests.OtherSha; break;
            case "run-repo": run["repository"]!["id"] = 99; break;
            case "run-head-repo": run["head_repository"]!["id"] = 99; break;
            case "run-pr": run["pull_requests"] = new JsonArray(); break;
        }
        using HttpClient http = new(handler);
        await Assert.ThrowsExceptionAsync<ContractException>(() =>
            new TrustedAdmission(Api(http)).VerifyAsync(FoundationTests.Config(), Request(), CancellationToken.None));
    }

    [TestMethod]
    public async Task PaginationHttpErrorsAndOversizeMetadataFailExplicitly()
    {
        using var handler = new Handler();
        using HttpClient http = new(handler);
        var api = Api(http);
        handler.Respond = _ => new(HttpStatusCode.Forbidden) { Content = new StringContent("secret must not be logged") };
        await Assert.ThrowsExceptionAsync<HttpRequestException>(() => api.GetAsync(api.Route + "/pulls/1", CancellationToken.None));
        handler.Respond = _ => new(HttpStatusCode.OK) { Content = new StringContent(new string(' ', Contracts.MaximumDocumentBytes + 1)) };
        await Assert.ThrowsExceptionAsync<ContractException>(() => api.GetAsync(api.Route + "/pulls/1", CancellationToken.None));
        handler.Respond = _ => new(HttpStatusCode.OK) { Content = new StringContent("[" + string.Join(",", Enumerable.Repeat("{}", 100)) + "]") };
        await Assert.ThrowsExceptionAsync<ContractException>(() =>
            new GitHubReviewApi(http, "test-token", "owner/repo", 1).ListAsync(api.Route + "/issues/1/events", null, CancellationToken.None));
        await Assert.ThrowsExceptionAsync<ContractException>(() => api.GetAsync("repos/other/repo/pulls/1", CancellationToken.None));
    }
}

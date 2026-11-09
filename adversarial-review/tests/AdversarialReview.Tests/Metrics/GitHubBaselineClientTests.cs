using System.Net;
using System.Text;
using System.Text.Json;
using AdversarialReview.Core.Metrics;
using Microsoft.VisualStudio.TestTools.UnitTesting;

namespace AdversarialReview.Tests.Metrics;

[TestClass]
public sealed class GitHubBaselineClientTests
{
    private static readonly DateTimeOffset Observation = DateTimeOffset.Parse("2026-10-16T12:00:00Z");

    [TestMethod]
    public async Task PagesPullRequestsAndTimelinesAndFindsOldCreatedRecentlyReady()
    {
        var requests = new List<string>();
        using var http = CreateHttp((request, _) =>
        {
            string path = request.RequestUri!.PathAndQuery;
            requests.Add(path);
            Assert.AreEqual("Bearer", request.Headers.Authorization!.Scheme);
            Assert.AreEqual("externally-supplied-test-token", request.Headers.Authorization.Parameter);
            Assert.IsTrue(request.Headers.UserAgent.Count > 0);
            Assert.AreEqual("GET", request.Method.Method);
            HttpResponseMessage response;
            if (path.Contains("/pulls?", StringComparison.Ordinal))
            {
                bool first = path.EndsWith("page=1", StringComparison.Ordinal);
                response = Json($"[{PullJson(first ? 1 : 2, created: first ? "2026-10-01T00:00:00Z" : "2024-01-01T00:00:00Z")}]");
                if (first)
                {
                    response.Headers.Add("Link", "<https://untrusted.invalid/steal-token>; rel=\"next\"");
                }
            }
            else if (path.Contains("/issues/2/", StringComparison.Ordinal))
            {
                response = Json(path.EndsWith("page=1", StringComparison.Ordinal)
                    ? """[{"event":"commented","body":"do not persist"}]"""
                    : """[{"event":"ready_for_review","created_at":"2026-10-02T00:00:00Z"}]""");
                if (path.EndsWith("page=1", StringComparison.Ordinal))
                {
                    response.Headers.Add("Link", "<https://api.github.com/next>; rel=\"next\"");
                }
            }
            else
            {
                response = Json("[]");
            }

            Assert.AreEqual("api.github.com", request.RequestUri.Host);
            return Task.FromResult(response);
        });
        var collector = new BaselineCollector(Client(http, token: "externally-supplied-test-token"));
        BaselineReport report = await collector.CollectAsync("owner", "repo");

        Assert.AreEqual(2, report.OpenCensored.Count);
        Assert.AreEqual(DateTimeOffset.Parse("2026-10-02T00:00:00Z"),
            report.OpenCensored.PullRequests.Single(p => p.Number == 2).ReadyAt);
        Assert.AreEqual(5, requests.Count);
        Assert.IsTrue(report.IsComplete);
        string serialized = JsonSerializer.Serialize(report);
        Assert.IsFalse(serialized.Contains("do not persist", StringComparison.Ordinal));
        Assert.IsFalse(serialized.Contains("externally-supplied-test-token", StringComparison.Ordinal));
    }

    [TestMethod]
    public async Task DoesNotFetchTimelinesForBotsDraftsOrClosedUnmerged()
    {
        int count = 0;
        using var http = CreateHttp((_, _) =>
        {
            count++;
            return Task.FromResult(Json($"[{PullJson(1, type: "Bot")},{PullJson(2, login: "tool[bot]")}," +
                $"{PullJson(3, draft: true)},{PullJson(4, state: "closed")}]"));
        });
        BaselineReport report = await new BaselineCollector(Client(http)).CollectAsync("o", "r");
        Assert.AreEqual(1, count);
        Assert.AreEqual(2, report.ExclusionCounts["BotAuthor"]);
        Assert.AreEqual(1, report.ExclusionCounts["CurrentlyDraft"]);
        Assert.AreEqual(1, report.ExclusionCounts["ClosedUnmerged"]);
        Assert.IsTrue(report.IsComplete);
    }

    [TestMethod]
    public async Task DoesNotFetchTimelinesForPullRequestsNotUpdatedInTheMeasurementWindow()
    {
        int requests = 0;
        using var http = CreateHttp((request, _) =>
        {
            requests++;
            return Task.FromResult(Json(request.RequestUri!.AbsolutePath.EndsWith("/pulls", StringComparison.Ordinal)
                ? $"[{PullJson(1, created: "2024-01-01T00:00:00Z", updated: "2026-04-15T00:00:00Z")}]"
                : "[]"));
        });

        BaselineReport report = await new BaselineCollector(Client(http)).CollectAsync("o", "r");

        Assert.AreEqual(1, requests);
        Assert.AreEqual(1, report.ExclusionCounts["OutsideReadinessWindow"]);
        Assert.IsTrue(report.IsComplete);
    }

    [TestMethod]
    public async Task ReportsRateLimitedTimelineAsIncompleteWithoutInferringReadiness()
    {
        using var http = CreateHttp((request, _) => Task.FromResult(
            request.RequestUri!.AbsolutePath.EndsWith("/pulls", StringComparison.Ordinal)
                ? Json($"[{PullJson(1)}]")
                : new HttpResponseMessage(HttpStatusCode.TooManyRequests)));

        BaselineReport report = await new BaselineCollector(Client(http)).CollectAsync("o", "r");

        Assert.IsFalse(report.IsComplete);
        Assert.AreEqual(1, report.ExclusionCounts["TimelineRateLimited"]);
        Assert.IsTrue(report.Diagnostics.Any(d => d.Code == "TimelineRateLimited"));
    }

    [TestMethod]
    public async Task PullRequestPageCapIsExplicitAndStillReportsKnownData()
    {
        using var http = CreateHttp((request, _) =>
        {
            HttpResponseMessage response = Json(request.RequestUri!.AbsolutePath.EndsWith("/pulls", StringComparison.Ordinal)
                ? $"[{PullJson(1)}]" : "[]");
            if (request.RequestUri.AbsolutePath.EndsWith("/pulls", StringComparison.Ordinal))
            {
                response.Headers.Add("Link", "<https://api.github.com/next>; rel=\"next\"");
            }

            return Task.FromResult(response);
        });
        BaselineReport report = await new BaselineCollector(Client(http, maxPages: 1)).CollectAsync("o", "r");
        Assert.AreEqual(1, report.OpenCensored.Count);
        Assert.IsFalse(report.IsComplete);
        Assert.IsTrue(report.Diagnostics.Any(d => d.Code == "PullRequestsPageLimitReached"));
    }

    [TestMethod]
    public async Task FullPageWithoutLinkDoesNotSilentlyTruncate()
    {
        int requests = 0;
        string page = "[" + string.Join(",", Enumerable.Range(1, 100).Select(n => PullJson(n, draft: true))) + "]";
        using var http = CreateHttp((request, _) =>
        {
            requests++;
            return Task.FromResult(Json(request.RequestUri!.Query.EndsWith("page=1", StringComparison.Ordinal) ? page : "[]"));
        });
        RepositorySnapshot snapshot = await Client(http).FetchSnapshotAsync("o", "r");
        Assert.AreEqual(2, requests);
        Assert.AreEqual(100, snapshot.PullRequests.Count);
        Assert.IsTrue(snapshot.IsComplete);
    }

    [TestMethod]
    public async Task TimelinePageCapDoesNotUsePartialReadiness()
    {
        using var http = CreateHttp((request, _) =>
        {
            bool pulls = request.RequestUri!.AbsolutePath.EndsWith("/pulls", StringComparison.Ordinal);
            HttpResponseMessage response = Json(pulls ? $"[{PullJson(1)}]"
                : """[{"event":"ready_for_review","created_at":"2026-10-02T00:00:00Z"}]""");
            if (!pulls)
            {
                response.Headers.Add("Link", "<https://api.github.com/next>; rel=\"next\"");
            }

            return Task.FromResult(response);
        });
        BaselineReport report = await new BaselineCollector(Client(http, maxPages: 1)).CollectAsync("o", "r");
        Assert.IsFalse(report.IsComplete);
        Assert.AreEqual(0, report.OpenCensored.Count);
        Assert.AreEqual(1, report.ExclusionCounts["TimelinePageLimitReached"]);
    }

    [DataTestMethod]
    [DataRow("not-json")]
    [DataRow("{}")]
    [DataRow("""[{"event":"ready_for_review","created_at":"not-a-date"}]""")]
    [DataRow("""[{"created_at":"2026-10-02T00:00:00Z"}]""")]
    public async Task MalformedTimelineIsExcludedWithoutFallback(string timeline)
    {
        using var http = CreateHttp((request, _) => Task.FromResult(Json(
            request.RequestUri!.AbsolutePath.EndsWith("/pulls", StringComparison.Ordinal)
                ? $"[{PullJson(1)}]" : timeline)));
        BaselineReport report = await new BaselineCollector(Client(http)).CollectAsync("o", "r");
        Assert.AreEqual(1, report.ExclusionCounts["TimelineMalformed"]);
        Assert.AreEqual(0, report.OpenCensored.Count);
        Assert.IsFalse(report.IsComplete);
    }

    [TestMethod]
    public async Task UnavailableTimelineAndListFailuresProduceSafeIncompleteReports()
    {
        using var http = CreateHttp((request, _) => Task.FromResult(
            request.RequestUri!.AbsolutePath.EndsWith("/pulls", StringComparison.Ordinal)
                ? Json($"[{PullJson(1)}]")
                : new HttpResponseMessage(HttpStatusCode.Forbidden) { Content = new StringContent("secret-body") }));
        BaselineReport report = await new BaselineCollector(Client(http)).CollectAsync("o", "r");
        Assert.IsFalse(report.IsComplete);
        Assert.AreEqual(1, report.ExclusionCounts["TimelineUnavailable"]);
        Assert.IsFalse(JsonSerializer.Serialize(report).Contains("secret-body", StringComparison.Ordinal));

        using var unavailable = CreateHttp((_, _) =>
            Task.FromResult(new HttpResponseMessage(HttpStatusCode.TooManyRequests)));
        BaselineReport failed = await new BaselineCollector(Client(unavailable)).CollectAsync("o", "r");
        Assert.IsFalse(failed.IsComplete);
        Assert.IsTrue(failed.Diagnostics.Any(d => d.Code == "PullRequestsRateLimited"));
    }

    [DataTestMethod]
    [DataRow("not-json")]
    [DataRow("{}")]
    public async Task MalformedListProducesIncompleteResult(string body)
    {
        using var http = CreateHttp((_, _) => Task.FromResult(Json(body)));
        RepositorySnapshot snapshot = await Client(http).FetchSnapshotAsync("o", "r");
        Assert.IsFalse(snapshot.IsComplete);
        Assert.IsTrue(snapshot.Diagnostics.Any(d => d.Code == "PullRequestsMalformed"));
    }

    [TestMethod]
    public async Task MalformedPullRequestDoesNotDiscardOtherItems()
    {
        using var http = CreateHttp((request, _) => Task.FromResult(Json(
            request.RequestUri!.AbsolutePath.EndsWith("/pulls", StringComparison.Ordinal)
                ? $"[{{\"number\":\"wrong-type\"}},{PullJson(1)}]" : "[]")));
        BaselineReport report = await new BaselineCollector(Client(http)).CollectAsync("o", "r");
        Assert.AreEqual(1, report.OpenCensored.Count);
        Assert.IsFalse(report.IsComplete);
        Assert.IsTrue(report.Diagnostics.Any(d => d.Code == "MalformedPullRequest"));
    }

    [TestMethod]
    public async Task CancellationIsPropagatedRatherThanReportedAsSuccess()
    {
        using var source = new CancellationTokenSource();
        using var http = CreateHttp(async (_, token) =>
        {
            source.Cancel();
            await Task.Delay(Timeout.InfiniteTimeSpan, token);
            return Json("[]");
        });
        await Assert.ThrowsExceptionAsync<TaskCanceledException>(() =>
            Client(http).FetchSnapshotAsync("o", "r", source.Token));
    }

    [TestMethod]
    public async Task TimeoutsAndNetworkErrorsAreIncompleteWithoutExceptionDetails()
    {
        using var timeout = CreateHttp((_, _) => throw new TaskCanceledException("private detail"));
        RepositorySnapshot timedOut = await Client(timeout).FetchSnapshotAsync("o", "r");
        Assert.IsFalse(timedOut.IsComplete);
        Assert.AreEqual("PullRequestsTimeout", timedOut.Diagnostics.Single().Code);

        using var network = CreateHttp((_, _) => throw new HttpRequestException("private detail"));
        RepositorySnapshot unavailable = await Client(network).FetchSnapshotAsync("o", "r");
        Assert.IsFalse(unavailable.IsComplete);
        Assert.AreEqual("PullRequestsUnavailable", unavailable.Diagnostics.Single().Code);
    }

    [TestMethod]
    public async Task DuplicatePagesAreExplicitlyIncompleteAndDoNotDoubleCount()
    {
        using var http = CreateHttp((request, _) =>
        {
            bool pulls = request.RequestUri!.AbsolutePath.EndsWith("/pulls", StringComparison.Ordinal);
            HttpResponseMessage response = Json(pulls ? $"[{PullJson(1)}]" : "[]");
            if (pulls && request.RequestUri.Query.EndsWith("page=1", StringComparison.Ordinal))
            {
                response.Headers.Add("Link", "<https://api.github.com/next>; rel=\"next\"");
            }

            return Task.FromResult(response);
        });
        BaselineReport report = await new BaselineCollector(Client(http)).CollectAsync("o", "r");
        Assert.AreEqual(1, report.OpenCensored.Count);
        Assert.IsFalse(report.IsComplete);
        Assert.IsTrue(report.Diagnostics.Any(d => d.Code == "DuplicatePullRequest"));
    }

    [TestMethod]
    public async Task ValidatesBoundsAndNamesBeforeSendingRequests()
    {
        int requests = 0;
        using var http = CreateHttp((_, _) => { requests++; return Task.FromResult(Json("[]")); });
        Assert.ThrowsException<ArgumentOutOfRangeException>(() => Client(http, maxPages: 0));
        await Assert.ThrowsExceptionAsync<ArgumentException>(() => Client(http).FetchSnapshotAsync("../o", "r"));
        await Assert.ThrowsExceptionAsync<ArgumentOutOfRangeException>(() =>
            new BaselineCollector(Client(http)).CollectAsync("o", "r", 0));
        Assert.AreEqual(0, requests);
        http.Timeout = Timeout.InfiniteTimeSpan;
        Assert.ThrowsException<ArgumentException>(() => Client(http));
    }

    private static GitHubBaselineClient Client(HttpClient http, int maxPages = 10, string? token = null) =>
        new(http, token, maxPages, new FixedTimeProvider());

    private static HttpClient CreateHttp(Func<HttpRequestMessage, CancellationToken, Task<HttpResponseMessage>> respond) =>
        new(new SyntheticHandler(respond)) { Timeout = TimeSpan.FromSeconds(5) };

    private static HttpResponseMessage Json(string content) =>
        new(HttpStatusCode.OK) { Content = new StringContent(content, Encoding.UTF8, "application/json") };

    private static string PullJson(int number, string login = "human", string type = "User",
        bool draft = false, string state = "open", string created = "2026-10-01T00:00:00Z",
        string updated = "2026-10-15T00:00:00Z") =>
        JsonSerializer.Serialize(new
        {
            number,
            user = new { login, type },
            created_at = created,
            updated_at = updated,
            state,
            draft,
            merged_at = (string?)null,
            closed_at = state == "closed" ? "2026-10-15T00:00:00Z" : null
        });

    private sealed class FixedTimeProvider : TimeProvider
    {
        public override DateTimeOffset GetUtcNow() => Observation;
    }

    private sealed class SyntheticHandler(
        Func<HttpRequestMessage, CancellationToken, Task<HttpResponseMessage>> respond) : HttpMessageHandler
    {
        protected override Task<HttpResponseMessage> SendAsync(HttpRequestMessage request, CancellationToken cancellationToken) =>
            respond(request, cancellationToken);
    }
}

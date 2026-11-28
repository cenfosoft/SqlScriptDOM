using System.Net;
using System.Text;
using System.Text.Json;
using Microsoft.VisualStudio.TestTools.UnitTesting;

namespace AdversarialReview.Tests;

[TestClass]
public sealed class RiskStageTests
{
    private sealed class Handler(Func<HttpRequestMessage, string?, HttpResponseMessage> respond) : HttpMessageHandler
    {
        public List<(HttpMethod Method, string Path, string? Body)> Requests { get; } = [];
        protected override async Task<HttpResponseMessage> SendAsync(HttpRequestMessage request, CancellationToken cancellationToken)
        {
            string? body = request.Content is null ? null : await request.Content.ReadAsStringAsync(cancellationToken);
            Requests.Add((request.Method, request.RequestUri!.AbsolutePath, body));
            return respond(request, body);
        }
    }

    private static HttpResponseMessage Json(string body, HttpStatusCode status = HttpStatusCode.OK) =>
        new(status) { Content = new StringContent(body, Encoding.UTF8, "application/json") };

    private static RiskRubric Rubric() => RiskRubric.Parse(
        File.ReadAllText(Path.Combine(AppContext.BaseDirectory, "config/risk-rubric.json")));

    private static (JevRiskAssessor, Handler) Jev(string classification = "low")
    {
        var handler = new Handler((_, _) => Json(
            """{"model":"jev-1.14.0","answers":{"overall_risk":{"type":"choice","choice":""" + "\"" + classification + "\"" +
            ""","confidence":0.8,"probabilities":{"low":0.8,"mid":0.15,"high":0.05}}},"usage":{"input_tokens":10,"output_tokens":2}}"""));
        return (new JevRiskAssessor(new HttpClient(handler) { Timeout = TimeSpan.FromSeconds(10) }, "test-key"), handler);
    }

    private static Task<RiskSection> Evaluate(AuditReport review, JevRiskAssessor? assessor, Dictionary<string, byte[]>? evidence = null) =>
        RiskStage.EvaluateAsync(review, [RiskSignalCollectorTests.Code()], evidence ?? RiskSignalCollectorTests.Evidence(),
            Rubric(), assessor, CancellationToken.None);

    [TestMethod]
    public async Task CompleteLowRiskReviewIsEligibleAndRendersRiskSection()
    {
        var (jev, _) = Jev();
        AuditReport review = RiskSignalCollectorTests.Review();
        RiskSection risk = await Evaluate(review, jev);
        Assert.IsTrue(risk.Decision.EligibleForLowRiskLabel);
        Assert.AreEqual(RiskStage.LowRiskLabel, risk.Label);
        Assert.AreEqual("pending", risk.AutoMergeOutcome);

        AuditReport published = review with { Risk = risk with { LabelOutcome = "applied", AutoMergeOutcome = "enabled" } };
        Contracts.Read<AuditReport>(Contracts.Write(published, "audit-report"), "audit-report");
        string comment = ReportRenderer.Render(published);
        StringAssert.Contains(comment, "Risk: low. Eligible for automatic merge after required approvals.");
        StringAssert.Contains(comment, "### Risk assessment (rubric initial)");
        StringAssert.Contains(comment, "1/2 added executable lines");
        StringAssert.Contains(comment, "automatic merge: enabled");
        Assert.IsFalse(comment.Contains("Not a merge gate", StringComparison.Ordinal));
    }

    [TestMethod]
    public async Task MissingInputsOrCredentialSkipJevAndRequireHumanReview()
    {
        var (jev, handler) = Jev();
        RiskSection missing = await Evaluate(RiskSignalCollectorTests.Review(), jev, RiskSignalCollectorTests.Evidence(coverage: false));
        Assert.AreEqual(0, handler.Requests.Count);
        Assert.IsNull(missing.Assessment);
        Assert.AreEqual(RiskStage.HumanReviewLabel, missing.Label);
        StringAssert.Contains(string.Join(" ", missing.Missing), "coverage");

        RiskSection noKey = await Evaluate(RiskSignalCollectorTests.Review(), null);
        Assert.IsFalse(noKey.Decision.EligibleForLowRiskLabel);
        Assert.IsNotNull(noKey.Factors);
        StringAssert.Contains(ReportRenderer.Render(RiskSignalCollectorTests.Review() with { Risk = noKey }), "not requested");
    }

    [TestMethod]
    public async Task HighFindingIsStillAssessedButBlocksAndStaleIsNeverAssessed()
    {
        var (jev, handler) = Jev();
        AuditReport review = RiskSignalCollectorTests.Review([RiskSignalCollectorTests.Finding("h1", "high")]);
        RiskSection risk = await Evaluate(review, jev);
        Assert.AreEqual(1, handler.Requests.Count);
        Assert.AreEqual("low", risk.Assessment!.Classification);
        Assert.IsFalse(risk.Decision.EligibleForLowRiskLabel);
        Assert.AreEqual(RiskStage.HumanReviewLabel, risk.Label);
        Assert.AreEqual("not-eligible", risk.AutoMergeOutcome);

        RiskSection stale = await Evaluate(review with { Status = "stale" }, jev);
        Assert.AreEqual(1, handler.Requests.Count);
        Assert.IsFalse(stale.Decision.EligibleForLowRiskLabel);
    }

    [TestMethod]
    public async Task ContractRejectsForgedEligibilityAndLegacyReportsOmitRisk()
    {
        var (jev, _) = Jev("mid");
        AuditReport review = RiskSignalCollectorTests.Review();
        RiskSection mid = await Evaluate(review, jev);
        Assert.ThrowsException<ContractException>(() => Contracts.Write(review with
        { Risk = mid with { Decision = new(true, []) } }, "audit-report"));
        RiskSection low = await Evaluate(review, Jev().Item1);
        Assert.ThrowsException<ContractException>(() => Contracts.Write(review with
        { Status = "incomplete", Risk = low }, "audit-report"));
        Assert.IsFalse(Contracts.Write(review, "audit-report").Contains("\"risk\"", StringComparison.Ordinal));
    }

    private static string Pr(string sha = FoundationTests.Sha, string state = "open", string autoMerge = "null") =>
        $$$"""{"state":"{{{state}}}","draft":false,"node_id":"PR_node","auto_merge":{{{autoMerge}}},"head":{"sha":"{{{sha}}}"}}""";

    [TestMethod]
    public async Task LabelsReplaceTheOppositeLabelOnlyForTheEvaluatedHead()
    {
        var handler = new Handler((request, _) => request.Method == HttpMethod.Delete
            ? Json("""{"message":"Label does not exist"}""", HttpStatusCode.NotFound)
            : request.RequestUri!.AbsolutePath.EndsWith("/pulls/7", StringComparison.Ordinal) ? Json(Pr()) : Json("[]"));
        var actions = new GitHubPullRequestActions(new HttpClient(handler) { Timeout = TimeSpan.FromSeconds(10) }, "token", "owner/repo");
        Assert.AreEqual("applied", await actions.ApplyRiskLabelAsync(7, FoundationTests.Sha, RiskStage.LowRiskLabel, CancellationToken.None));
        Assert.AreEqual(HttpMethod.Post, handler.Requests[1].Method);
        StringAssert.Contains(handler.Requests[1].Body, RiskStage.LowRiskLabel);
        StringAssert.EndsWith(handler.Requests[2].Path, Uri.EscapeDataString(RiskStage.HumanReviewLabel));

        await Assert.ThrowsExceptionAsync<ContractException>(() =>
            actions.ApplyRiskLabelAsync(7, FoundationTests.OtherSha, RiskStage.LowRiskLabel, CancellationToken.None));
        await Assert.ThrowsExceptionAsync<ContractException>(() =>
            actions.ApplyRiskLabelAsync(7, FoundationTests.Sha, "merge-now", CancellationToken.None));
    }

    [TestMethod]
    public async Task AutoMergeBindsTheHeadAndApprovalRequiresTheNamedEnvironment()
    {
        string graphql = """{"data":{"enablePullRequestAutoMerge":{"pullRequest":{"number":7}}}}""";
        string autoMerge = "null";
        var handler = new Handler((request, _) => request.RequestUri!.AbsolutePath switch
        {
            "/graphql" => Json(graphql),
            "/repos/owner/repo/actions/runs/9/approvals" =>
                Json("""[{"state":"approved","environments":[{"name":"adversarial-review"}]},{"state":"rejected","environments":[{"name":"other"}]}]"""),
            _ => Json(Pr(autoMerge: autoMerge))
        });
        var actions = new GitHubPullRequestActions(new HttpClient(handler) { Timeout = TimeSpan.FromSeconds(10) }, "token", "owner/repo");
        Assert.AreEqual("enabled", await actions.EnableAutoMergeAsync(7, FoundationTests.Sha, CancellationToken.None));
        using (JsonDocument mutation = JsonDocument.Parse(handler.Requests[^1].Body!))
        {
            Assert.AreEqual(FoundationTests.Sha, mutation.RootElement.GetProperty("variables").GetProperty("sha").GetString());
            StringAssert.Contains(mutation.RootElement.GetProperty("query").GetString(), "mergeMethod: MERGE");
        }
        graphql = """{"errors":[{"message":"Auto merge is not allowed for this repository"}]}""";
        Assert.AreEqual("failed", await actions.EnableAutoMergeAsync(7, FoundationTests.Sha, CancellationToken.None));

        // Not enabled: nothing to disable and no mutation is sent.
        int requests = handler.Requests.Count;
        Assert.AreEqual("not-eligible", await actions.DisableAutoMergeAsync(7, FoundationTests.Sha, CancellationToken.None));
        Assert.AreEqual(requests + 1, handler.Requests.Count);
        autoMerge = """{"merge_method":"merge"}""";
        graphql = """{"data":{"disablePullRequestAutoMerge":{"pullRequest":{"number":7}}}}""";
        Assert.AreEqual("disabled", await actions.DisableAutoMergeAsync(7, FoundationTests.Sha, CancellationToken.None));
        StringAssert.Contains(handler.Requests[^1].Body, "disablePullRequestAutoMerge");

        Assert.IsTrue(await actions.IsEnvironmentApprovedAsync(9, "adversarial-review", CancellationToken.None));
        Assert.IsFalse(await actions.IsEnvironmentApprovedAsync(9, "other", CancellationToken.None));
    }
}

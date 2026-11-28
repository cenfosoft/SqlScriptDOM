using System.Net;
using System.Text;
using System.Text.Json;
using Microsoft.VisualStudio.TestTools.UnitTesting;

namespace AdversarialReview.Tests;

[TestClass]
public sealed class JevRiskAssessorTests
{
    private const string Key = "jev-test-key-not-a-secret";

    private sealed class Handler(Func<HttpRequestMessage, CancellationToken, Task<HttpResponseMessage>> respond) : HttpMessageHandler
    {
        public HttpRequestMessage? Request { get; private set; }
        public string? Body { get; private set; }

        protected override async Task<HttpResponseMessage> SendAsync(HttpRequestMessage request, CancellationToken cancellationToken)
        {
            Request = request;
            Body = request.Content is null ? null : await request.Content.ReadAsStringAsync(cancellationToken);
            return await respond(request, cancellationToken);
        }
    }

    private static RiskRubric Rubric() => RiskRubric.Parse(
        File.ReadAllText(Path.Combine(AppContext.BaseDirectory, "config/risk-rubric.json")));

    private static RiskFactors Factors(bool blocked = false) => Rubric().Classify(
        new(40, 10, 30, 30, 1, 0, 0, true, blocked));

    private static string Answer(string choice = "low", string confidence = "0.82", string probabilities =
        """{ "low": 0.82, "mid": 0.15, "high": 0.03 }""") => $$"""
        {
          "model": "jev-1.14.0",
          "answers": {
            "overall_risk": { "type": "choice", "choice": "{{choice}}", "confidence": {{confidence}}, "probabilities": {{probabilities}} }
          },
          "usage": { "input_tokens": 412, "output_tokens": 9 }
        }
        """;

    private static (JevRiskAssessor Assessor, Handler Handler) Create(Func<HttpRequestMessage, CancellationToken, Task<HttpResponseMessage>> respond)
    {
        var handler = new Handler(respond);
        return (new JevRiskAssessor(new HttpClient(handler) { Timeout = TimeSpan.FromSeconds(30) }, Key), handler);
    }

    private static (JevRiskAssessor Assessor, Handler Handler) Create(Func<HttpRequestMessage, Task<HttpResponseMessage>> respond) =>
        Create((request, _) => respond(request));

    private static Task<HttpResponseMessage> Json(string body, HttpStatusCode status = HttpStatusCode.OK) =>
        Task.FromResult(new HttpResponseMessage(status) { Content = new StringContent(body, Encoding.UTF8, "application/json") });

    [TestMethod]
    public async Task SendsOnlyCategoricalFactorsWithBearerAuthentication()
    {
        var (assessor, handler) = Create(_ => Json(Answer()));
        JevRiskAssessment result = await assessor.AssessAsync(Rubric(), Factors(), CancellationToken.None);

        Assert.AreEqual(HttpMethod.Post, handler.Request!.Method);
        Assert.AreEqual(JevRiskAssessor.Endpoint, handler.Request.RequestUri);
        Assert.AreEqual("Bearer", handler.Request.Headers.Authorization!.Scheme);
        Assert.AreEqual(Key, handler.Request.Headers.Authorization.Parameter);
        using JsonDocument body = JsonDocument.Parse(handler.Body!);
        Assert.AreEqual("jev-1.14.0", body.RootElement.GetProperty("model").GetString());
        string[] stateFields = body.RootElement.GetProperty("state").EnumerateObject().Select(p => p.Name).Order().ToArray();
        CollectionAssert.AreEqual(new[] { "addedLineCoverage", "adversarialFindings", "changedLines", "integrationTests",
            "newStaticAnalysisWarnings", "rubricVersion", "unresolvedHighOrCriticalFinding" }, stateFields);
        JsonElement question = body.RootElement.GetProperty("questions").GetProperty(JevRiskAssessor.QuestionName);
        Assert.AreEqual("choice", question.GetProperty("type").GetString());
        CollectionAssert.AreEqual(new[] { "high", "low", "mid" },
            question.GetProperty("criteria").EnumerateObject().Select(p => p.Name).Order().ToArray());
        Assert.IsFalse(handler.Body!.Contains(Key, StringComparison.Ordinal));

        Assert.AreEqual("assessed", result.Status);
        Assert.AreEqual("low", result.Classification);
        Assert.AreEqual(0.82m, result.Confidence);
        Assert.AreEqual(412, result.InputTokens);
    }

    [TestMethod]
    public async Task OnlyAssessedLowRiskWithoutBlockersIsEligible()
    {
        JevRiskAssessment low = await Create(_ => Json(Answer())).Assessor.AssessAsync(Rubric(), Factors(), CancellationToken.None);
        Assert.IsTrue(JevRiskAssessor.Decide(Factors(), low).EligibleForLowRiskLabel);

        JevRiskAssessment mid = await Create(_ => Json(Answer("mid"))).Assessor.AssessAsync(Rubric(), Factors(), CancellationToken.None);
        Assert.IsFalse(JevRiskAssessor.Decide(Factors(), mid).EligibleForLowRiskLabel);

        // A high finding still yields a risk assessment but blocks independently.
        RiskDecision blocked = JevRiskAssessor.Decide(Factors(blocked: true), low with { });
        Assert.IsFalse(blocked.EligibleForLowRiskLabel);
        StringAssert.Contains(blocked.Reasons.Single(), "high or critical");
    }

    [TestMethod]
    public async Task ServiceFailuresAreRecordedAsUnavailableAndNeverEligible()
    {
        foreach (HttpStatusCode status in new[] { HttpStatusCode.Unauthorized, HttpStatusCode.TooManyRequests, (HttpStatusCode)529 })
        {
            JevRiskAssessment result = await Create(_ => Json("""{"detail":"echoed secret"}""", status))
                .Assessor.AssessAsync(Rubric(), Factors(), CancellationToken.None);
            Assert.AreEqual("unavailable", result.Status);
            Assert.IsNull(result.Classification);
            StringAssert.Contains(result.Reason, ((int)status).ToString(System.Globalization.CultureInfo.InvariantCulture));
            Assert.IsFalse(result.Reason!.Contains("echoed", StringComparison.Ordinal));
            Assert.IsFalse(JevRiskAssessor.Decide(Factors(), result).EligibleForLowRiskLabel);
        }

        JevRiskAssessment unreachable = await Create(_ => throw new HttpRequestException("dns"))
            .Assessor.AssessAsync(Rubric(), Factors(), CancellationToken.None);
        Assert.AreEqual("unavailable", unreachable.Status);
    }

    [TestMethod]
    public async Task TimeoutIsUnavailableButCallerCancellationPropagates()
    {
        RiskRubric rubric = Rubric() with { JevTimeoutSeconds = 1 };
        JevRiskAssessment timedOut = await Create(async (_, token) =>
        {
            await Task.Delay(TimeSpan.FromSeconds(10), token);
            return new HttpResponseMessage(HttpStatusCode.OK);
        }).Assessor.AssessAsync(rubric, rubric.Classify(new(1, 0, 1, 1, 0, 0, 0, null, false)), CancellationToken.None)
            .WaitAsync(TimeSpan.FromSeconds(20));
        Assert.AreEqual("unavailable", timedOut.Status);
        StringAssert.Contains(timedOut.Reason, "timed out");

        using var cancelled = new CancellationTokenSource();
        cancelled.Cancel();
        Exception? propagated = null;
        try { await Create(_ => Json(Answer())).Assessor.AssessAsync(Rubric(), Factors(), cancelled.Token); }
        catch (Exception error) { propagated = error; }
        Assert.IsInstanceOfType(propagated, typeof(OperationCanceledException));
    }

    [TestMethod]
    public async Task MalformedOrUnexpectedResponsesAreInvalid()
    {
        foreach (string body in new[]
        {
            "not json",
            """{"model":"jev-1.14.0","answers":{},"usage":{"input_tokens":1,"output_tokens":1}}""",
            Answer("unknown"),
            Answer(confidence: "1.5"),
            Answer(probabilities: """{ "low": 0.9, "mid": 0.1 }"""),
            Answer(probabilities: """{ "low": 0.9, "mid": 0.1, "critical": 0.0 }"""),
            Answer().Replace("\"choice\", \"choice\"", "\"score\", \"choice\"", StringComparison.Ordinal),
            Answer().Replace("412", "-1", StringComparison.Ordinal)
        })
        {
            JevRiskAssessment result = await Create(_ => Json(body)).Assessor.AssessAsync(Rubric(), Factors(), CancellationToken.None);
            Assert.AreEqual("invalid", result.Status, body);
            Assert.IsFalse(JevRiskAssessor.Decide(Factors(), result).EligibleForLowRiskLabel);
        }
    }

    [TestMethod]
    public void RubricRequiresPinnedModelAndMatchingFactorVersion()
    {
        foreach (string model in new[] { "jev-latest", "jev-preview", "gpt-4" })
            Assert.ThrowsException<ContractException>(() => (Rubric() with { JevModel = model }).Classify(
                new(1, 0, 1, 1, 0, 0, 0, null, false)));
        Assert.ThrowsException<ContractException>(() =>
            JevRiskAssessor.BuildRequest(Rubric() with { Version = "other" }, Factors()));
        Assert.ThrowsException<ArgumentException>(() => new JevRiskAssessor(
            new HttpClient { Timeout = Timeout.InfiniteTimeSpan }, Key));

        string sample = File.ReadAllText(Path.Combine(AppContext.BaseDirectory, "fixtures/risk-signals.sample.json"));
        Assert.AreEqual("low", Rubric().Classify(Contracts.Read<RiskSignals>(sample, "risk-signals")).ChangedLines);
        // A missing count must fail rather than default to zero and understate risk.
        Assert.ThrowsException<ContractException>(() => Contracts.Read<RiskSignals>(
            sample.Replace("\"addedCodeLines\": 40,", "", StringComparison.Ordinal), "risk-signals"));
    }
}

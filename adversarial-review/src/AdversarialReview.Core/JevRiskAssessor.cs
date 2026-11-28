using System.Net.Http.Headers;
using System.Text;
using System.Text.Json;

namespace AdversarialReview;

public sealed record JevRiskAssessment(
    string SchemaVersion, string Status, string RubricVersion, string RequestedModel,
    string? AnsweredModel, string? Classification, decimal? Confidence,
    IReadOnlyDictionary<string, decimal>? Probabilities, int? InputTokens, int? OutputTokens,
    string? Reason);

public sealed record RiskDecision(bool EligibleForLowRiskLabel, string[] Reasons);

public sealed class JevRiskAssessor
{
    public static readonly Uri Endpoint = new("https://api.typesafe.ai/v1/systemone");
    public const string QuestionName = "overall_risk";
    private const int MaximumResponseBytes = 64 * 1024;
    private static readonly string[] Levels = ["low", "mid", "high"];
    private readonly HttpClient http;
    private readonly string apiKey;

    public JevRiskAssessor(HttpClient http, string apiKey)
    {
        ArgumentNullException.ThrowIfNull(http);
        if (string.IsNullOrWhiteSpace(apiKey))
            throw new ArgumentException("A JEV API key is required.", nameof(apiKey));
        if (http.Timeout == Timeout.InfiniteTimeSpan)
            throw new ArgumentException("A finite HTTP timeout is required.", nameof(http));
        this.http = http;
        this.apiKey = apiKey;
    }

    // Only categorical factors are sent: no source code, paths, finding text, or identities.
    public static string BuildRequest(RiskRubric rubric, RiskFactors factors)
    {
        ArgumentNullException.ThrowIfNull(rubric);
        ArgumentNullException.ThrowIfNull(factors);
        if (factors.RubricVersion != rubric.Version)
            throw new ContractException("Risk factors were classified with a different rubric version.");
        var request = new
        {
            model = rubric.JevModel,
            state = new
            {
                rubricVersion = rubric.Version,
                changedLines = factors.ChangedLines,
                addedLineCoverage = factors.Coverage,
                adversarialFindings = factors.AdversarialFindings,
                newStaticAnalysisWarnings = factors.StaticWarnings,
                integrationTests = factors.HasIntegrationTests switch
                {
                    true => "present",
                    false => "absent",
                    null => "unknown"
                },
                unresolvedHighOrCriticalFinding = factors.HasUnresolvedHighOrCriticalFinding
            },
            questions = new Dictionary<string, object>
            {
                [QuestionName] = new
                {
                    type = "choice",
                    instructions = string.Join(" ",
                        "Assess the overall integration risk of a pull request that already passed its required build, test, and static-analysis checks and an adversarial code review.",
                        $"changedLines is the sum of added and deleted code lines: low is 0-{rubric.ChangedLinesLowMaximum}, mid is {rubric.ChangedLinesLowMaximum + 1}-{rubric.ChangedLinesMidMaximum}, high is more.",
                        $"addedLineCoverage is test coverage of added executable lines: low is {rubric.CoverageLowMinimumPercent}%, high is {rubric.CoverageHighMaximumPercent}% or less, mid is in between; not-applicable means no executable lines were added.",
                        $"adversarialFindings: low is up to {rubric.LowFindingsLowMaximum} low-severity findings, mid is more low findings or up to {rubric.MediumFindingsMidMaximum} medium finding(s), high is more medium findings.",
                        $"newStaticAnalysisWarnings: low is up to {rubric.WarningsLowMaximum}, mid is up to {rubric.WarningsMidMaximum}, high is more.",
                        "Combine the factors holistically; do not simply select the highest factor. Smaller changes, complete coverage, and integration tests reduce risk.",
                        "An unresolved high or critical finding is a separate merge blocker; still assess its contribution to risk."),
                    criteria = new Dictionary<string, string>
                    {
                        ["low"] = "Low risk: suitable to propose for integration without additional human scrutiny.",
                        ["mid"] = "Moderate risk: a human reviewer should inspect the change.",
                        ["high"] = "High risk: the change needs careful human review."
                    }
                }
            }
        };
        return JsonSerializer.Serialize(request);
    }

    public async Task<JevRiskAssessment> AssessAsync(
        RiskRubric rubric, RiskFactors factors, CancellationToken cancellationToken)
    {
        string body = BuildRequest(rubric, factors);
        JevRiskAssessment Unavailable(string reason) => new(
            "1.0", "unavailable", rubric.Version, rubric.JevModel, null, null, null, null, null, null, reason);

        using CancellationTokenSource timeout = CancellationTokenSource.CreateLinkedTokenSource(cancellationToken);
        timeout.CancelAfter(TimeSpan.FromSeconds(rubric.JevTimeoutSeconds));
        try
        {
            using HttpRequestMessage request = new(HttpMethod.Post, Endpoint);
            request.Headers.Authorization = new AuthenticationHeaderValue("Bearer", apiKey);
            request.Headers.UserAgent.ParseAdd("SqlScriptDom-AdversarialReview/0.1");
            request.Headers.Accept.ParseAdd("application/json");
            request.Content = new StringContent(body, Encoding.UTF8, "application/json");
            using HttpResponseMessage response = await http.SendAsync(
                request, HttpCompletionOption.ResponseHeadersRead, timeout.Token);
            // Response bodies are never logged; error details may echo request content.
            if (!response.IsSuccessStatusCode)
                return Unavailable($"JEV returned HTTP {(int)response.StatusCode}; no automatic retry.");
            byte[] content = await GitHubReviewApi.ReadBoundedAsync(response.Content, MaximumResponseBytes, timeout.Token);
            return Parse(content, rubric);
        }
        catch (OperationCanceledException) when (!cancellationToken.IsCancellationRequested)
        {
            return Unavailable("JEV request timed out; no automatic retry.");
        }
        catch (HttpRequestException)
        {
            return Unavailable("JEV could not be reached; no automatic retry.");
        }
        catch (ContractException)
        {
            return Unavailable("JEV response exceeded its size limit.");
        }
    }

    internal static JevRiskAssessment Parse(byte[] content, RiskRubric rubric)
    {
        JevRiskAssessment Invalid(string reason) => new(
            "1.0", "invalid", rubric.Version, rubric.JevModel, null, null, null, null, null, null, reason);
        try
        {
            using JsonDocument document = JsonDocument.Parse(content, new JsonDocumentOptions { MaxDepth = 16 });
            JsonElement root = document.RootElement;
            if (root.ValueKind != JsonValueKind.Object
                || !root.TryGetProperty("model", out JsonElement model) || model.ValueKind != JsonValueKind.String
                || string.IsNullOrWhiteSpace(model.GetString()) || model.GetString()!.Length > 64
                || !root.TryGetProperty("answers", out JsonElement answers) || answers.ValueKind != JsonValueKind.Object
                || !answers.TryGetProperty(QuestionName, out JsonElement answer) || answer.ValueKind != JsonValueKind.Object
                || !root.TryGetProperty("usage", out JsonElement usage) || usage.ValueKind != JsonValueKind.Object)
                return Invalid("JEV response is missing its model, answer, or usage.");
            if (!answer.TryGetProperty("type", out JsonElement type) || type.ValueKind != JsonValueKind.String
                || type.GetString() != "choice"
                || !answer.TryGetProperty("choice", out JsonElement choice) || choice.ValueKind != JsonValueKind.String
                || !Levels.Contains(choice.GetString(), StringComparer.Ordinal)
                || !answer.TryGetProperty("confidence", out JsonElement confidence)
                || confidence.ValueKind != JsonValueKind.Number
                || !answer.TryGetProperty("probabilities", out JsonElement probabilities)
                || probabilities.ValueKind != JsonValueKind.Object)
                return Invalid("JEV returned an unsupported risk classification.");
            decimal confidenceValue = confidence.GetDecimal();
            if (confidenceValue is < 0 or > 1)
                return Invalid("JEV confidence is outside 0-1.");
            Dictionary<string, decimal> levels = new(StringComparer.Ordinal);
            foreach (JsonProperty level in probabilities.EnumerateObject())
            {
                if (!Levels.Contains(level.Name, StringComparer.Ordinal) || level.Value.ValueKind != JsonValueKind.Number)
                    return Invalid("JEV probabilities do not match the requested levels.");
                decimal probability = level.Value.GetDecimal();
                if (probability is < 0 or > 1 || !levels.TryAdd(level.Name, probability))
                    return Invalid("JEV probabilities do not match the requested levels.");
            }
            if (levels.Count != Levels.Length)
                return Invalid("JEV probabilities do not match the requested levels.");
            if (!usage.TryGetProperty("input_tokens", out JsonElement input) || input.ValueKind != JsonValueKind.Number
                || !input.TryGetInt32(out int inputTokens)
                || !usage.TryGetProperty("output_tokens", out JsonElement output) || output.ValueKind != JsonValueKind.Number
                || !output.TryGetInt32(out int outputTokens)
                || inputTokens < 0 || outputTokens < 0)
                return Invalid("JEV usage is missing or invalid.");
            return new("1.0", "assessed", rubric.Version, rubric.JevModel, model.GetString(), choice.GetString(),
                confidenceValue, levels, inputTokens, outputTokens, null);
        }
        catch (Exception error) when (error is JsonException or FormatException or OverflowException or InvalidOperationException)
        {
            return Invalid("JEV returned malformed JSON; payload is not logged.");
        }
    }

    // Labeling stays disabled until calibration is approved; this only states eligibility.
    public static RiskDecision Decide(RiskFactors factors, JevRiskAssessment assessment)
    {
        ArgumentNullException.ThrowIfNull(factors);
        ArgumentNullException.ThrowIfNull(assessment);
        List<string> reasons = [];
        if (assessment.Status != "assessed")
            reasons.Add($"JEV assessment {assessment.Status}: {assessment.Reason}");
        else if (assessment.Classification != "low")
            reasons.Add($"JEV classified the overall risk as {assessment.Classification}.");
        if (assessment.RubricVersion != factors.RubricVersion)
            reasons.Add("JEV assessment used a different rubric version.");
        if (factors.HasUnresolvedHighOrCriticalFinding)
            reasons.Add("An unresolved high or critical adversarial finding blocks merge readiness.");
        return new(reasons.Count == 0, reasons.ToArray());
    }
}

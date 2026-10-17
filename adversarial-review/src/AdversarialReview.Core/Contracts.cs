using System.Text.Json;
using System.Text.Json.Nodes;
using System.Text.Json.Serialization;
using Json.Schema;

namespace AdversarialReview;

public sealed class ContractException(string message) : Exception(message);

public static class Contracts
{
    public const string Version = "1.0";
    public const int MaximumDocumentBytes = 2 * 1024 * 1024;

    public static JsonSerializerOptions JsonOptions { get; } = new()
    {
        PropertyNamingPolicy = JsonNamingPolicy.CamelCase,
        UnmappedMemberHandling = JsonUnmappedMemberHandling.Disallow,
        WriteIndented = true,
        MaxDepth = 32
    };

    public static T Read<T>(string json, string schemaName)
    {
        Validate(json, schemaName);
        return JsonSerializer.Deserialize<T>(json, JsonOptions)
            ?? throw new ContractException("The JSON document cannot be null.");
    }

    public static string Write<T>(T value, string schemaName)
    {
        string json = JsonSerializer.Serialize(value, JsonOptions);
        Validate(json, schemaName);
        return json;
    }

    public static void Validate(string json, string schemaName)
    {
        if (System.Text.Encoding.UTF8.GetByteCount(json) > MaximumDocumentBytes)
            throw new ContractException("JSON exceeds the document size limit.");

        using JsonDocument document = ParseDocument(json);
        RejectDuplicateProperties(document.RootElement);
        EvaluationOptions options = new() { OutputFormat = OutputFormat.List };
        options.SchemaRegistry.Register(LoadSchema("contracts"));
        JsonSchema schema = LoadSchema(schemaName);
        EvaluationResults result = schema.Evaluate(JsonNode.Parse(json), options);
        if (!result.IsValid)
            throw new ContractException($"Document does not satisfy {schemaName} schema version {Version}.");
    }

    private static JsonDocument ParseDocument(string json)
    {
        try
        {
            return JsonDocument.Parse(json, new JsonDocumentOptions { MaxDepth = 32 });
        }
        catch (JsonException)
        {
            throw new ContractException("Malformed JSON or document depth exceeds the limit.");
        }
    }

    private static JsonSchema LoadSchema(string name)
    {
        if (name is not ("contracts" or "finding" or "check-result" or "review-run" or "audit-report" or "configuration"))
            throw new ArgumentException("Unknown schema name.", nameof(name));
        var assembly = typeof(Contracts).Assembly;
        string resourceName = assembly.GetManifestResourceNames()
            .Single(n => n.EndsWith($".{name}.schema.json", StringComparison.Ordinal));
        using Stream stream = assembly.GetManifestResourceStream(resourceName)
            ?? throw new InvalidOperationException("Embedded schema is unavailable.");
        using StreamReader reader = new(stream);
        return JsonSchema.FromText(reader.ReadToEnd());
    }

    private static void RejectDuplicateProperties(JsonElement element)
    {
        if (element.ValueKind == JsonValueKind.Object)
        {
            HashSet<string> names = new(StringComparer.Ordinal);
            foreach (JsonProperty property in element.EnumerateObject())
            {
                if (!names.Add(property.Name))
                    throw new ContractException("Duplicate JSON properties are not permitted.");
                RejectDuplicateProperties(property.Value);
            }
        }
        else if (element.ValueKind == JsonValueKind.Array)
        {
            foreach (JsonElement item in element.EnumerateArray())
                RejectDuplicateProperties(item);
        }
    }
}

public sealed record Evidence(string Kind, string Reference, string Detail, int? StartLine, int? EndLine);

public sealed record Finding(
    string SchemaVersion, string Id, string HeadSha, string Source, string Category,
    string Severity, decimal Confidence, string Title, string Description,
    Evidence[] Evidence, string Recommendation, string Disposition);

public sealed record CheckResult(
    string SchemaVersion, string HeadSha, string Name, string Platform, string Conclusion, string Artifact);

public sealed record ReviewRun(
    string SchemaVersion, string Repository, int PullRequest, string HeadSha,
    string BaseSha, string ConfigurationSha, long WorkflowRunId, int RunAttempt,
    string AuthorizationId, string AuthorizedActor);

public sealed record ReviewRound(
    int Number, string Provider, string Model, string SelectionReason, string PromptVersion,
    int? InputTokens, int? OutputTokens, long LatencyMilliseconds, decimal? EstimatedCostUsd, string Outcome);

public sealed record AuditReport(
    string SchemaVersion, string Mode, string Calibration, ReviewRun Run, CheckResult[] Checks,
    Finding[] Findings, ReviewRound[] Rounds, string Status, decimal? RiskScore,
    string[] Blockers, string[] Limitations);

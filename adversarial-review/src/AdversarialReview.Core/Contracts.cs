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
        ValidateCore(json, schemaName, allowLegacyCompleteHighRisk: false);
        return json;
    }

    public static void Validate(string json, string schemaName)
    {
        // Audit v1.0 readers must accept complete reports produced before risk escalation.
        ValidateCore(json, schemaName, allowLegacyCompleteHighRisk: true);
    }

    internal static string WriteForDisplay<T>(T value, string schemaName)
    {
        string json = JsonSerializer.Serialize(value, JsonOptions);
        ValidateCore(json, schemaName, allowLegacyCompleteHighRisk: true);
        return json;
    }

    private static void ValidateCore(string json, string schemaName, bool allowLegacyCompleteHighRisk)
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
        ValidateSemantics(document.RootElement, schemaName, allowLegacyCompleteHighRisk);
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

    private static void ValidateSemantics(JsonElement root, string schemaName, bool allowLegacyCompleteHighRisk)
    {
        if (schemaName == "finding")
            ValidateFinding(JsonSerializer.Deserialize<Finding>(root, JsonOptions)!);
        if (schemaName != "audit-report") return;

        AuditReport audit = JsonSerializer.Deserialize<AuditReport>(root, JsonOptions)!;
        if (audit.Findings.Select(f => f.Id).Distinct(StringComparer.Ordinal).Count() != audit.Findings.Length)
            throw new ContractException("Duplicate finding identifiers are not permitted.");
        foreach (Finding finding in audit.Findings)
        {
            ValidateFinding(finding);
            if (finding.HeadSha != audit.Run.HeadSha)
                throw new ContractException("Finding SHA does not match the audit run.");
        }
        if (audit.Checks.Any(c => c.HeadSha != audit.Run.HeadSha))
            throw new ContractException("Check SHA does not match the audit run.");
        if (audit.Rounds.Where((round, index) => round.Number != index + 1).Any()
            || audit.Rounds.Length > audit.ExpectedRounds)
            throw new ContractException("Audit rounds must be ordered, contiguous, and within the expected count.");
        bool incompleteRounds = audit.Rounds.Length != audit.ExpectedRounds
            || audit.Rounds.Any(r => r.Outcome != "completed");
        bool elevatedRisk = audit.Findings.Any(f => f.Severity is "critical" or "high"
            && f.Disposition is "unreviewed" or "confirmed");
        if (audit.Status == "complete" && (incompleteRounds || (!allowLegacyCompleteHighRisk && elevatedRisk)
            || CheckPolicy.Blockers(audit.Run.HeadSha, audit.Checks).Length != 0))
            throw new ContractException("A complete audit requires all expected rounds and mandatory checks.");
    }

    private static void ValidateFinding(Finding finding)
    {
        foreach (Evidence evidence in finding.Evidence)
        {
            if (evidence.Kind == "file")
            {
                if (!IsSafeRelativePath(evidence.Reference))
                    throw new ContractException("File evidence must use a safe repository-relative path.");
                if (evidence.StartLine is null || evidence.EndLine is null || evidence.StartLine > evidence.EndLine)
                    throw new ContractException("File evidence requires an ordered line range.");
            }
            else if (evidence.StartLine is not null || evidence.EndLine is not null)
                throw new ContractException("Only file evidence may contain source line numbers.");
        }
    }

    public static bool IsSafeRelativePath(string path) =>
        !string.IsNullOrWhiteSpace(path) && !path.StartsWith('/')
        && !path.Contains('\\') && !path.Contains(':') && !path.Any(char.IsControl)
        && path.Split('/').All(part => part.Length > 0 && part is not ("." or ".."));

    public static bool IsSha(string value) =>
        value.Length == 40 && value.All(c => c is >= '0' and <= '9' or >= 'a' and <= 'f');

    private static JsonSchema LoadSchema(string name)
    {
        if (name is not ("contracts" or "finding" or "check-result" or "review-run" or "audit-report" or "configuration" or "evidence-manifest" or "reviewer-output" or "review-context" or "preparation-failure"))
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
    Finding[] Findings, ReviewRound[] Rounds, int ExpectedRounds, string Status, decimal? RiskScore,
    string[] Blockers, string[] Limitations);

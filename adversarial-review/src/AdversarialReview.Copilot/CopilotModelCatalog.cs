using GitHub.Copilot;

namespace AdversarialReview.Copilot;

public sealed record CopilotModel(
    string Id, string Name, string? PolicyState, int? MaximumPromptTokens,
    int MaximumContextTokens);

public sealed record CopilotCatalog(
    string SchemaVersion, string Provider, DateTimeOffset DiscoveredAt,
    CopilotModel[] Models, string[] Limitations);

public static class CopilotModelCatalog
{
    public const string Provider = "github-copilot";

    public static CopilotCatalog Create(IEnumerable<ModelInfo> models, DateTimeOffset discoveredAt)
    {
        CopilotModel[] entries = models.Take(1001).Select(ToModel)
            .OrderBy(m => m.Id, StringComparer.Ordinal).ToArray();
        if (entries.Length == 0 || entries.Length > 1000
            || entries.Any(m => string.IsNullOrWhiteSpace(m.Id) || m.Id.Length > 200 || m.Id.Any(char.IsControl)
                || string.IsNullOrWhiteSpace(m.Name) || m.Name.Length > 256
                || m.MaximumPromptTokens is < 0 || m.MaximumContextTokens < 0
                || m.PolicyState is { Length: > 32 } || m.PolicyState?.Any(char.IsControl) is true)
            || entries.Select(m => m.Id).Distinct(StringComparer.Ordinal).Count() != entries.Length)
            throw new ContractException("Copilot returned an empty, duplicate, or invalid model catalog.");
        return new("1.0", Provider, discoveredAt, entries,
            ["Discovery lists metadata only; no inference session or prompt is created.",
             "Catalog presence is not proof of inference entitlement. Disabled or unknown policy states are not eligible.",
             "Zero or absent token limits are unknown, not unlimited. Auto aliases cannot be used for deterministic rotation.",
             "Pricing, quota, billing conversion, and approved spending limits must be verified separately."]);
    }

    private static CopilotModel ToModel(ModelInfo model)
    {
        if (model is null || model.Capabilities?.Limits is null)
            throw new ContractException("Copilot returned missing model capabilities.");
        return new(model.Id, model.Name, model.Policy?.State, model.Capabilities.Limits.MaxPromptTokens,
            model.Capabilities.Limits.MaxContextWindowTokens);
    }
}

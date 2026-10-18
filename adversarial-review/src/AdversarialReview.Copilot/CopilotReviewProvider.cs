using GitHub.Copilot;
using GitHub.Copilot.Rpc;

namespace AdversarialReview.Copilot;

public sealed record CopilotBillingConversion(decimal NanoUnitsPerCredit, decimal UsdPerCredit)
{
    public void Validate()
    {
        if (NanoUnitsPerCredit <= 0 || UsdPerCredit <= 0)
            throw new ContractException("Explicit verified Copilot billing conversion is required.");
    }
}

public sealed class CopilotReviewProvider : IReviewProvider
{
    private readonly ICopilotRuntime runtime;
    private readonly CopilotCatalog catalog;
    private readonly CopilotBillingConversion billing;
    private readonly string trustedPrompt;

    public CopilotReviewProvider(ICopilotRuntime runtime, CopilotCatalog catalog,
        CopilotBillingConversion billing, string trustedPrompt)
    {
        billing.Validate();
        if (catalog.Provider != CopilotModelCatalog.Provider || catalog.SchemaVersion != "1.0"
            || string.IsNullOrWhiteSpace(trustedPrompt)
            || System.Text.Encoding.UTF8.GetByteCount(trustedPrompt) > Contracts.MaximumDocumentBytes
            || catalog.Models.Length == 0
            || catalog.Models.Select(m => m.Id).Distinct(StringComparer.Ordinal).Count() != catalog.Models.Length)
            throw new ContractException("A current Copilot catalog and bounded trusted reviewer prompt are required.");
        this.runtime = runtime;
        this.catalog = catalog;
        this.billing = billing;
        this.trustedPrompt = trustedPrompt;
        ReportRenderer.RejectCredentialData(System.Text.Json.JsonSerializer.Serialize(trustedPrompt), []);
    }

    public bool IsAvailable(ModelEntry model) => model.Enabled
        && model.Provider == CopilotModelCatalog.Provider
        && model.Model != "auto"
        && catalog.Models.Any(m => m.Id == model.Model && m.PolicyState == "enabled"
            && m.MaximumContextTokens > 0);

    public async Task<ModelResponse> ReviewAsync(ModelRequest request, CancellationToken cancellationToken)
    {
        cancellationToken.ThrowIfCancellationRequested();
        try
        {
            return await ReviewCoreAsync(request, cancellationToken);
        }
        catch (OverflowException)
        {
            throw new ContractException("Copilot accounting exceeds representable limits; cost remains unconfirmed.");
        }
    }

    private async Task<ModelResponse> ReviewCoreAsync(ModelRequest request, CancellationToken cancellationToken)
    {
        if (!IsAvailable(request.Model))
            throw new ContractException("Requested Copilot model is absent or not explicitly policy-enabled.");
        CopilotModel model = catalog.Models.Single(m => m.Id == request.Model.Model);
        if (request.MaxInputTokens <= 0 || request.MaxOutputTokens <= 0 || request.MaximumCostUsd <= 0
            || request.MaxInputTokens > (model.MaximumPromptTokens ?? model.MaximumContextTokens)
            || (long)request.MaxInputTokens + request.MaxOutputTokens > model.MaximumContextTokens)
            throw new ContractException("Copilot request limits are invalid or exceed the discovered model limits.");
        Contracts.Validate(request.Context, "review-context");
        ReportRenderer.RejectCredentialData(request.Context, []);
        var session = CreateSessionConfig(request, trustedPrompt, billing);
        CopilotReply reply = await runtime.ReviewAsync(session, request.Context, cancellationToken);
        if (reply.Usage.Length == 0 || reply.Usage.Length > 1000)
            throw new ContractException("Copilot returned no complete bounded usage accounting; cost remains unknown.");
        long input = 0, output = 0;
        decimal nanoUnits = 0;
        foreach (AssistantUsageData usage in reply.Usage)
        {
            if (usage.Model != request.Model.Model || usage.InputTokens is null or < 0
                || usage.OutputTokens is null or < 0 || usage.AiCreditsStatus != AiCreditsStatus.Complete
                || usage.CopilotUsage is null || !double.IsFinite(usage.CopilotUsage.TotalNanoAiu)
                || usage.CopilotUsage.TotalNanoAiu < 0 || usage.ContentFilterTriggered is true
                || usage.FinishReason is not ("stop" or "end_turn"))
                throw new ContractException("Copilot accounting is incomplete, substituted, filtered, or truncated; cost remains unconfirmed.");
            input = checked(input + usage.InputTokens.Value);
            output = checked(output + usage.OutputTokens.Value);
            nanoUnits += checked((decimal)usage.CopilotUsage.TotalNanoAiu);
        }
        decimal cost = decimal.Ceiling(nanoUnits / billing.NanoUnitsPerCredit * billing.UsdPerCredit * 1_000_000m) / 1_000_000m;
        if (input > request.MaxInputTokens || output > request.MaxOutputTokens || cost > request.MaximumCostUsd
            || reply.LatencyMilliseconds < 0)
            throw new ContractException("Copilot usage exceeded the requested bounds; retain the spend reservation.");
        return new(reply.Content, checked((int)input), checked((int)output), reply.LatencyMilliseconds, cost);
    }

    // Capability overrides are experimental in the pinned SDK; keep their use localized.
#pragma warning disable GHCP001
    public static SessionConfig CreateSessionConfig(
        ModelRequest request, string trustedPrompt, CopilotBillingConversion billing)
    {
        billing.Validate();
        return new()
        {
            Model = request.Model.Model,
            AvailableTools = [],
            Tools = [],
            McpServers = new Dictionary<string, McpServerConfig>(),
            OnPermissionRequest = (_, _) => Task.FromResult(PermissionDecision.Reject("Report-only reviewer; no tools permitted.")),
            SystemMessage = new() { Mode = SystemMessageMode.Replace, Content = trustedPrompt },
            EnableConfigDiscovery = false,
            EnableOnDemandInstructionDiscovery = false,
            EnableFileHooks = false,
            EnableHostGitOperations = false,
            EnableSessionStore = false,
            EnableGitHubCacheStore = false,
            EnableSkills = false,
            EnableSessionTelemetry = false,
            EnableFileChangeTracking = false,
            SkipEmbeddingRetrieval = true,
            CustomAgents = [],
            InfiniteSessions = new() { Enabled = false },
            Streaming = false,
            ModelCapabilities = new()
            {
                Limits = new()
                {
                    MaxPromptTokens = request.MaxInputTokens,
                    MaxOutputTokens = request.MaxOutputTokens
                }
            },
            SessionLimits = new() { MaxAiCredits = (double)(request.MaximumCostUsd / billing.UsdPerCredit) }
        };
    }
#pragma warning restore GHCP001
}

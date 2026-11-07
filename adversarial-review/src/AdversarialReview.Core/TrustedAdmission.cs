using System.Globalization;
using System.Text.Json;

namespace AdversarialReview;

public sealed record AdmissionRequest(
    int PullRequest, string HeadSha, string Actor, long LabelEventId, long ValidationRunId,
    int ValidationAttempt, string ConfigurationSha);

public sealed record AdmittedReview(
    string Repository, long RepositoryId, int PullRequest, string HeadSha, string BaseSha,
    string ConfigurationSha, string Actor, string Label, long LabelEventId, DateTimeOffset AuthorizedAt,
    long ValidationRunId, int ValidationAttempt, long WorkflowId, long HeadRepositoryId);

public sealed class TrustedAdmission(GitHubReviewApi api)
{
    // Request head/actor must originate in a trusted pull_request_target:labeled snapshot,
    // never reconstructed from label presence or contributor-controlled files.
    public async Task<AdmittedReview> VerifyAsync(
        ReviewConfiguration configuration, AdmissionRequest request, CancellationToken cancellationToken)
    {
        ReviewConfiguration.Parse(Contracts.Write(configuration, "configuration"));
        if (request.PullRequest <= 0 || !Contracts.IsSha(request.HeadSha) || !Contracts.IsSha(request.ConfigurationSha)
            || request.LabelEventId <= 0 || request.ValidationRunId <= 0 || request.ValidationAttempt <= 0
            || !configuration.AuthorizedActors.Contains(request.Actor, StringComparer.OrdinalIgnoreCase))
            throw new ContractException("Admission identity or authorizing actor is invalid.");
        using JsonDocument repository = await api.GetAsync(api.Route + "/actions/workflows/pr-validation.yml", cancellationToken);
        JsonElement workflow = repository.RootElement;
        if (workflow.GetProperty("state").GetString() != "active"
            || workflow.GetProperty("path").GetString() != ".github/workflows/pr-validation.yml")
            throw new ContractException("The authoritative validation workflow is not active or has a different path.");
        long workflowId = workflow.GetProperty("id").GetInt64();
        using JsonDocument pr = await api.GetAsync($"{api.Route}/pulls/{request.PullRequest}", cancellationToken);
        JsonElement root = pr.RootElement;
        if (root.GetProperty("number").GetInt32() != request.PullRequest
            || root.GetProperty("state").GetString() != "open" || root.GetProperty("draft").GetBoolean()
            || root.GetProperty("head").GetProperty("sha").GetString() != request.HeadSha
            || !root.GetProperty("labels").EnumerateArray().Any(l => l.GetProperty("name").GetString() == configuration.AuthorizationLabel)
            || !string.Equals(root.GetProperty("base").GetProperty("repo").GetProperty("full_name").GetString(),
                api.Repository, StringComparison.OrdinalIgnoreCase))
            throw new ContractException("PR identity, current head, state, base repository, or opt-in label is invalid.");
        long repositoryId = root.GetProperty("base").GetProperty("repo").GetProperty("id").GetInt64();
        long headRepositoryId = root.GetProperty("head").GetProperty("repo").GetProperty("id").GetInt64();
        string baseSha = root.GetProperty("base").GetProperty("sha").GetString()!;
        if (repositoryId <= 0 || headRepositoryId <= 0 || !Contracts.IsSha(baseSha))
            throw new ContractException("GitHub PR repository identities or base SHA are invalid.");

        JsonElement[] events = await api.ListAsync($"{api.Route}/issues/{request.PullRequest}/events", null, cancellationToken);
        JsonElement[] labels = events.Where(e =>
            e.GetProperty("event").GetString() is "labeled" or "unlabeled"
            && e.GetProperty("label").GetProperty("name").GetString() == configuration.AuthorizationLabel)
            .OrderBy(e => e.GetProperty("id").GetInt64()).ToArray();
        if (labels.Length == 0) throw new ContractException("No authenticated authorization label event exists.");
        JsonElement label = labels[^1];
        if (label.GetProperty("id").GetInt64() != request.LabelEventId || label.GetProperty("event").GetString() != "labeled"
            || label.GetProperty("actor").GetProperty("type").GetString() != "User"
            || !string.Equals(label.GetProperty("actor").GetProperty("login").GetString(), request.Actor, StringComparison.OrdinalIgnoreCase))
            throw new ContractException("Authorization is removed, superseded, or belongs to another actor/event.");
        DateTimeOffset authorizedAt = label.GetProperty("created_at").GetDateTimeOffset();

        using JsonDocument run = await api.GetAsync(
            $"{api.Route}/actions/runs/{request.ValidationRunId}/attempts/{request.ValidationAttempt}", cancellationToken);
        JsonElement validation = run.RootElement;
        if (validation.GetProperty("id").GetInt64() != request.ValidationRunId
            || validation.GetProperty("run_attempt").GetInt32() != request.ValidationAttempt
            || validation.GetProperty("workflow_id").GetInt64() != workflowId
            || validation.GetProperty("repository").GetProperty("id").GetInt64() != repositoryId
            || validation.GetProperty("head_repository").GetProperty("id").GetInt64() != headRepositoryId
            || validation.GetProperty("event").GetString() != "pull_request"
            || validation.GetProperty("status").GetString() != "completed"
            || validation.GetProperty("conclusion").GetString() != "success"
            || validation.GetProperty("head_sha").GetString() != request.HeadSha
            || !validation.GetProperty("pull_requests").EnumerateArray().Any(p =>
                p.GetProperty("number").GetInt32() == request.PullRequest
                && p.GetProperty("head").GetProperty("sha").GetString() == request.HeadSha))
            throw new ContractException("Validation run is not successful authoritative evidence for this exact PR head and attempt.");
        string workflowFile = $"{api.Route}/contents/.github/workflows/pr-validation.yml";
        using JsonDocument trustedWorkflow = await api.GetAsync($"{workflowFile}?ref={request.ConfigurationSha}", cancellationToken);
        using JsonDocument evaluatedWorkflow = await api.GetAsync($"{workflowFile}?ref={request.HeadSha}", cancellationToken);
        string? trustedBlob = trustedWorkflow.RootElement.GetProperty("sha").GetString();
        if (!Contracts.IsSha(trustedBlob ?? "") || trustedBlob != evaluatedWorkflow.RootElement.GetProperty("sha").GetString()
            || trustedWorkflow.RootElement.GetProperty("type").GetString() != "file"
            || evaluatedWorkflow.RootElement.GetProperty("type").GetString() != "file")
            throw new ContractException("PR validation workflow differs from trusted policy; human handling is required.");
        return new(api.Repository, repositoryId, request.PullRequest, request.HeadSha, baseSha,
            request.ConfigurationSha, request.Actor, configuration.AuthorizationLabel, request.LabelEventId,
            authorizedAt, request.ValidationRunId, request.ValidationAttempt, workflowId, headRepositoryId);
    }

    public static string AuthorizationId(AdmittedReview admitted) =>
        $"github-label-{admitted.RepositoryId.ToString(CultureInfo.InvariantCulture)}-{admitted.LabelEventId.ToString(CultureInfo.InvariantCulture)}";
}

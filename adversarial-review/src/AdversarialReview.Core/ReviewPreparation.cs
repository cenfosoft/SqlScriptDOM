using System.Text.Json;

namespace AdversarialReview;

public sealed record LabelSnapshot(string Repository, int PullRequest, string HeadSha, string Actor,
    string Label, DateTimeOffset UpdatedAt)
{
    public static LabelSnapshot Parse(string eventJson, string eventName, string repository, string actor, ReviewConfiguration config)
    {
        if (System.Text.Encoding.UTF8.GetByteCount(eventJson) > Contracts.MaximumDocumentBytes)
            throw new ContractException("GitHub webhook snapshot exceeds its ceiling.");
        using JsonDocument document = JsonDocument.Parse(eventJson, new JsonDocumentOptions { MaxDepth = 32 });
        JsonElement root = document.RootElement;
        JsonElement pr = root.GetProperty("pull_request");
        if (eventName != "pull_request_target" || root.GetProperty("action").GetString() != "labeled"
            || root.GetProperty("repository").GetProperty("full_name").GetString() != repository
            || root.GetProperty("sender").GetProperty("login").GetString() != actor
            || root.GetProperty("sender").GetProperty("type").GetString() != "User"
            || !config.AuthorizedActors.Contains(actor, StringComparer.OrdinalIgnoreCase)
            || root.GetProperty("label").GetProperty("name").GetString() != config.AuthorizationLabel
            || pr.GetProperty("draft").GetBoolean() || pr.GetProperty("state").GetString() != "open"
            || pr.GetProperty("base").GetProperty("ref").GetString() != root.GetProperty("repository").GetProperty("default_branch").GetString()
            || pr.GetProperty("base").GetProperty("repo").GetProperty("full_name").GetString() != repository
            || root.GetProperty("number").GetInt32() != pr.GetProperty("number").GetInt32()
            || !Contracts.IsSha(pr.GetProperty("head").GetProperty("sha").GetString()!))
            throw new ContractException("Expected an authorized default-branch labeled webhook with matching trusted Actions metadata.");
        return new(repository, root.GetProperty("number").GetInt32(), pr.GetProperty("head").GetProperty("sha").GetString()!,
            actor, config.AuthorizationLabel, pr.GetProperty("updated_at").GetDateTimeOffset());
    }
}

// This entry point has no provider/ledger dependency and cannot invoke inference, even if config is enabled.
public sealed class ReviewPreparation(GitHubReviewApi api, GitHubReviewEvidence evidence)
{
    public async Task<AuditReport> PrepareAsync(
        ReviewConfiguration config, LabelSnapshot snapshot, string configurationSha,
        long reportingRunId, int reportingAttempt, CancellationToken cancellationToken)
    {
        ReviewConfiguration.Parse(Contracts.Write(config, "configuration"));
        if (snapshot.Repository != api.Repository || snapshot.PullRequest <= 0 || !Contracts.IsSha(snapshot.HeadSha)
            || !Contracts.IsSha(configurationSha) || reportingRunId <= 0 || reportingAttempt <= 0
            || snapshot.Label != config.AuthorizationLabel
            || !config.AuthorizedActors.Contains(snapshot.Actor, StringComparer.OrdinalIgnoreCase))
            throw new ContractException("Webhook snapshot is outside trusted preparation scope.");
        JsonElement[] events = await api.ListAsync($"{api.Route}/issues/{snapshot.PullRequest}/events", null, cancellationToken);
        JsonElement[] matches = events.Where(e => e.GetProperty("event").GetString() == "labeled"
            && e.GetProperty("label").GetProperty("name").GetString() == snapshot.Label
            && string.Equals(e.GetProperty("actor").GetProperty("login").GetString(), snapshot.Actor, StringComparison.OrdinalIgnoreCase)
            && e.GetProperty("created_at").GetDateTimeOffset() == snapshot.UpdatedAt).ToArray();
        if (matches.Length != 1)
            throw new ContractException("Cannot uniquely correlate webhook update time to a label event; reapply after CI completes.");
        JsonElement[] runs = await api.ListAsync(
            $"{api.Route}/actions/workflows/pr-validation.yml/runs?event=pull_request&head_sha={snapshot.HeadSha}", "workflow_runs", cancellationToken);
        JsonElement[] candidates = runs.Where(r => r.GetProperty("head_sha").GetString() == snapshot.HeadSha)
            .OrderByDescending(r => r.GetProperty("id").GetInt64()).ToArray();
        if (candidates.Length == 0)
            throw new ContractException("No authoritative validation run exists for this head; reapply the label after CI completes.");
        JsonElement selected = candidates[0];
        var request = new AdmissionRequest(snapshot.PullRequest, snapshot.HeadSha, snapshot.Actor,
            matches[0].GetProperty("id").GetInt64(), selected.GetProperty("id").GetInt64(),
            selected.GetProperty("run_attempt").GetInt32(), configurationSha);
        AdmittedReview admitted = await new TrustedAdmission(api).VerifyAsync(config, request, cancellationToken);
        IngestedEvidence collected = await evidence.CollectAsync(admitted, cancellationToken);
        // Check the head again after bounded downloads; never report stale evidence as current.
        PullRequestState current = await new GitHubReviewContext(api).ReadStateAsync(snapshot.PullRequest, cancellationToken);
        ReviewRun run = TrustedReviewHost.CreateRun(admitted, reportingRunId, reportingAttempt);
        AuditReport report = AuditEngine.Create(run, current.HeadSha, collected.Checks, [], [], config.Rounds ?? 1);
        report = report with
        {
            Status = current.HeadSha == admitted.HeadSha ? "incomplete" : "stale",
            Blockers = [.. report.Blockers, .. config.ExecutionBlockers(), .. collected.Blockers,
                "Preparation only: no inference, environment approval, or spend reservation was performed."],
            Limitations = [.. report.Limitations,
                "This report is not a reusable paid-review authorization. Label correlation is preparation-only.",
                "A trusted persistent executor must verify authenticated admission and enforce approved budgets before activation."]
        };
        if (!current.IsOpen || current.IsDraft)
            report = report with { Blockers = [.. report.Blockers, "PR is now closed or draft."] };
        Contracts.Write(report, "audit-report");
        return report;
    }
}

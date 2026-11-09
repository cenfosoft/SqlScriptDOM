namespace AdversarialReview;

public sealed record ModelRequest(
    ModelEntry Model, string Context, int MaxInputTokens, int MaxOutputTokens,
    string RequestIdentity, decimal MaximumCostUsd);

public sealed record ModelResponse(
    string FindingsJson, int InputTokens, int OutputTokens,
    long LatencyMilliseconds, decimal CostUsd);

public interface IReviewProvider
{
    bool IsAvailable(ModelEntry model);
    Task<ModelResponse> ReviewAsync(ModelRequest request, CancellationToken cancellationToken);
}

public sealed record CycleClaim(
    string Repository, int PullRequest, string HeadSha, string AuthorizationId,
    decimal MaximumCostUsd, decimal MonthlyLimitUsd, int MaximumRounds,
    string AuthorizedActor, string AuthorizationLabel);

public interface IReviewSpendLedger
{
    // Implementations must durably reserve the whole cycle atomically across all PRs.
    Task ClaimAsync(CycleClaim claim, CancellationToken cancellationToken);

    // Revalidate the reservation before each request and return its absolute expiry.
    Task<DateTimeOffset> EnsureActiveAsync(CycleClaim claim, CancellationToken cancellationToken);

    // An unsuccessful/unknown request retains its full reservation for reconciliation.
    Task RecordAsync(CycleClaim claim, ReviewRound round, CancellationToken cancellationToken);

    // Final status is persisted with the authorization and SHA for auditability.
    Task RecordFinalStatusAsync(CycleClaim claim, string status, CancellationToken cancellationToken);
}

public sealed record CycleResult(AuditReport Report, string[] Diagnostics);

public sealed class ReviewCycle(IReviewProvider provider, IReviewSpendLedger ledger, TimeProvider? timeProvider = null)
{
    public async Task<CycleResult> RunAsync(
        ReviewConfiguration configuration, ReviewRun run, AuthorizationContext authorization,
        ReviewContext context, CheckResult[] checks, ReviewRound[] priorHistory,
        Func<CancellationToken, Task<PullRequestState>> readCurrentState,
        int maximumContextBytes, IEnumerable<string> knownSecrets, CancellationToken cancellationToken)
    {
        // Validate direct callers as strictly as JSON configuration callers.
        configuration = ReviewConfiguration.Parse(Contracts.Write(configuration, "configuration"));
        Contracts.Write(run, "review-run");
        if (run.HeadSha != context.HeadSha || run.ConfigurationSha != context.ConfigurationSha
            || run.HeadSha != authorization.HeadSha || run.AuthorizationId != authorization.AuthorizationId
            || !string.Equals(run.AuthorizedActor, authorization.Actor, StringComparison.OrdinalIgnoreCase))
            throw new ContractException("Run, authorization, and context identities do not match.");
        string[] secrets = knownSecrets.ToArray();
        string boundedContext = ReviewContextBuilder.Build(context, maximumContextBytes, secrets);
        PullRequestState state = await readCurrentState(cancellationToken);
        GateResult gate = ReviewGate.Evaluate(configuration, authorization, state, checks);
        if (!gate.EligibleForReservation)
            throw new ContractException("Review preflight rejected execution: " + string.Join(" ", gate.Reasons));
        if (priorHistory.Length > 100)
            throw new ContractException("Prior model history exceeds its bound.");
        foreach (ReviewRound round in priorHistory)
            ValidateHistoryRound(round);
        // This checks history before consuming authorization, even if no provider call follows.
        ModelRotation.Select(configuration, run.HeadSha, context.HeadSha, priorHistory, provider.IsAvailable);
        foreach (ReviewRound round in priorHistory)
        {
            if (!configuration.Models.Any(m => m.Provider == round.Provider && m.Model == round.Model))
                throw new ContractException("Prior history contains an unknown configured model.");
        }

        int rounds = configuration.Rounds!.Value;
        var claim = new CycleClaim(run.Repository, run.PullRequest, run.HeadSha, run.AuthorizationId,
            configuration.Limits.PerAuthorizationUsd!.Value, configuration.Limits.MonthlyUsd!.Value, rounds,
            authorization.Actor, authorization.Label);
        await ledger.ClaimAsync(claim, cancellationToken);
        List<ReviewRound> completed = [];
        List<string> diagnostics = [];
        Dictionary<string, Finding> findings = new(StringComparer.Ordinal);
        decimal spent = 0;
        long inputTokensUsed = 0;
        long outputTokensUsed = 0;

        AuditReport report;
        try
        {
            for (int index = 0; index < rounds; index++)
            {
                state = await readCurrentState(cancellationToken);
                if (!state.IsOpen || state.IsDraft || state.HeadSha != run.HeadSha)
                {
                    diagnostics.Add("PR state changed; no further model calls were made.");
                    break;
                }
                DateTimeOffset expires = await ledger.EnsureActiveAsync(claim, cancellationToken);
                TimeSpan leaseRemaining = expires - (timeProvider ?? TimeProvider.System).GetUtcNow();
                if (leaseRemaining <= TimeSpan.Zero)
                {
                    diagnostics.Add("Spend reservation expired; no further provider calls were made.");
                    break;
                }
                ModelSelection selection = ModelRotation.Select(configuration, run.HeadSha, run.HeadSha,
                    Renumber([.. priorHistory, .. completed]), provider.IsAvailable);
                decimal remaining = claim.MaximumCostUsd - spent;
                if (remaining <= 0)
                {
                    diagnostics.Add("Per-authorization budget exhausted; no further model calls were made.");
                    break;
                }
                int remainingInputTokens = checked((int)(configuration.Limits.MaxInputTokens!.Value - inputTokensUsed));
                int remainingOutputTokens = checked((int)(configuration.Limits.MaxOutputTokens!.Value - outputTokensUsed));
                if (remainingInputTokens <= 0 || remainingOutputTokens <= 0)
                {
                    diagnostics.Add("Per-PR token budget exhausted; no further model calls were made.");
                    break;
                }
                string requestId = $"{run.AuthorizationId}:{index + 1}";
                ModelRequest request = new(selection.Entry, boundedContext, remainingInputTokens,
                    remainingOutputTokens, requestId, remaining);
                ReviewRound result;
                Finding[] accepted = [];
                using CancellationTokenSource timeout = CancellationTokenSource.CreateLinkedTokenSource(cancellationToken);
                TimeSpan configuredTimeout = TimeSpan.FromSeconds(configuration.Limits.TimeoutSeconds!.Value);
                timeout.CancelAfter(leaseRemaining < configuredTimeout ? leaseRemaining : configuredTimeout);
                try
                {
                    ModelResponse response = await provider.ReviewAsync(request, timeout.Token).WaitAsync(timeout.Token);
                    if (response.InputTokens < 0 || response.OutputTokens < 0
                        || response.InputTokens > request.MaxInputTokens || response.OutputTokens > request.MaxOutputTokens
                        || response.LatencyMilliseconds < 0 || response.CostUsd < 0 || response.CostUsd > remaining)
                        throw new ContractException("Provider usage is invalid or exceeds the reserved limits.");
                    inputTokensUsed = checked(inputTokensUsed + response.InputTokens);
                    outputTokensUsed = checked(outputTokensUsed + response.OutputTokens);
                    result = Round(index, selection, response, "completed");
                    try
                    {
                        accepted = ReviewerOutput.Parse(response.FindingsJson, context, secrets);
                        if (accepted.Any(f => findings.TryGetValue(f.Id, out Finding? previous)
                            && Contracts.Write(previous, "finding") != Contracts.Write(f, "finding")))
                            throw new ContractException("Conflicting findings reuse an identifier.");
                    }
                    catch (ContractException)
                    {
                        result = result with { Outcome = "malformed" };
                        accepted = [];
                        diagnostics.Add("Model output was rejected; response content is not logged.");
                    }
                }
                catch (Exception error) when (error is HttpRequestException or OperationCanceledException or ContractException)
                {
                    result = new(index + 1, selection.Entry.Provider, selection.Entry.Model, selection.Reason,
                        "1.0", null, null, 0, null, cancellationToken.IsCancellationRequested ? "cancelled" : "failed");
                    diagnostics.Add("Provider request failed or its usage was invalid; billing may be unknown. No automatic retry.");
                }
                // Persist even when the request was cancelled: losing this record risks replay/spend.
                using CancellationTokenSource settlement = new(TimeSpan.FromSeconds(30));
                await ledger.RecordAsync(claim, result, settlement.Token);
                completed.Add(result);
                if (result.EstimatedCostUsd is decimal cost) spent += cost;
                foreach (Finding finding in accepted)
                {
                    findings.TryAdd(finding.Id, finding);
                }
                if (accepted.Any(RequiresHumanReview))
                {
                    diagnostics.Add("An unresolved critical/high finding requires human review; no further model calls were made.");
                    break;
                }
                if (result.Outcome != "completed") break;
            }

            using CancellationTokenSource finalRead = new(TimeSpan.FromSeconds(30));
            state = await readCurrentState(finalRead.Token);
            report = AuditEngine.Create(run, state.HeadSha, checks,
                findings.Values.ToArray(), completed.ToArray(), rounds);
            if ((!state.IsOpen || state.IsDraft) && report.Status != "stale")
                report = report with { Status = "incomplete", Blockers = [.. report.Blockers, "PR is now closed or draft."] };
        }
        catch (Exception error) when (error is ContractException or HttpRequestException or OperationCanceledException)
        {
            using CancellationTokenSource recovery = new(TimeSpan.FromSeconds(30));
            await ledger.RecordFinalStatusAsync(claim, "human-review-required", recovery.Token);
            throw;
        }

        string finalStatus = AuditEngine.RequiresHumanReview(report) ? "human-review-required" : report.Status;
        using CancellationTokenSource finalStatusToken = new(TimeSpan.FromSeconds(30));
        await ledger.RecordFinalStatusAsync(claim, finalStatus, finalStatusToken.Token);
        return new(report with { Limitations = [.. report.Limitations, .. diagnostics] }, diagnostics.ToArray());
    }

    private static bool RequiresHumanReview(Finding finding) =>
        finding.Severity is "critical" or "high"
        && finding.Disposition is "unreviewed" or "confirmed";

    private static ReviewRound[] Renumber(ReviewRound[] history) =>
        history.Select((round, index) => round with { Number = index + 1 }).ToArray();

    private static void ValidateHistoryRound(ReviewRound round)
    {
        if (round.Number <= 0 || round.LatencyMilliseconds < 0 || round.InputTokens < 0
            || round.OutputTokens < 0 || round.EstimatedCostUsd < 0
            || round.Outcome is not ("completed" or "failed" or "malformed" or "cancelled")
            || string.IsNullOrWhiteSpace(round.PromptVersion) || string.IsNullOrWhiteSpace(round.SelectionReason))
            throw new ContractException("Prior model history contains invalid usage or outcome metadata.");
    }

    private static ReviewRound Round(int index, ModelSelection selection, ModelResponse response, string outcome) =>
        new(index + 1, selection.Entry.Provider, selection.Entry.Model, selection.Reason, "1.0",
            response.InputTokens, response.OutputTokens, response.LatencyMilliseconds, response.CostUsd, outcome);
}

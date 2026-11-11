namespace AdversarialReview;

public sealed record AuthorizationContext(
    string Actor, string Label, string HeadSha, string AuthorizationId,
    bool EnvironmentApproved, bool AlreadyConsumed);

public sealed record PullRequestState(string HeadSha, bool IsOpen, bool IsDraft);

public sealed record GateResult(bool EligibleForReservation, string[] Reasons);

public static class ReviewGate
{
    // This pure preflight does not authenticate GitHub events or reserve spending.
    public static GateResult Evaluate(
        ReviewConfiguration configuration, AuthorizationContext authorization,
        PullRequestState pullRequest, IReadOnlyList<CheckResult> checks)
    {
        List<string> reasons = [.. configuration.ExecutionBlockers()];
        if (!configuration.AuthorizedActors.Contains(authorization.Actor, StringComparer.OrdinalIgnoreCase))
            reasons.Add("Event actor is not authorized.");
        if (authorization.Label != configuration.AuthorizationLabel)
            reasons.Add("Authorization label does not match.");
        if (string.IsNullOrWhiteSpace(authorization.AuthorizationId))
            reasons.Add("Authorization event identity is missing.");
        if (!authorization.EnvironmentApproved) reasons.Add("Environment approval is missing.");
        if (authorization.AlreadyConsumed) reasons.Add("Authorization is already consumed.");
        if (!pullRequest.IsOpen || pullRequest.IsDraft) reasons.Add("PR is closed or draft.");
        if (authorization.HeadSha != pullRequest.HeadSha) reasons.Add("Authorization is stale.");
        reasons.AddRange(CheckPolicy.Blockers(pullRequest.HeadSha, checks));
        return new(reasons.Count == 0, reasons.ToArray());
    }
}

public static class CheckPolicy
{
    public static readonly (string Name, string Platform)[] Required =
    [
        ("build", "windows"), ("build", "ubuntu"),
        ("test", "windows"), ("test", "ubuntu"),
        ("semgrep", "independent"), ("roslyn", "independent")
    ];

    public static string[] Blockers(string headSha, IReadOnlyList<CheckResult> checks)
    {
        List<string> errors = [];
        foreach (CheckResult check in checks)
            Contracts.Write(check, "check-result");
        if (checks.Any(c => c.HeadSha != headSha))
            errors.Add("Check evidence contains a different head SHA.");
        foreach ((string name, string platform) in Required)
        {
            CheckResult[] matches = checks.Where(c => c.Name == name && c.Platform == platform).ToArray();
            if (matches.Length != 1 || matches[0].Conclusion != "passed")
                errors.Add($"Required {name}/{platform} evidence is missing, duplicated, or unsuccessful.");
        }
        return errors.ToArray();
    }
}

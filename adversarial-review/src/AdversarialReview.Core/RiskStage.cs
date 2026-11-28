namespace AdversarialReview;

public static class RiskStage
{
    public const string LowRiskLabel = "adversarial-review:low-risk";
    public const string HumanReviewLabel = "adversarial-review:human-review";

    // assessor is null when no JEV credential is configured; the result then requires human review.
    public static async Task<RiskSection> EvaluateAsync(
        AuditReport review, IReadOnlyList<PullRequestFile> files, IReadOnlyDictionary<string, byte[]> evidenceFiles,
        RiskRubric rubric, JevRiskAssessor? assessor, CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(review);
        ArgumentNullException.ThrowIfNull(rubric);
        if (review.Status == "stale")
            return Skipped(rubric, ["The evaluated head SHA is stale; no risk assessment was requested."]);
        RiskSignalResult collected = RiskSignalCollector.Collect(files, review, evidenceFiles);
        if (collected.Signals is null)
            return Skipped(rubric, collected.Missing) with { Missing = collected.Missing };
        RiskFactors factors = rubric.Classify(collected.Signals);
        if (assessor is null)
            return new(rubric.Version, collected.Signals, factors, null,
                new(false, ["No JEV credential is configured; the risk assessment was not requested."]),
                [], HumanReviewLabel, "pending", "not-eligible");
        JevRiskAssessment assessment = await assessor.AssessAsync(rubric, factors, cancellationToken);
        RiskDecision decision = JevRiskAssessor.Decide(factors, assessment);
        if (review.Status != "complete")
            decision = new(false, [.. decision.Reasons, "The adversarial review audit is incomplete; see blockers."]);
        return new(rubric.Version, collected.Signals, factors, assessment, decision, [],
            decision.EligibleForLowRiskLabel ? LowRiskLabel : HumanReviewLabel, "pending",
            decision.EligibleForLowRiskLabel ? "pending" : "not-eligible");
    }

    private static RiskSection Skipped(RiskRubric rubric, string[] reasons) =>
        new(rubric.Version, null, null, null, new(false, reasons), [], HumanReviewLabel, "pending", "not-eligible");
}

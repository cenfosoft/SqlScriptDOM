using System.Text.Json;
using AdversarialReview.Core.Metrics;
using Microsoft.VisualStudio.TestTools.UnitTesting;

namespace AdversarialReview.Tests.Metrics;

[TestClass]
public sealed class BaselineMarkdownRendererTests
{
    private static readonly DateTimeOffset Observation = DateTimeOffset.Parse("2026-10-16T12:00:00Z");

    [TestMethod]
    public void RendersSourceProvenanceSeparateCohortsAndCollectionPolicy()
    {
        BaselineReport fork = CreateReport("cenfosoft/SqlScriptDOM");
        BaselineReport upstream = CreateReport("microsoft/SqlScriptDOM");
        var report = new BaselineCollectionReport(
            "1.0", Observation, 6, 100, "phase1-human-nondraft-merged-only",
            "First ready-for-review event",
            "merged_at; unmerged closures excluded",
            "UTC",
            ["Exclude bots, current drafts, and closed-unmerged PRs."],
            [fork, upstream]);

        string markdown = BaselineMarkdownRenderer.Render(report);
        string json = JsonSerializer.Serialize(report, new JsonSerializerOptions(JsonSerializerDefaults.Web));

        StringAssert.Contains(markdown, "[cenfosoft/SqlScriptDOM](https://github.com/cenfosoft/SqlScriptDOM)");
        StringAssert.Contains(markdown, "[microsoft/SqlScriptDOM#1](https://github.com/microsoft/SqlScriptDOM/pull/1)");
        StringAssert.Contains(markdown, "Merged PR durations");
        StringAssert.Contains(markdown, "Open PR censored ages");
        StringAssert.Contains(markdown, "Median merged lead time (hours)");
        StringAssert.Contains(markdown, "No improvement target or minimum sample threshold has been approved.");
        StringAssert.Contains(markdown, "INCOMPLETE");
        StringAssert.Contains(markdown, "ClosedUnmerged");
        StringAssert.Contains(json, "\"startEvent\"");
        StringAssert.Contains(json, "\"filterProfile\"");
        StringAssert.Contains(json, "\"microsoft/SqlScriptDOM\"");
        StringAssert.Contains(json, "\"openCensored\"");
        StringAssert.Contains(json, "\"https://github.com/microsoft/SqlScriptDOM/pull/1\"");
    }

    private static BaselineReport CreateReport(string repository)
    {
        PullRequestSnapshot merged = Pull(1) with
        {
            State = "closed",
            MergedAt = Observation.AddDays(-2),
            ClosedAt = Observation.AddDays(-2)
        };
        PullRequestSnapshot open = Pull(2) with { CreatedAt = Observation.AddDays(-1) };
        PullRequestSnapshot closedUnmerged = Pull(3) with
        {
            State = "closed",
            ClosedAt = Observation.AddDays(-3)
        };
        RepositorySnapshot snapshot = new(repository, Observation, [merged, open, closedUnmerged], false,
            [new BaselineDiagnostic("PullRequestsPageLimitReached")]);
        return BaselineCalculator.Calculate(snapshot);
    }

    private static PullRequestSnapshot Pull(int number) => new(
        number, "person", "User", Observation.AddDays(-10), Observation.AddDays(-1),
        "open", false, null, null, TimelineStatus.Complete, []);
}

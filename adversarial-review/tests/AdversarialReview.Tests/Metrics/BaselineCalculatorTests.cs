using System.Text.Json;
using AdversarialReview.Core.Metrics;
using Microsoft.VisualStudio.TestTools.UnitTesting;

namespace AdversarialReview.Tests.Metrics;

[TestClass]
public sealed class BaselineCalculatorTests
{
    private static readonly DateTimeOffset Observation = DateTimeOffset.Parse("2026-10-16T12:00:00Z");

    [TestMethod]
    public void KeepsMergedAndOpenCohortsSeparateAndComputesMergedMedian()
    {
        PullRequestSnapshot[] prs =
        [
            Pull(1) with { MergedAt = Observation.AddDays(-8), State = "closed", ClosedAt = Observation.AddDays(-8) },
            Pull(2) with { MergedAt = Observation.AddDays(-6), State = "closed", ClosedAt = Observation.AddDays(-6) },
            Pull(3) with { CreatedAt = Observation.AddDays(-1) }
        ];
        BaselineReport result = Calculate(prs);

        Assert.AreEqual(2, result.Merged.Count);
        Assert.AreEqual(72d, result.Merged.MedianDurationHours);
        Assert.AreEqual(1, result.OpenCensored.Count);
        Assert.AreEqual(24d, result.OpenCensored.PullRequests[0].DurationHours);
        Assert.IsTrue(result.IsComplete);
    }

    [TestMethod]
    public void EmptyAndOddMergedSamplesHaveCorrectMedian()
    {
        Assert.IsNull(Calculate([]).Merged.MedianDurationHours);
        BaselineReport report = Calculate(
        [
            Pull(1) with { State = "closed", MergedAt = Observation.AddDays(-9) },
            Pull(2) with { State = "closed", MergedAt = Observation.AddDays(-4) },
            Pull(3) with { State = "closed", MergedAt = Observation.AddDays(-8) }
        ]);
        Assert.AreEqual(48d, report.Merged.MedianDurationHours);
    }

    [TestMethod]
    public void UsesReadinessWindowRatherThanCreationAndIncludesBothBoundaries()
    {
        DateTimeOffset start = Observation.AddMonths(-6);
        BaselineReport report = Calculate(
        [
            Pull(1) with { CreatedAt = Observation.AddYears(-2), ReadinessEvents = [Ready(start.AddDays(2))] },
            Pull(2) with { CreatedAt = start },
            Pull(3) with { CreatedAt = start.AddTicks(-1) },
            Pull(4) with { CreatedAt = Observation, UpdatedAt = Observation }
        ]);

        CollectionAssert.AreEqual(new[] { 1, 2, 4 }, report.OpenCensored.PullRequests.Select(p => p.Number).ToArray());
        Assert.AreEqual(start, report.RangeStartInclusive);
        Assert.AreEqual(1, report.ExclusionCounts["OutsideReadinessWindow"]);
        Assert.AreEqual(0d, report.OpenCensored.PullRequests.Single(p => p.Number == 4).DurationHours);
    }

    [TestMethod]
    public void TracksFirstReadinessAndIncludesSubsequentDraftIntervals()
    {
        DateTimeOffset created = Observation.AddDays(-10);
        BaselineReport report = Calculate(
        [
            Pull(1) with
            {
                ReadinessEvents =
                [
                    Ready(created.AddDays(2)),
                    Draft(created.AddDays(3)),
                    Ready(created.AddDays(5))
                ]
            },
            Pull(2) with { ReadinessEvents = [Draft(created.AddDays(2)), Ready(created.AddDays(5))] }
        ]);

        Assert.AreEqual(created.AddDays(2), report.OpenCensored.PullRequests[0].ReadyAt);
        Assert.AreEqual(192d, report.OpenCensored.PullRequests[0].DurationHours);
        Assert.AreEqual(created, report.OpenCensored.PullRequests[1].ReadyAt);
    }

    [TestMethod]
    public void ExcludesBotsDraftsClosedAndUnknownAuthorsWithoutRequestingReadiness()
    {
        BaselineReport report = Calculate(
        [
            Pull(1) with { AuthorType = "Bot", TimelineStatus = TimelineStatus.NotRequested },
            Pull(2) with { AuthorLogin = "automation[BOT]", TimelineStatus = TimelineStatus.NotRequested },
            Pull(3) with { IsDraft = true, TimelineStatus = TimelineStatus.NotRequested },
            Pull(4) with { State = "closed", TimelineStatus = TimelineStatus.NotRequested },
            Pull(5) with { AuthorType = "Organization", TimelineStatus = TimelineStatus.NotRequested }
        ]);

        Assert.AreEqual(0, report.Merged.Count + report.OpenCensored.Count);
        Assert.AreEqual(2, report.ExclusionCounts["BotAuthor"]);
        Assert.AreEqual(1, report.ExclusionCounts["CurrentlyDraft"]);
        Assert.AreEqual(1, report.ExclusionCounts["ClosedUnmerged"]);
        Assert.AreEqual(1, report.ExclusionCounts["UnknownAuthor"]);
        Assert.IsFalse(report.IsComplete);
    }

    [TestMethod]
    public void NeverReadyDraftIsExcludedAndKnownExclusionsDoNotMakeDataIncomplete()
    {
        BaselineReport report = Calculate([Pull(1) with { IsDraft = true }]);
        Assert.AreEqual(1, report.ExclusionCounts["CurrentlyDraft"]);
        Assert.IsTrue(report.IsComplete);
    }

    [TestMethod]
    public void MissingReadinessIsNotInvented()
    {
        BaselineReport report = Calculate(
            [Pull(1) with { ReadinessEvents = [Draft(Observation.AddDays(-5))] }]);
        Assert.AreEqual(1, report.ExclusionCounts["MissingReadiness"]);
        Assert.AreEqual(0, report.OpenCensored.Count);
        Assert.IsFalse(report.IsComplete);
    }

    [DataTestMethod]
    [DataRow(TimelineStatus.Unavailable)]
    [DataRow(TimelineStatus.Malformed)]
    [DataRow(TimelineStatus.PageLimitReached)]
    [DataRow(TimelineStatus.NotRequested)]
    public void IncompleteTimelinesNeverFallBackToCreation(TimelineStatus status)
    {
        BaselineReport report = Calculate([Pull(1) with { TimelineStatus = status }]);
        Assert.AreEqual(0, report.OpenCensored.Count);
        Assert.AreEqual(1, report.ExclusionCounts[$"Timeline{status}"]);
        Assert.IsFalse(report.IsComplete);
    }

    [TestMethod]
    public void RejectsMalformedTransitionsAndReadinessAfterMerge()
    {
        BaselineReport report = Calculate(
        [
            Pull(1) with { ReadinessEvents = [Ready(Observation.AddDays(-8)), Ready(Observation.AddDays(-7))] },
            Pull(2) with { ReadinessEvents = [Ready(Observation.AddDays(-11))] },
            Pull(3) with
            {
                State = "closed", MergedAt = Observation.AddDays(-5),
                ReadinessEvents = [Ready(Observation.AddDays(-4))]
            }
        ]);
        Assert.AreEqual(1, report.ExclusionCounts["MalformedReadinessTransitions"]);
        Assert.AreEqual(2, report.ExclusionCounts["InvalidReadinessTimestamp"]);
        Assert.IsFalse(report.IsComplete);
    }

    [TestMethod]
    public void DoesNotInventHistoricalStateFromFutureMergeCloseOrUpdate()
    {
        BaselineReport report = Calculate(
        [
            Pull(1) with { State = "closed", MergedAt = Observation.AddDays(1) },
            Pull(2) with { State = "closed", ClosedAt = Observation.AddDays(1) },
            Pull(3) with { UpdatedAt = Observation.AddSeconds(1) },
            Pull(4) with { CreatedAt = Observation.AddSeconds(1) },
            Pull(5) with { ReadinessEvents = [Ready(Observation.AddSeconds(1))] }
        ]);
        Assert.AreEqual(4, report.ExclusionCounts["StateAfterObservation"]);
        Assert.AreEqual(1, report.ExclusionCounts["InvalidReadinessTimestamp"]);
        Assert.AreEqual(0, report.OpenCensored.Count);
        Assert.IsFalse(report.IsComplete);
    }

    [TestMethod]
    public void UsesCalendarMonthsAndElapsedUtcTime()
    {
        DateTimeOffset observed = DateTimeOffset.Parse("2026-08-31T12:00:00+02:00");
        PullRequestSnapshot pr = Pull(1) with
        {
            CreatedAt = DateTimeOffset.Parse("2026-08-30T12:00:00-04:00"),
            UpdatedAt = DateTimeOffset.Parse("2026-08-30T12:00:00-04:00")
        };
        BaselineReport report = BaselineCalculator.Calculate(new("o/r", observed, [pr], true, []), 6);
        Assert.AreEqual(DateTimeOffset.Parse("2026-02-28T12:00:00+02:00"), report.RangeStartInclusive);
        Assert.AreEqual(18d, report.OpenCensored.PullRequests[0].DurationHours);
    }

    [TestMethod]
    public void SavedSnapshotAndReportRoundTripThroughJson()
    {
        var snapshot = new RepositorySnapshot("owner/repo", Observation, [Pull(1)], true, []);
        string input = JsonSerializer.Serialize(snapshot);
        RepositorySnapshot restored = JsonSerializer.Deserialize<RepositorySnapshot>(input)!;
        BaselineReport report = BaselineCalculator.Calculate(restored);
        BaselineReport restoredReport = JsonSerializer.Deserialize<BaselineReport>(JsonSerializer.Serialize(report))!;

        Assert.AreEqual("1.0", restoredReport.SchemaVersion);
        Assert.AreEqual(report.ObservedAt, restoredReport.ObservedAt);
        Assert.AreEqual(240d, restoredReport.OpenCensored.PullRequests[0].DurationHours);
        Assert.AreEqual("current-state-snapshot", restoredReport.ObservationMode);
        Assert.IsTrue(restoredReport.Limitations.Count > 0);
    }

    [TestMethod]
    public void InvalidStatesAndDuplicatesAreDiagnosed()
    {
        BaselineReport report = Calculate(
        [
            Pull(1),
            Pull(1),
            Pull(2) with { MergedAt = Observation.AddDays(-1), State = "open" },
            Pull(3) with { State = "unknown" }
        ]);
        Assert.AreEqual(1, report.OpenCensored.Count);
        Assert.AreEqual(1, report.ExclusionCounts["DuplicatePullRequest"]);
        Assert.AreEqual(2, report.ExclusionCounts["InvalidPullRequestState"]);
        Assert.IsFalse(report.IsComplete);
    }

    [DataTestMethod]
    [DataRow(0)]
    [DataRow(-1)]
    [DataRow(1201)]
    public void RejectsInvalidMonthRanges(int months)
    {
        Assert.ThrowsException<ArgumentOutOfRangeException>(() =>
            BaselineCalculator.Calculate(new("o/r", Observation, [], true, []), months));
    }

    private static BaselineReport Calculate(PullRequestSnapshot[] prs) =>
        BaselineCalculator.Calculate(new("owner/repo", Observation, prs, true, []));

    private static PullRequestSnapshot Pull(int number) => new(number, "person", "User",
        Observation.AddDays(-10), Observation.AddDays(-1), "open", false, null, null, TimelineStatus.Complete, []);

    private static ReadinessEvent Ready(DateTimeOffset at) => new("ready_for_review", at);
    private static ReadinessEvent Draft(DateTimeOffset at) => new("convert_to_draft", at);
}

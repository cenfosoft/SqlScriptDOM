namespace AdversarialReview.Core.Metrics;

/// <summary>Pure calculation over a saved current-state snapshot; no clock or network access.</summary>
public static class BaselineCalculator
{
    public static BaselineReport Calculate(RepositorySnapshot snapshot, int months = 6)
    {
        ArgumentNullException.ThrowIfNull(snapshot);
        if (months < 1 || months > 1200)
        {
            throw new ArgumentOutOfRangeException(nameof(months), "Use between 1 and 1200 trailing calendar months.");
        }

        DateTimeOffset start = snapshot.ObservedAt.AddMonths(-months);
        var merged = new List<BaselinePullRequest>();
        var open = new List<BaselinePullRequest>();
        var exclusions = new SortedDictionary<string, int>(StringComparer.Ordinal);
        var diagnostics = snapshot.Diagnostics.ToList();
        bool complete = snapshot.IsComplete;
        var seen = new HashSet<int>();

        foreach (PullRequestSnapshot pr in snapshot.PullRequests)
        {
            string? exclusion = null;
            bool uncertain = false;
            DateTimeOffset? ready = null;
            if (!seen.Add(pr.Number))
            {
                exclusion = "DuplicatePullRequest";
                uncertain = true;
            }
            else if (pr.AuthorType.Equals("Bot", StringComparison.OrdinalIgnoreCase)
                || pr.AuthorLogin.EndsWith("[bot]", StringComparison.OrdinalIgnoreCase))
            {
                exclusion = "BotAuthor";
            }
            else if (!pr.AuthorType.Equals("User", StringComparison.OrdinalIgnoreCase)
                || string.IsNullOrWhiteSpace(pr.AuthorLogin))
            {
                exclusion = "UnknownAuthor";
                uncertain = true;
            }
            else if (pr.CreatedAt > snapshot.ObservedAt || pr.UpdatedAt > snapshot.ObservedAt
                || pr.MergedAt > snapshot.ObservedAt || pr.ClosedAt > snapshot.ObservedAt)
            {
                exclusion = "StateAfterObservation";
                uncertain = true;
            }
            else if (pr.IsDraft)
            {
                exclusion = "CurrentlyDraft";
            }
            else if (pr.State == "closed" && pr.MergedAt is null)
            {
                exclusion = "ClosedUnmerged";
            }
            else if ((pr.State != "open" && pr.State != "closed")
                || (pr.State == "open" && (pr.MergedAt is not null || pr.ClosedAt is not null))
                || pr.UpdatedAt < pr.CreatedAt
                || pr.MergedAt < pr.CreatedAt || pr.ClosedAt < pr.CreatedAt)
            {
                exclusion = "InvalidPullRequestState";
                uncertain = true;
            }
            else if (pr.TimelineStatus != TimelineStatus.Complete)
            {
                exclusion = $"Timeline{pr.TimelineStatus}";
                uncertain = true;
            }
            else
            {
                (ready, exclusion) = FindReadiness(pr, snapshot.ObservedAt);
                uncertain = exclusion is not null;
            }

            if (exclusion is null && ready < start)
            {
                exclusion = "OutsideReadinessWindow";
            }

            if (exclusion is not null)
            {
                exclusions[exclusion] = exclusions.GetValueOrDefault(exclusion) + 1;
                diagnostics.Add(new BaselineDiagnostic(exclusion, pr.Number));
                complete &= !uncertain;
                continue;
            }

            DateTimeOffset end = pr.MergedAt ?? snapshot.ObservedAt;
            var entry = new BaselinePullRequest(pr.Number, ready!.Value, end, (end - ready.Value).TotalHours);
            (pr.MergedAt is null ? open : merged).Add(entry);
        }

        double[] durations = merged.Select(p => p.DurationHours).Order().ToArray();
        double? median = durations.Length == 0 ? null
            : durations.Length % 2 == 1 ? durations[durations.Length / 2]
            : (durations[durations.Length / 2 - 1] + durations[durations.Length / 2]) / 2;

        return new BaselineReport(
            "1.0", snapshot.Repository, snapshot.ObservedAt, start, snapshot.ObservedAt, months, complete,
            "current-state-snapshot",
            [
                "Current-state API data cannot reconstruct historical draft, closed or reopened states; do not relabel a snapshot with a different observation date.",
                "Collection is not atomic. PRs updated after observation are excluded and make the report incomplete.",
                "Readiness is the first ready-for-review instant, or creation for a PR initially non-draft. Elapsed time includes any later draft intervals.",
                "A complete GitHub timeline with no readiness transitions is treated as a PR created non-draft. Unavailable, malformed or incomplete timelines are never inferred.",
                "Open ages are right-censored and are not included in the merged-only median."
            ],
            new MergedBaselineCohort(merged.Count, median, merged.OrderBy(p => p.Number).ToArray()),
            new OpenBaselineCohort(open.Count, open.OrderBy(p => p.Number).ToArray()),
            exclusions, diagnostics);
    }

    private static (DateTimeOffset? ReadyAt, string? Exclusion) FindReadiness(
        PullRequestSnapshot pr, DateTimeOffset observedAt)
    {
        ReadinessEvent[] events = pr.ReadinessEvents.OrderBy(e => e.OccurredAt).ToArray();
        // The first transition establishes the initial draft state without inventing a ready date.
        bool draft = events.FirstOrDefault()?.Kind == "ready_for_review";
        DateTimeOffset? ready = draft ? null : pr.CreatedAt;
        DateTimeOffset end = pr.MergedAt ?? observedAt;
        foreach (ReadinessEvent item in events)
        {
            if (item.OccurredAt < pr.CreatedAt || item.OccurredAt > end)
            {
                return (null, "InvalidReadinessTimestamp");
            }

            if (item.Kind == "ready_for_review" && draft)
            {
                ready ??= item.OccurredAt;
                draft = false;
            }
            else if (item.Kind == "convert_to_draft" && !draft)
            {
                draft = true;
            }
            else
            {
                return (null, "MalformedReadinessTransitions");
            }
        }

        return draft || ready is null ? (null, "MissingReadiness") : (ready, null);
    }
}

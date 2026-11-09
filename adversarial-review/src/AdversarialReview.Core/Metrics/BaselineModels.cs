namespace AdversarialReview.Core.Metrics;

public sealed record ReadinessEvent(string Kind, DateTimeOffset OccurredAt);

public enum TimelineStatus
{
    Complete,
    Unavailable,
    RateLimited,
    Malformed,
    PageLimitReached,
    NotRequested
}

public sealed record PullRequestSnapshot(
    int Number,
    string AuthorLogin,
    string AuthorType,
    DateTimeOffset CreatedAt,
    DateTimeOffset UpdatedAt,
    string State,
    bool IsDraft,
    DateTimeOffset? MergedAt,
    DateTimeOffset? ClosedAt,
    TimelineStatus TimelineStatus,
    IReadOnlyList<ReadinessEvent> ReadinessEvents);

/// <summary>Diagnostics contain codes and PR numbers only, never API response bodies or credentials.</summary>
public sealed record BaselineDiagnostic(string Code, int? PullRequestNumber = null);

/// <summary>
/// Current-state API snapshot, not a reconstruction of historical open/closed or draft state.
/// ObservedAt must be the time collection began; snapshots may be persisted for deterministic recalculation.
/// </summary>
public sealed record RepositorySnapshot(
    string Repository,
    DateTimeOffset ObservedAt,
    IReadOnlyList<PullRequestSnapshot> PullRequests,
    bool IsComplete,
    IReadOnlyList<BaselineDiagnostic> Diagnostics);

public sealed record BaselinePullRequest(
    int Number,
    string Url,
    DateTimeOffset ReadyAt,
    DateTimeOffset EndAt,
    double DurationHours);

public sealed record MergedBaselineCohort(
    int Count,
    double? MedianDurationHours,
    IReadOnlyList<BaselinePullRequest> PullRequests);

public sealed record OpenBaselineCohort(
    int Count,
    IReadOnlyList<BaselinePullRequest> PullRequests);

public sealed record BaselineReport(
    string SchemaVersion,
    string Repository,
    DateTimeOffset ObservedAt,
    DateTimeOffset RangeStartInclusive,
    DateTimeOffset RangeEndInclusive,
    int Months,
    bool IsComplete,
    string ObservationMode,
    IReadOnlyList<string> Limitations,
    MergedBaselineCohort Merged,
    OpenBaselineCohort OpenCensored,
    IReadOnlyDictionary<string, int> ExclusionCounts,
    IReadOnlyList<BaselineDiagnostic> Diagnostics);

public sealed record BaselineCollectionReport(
    string SchemaVersion,
    DateTimeOffset GeneratedAt,
    int Months,
    int MaxPages,
    string FilterProfile,
    string StartEvent,
    string EndEvent,
    string TimeZone,
    IReadOnlyList<string> Filters,
    IReadOnlyList<BaselineReport> Repositories);

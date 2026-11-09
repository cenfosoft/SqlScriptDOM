using System.Globalization;
using System.Text;

namespace AdversarialReview.Core.Metrics;

public static class BaselineMarkdownRenderer
{
    public static string Render(BaselineCollectionReport report)
    {
        ArgumentNullException.ThrowIfNull(report);
        var markdown = new StringBuilder();
        markdown.AppendLine("# Pull request lead-time baseline");
        markdown.AppendLine();
        markdown.AppendLine($"- Extracted at (UTC): {FormatDate(report.GeneratedAt)}");
        markdown.AppendLine($"- Window: trailing {report.Months} calendar months; per-repository range is included below.");
        markdown.AppendLine($"- Filter profile: `{report.FilterProfile}`");
        markdown.AppendLine($"- Start event: {report.StartEvent}");
        markdown.AppendLine($"- End event: {report.EndEvent}");
        markdown.AppendLine($"- Elapsed-time zone and units: {report.TimeZone}; hours");
        markdown.AppendLine($"- Maximum pages per GitHub API resource: {report.MaxPages}");
        markdown.AppendLine("- Metric: median is computed only from merged PR durations; open PR ages are right-censored and reported separately.");
        markdown.AppendLine("- Interpretation: this historical comparison is descriptive, not causal. No improvement target or minimum sample threshold has been approved.");
        markdown.AppendLine();
        markdown.AppendLine("## Filters and cohort rules");
        foreach (string filter in report.Filters)
        {
            markdown.AppendLine($"- {filter}");
        }

        markdown.AppendLine();
        markdown.AppendLine("## Repository comparison");
        markdown.AppendLine();
        markdown.AppendLine("| Repository | Collection | Merged sample | Median merged lead time (hours) | Open censored sample |");
        markdown.AppendLine("|---|---|---:|---:|---:|");
        foreach (BaselineReport repository in report.Repositories)
        {
            markdown.AppendLine(
                $"| [{repository.Repository}](https://github.com/{repository.Repository}) | {(repository.IsComplete ? "complete" : "INCOMPLETE")} | {repository.Merged.Count} | {FormatHours(repository.Merged.MedianDurationHours)} | {repository.OpenCensored.Count} |");
        }

        foreach (BaselineReport repository in report.Repositories)
        {
            markdown.AppendLine();
            markdown.AppendLine($"## {repository.Repository}");
            markdown.AppendLine();
            markdown.AppendLine($"- Observation time (UTC): {FormatDate(repository.ObservedAt)}");
            markdown.AppendLine($"- Inclusive start event window: {FormatDate(repository.RangeStartInclusive)} through {FormatDate(repository.RangeEndInclusive)}");
            markdown.AppendLine($"- Completeness: {(repository.IsComplete ? "complete" : "INCOMPLETE; do not present partial cohorts as a complete baseline")}");
            markdown.AppendLine($"- Merged PRs: **{repository.Merged.Count}**; median: **{FormatHours(repository.Merged.MedianDurationHours)} hours**.");
            markdown.AppendLine($"- Open PRs: **{repository.OpenCensored.Count}**; censored ages are not included in the median.");
            markdown.AppendLine();
            AppendPullRequests(markdown, "Merged PR durations", repository, repository.Merged.PullRequests, "Merged at");
            AppendPullRequests(markdown, "Open PR censored ages", repository, repository.OpenCensored.PullRequests, "Observed at");

            if (repository.ExclusionCounts.Count > 0)
            {
                markdown.AppendLine();
                markdown.AppendLine("### Exclusions");
                foreach ((string reason, int count) in repository.ExclusionCounts.OrderBy(item => item.Key, StringComparer.Ordinal))
                {
                    markdown.AppendLine($"- `{reason}`: {count}");
                }
            }

            if (repository.Diagnostics.Count > 0)
            {
                markdown.AppendLine();
                markdown.AppendLine("### Diagnostics");
                foreach (BaselineDiagnostic diagnostic in repository.Diagnostics)
                {
                    string pullRequest = diagnostic.PullRequestNumber is int number
                        ? $" (`{repository.Repository}#{number}`)"
                        : string.Empty;
                    markdown.AppendLine($"- `{diagnostic.Code}`{pullRequest}");
                }
            }

            if (repository.Limitations.Count > 0)
            {
                markdown.AppendLine();
                markdown.AppendLine("### Limitations");
                foreach (string limitation in repository.Limitations)
                {
                    markdown.AppendLine($"- {limitation}");
                }
            }
        }

        return markdown.ToString();
    }

    private static void AppendPullRequests(StringBuilder markdown, string heading, BaselineReport report,
        IReadOnlyList<BaselinePullRequest> pullRequests, string endLabel)
    {
        markdown.AppendLine();
        markdown.AppendLine($"### {heading}");
        markdown.AppendLine();
        if (pullRequests.Count == 0)
        {
            markdown.AppendLine("No eligible PRs.");
            return;
        }

        markdown.AppendLine("| Pull request | Start (UTC) | " + endLabel + " (UTC) | Duration / censored age (hours) |");
        markdown.AppendLine("|---|---|---|---:|");
        foreach (BaselinePullRequest pullRequest in pullRequests)
        {
            markdown.AppendLine(
                $"| [{report.Repository}#{pullRequest.Number}]({pullRequest.Url}) | {FormatDate(pullRequest.ReadyAt)} | {FormatDate(pullRequest.EndAt)} | {FormatHours(pullRequest.DurationHours)} |");
        }
    }

    private static string FormatDate(DateTimeOffset value) =>
        value.ToUniversalTime().ToString("yyyy-MM-dd'T'HH:mm:ss'Z'", CultureInfo.InvariantCulture);

    private static string FormatHours(double? value) =>
        value?.ToString("0.00", CultureInfo.InvariantCulture) ?? "not available (n=0)";
}

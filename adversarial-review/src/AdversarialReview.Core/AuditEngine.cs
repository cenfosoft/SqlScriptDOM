using System.Globalization;
using System.Net;
using System.Text;
using System.Text.RegularExpressions;

namespace AdversarialReview;

public static class AuditEngine
{
    public static AuditReport Create(
        ReviewRun run, string currentHeadSha, CheckResult[] checks, Finding[] findings,
        ReviewRound[] rounds, int expectedRounds)
    {
        Contracts.Write(run, "review-run");
        List<string> blockers = [.. CheckPolicy.Blockers(run.HeadSha, checks)];
        bool stale = currentHeadSha != run.HeadSha;
        if (stale) blockers.Add("The evaluated PR head SHA is stale.");
        if (rounds.Length != expectedRounds || rounds.Any(r => r.Outcome != "completed"))
            blockers.Add("One or more configured review rounds are incomplete.");
        foreach (Finding finding in findings.OrderBy(f => f.Id, StringComparer.Ordinal))
        {
            if (finding.Severity is "critical" or "high" && finding.Disposition is "unreviewed" or "confirmed")
                blockers.Add($"Unresolved {finding.Severity} finding: {finding.Id}.");
        }
        string status = stale ? "stale" :
            CheckPolicy.Blockers(run.HeadSha, checks).Length > 0
            || rounds.Length != expectedRounds || rounds.Any(r => r.Outcome != "completed") ? "incomplete" : "complete";
        AuditReport report = new(
            "1.0", "report-only", "uncalibrated", run,
            checks.OrderBy(c => c.Name, StringComparer.Ordinal).ThenBy(c => c.Platform, StringComparer.Ordinal).ToArray(),
            findings.OrderBy(f => SeverityOrder(f.Severity)).ThenBy(f => f.Id, StringComparer.Ordinal).ToArray(),
            rounds, expectedRounds, status, null, blockers.ToArray(),
            ["Advisory only; existing CI remains authoritative.",
             "Numeric risk weights and thresholds are not approved. No numeric score or merge clearance is produced.",
             "Offline input validation does not establish GitHub event or artifact authenticity."]);
        Contracts.Write(report, "audit-report");
        return report;
    }

    private static int SeverityOrder(string severity) => severity switch
    {
        "critical" => 0, "high" => 1, "medium" => 2, "low" => 3, _ => 4
    };
}

public static partial class ReportRenderer
{
    public const string Marker = "<!-- scriptdom-adversarial-review:v1 -->";
    public const int MaximumCommentCharacters = 45000;

    public static string Render(AuditReport report, IEnumerable<string>? knownSecrets = null)
    {
        Contracts.Write(report, "audit-report");
        string[] secrets = (knownSecrets ?? []).Where(s => !string.IsNullOrEmpty(s))
            .OrderByDescending(s => s.Length).ToArray();
        string Safe(string text) => Escape(Redact(text, secrets));
        StringBuilder output = new(Marker + "\n## Adversarial review (report-only)\n\n");
        output.AppendLine("**Not a merge gate. Risk model: uncalibrated; numeric score unavailable.**\n");
        output.AppendLine($"PR: {Safe(report.Run.Repository)} #{report.Run.PullRequest}");
        output.AppendLine($"Head SHA: `{report.Run.HeadSha}`  ");
        output.AppendLine($"Trusted configuration: `{report.Run.ConfigurationSha}`  ");
        output.AppendLine($"Status: **{report.Status}**; rounds: {report.Rounds.Length}/{report.ExpectedRounds}\n");
        output.AppendLine($"[Workflow run](https://github.com/{report.Run.Repository}/actions/runs/{report.Run.WorkflowRunId})\n");
        output.AppendLine("### Deterministic checks\n\n| Check | Platform | Conclusion |\n|---|---|---|");
        foreach (CheckResult check in report.Checks)
            output.AppendLine($"| {check.Name} | {check.Platform} | {check.Conclusion} |");
        output.AppendLine("\n### Findings\n");
        if (report.Findings.Length == 0)
            output.AppendLine("No findings recorded. This does not prove the change is safe.\n");
        foreach (Finding finding in report.Findings)
        {
            StringBuilder section = new();
            section.AppendLine($"#### {Safe(finding.Id)}: {Safe(finding.Title)}");
            section.AppendLine($"Severity: {finding.Severity}; confidence: {finding.Confidence.ToString(CultureInfo.InvariantCulture)}; disposition: {finding.Disposition}\n");
            section.AppendLine(Safe(finding.Description) + "\n");
            foreach (Evidence evidence in finding.Evidence)
            {
                string reference = evidence.Kind == "file"
                    ? $"[{Safe(evidence.Reference)}:{evidence.StartLine}](https://github.com/{report.Run.Repository}/blob/{report.Run.HeadSha}/{EncodePath(evidence.Reference)}#L{evidence.StartLine}-L{evidence.EndLine})"
                    : Safe(evidence.Reference);
                section.AppendLine($"- {reference}: {Safe(evidence.Detail)}");
            }
            section.AppendLine("\nRecommendation: " + Safe(finding.Recommendation) + "\n");
            if (output.Length + section.Length > MaximumCommentCharacters - 10000)
            {
                output.AppendLine("Additional findings omitted from this comment; consult the audit JSON artifact.\n");
                break;
            }
            output.Append(section);
        }
        output.AppendLine("\n### Model rounds\n\n| Round | Provider / model | Outcome | Tokens in/out | Estimated USD |\n|---|---|---|---|---|");
        foreach (ReviewRound round in report.Rounds)
        {
            string tokens = $"{round.InputTokens?.ToString(CultureInfo.InvariantCulture) ?? "unknown"}/{round.OutputTokens?.ToString(CultureInfo.InvariantCulture) ?? "unknown"}";
            output.AppendLine($"| {round.Number} | {Safe(round.Provider)} / {Safe(round.Model)} | {round.Outcome} | {tokens} | {round.EstimatedCostUsd?.ToString(CultureInfo.InvariantCulture) ?? "unknown"} |");
        }
        AppendBoundedList(output, "Blockers / follow-up", report.Blockers, Safe);
        AppendBoundedList(output, "Limitations", report.Limitations, Safe);
        if (output.Length > MaximumCommentCharacters)
            throw new ContractException("Rendered comment exceeds the safe publication limit; use the audit artifact.");
        return output.ToString().Replace("\r\n", "\n", StringComparison.Ordinal);
    }

    private static void AppendBoundedList(StringBuilder output, string title, string[] values, Func<string, string> safe)
    {
        output.AppendLine($"\n### {title}\n");
        foreach (string value in values)
        {
            string line = "- " + safe(value);
            if (output.Length + line.Length > MaximumCommentCharacters - 500)
            {
                output.AppendLine("- Additional details omitted; consult the audit JSON artifact.");
                break;
            }
            output.AppendLine(line);
        }
    }

    public static string Redact(string value, IEnumerable<string> knownSecrets)
    {
        foreach (string secret in knownSecrets.Where(s => !string.IsNullOrEmpty(s)).OrderByDescending(s => s.Length))
            value = value.Replace(secret, "[REDACTED]", StringComparison.Ordinal);
        return TokenPattern().Replace(value, "[REDACTED]");
    }

    private static string Escape(string value)
    {
        value = WebUtility.HtmlEncode(value.Replace('\r', ' ').Replace('\n', ' '));
        foreach (char character in "\\`*_[]()#!|@")
            value = value.Replace(character.ToString(), $"&#{(int)character};", StringComparison.Ordinal);
        return value;
    }

    private static string EncodePath(string path) =>
        string.Join("/", path.Split('/').Select(Uri.EscapeDataString));

    [GeneratedRegex(@"\b(?:gh[pousr]_[A-Za-z0-9_]{16,}|github_pat_[A-Za-z0-9_]{16,}|sk-[A-Za-z0-9_-]{16,})", RegexOptions.CultureInvariant, 1000)]
    private static partial Regex TokenPattern();
}

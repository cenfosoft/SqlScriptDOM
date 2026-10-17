using System.Text;
using System.Text.Json;

namespace AdversarialReview;

public sealed record ReviewFile(string Path, int LineCount);

public sealed record ReviewContext(
    string HeadSha, string ConfigurationSha, string TrustedGuidance,
    string UntrustedDiff, ReviewFile[] Files, string[] Checks, string[] Artifacts);

public static class ReviewContextBuilder
{
    public static string Build(ReviewContext context, int maximumUtf8Bytes, IEnumerable<string> knownSecrets)
    {
        ValidateContext(context);
        if (maximumUtf8Bytes <= 0 || maximumUtf8Bytes > Contracts.MaximumDocumentBytes)
            throw new ContractException("Context byte limit must be positive and within the document ceiling.");
        string json = JsonSerializer.Serialize(new
        {
            schemaVersion = "1.0",
            context.HeadSha,
            context.ConfigurationSha,
            trustedGuidance = context.TrustedGuidance,
            untrustedData = new { diff = context.UntrustedDiff, files = context.Files, checks = context.Checks, artifacts = context.Artifacts }
        }, Contracts.JsonOptions);
        if (Encoding.UTF8.GetByteCount(json) > maximumUtf8Bytes)
            throw new ContractException("Review context exceeds its UTF-8 byte budget; no silent truncation is permitted.");
        ReportRenderer.RejectCredentialData(json, knownSecrets);
        return json;
    }

    internal static void ValidateContext(ReviewContext context)
    {
        if (!Contracts.IsSha(context.HeadSha) || !Contracts.IsSha(context.ConfigurationSha))
            throw new ContractException("Context requires valid head and trusted configuration SHAs.");
        if (string.IsNullOrWhiteSpace(context.TrustedGuidance))
            throw new ContractException("Trusted guidance is required.");
        if (context.Files.Length > 1000 || context.Files.Any(f => !Contracts.IsSafeRelativePath(f.Path) || f.LineCount < 0))
            throw new ContractException("Review file inventory is invalid or exceeds the file limit.");
        if (context.Files.Select(f => f.Path).Distinct(StringComparer.Ordinal).Count() != context.Files.Length
            || context.Checks.Distinct(StringComparer.Ordinal).Count() != context.Checks.Length
            || context.Artifacts.Distinct(StringComparer.Ordinal).Count() != context.Artifacts.Length)
            throw new ContractException("Review context inventories must not contain duplicate entries.");
    }
}

public static class ReviewerOutput
{
    public static Finding[] Parse(string json, ReviewContext context, IEnumerable<string> knownSecrets)
    {
        ReviewContextBuilder.ValidateContext(context);
        Finding[] findings = Contracts.Read<Finding[]>(json, "reviewer-output");
        ReportRenderer.RejectCredentialData(json, knownSecrets);
        if (findings.Select(f => f.Id).Distinct(StringComparer.Ordinal).Count() != findings.Length)
            throw new ContractException("Reviewer output contains duplicate finding identifiers.");
        foreach (Finding finding in findings)
        {
            Contracts.Write(finding, "finding");
            if (finding.HeadSha != context.HeadSha || finding.Source != "reviewer" || finding.Disposition != "unreviewed")
                throw new ContractException("Reviewer output changed the SHA, source, or maintainer-owned disposition.");
            foreach (Evidence evidence in finding.Evidence)
            {
                if (evidence.Kind == "file")
                {
                    ReviewFile? file = context.Files.SingleOrDefault(f => f.Path == evidence.Reference);
                    if (file is null || evidence.EndLine > file.LineCount)
                        throw new ContractException("Reviewer file evidence is outside the supplied context.");
                }
                else if (evidence.Kind == "check" && !context.Checks.Contains(evidence.Reference, StringComparer.Ordinal)
                    || evidence.Kind == "artifact" && !context.Artifacts.Contains(evidence.Reference, StringComparer.Ordinal))
                    throw new ContractException("Reviewer check or artifact evidence is outside the supplied context.");
            }
        }
        return findings;
    }
}

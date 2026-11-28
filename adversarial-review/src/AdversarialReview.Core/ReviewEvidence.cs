using System.IO.Compression;
using System.Net;
using System.Security.Cryptography;
using System.Text;
using System.Text.Json;

namespace AdversarialReview;

public sealed record IngestedEvidence(CheckResult[] Checks, string[] Blockers)
{
    // Verified raw files needed for risk signals, keyed by "<source>/<path>".
    public IReadOnlyDictionary<string, byte[]> Files { get; init; } = new Dictionary<string, byte[]>(StringComparer.Ordinal);
}

public sealed record VerifiedArchive(string Conclusion, IReadOnlyDictionary<string, byte[]> Files);

public static class ReviewEvidenceArchive
{
    public const int MaximumArchiveBytes = 55 * 1024 * 1024;

    // Inspect in memory, never extract contributor-controlled paths into the host.
    public static CheckResult Verify(byte[] zip, EvidenceIdentity identity, string check, string platform, string reference)
    {
        VerifiedArchive archive = Open(zip, identity, check, platform, _ => false);
        return new("1.0", identity.HeadSha, check, platform, archive.Conclusion, reference);
    }

    // Returns the manifest conclusion and the verified bytes of files selected by retain.
    public static VerifiedArchive Open(byte[] zip, EvidenceIdentity identity, string check, string platform, Func<string, bool> retain)
    {
        if (zip.Length > MaximumArchiveBytes) throw new ContractException("Evidence archive exceeds its compressed byte ceiling.");
        try
        {
            using MemoryStream input = new(zip, writable: false);
            using ZipArchive archive = new(input, ZipArchiveMode.Read);
            if (archive.Entries.Count is < 2 or > EvidenceManifest.MaximumFiles + 1)
                throw new ContractException("Evidence archive entry count is invalid.");
            Dictionary<string, (long Bytes, string Sha256)> files = new(StringComparer.Ordinal);
            Dictionary<string, byte[]> retained = new(StringComparer.Ordinal);
            HashSet<string> names = new(StringComparer.OrdinalIgnoreCase);
            string? manifestJson = null;
            long total = 0;
            foreach (ZipArchiveEntry entry in archive.Entries)
            {
                string path = entry.FullName;
                int type = (entry.ExternalAttributes >> 16) & 0xF000;
                if (!Contracts.IsSafeRelativePath(path) || path.Split('/').Length > 33
                    || path.Split('/').Any(p => p.EndsWith('.') || p.EndsWith(' ') || IsDeviceName(p))
                    || !names.Add(path) || type is not (0 or 0x8000)
                    || (entry.ExternalAttributes & (int)FileAttributes.ReparsePoint) != 0
                    || entry.Length > (path == EvidenceManifest.FileName ? Contracts.MaximumDocumentBytes : EvidenceManifest.MaximumFileBytes)
                    || (total += entry.Length) > EvidenceManifest.MaximumTotalBytes + Contracts.MaximumDocumentBytes)
                    throw new ContractException("Evidence archive has unsafe paths, links, duplicates, types, or sizes.");
                using Stream source = entry.Open();
                using MemoryStream content = new();
                byte[] buffer = new byte[81920];
                while (true)
                {
                    int read = source.Read(buffer);
                    if (read == 0) break;
                    if (content.Length + read > entry.Length)
                        throw new ContractException("Evidence expands beyond its declared size.");
                    content.Write(buffer, 0, read);
                }
                if (content.Length != entry.Length) throw new ContractException("Evidence entry has an inconsistent size.");
                byte[] bytes = content.ToArray();
                if (path == EvidenceManifest.FileName)
                    manifestJson = new UTF8Encoding(false, true).GetString(bytes);
                else
                {
                    files.Add(path, (bytes.Length, Convert.ToHexString(SHA256.HashData(bytes)).ToLowerInvariant()));
                    if (retain(path)) retained.Add(path, bytes);
                }
            }
            if (manifestJson is null) throw new ContractException("Evidence manifest is missing.");
            EvidenceManifest manifest = Contracts.Read<EvidenceManifest>(manifestJson, "evidence-manifest");
            if (manifest.Identity != identity || manifest.Check != check || manifest.Platform != platform
                || manifest.Files.Length != files.Count
                || manifest.Files.Select(f => f.Path).Distinct(StringComparer.OrdinalIgnoreCase).Count() != manifest.Files.Length
                || manifest.Files.Any(f => !files.TryGetValue(f.Path, out var actual) || actual.Bytes != f.Bytes || actual.Sha256 != f.Sha256))
                throw new ContractException("Evidence manifest identity or raw file checksums do not match.");
            return new(manifest.Conclusion, retained);
        }
        catch (Exception error) when (error is InvalidDataException or DecoderFallbackException)
        {
            throw new ContractException("Evidence archive is corrupt or has invalid encoding; contents are not logged.");
        }
    }

    private static bool IsDeviceName(string part)
    {
        string name = part.Split('.')[0];
        return name.Equals("CON", StringComparison.OrdinalIgnoreCase) || name.Equals("PRN", StringComparison.OrdinalIgnoreCase)
            || name.Equals("AUX", StringComparison.OrdinalIgnoreCase) || name.Equals("NUL", StringComparison.OrdinalIgnoreCase)
            || name.Length == 4 && (name.StartsWith("COM", StringComparison.OrdinalIgnoreCase) || name.StartsWith("LPT", StringComparison.OrdinalIgnoreCase))
                && name[3] is >= '1' and <= '9';
    }
}

public sealed class GitHubReviewEvidence(GitHubReviewApi api, HttpClient artifactHttp)
{
    // The artifact, job, and step names mirror .github/workflows/pr-validation.yml.
    internal const string BuildScriptDomStep = "Build ScriptDOM on .NET 8";
    internal const string BuildTestsStep = "Build the SqlDom test assembly on .NET 8";
    internal const string RunTestsStep = "Run the complete SqlDom unit-test suite";
    internal const string RoslynStep = "Run scoped Roslyn NetAnalyzers";
    internal const string SemgrepStep = "Run Semgrep rules";

    // Raw files retained for risk signals; everything else is only checksum-verified.
    internal static readonly string[] RetainedFiles = ["coverage.cobertura.xml", "semgrep.sarif", "roslyn.log"];

    private sealed record Source(string Key, string ArtifactName, string ManifestCheck, string ManifestPlatform,
        string JobName, (string Check, string Platform, string[] Steps, bool UseManifestConclusion)[] Checks);

    internal static string ArtifactSuffix(AdmittedReview admission) =>
        $"{admission.RepositoryId}-pr{admission.PullRequest}-{admission.HeadSha}-run{admission.ValidationRunId}-attempt{admission.ValidationAttempt}";

    public async Task<IngestedEvidence> CollectAsync(AdmittedReview admission, CancellationToken cancellationToken)
    {
        if (artifactHttp.Timeout == Timeout.InfiniteTimeSpan || admission.Repository != api.Repository)
            throw new ContractException("Evidence requires the admitted repository and bounded artifact HTTP client.");
        JsonElement[] artifacts = await api.ListAsync($"{api.Route}/actions/runs/{admission.ValidationRunId}/artifacts", "artifacts", cancellationToken);
        JsonElement[] jobs = await api.ListAsync(
            $"{api.Route}/actions/runs/{admission.ValidationRunId}/attempts/{admission.ValidationAttempt}/jobs", "jobs", cancellationToken);
        string suffix = ArtifactSuffix(admission);
        Source[] sources =
        [
            .. new[] { "windows", "ubuntu" }.Select(platform => new Source(
                "test-" + platform, $"ut-scriptdom-{suffix}-{platform}-latest", "test", platform, $"build-and-test ({platform}-latest)",
                [("build", platform, [BuildScriptDomStep, BuildTestsStep], false), ("test", platform, [RunTestsStep], true)])),
            new Source("static-analysis", $"static-analysis-{suffix}", "static-analysis", "ubuntu", "static-analysis",
                [("roslyn", "independent", [RoslynStep], false), ("semgrep", "independent", [SemgrepStep], false)])
        ];
        var identity = new EvidenceIdentity(admission.Repository, admission.PullRequest, admission.HeadSha,
            admission.ValidationRunId, admission.ValidationAttempt);
        List<CheckResult> checks = [];
        List<string> blockers = [];
        Dictionary<string, byte[]> files = new(StringComparer.Ordinal);
        foreach (Source source in sources)
        {
            JsonElement[] candidates = artifacts.Where(a => a.GetProperty("name").GetString() == source.ArtifactName).ToArray();
            if (candidates.Length != 1)
            {
                blockers.AddRange(source.Checks.Select(c => $"Required {c.Check}/{c.Platform} artifact is missing or duplicated."));
                continue;
            }
            JsonElement[] matchingJobs = jobs.Where(j => j.GetProperty("name").GetString() == source.JobName).ToArray();
            if (matchingJobs.Length != 1 || matchingJobs[0].GetProperty("status").GetString() != "completed"
                || matchingJobs[0].GetProperty("conclusion").GetString() != "success"
                || matchingJobs[0].GetProperty("run_id").GetInt64() != admission.ValidationRunId)
                throw new ContractException("Evidence does not have one successful authoritative job in the admitted attempt.");
            JsonElement[] steps = matchingJobs[0].GetProperty("steps").EnumerateArray().ToArray();
            foreach (var check in source.Checks)
            foreach (string stepName in check.Steps)
            {
                JsonElement[] matchingSteps = steps.Where(s => s.GetProperty("name").GetString() == stepName).ToArray();
                if (matchingSteps.Length != 1 || matchingSteps[0].GetProperty("status").GetString() != "completed"
                    || matchingSteps[0].GetProperty("conclusion").GetString() != "success")
                    throw new ContractException("Required validation step is missing, skipped, or unsuccessful.");
            }
            JsonElement artifact = candidates[0];
            long id = artifact.GetProperty("id").GetInt64();
            if (id <= 0 || artifact.GetProperty("expired").GetBoolean()
                || artifact.GetProperty("size_in_bytes").GetInt64() is <= 0 or > ReviewEvidenceArchive.MaximumArchiveBytes
                || artifact.GetProperty("workflow_run").GetProperty("id").GetInt64() != admission.ValidationRunId
                || artifact.GetProperty("workflow_run").GetProperty("repository_id").GetInt64() != admission.RepositoryId
                || artifact.GetProperty("workflow_run").GetProperty("head_repository_id").GetInt64() != admission.HeadRepositoryId
                || artifact.GetProperty("workflow_run").GetProperty("head_sha").GetString() != admission.HeadSha)
                throw new ContractException("Artifact is expired, oversized, or belongs to a different run/repository/head.");
            byte[] zip = await DownloadAsync(id, cancellationToken);
            VerifiedArchive verified = ReviewEvidenceArchive.Open(zip, identity, source.ManifestCheck, source.ManifestPlatform,
                path => RetainedFiles.Contains(path.Split('/')[^1], StringComparer.Ordinal));
            foreach (var check in source.Checks)
                checks.Add(new("1.0", admission.HeadSha, check.Check, check.Platform,
                    check.UseManifestConclusion ? verified.Conclusion : "passed", $"actions-artifact:{id}:{check.Check}-{check.Platform}"));
            foreach ((string path, byte[] bytes) in verified.Files)
                files.Add($"{source.Key}/{path}", bytes);
        }
        blockers.AddRange(CheckPolicy.Blockers(admission.HeadSha, checks));
        return new(checks.ToArray(), blockers.Distinct(StringComparer.Ordinal).ToArray()) { Files = files };
    }

    private async Task<byte[]> DownloadAsync(long artifactId, CancellationToken cancellationToken)
    {
        using HttpResponseMessage response = await api.SendAsync($"{api.Route}/actions/artifacts/{artifactId}/zip", cancellationToken);
        if (response.StatusCode == HttpStatusCode.OK)
            return await GitHubReviewApi.ReadBoundedAsync(response.Content, ReviewEvidenceArchive.MaximumArchiveBytes, cancellationToken);
        if (response.StatusCode != HttpStatusCode.Found || response.Headers.Location is not Uri uri
            || uri.Scheme != Uri.UriSchemeHttps || !uri.IsDefaultPort || !string.IsNullOrEmpty(uri.UserInfo)
            || !string.IsNullOrEmpty(uri.Fragment)
            || !(uri.Host.EndsWith(".blob.core.windows.net", StringComparison.OrdinalIgnoreCase)
                || uri.Host.EndsWith(".actions.githubusercontent.com", StringComparison.OrdinalIgnoreCase)))
            throw new ContractException("GitHub artifact redirect is absent or outside approved HTTPS storage origins.");
        using HttpRequestMessage download = new(HttpMethod.Get, uri);
        // Never forward the GitHub credential to signed storage URLs.
        using HttpResponseMessage stored = await artifactHttp.SendAsync(download, HttpCompletionOption.ResponseHeadersRead, cancellationToken);
        if (stored.StatusCode != HttpStatusCode.OK)
            throw new ContractException("Artifact storage download failed or redirected again.");
        return await GitHubReviewApi.ReadBoundedAsync(stored.Content, ReviewEvidenceArchive.MaximumArchiveBytes, cancellationToken);
    }
}

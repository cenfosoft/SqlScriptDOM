using System.IO.Compression;
using System.Net;
using System.Security.Cryptography;
using System.Text;
using System.Text.Json;

namespace AdversarialReview;

public sealed record IngestedEvidence(CheckResult[] Checks, string[] Blockers);

public static class ReviewEvidenceArchive
{
    public const int MaximumArchiveBytes = 55 * 1024 * 1024;

    // Inspect in memory, never extract contributor-controlled paths into the host.
    public static CheckResult Verify(byte[] zip, EvidenceIdentity identity, string check, string platform, string reference)
    {
        if (zip.Length > MaximumArchiveBytes) throw new ContractException("Evidence archive exceeds its compressed byte ceiling.");
        try
        {
            using MemoryStream input = new(zip, writable: false);
            using ZipArchive archive = new(input, ZipArchiveMode.Read);
            if (archive.Entries.Count is < 2 or > EvidenceManifest.MaximumFiles + 1)
                throw new ContractException("Evidence archive entry count is invalid.");
            Dictionary<string, (long Bytes, string Sha256)> files = new(StringComparer.Ordinal);
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
                    files.Add(path, (bytes.Length, Convert.ToHexString(SHA256.HashData(bytes)).ToLowerInvariant()));
            }
            if (manifestJson is null) throw new ContractException("Evidence manifest is missing.");
            EvidenceManifest manifest = Contracts.Read<EvidenceManifest>(manifestJson, "evidence-manifest");
            if (manifest.Identity != identity || manifest.Check != check || manifest.Platform != platform
                || manifest.Files.Length != files.Count
                || manifest.Files.Select(f => f.Path).Distinct(StringComparer.OrdinalIgnoreCase).Count() != manifest.Files.Length
                || manifest.Files.Any(f => !files.TryGetValue(f.Path, out var actual) || actual.Bytes != f.Bytes || actual.Sha256 != f.Sha256))
                throw new ContractException("Evidence manifest identity or raw file checksums do not match.");
            return new("1.0", identity.HeadSha, check, platform, manifest.Conclusion, reference);
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
    public async Task<IngestedEvidence> CollectAsync(AdmittedReview admission, CancellationToken cancellationToken)
    {
        if (artifactHttp.Timeout == Timeout.InfiniteTimeSpan || admission.Repository != api.Repository)
            throw new ContractException("Evidence requires the admitted repository and bounded artifact HTTP client.");
        JsonElement[] artifacts = await api.ListAsync($"{api.Route}/actions/runs/{admission.ValidationRunId}/artifacts", "artifacts", cancellationToken);
        JsonElement[] jobs = await api.ListAsync(
            $"{api.Route}/actions/runs/{admission.ValidationRunId}/attempts/{admission.ValidationAttempt}/jobs", "jobs", cancellationToken);
        List<CheckResult> checks = [];
        List<string> blockers = [];
        foreach ((string check, string platform) in CheckPolicy.Required)
        {
            string os = platform == "independent" ? platform : platform == "windows" ? "windows-latest" : "ubuntu-latest";
            string name = $"{check}-{os}-{admission.HeadSha}-{admission.ValidationAttempt}";
            JsonElement[] candidates = artifacts.Where(a => a.GetProperty("name").GetString() == name).ToArray();
            if (candidates.Length != 1)
            {
                blockers.Add($"Required {check}/{platform} artifact is missing or duplicated.");
                continue;
            }
            string jobName = platform == "independent" ? check : $"{check} ({os})";
            JsonElement[] matchingJobs = jobs.Where(j => j.GetProperty("name").GetString() == jobName).ToArray();
            if (matchingJobs.Length != 1 || matchingJobs[0].GetProperty("status").GetString() != "completed"
                || matchingJobs[0].GetProperty("conclusion").GetString() != "success"
                || matchingJobs[0].GetProperty("run_id").GetInt64() != admission.ValidationRunId)
                throw new ContractException("Evidence does not have one successful authoritative job in the admitted attempt.");
            string stepName = check == "build" ? "Build"
                : check == "test" ? platform == "windows" ? "Run tests Windows" : "Run tests Linux"
                : check == "semgrep" ? "Run Semgrep" : "Run Roslyn";
            JsonElement[] steps = matchingJobs[0].GetProperty("steps").EnumerateArray()
                .Where(s => s.GetProperty("name").GetString() == stepName).ToArray();
            if (steps.Length != 1 || steps[0].GetProperty("status").GetString() != "completed"
                || steps[0].GetProperty("conclusion").GetString() != "success")
                throw new ContractException("Required validation step is missing, skipped, or unsuccessful.");
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
            var identity = new EvidenceIdentity(admission.Repository, admission.PullRequest, admission.HeadSha,
                admission.ValidationRunId, admission.ValidationAttempt);
            checks.Add(ReviewEvidenceArchive.Verify(zip, identity, check, platform, $"actions-artifact:{id}"));
        }
        blockers.AddRange(CheckPolicy.Blockers(admission.HeadSha, checks));
        return new(checks.ToArray(), blockers.Distinct(StringComparer.Ordinal).ToArray());
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

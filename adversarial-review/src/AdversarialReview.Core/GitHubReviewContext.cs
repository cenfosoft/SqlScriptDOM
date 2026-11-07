using System.Text;
using System.Text.Json;

namespace AdversarialReview;

public sealed class GitHubReviewContext(GitHubReviewApi api)
{
    public async Task<PullRequestState> ReadStateAsync(int pullRequest, CancellationToken cancellationToken)
    {
        if (pullRequest <= 0) throw new ContractException("A positive PR number is required.");
        using JsonDocument pr = await api.GetAsync($"{api.Route}/pulls/{pullRequest}", cancellationToken);
        return new(pr.RootElement.GetProperty("head").GetProperty("sha").GetString()!,
            pr.RootElement.GetProperty("state").GetString() == "open", pr.RootElement.GetProperty("draft").GetBoolean());
    }

    public async Task<ReviewContext> BuildAsync(
        AdmittedReview admission, string trustedGuidance, CheckResult[] checks, CancellationToken cancellationToken)
    {
        JsonElement[] files = await api.ListAsync($"{api.Route}/pulls/{admission.PullRequest}/files", null, cancellationToken);
        // GitHub caps this API at 3000 files; the stricter context ceiling prevents accepting its truncated edge.
        if (files.Length is < 1 or > 1000)
            throw new ContractException("PR file inventory is empty or exceeds the context ceiling.");
        List<ReviewFile> inventory = [];
        List<object> patches = [];
        long bytes = 0;
        foreach (JsonElement file in files)
        {
            string path = file.GetProperty("filename").GetString()!;
            string sha = file.GetProperty("sha").GetString()!;
            if (!Contracts.IsSafeRelativePath(path) || !Contracts.IsSha(sha)
                || !file.TryGetProperty("patch", out JsonElement patchElement) || patchElement.ValueKind != JsonValueKind.String)
                throw new ContractException("File path, blob SHA, or complete textual patch is unavailable; no partial context is accepted.");
            string patch = patchElement.GetString()!;
            string[] patchLines = patch.Split('\n');
            if (patchLines.Count(l => l.StartsWith('+')) != file.GetProperty("additions").GetInt32()
                || patchLines.Count(l => l.StartsWith('-')) != file.GetProperty("deletions").GetInt32())
                throw new ContractException("Textual patch is incomplete or differs from GitHub change counts.");
            int lineCount = 0;
            string? content = null;
            string status = file.GetProperty("status").GetString()!;
            if (status != "removed")
            {
                using JsonDocument blob = await api.GetAsync($"{api.Route}/git/blobs/{sha}", cancellationToken);
                JsonElement raw = blob.RootElement;
                if (raw.GetProperty("sha").GetString() != sha || raw.GetProperty("encoding").GetString() != "base64"
                    || raw.GetProperty("size").GetInt64() > Contracts.MaximumDocumentBytes)
                    throw new ContractException("GitHub blob identity, encoding, or size is invalid.");
                byte[] decoded;
                try { decoded = Convert.FromBase64String(raw.GetProperty("content").GetString()!); }
                catch (FormatException) { throw new ContractException("GitHub returned invalid base64 source content."); }
                if (decoded.Length != raw.GetProperty("size").GetInt64())
                    throw new ContractException("GitHub blob byte size differs from its metadata.");
                // Validate Git's blob hash, not just an untrusted metadata SHA.
                byte[] header = Encoding.ASCII.GetBytes($"blob {decoded.Length}\0");
                using var hash = System.Security.Cryptography.IncrementalHash.CreateHash(System.Security.Cryptography.HashAlgorithmName.SHA1);
                hash.AppendData(header);
                hash.AppendData(decoded);
                if (Convert.ToHexString(hash.GetHashAndReset()).ToLowerInvariant() != sha)
                    throw new ContractException("GitHub source bytes do not match their Git blob identity.");
                try { content = new UTF8Encoding(false, true).GetString(decoded); }
                catch (DecoderFallbackException) { throw new ContractException("Changed source is not valid UTF-8."); }
                if (content.Contains('\0')) throw new ContractException("Binary source content is not accepted as text context.");
                lineCount = content.Length == 0 ? 0 : content.Count(c => c == '\n') + (content.EndsWith('\n') ? 0 : 1);
                bytes += decoded.Length;
            }
            bytes += Encoding.UTF8.GetByteCount(patch);
            if (bytes > Contracts.MaximumDocumentBytes)
                throw new ContractException("Source and patch content exceeds the context byte ceiling.");
            inventory.Add(new(path, lineCount));
            patches.Add(new { path, status, patch, headContent = content });
        }
        PullRequestState current = await ReadStateAsync(admission.PullRequest, cancellationToken);
        if (current.HeadSha != admission.HeadSha || !current.IsOpen || current.IsDraft)
            throw new ContractException("PR changed while fetching review context.");
        var context = new ReviewContext(admission.HeadSha, admission.ConfigurationSha, trustedGuidance,
            JsonSerializer.Serialize(patches, Contracts.JsonOptions), inventory.ToArray(),
            checks.Select(c => c.Name + "/" + c.Platform).ToArray(), checks.Select(c => c.Artifact).ToArray());
        ReviewContextBuilder.Build(context, Contracts.MaximumDocumentBytes, []);
        return context;
    }
}

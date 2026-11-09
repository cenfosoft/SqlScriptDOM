using System.Security.Cryptography;

namespace AdversarialReview;

public sealed record EvidenceIdentity(
    string Repository, int PullRequest, string HeadSha, long WorkflowRunId, int RunAttempt);

public sealed record EvidenceFile(string Path, long Bytes, string Sha256);

public sealed record EvidenceManifest(
    string SchemaVersion, EvidenceIdentity Identity, string Check, string Platform,
    string Conclusion, EvidenceFile[] Files)
{
    public const int MaximumFiles = 1000;
    public const long MaximumFileBytes = 20 * 1024 * 1024;
    public const long MaximumTotalBytes = 50 * 1024 * 1024;
    public const string FileName = "manifest.json";

    public static EvidenceManifest Create(string directory, EvidenceIdentity identity, string check, string platform, string conclusion)
    {
        platform = platform switch
        {
            "windows-latest" => "windows",
            "ubuntu-latest" => "ubuntu",
            _ => platform
        };
        List<EvidenceFile> files = [];
        string root = Path.GetFullPath(directory);
        RejectLink(root);
        foreach (string path in EnumerateFiles(root, 0))
        {
            string relative = Path.GetRelativePath(root, path).Replace('\\', '/');
            if (relative == FileName) continue;
            if (!Contracts.IsSafeRelativePath(relative))
                throw new ContractException("Evidence contains an unsafe path.");
            using FileStream stream = File.OpenRead(path);
            if (stream.Length > MaximumFileBytes) throw new ContractException("Evidence file exceeds size limit.");
            files.Add(new(relative, stream.Length, Convert.ToHexString(SHA256.HashData(stream)).ToLowerInvariant()));
            if (files.Count > MaximumFiles || files.Sum(f => f.Bytes) > MaximumTotalBytes)
                throw new ContractException("Evidence exceeds file count or total size limit.");
        }
        var manifest = new EvidenceManifest("1.0", identity, check, platform, conclusion,
            files.OrderBy(f => f.Path, StringComparer.Ordinal).ToArray());
        Contracts.Write(manifest, "evidence-manifest");
        if (files.Count == 0) throw new ContractException("No raw evidence files were found.");
        return manifest;
    }

    public void Verify(string directory, EvidenceIdentity expected)
    {
        Contracts.Write(this, "evidence-manifest");
        if (Identity != expected) throw new ContractException("Evidence identity does not match trusted workflow metadata.");
        if (Files.Select(f => f.Path).Distinct(StringComparer.OrdinalIgnoreCase).Count() != Files.Length)
            throw new ContractException("Evidence paths are duplicated.");
        var actual = Create(directory, Identity, Check, Platform, Conclusion);
        if (!Files.OrderBy(f => f.Path, StringComparer.Ordinal).SequenceEqual(actual.Files))
            throw new ContractException("Evidence files differ from their manifest.");
    }

    private static IEnumerable<string> EnumerateFiles(string root, int depth)
    {
        if (depth > 32) throw new ContractException("Evidence directory nesting exceeds the limit.");
        foreach (string path in Directory.EnumerateFileSystemEntries(root).Order(StringComparer.Ordinal))
        {
            RejectLink(path);
            if (Directory.Exists(path))
            {
                foreach (string child in EnumerateFiles(path, depth + 1)) yield return child;
            }
            else yield return path;
        }
    }

    private static void RejectLink(string path)
    {
        if ((File.GetAttributes(path) & FileAttributes.ReparsePoint) != 0)
            throw new ContractException("Evidence must not contain symbolic links or reparse points.");
    }
}

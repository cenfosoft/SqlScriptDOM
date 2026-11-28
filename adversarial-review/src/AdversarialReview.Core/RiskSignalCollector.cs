using System.Text.Json;
using System.Text.RegularExpressions;
using System.Xml;

namespace AdversarialReview;

public sealed record PullRequestFile(string Path, string Status, int Additions, int Deletions, string? Patch);

public sealed record RiskSignalResult(RiskSignals? Signals, string[] Missing);

// Derives risk signals for the admitted head SHA from trusted API data and verified CI evidence.
public static partial class RiskSignalCollector
{
    public const long MaximumCoverageBytes = 20 * 1024 * 1024;
    private static readonly string[] ExcludedPrefixes =
        ["docs/", "release-notes/", "Test/SqlDom/TestScripts/", "Test/SqlDom/Baselines"];

    public static PullRequestFile[] ParseFiles(IEnumerable<JsonElement> files) => files.Select(file => new PullRequestFile(
        file.GetProperty("filename").GetString()!, file.GetProperty("status").GetString()!,
        file.GetProperty("additions").GetInt32(), file.GetProperty("deletions").GetInt32(),
        file.TryGetProperty("patch", out JsonElement patch) && patch.ValueKind == JsonValueKind.String ? patch.GetString() : null))
        .ToArray();

    public static RiskSignalResult Collect(
        IReadOnlyList<PullRequestFile> files, AuditReport review, IReadOnlyDictionary<string, byte[]> evidenceFiles)
    {
        ArgumentNullException.ThrowIfNull(files);
        ArgumentNullException.ThrowIfNull(review);
        ArgumentNullException.ThrowIfNull(evidenceFiles);
        List<string> missing = [];
        if (files.Count == 0) missing.Add("The PR file list is empty.");
        if (files.Any(f => !Contracts.IsSafeRelativePath(f.Path) || f.Additions < 0 || f.Deletions < 0))
            throw new ContractException("PR file metadata is invalid.");

        PullRequestFile[] codeFiles = files.Where(IsCodeFile).ToArray();
        long added = codeFiles.Sum(f => (long)f.Additions);
        long deleted = codeFiles.Sum(f => (long)f.Deletions);
        if (added > int.MaxValue || deleted > int.MaxValue) missing.Add("Changed line counts exceed the supported range.");

        (int Low, int Medium, bool Blocking)? findings = CountFindings(review, missing);
        (int Covered, int Instrumentable)? coverage = MeasureCoverage(files, evidenceFiles, missing);
        int? warnings = CountWarnings(files, evidenceFiles, missing);

        if (missing.Count != 0) return new(null, missing.ToArray());
        return new(new((int)added, (int)deleted, coverage!.Value.Covered, coverage.Value.Instrumentable,
            findings!.Value.Low, findings.Value.Medium, warnings!.Value, HasIntegrationTests(files), findings.Value.Blocking), []);
    }

    internal static bool IsCodeFile(PullRequestFile file) =>
        !file.Path.EndsWith(".md", StringComparison.OrdinalIgnoreCase)
        && !ExcludedPrefixes.Any(prefix => file.Path.StartsWith(prefix, StringComparison.Ordinal));

    // A parser round-trip test counts only when both its script and a matching baseline are present in the PR.
    internal static bool HasIntegrationTests(IReadOnlyList<PullRequestFile> files)
    {
        PullRequestFile[] present = files.Where(f => f.Status != "removed").ToArray();
        HashSet<string> baselines = present
            .Where(f => f.Path.StartsWith("Test/SqlDom/Baselines", StringComparison.Ordinal) && f.Path.Count(c => c == '/') == 3)
            .Select(f => f.Path.Split('/')[^1]).ToHashSet(StringComparer.Ordinal);
        return present.Any(f => f.Path.StartsWith("Test/SqlDom/TestScripts/", StringComparison.Ordinal)
            && baselines.Contains(f.Path.Split('/')[^1]));
    }

    private static (int, int, bool)? CountFindings(AuditReport review, List<string> missing)
    {
        if (review.Rounds.Length == 0 || review.Rounds.Any(r => r.Outcome != "completed"))
        {
            missing.Add("The adversarial review did not complete its rounds; findings are unreliable.");
            return null;
        }
        Finding[] unresolved = review.Findings.Where(f => f.Disposition is "unreviewed" or "confirmed").ToArray();
        return (unresolved.Count(f => f.Severity == "low"), unresolved.Count(f => f.Severity == "medium"),
            unresolved.Any(f => f.Severity is "high" or "critical"));
    }

    private static (int, int)? MeasureCoverage(
        IReadOnlyList<PullRequestFile> files, IReadOnlyDictionary<string, byte[]> evidenceFiles, List<string> missing)
    {
        // With a TRX logger, vstest also copies the report under <deployment>/In/<host>/; ignore that copy.
        KeyValuePair<string, byte[]>[] reports = evidenceFiles
            .Where(f => f.Key.StartsWith("test-ubuntu/", StringComparison.Ordinal)
                && f.Key.EndsWith("/coverage.cobertura.xml", StringComparison.Ordinal)
                && !f.Key.Contains("/In/", StringComparison.Ordinal)).ToArray();
        if (reports.Length != 1)
        {
            missing.Add(reports.Length == 0 ? "The coverage report is missing." : "Multiple coverage reports are ambiguous.");
            return null;
        }
        Dictionary<string, Dictionary<int, int>> lines;
        try { lines = ParseCobertura(reports[0].Value); }
        catch (Exception error) when (error is XmlException or FormatException or OverflowException or ContractException)
        {
            missing.Add("The coverage report is malformed.");
            return null;
        }
        int covered = 0, instrumentable = 0;
        foreach (PullRequestFile file in files.Where(f => f.Status != "removed"))
        {
            Dictionary<int, int>[] matches = lines
                .Where(entry => entry.Key == file.Path || entry.Key.EndsWith("/" + file.Path, StringComparison.Ordinal))
                .Select(entry => entry.Value).ToArray();
            if (matches.Length == 0) continue;
            if (file.Patch is null)
            {
                missing.Add($"The patch for an instrumented file is unavailable: {file.Path}.");
                return null;
            }
            foreach (int line in AddedLines(file.Patch))
            {
                int? hits = matches.Where(m => m.ContainsKey(line)).Select(m => (int?)m[line]).Max();
                if (hits is null) continue;
                instrumentable++;
                if (hits > 0) covered++;
            }
        }
        return (covered, instrumentable);
    }

    internal static Dictionary<string, Dictionary<int, int>> ParseCobertura(byte[] xml)
    {
        if (xml.Length > MaximumCoverageBytes) throw new ContractException("Coverage report exceeds its byte ceiling.");
        var settings = new XmlReaderSettings
        {
            DtdProcessing = DtdProcessing.Prohibit, XmlResolver = null, MaxCharactersInDocument = MaximumCoverageBytes,
            IgnoreComments = true, IgnoreWhitespace = true
        };
        List<string> sources = [];
        Dictionary<string, Dictionary<int, int>> result = new(StringComparer.Ordinal);
        Dictionary<int, int>? current = null;
        using var stream = new MemoryStream(xml, writable: false);
        using XmlReader reader = XmlReader.Create(stream, settings);
        while (reader.Read())
        {
            if (reader.NodeType != XmlNodeType.Element) continue;
            switch (reader.Name)
            {
                case "source":
                    sources.Add(Normalize(reader.ReadElementContentAsString()).TrimEnd('/'));
                    break;
                case "class":
                    string filename = Normalize(reader.GetAttribute("filename") ?? throw new ContractException("Coverage class has no file."));
                    string[] paths = filename.StartsWith('/') || filename.Contains(':') || sources.Count == 0
                        ? [filename] : sources.Select(s => s + "/" + filename).ToArray();
                    // A class split across partial declarations shares one line map.
                    current = paths.Select(p => result.GetValueOrDefault(p)).FirstOrDefault(m => m is not null) ?? new();
                    foreach (string path in paths) result[path] = current;
                    break;
                case "method":
                    // Method-level lines repeat class-level lines; skip the subtree.
                    reader.Skip();
                    break;
                case "line" when current is not null:
                    int number = int.Parse(reader.GetAttribute("number")!, System.Globalization.CultureInfo.InvariantCulture);
                    int hits = (int)Math.Min(int.MaxValue,
                        long.Parse(reader.GetAttribute("hits")!, System.Globalization.CultureInfo.InvariantCulture));
                    if (number < 1 || hits < 0) throw new ContractException("Coverage line data is invalid.");
                    current[number] = current.TryGetValue(number, out int previous) ? Math.Max(previous, hits) : hits;
                    break;
            }
        }
        return result;
    }

    internal static IEnumerable<int> AddedLines(string patch)
    {
        int next = 0;
        bool inHunk = false;
        foreach (string line in patch.Split('\n'))
        {
            Match header = HunkHeader().Match(line);
            if (header.Success)
            {
                next = int.Parse(header.Groups[1].Value, System.Globalization.CultureInfo.InvariantCulture);
                inHunk = true;
                continue;
            }
            if (!inHunk || line.StartsWith('\\')) continue;
            if (line.StartsWith('+')) yield return next++;
            else if (!line.StartsWith('-')) next++;
        }
    }

    // Approximates "new" warnings as warnings reported in files the PR changed; see docs/jev-risk-assessment.md.
    private static int? CountWarnings(
        IReadOnlyList<PullRequestFile> files, IReadOnlyDictionary<string, byte[]> evidenceFiles, List<string> missing)
    {
        string[] changed = files.Where(f => f.Status != "removed").Select(f => f.Path).ToArray();
        bool InChanged(string path)
        {
            path = Normalize(path);
            if (path.StartsWith("./", StringComparison.Ordinal)) path = path[2..];
            return changed.Any(c => path == c || path.EndsWith("/" + c, StringComparison.Ordinal));
        }
        if (!evidenceFiles.TryGetValue("static-analysis/semgrep.sarif", out byte[]? sarif)
            || !evidenceFiles.TryGetValue("static-analysis/roslyn.log", out byte[]? roslyn))
        {
            missing.Add("Static-analysis reports are missing.");
            return null;
        }
        int semgrep = 0;
        try
        {
            using JsonDocument document = JsonDocument.Parse(sarif, new JsonDocumentOptions { MaxDepth = 64 });
            foreach (JsonElement run in document.RootElement.GetProperty("runs").EnumerateArray())
            {
                if (!run.TryGetProperty("results", out JsonElement results)) continue;
                foreach (JsonElement result in results.EnumerateArray())
                {
                    string? uri = result.GetProperty("locations")[0].GetProperty("physicalLocation")
                        .GetProperty("artifactLocation").GetProperty("uri").GetString();
                    if (uri is not null && InChanged(Uri.UnescapeDataString(uri))) semgrep++;
                }
            }
        }
        catch (Exception error) when (error is JsonException or KeyNotFoundException or InvalidOperationException or IndexOutOfRangeException)
        {
            missing.Add("The Semgrep report is malformed.");
            return null;
        }
        // MSBuild repeats each warning in its summary, so distinct locations are counted once.
        HashSet<string> roslynWarnings = new(StringComparer.Ordinal);
        foreach (string line in System.Text.Encoding.UTF8.GetString(roslyn).Split('\n'))
        {
            Match warning = RoslynWarning().Match(line.TrimEnd('\r'));
            if (warning.Success && InChanged(warning.Groups["path"].Value.Trim()))
                roslynWarnings.Add($"{Normalize(warning.Groups["path"].Value.Trim())}|{warning.Groups["position"].Value}|{warning.Groups["code"].Value}");
        }
        return semgrep + roslynWarnings.Count;
    }

    private static string Normalize(string path) => path.Replace('\\', '/');

    [GeneratedRegex(@"^@@ -\d+(?:,\d+)? \+(\d+)(?:,\d+)? @@", RegexOptions.CultureInvariant, 1000)]
    private static partial Regex HunkHeader();

    // Multi-node MSBuild prefixes log lines with "N>"; the summary repeats them without it.
    [GeneratedRegex(@"^\s*(?:\d+>)?(?<path>[^()]+)\((?<position>\d+,\d+(?:,\d+,\d+)?)\): warning (?<code>[A-Za-z]+\d+):", RegexOptions.CultureInvariant, 1000)]
    private static partial Regex RoslynWarning();
}

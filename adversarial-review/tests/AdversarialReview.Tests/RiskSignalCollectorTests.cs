using System.Text;
using Microsoft.VisualStudio.TestTools.UnitTesting;

namespace AdversarialReview.Tests;

[TestClass]
public sealed class RiskSignalCollectorTests
{
    private const string Cobertura = """
        <?xml version="1.0" encoding="utf-8"?>
        <coverage line-rate="0.5" version="1.9">
          <sources><source>/home/runner/work/SqlScriptDOM/SqlScriptDOM/SqlScriptDom/</source></sources>
          <packages><package name="ScriptDom"><classes>
            <class name="A" filename="Parser/A.cs">
              <methods><method name="M"><lines><line number="2" hits="0" /></lines></method></methods>
              <lines><line number="2" hits="3" /><line number="3" hits="0" /><line number="9" hits="1" /></lines>
            </class>
          </classes></package></packages>
        </coverage>
        """;

    private const string Sarif = """
        {"runs":[{"results":[
          {"ruleId":"r1","locations":[{"physicalLocation":{"artifactLocation":{"uri":"SqlScriptDom/Parser/A.cs"}}}]},
          {"ruleId":"r2","locations":[{"physicalLocation":{"artifactLocation":{"uri":"SqlScriptDom/Other.cs"}}}]}
        ]}]}
        """;

    private const string RoslynLog = """
        /home/runner/work/SqlScriptDOM/SqlScriptDOM/SqlScriptDom/Parser/A.cs(2,5): warning CA1822: Mark members as static [/x.csproj]
        /home/runner/work/SqlScriptDOM/SqlScriptDOM/SqlScriptDom/obj/Generated.cs(7,1): warning CS0168: unused [/x.csproj]
          /home/runner/work/SqlScriptDOM/SqlScriptDOM/SqlScriptDom/Parser/A.cs(2,5): warning CA1822: Mark members as static [/x.csproj]
        """;

    // Hunk adds new lines 2 and 3 (instrumentable), and line 5 (not instrumentable).
    private const string Patch = "@@ -1,2 +1,5 @@\n context\n+added two\n+added three\n context four\n+added five\n-removed";

    internal static PullRequestFile Code() => new("SqlScriptDom/Parser/A.cs", "modified", 3, 1, Patch);

    internal static Dictionary<string, byte[]> Evidence(bool coverage = true, bool analysis = true)
    {
        Dictionary<string, byte[]> files = new(StringComparer.Ordinal);
        if (coverage) files["test-ubuntu/guid/coverage.cobertura.xml"] = Encoding.UTF8.GetBytes(Cobertura);
        if (analysis)
        {
            files["static-analysis/semgrep.sarif"] = Encoding.UTF8.GetBytes(Sarif);
            files["static-analysis/roslyn.log"] = Encoding.UTF8.GetBytes(RoslynLog);
        }
        return files;
    }

    internal static Finding Finding(string id, string severity, string disposition = "unreviewed") =>
        Contracts.Read<Finding>(File.ReadAllText(Path.Combine(AppContext.BaseDirectory, "fixtures/finding.valid.json")), "finding")
            with { Id = id, Severity = severity, Disposition = disposition };

    internal static AuditReport Review(Finding[]? findings = null, string outcome = "completed") => AuditEngine.Create(
        FoundationTests.Run(), FoundationTests.Sha, FoundationTests.Checks(), findings ?? [],
        [FoundationTests.Round(1, "first") with { Outcome = outcome }], 1);

    [TestMethod]
    public void ChangedLinesExcludeDocsReleaseNotesAndSqlTestData()
    {
        PullRequestFile[] files =
        [
            Code(),
            new("docs/guide.md", "modified", 50, 5, "@@ -1 +1 @@\n+x"),
            new("README.md", "modified", 7, 0, null),
            new("release-notes/170/170.1.md", "added", 30, 0, null),
            new("Test/SqlDom/TestScripts/X170.sql", "added", 40, 0, null),
            new("Test/SqlDom/Baselines170/X170.sql", "added", 40, 0, null),
            new("Test/SqlDom/Only170SyntaxTests.cs", "modified", 2, 0, "@@ -1 +1,2 @@\n x\n+y")
        ];
        RiskSignals signals = RiskSignalCollector.Collect(files, Review(), Evidence()).Signals!;
        Assert.AreEqual(5, signals.AddedCodeLines);
        Assert.AreEqual(1, signals.DeletedCodeLines);
        Assert.IsTrue(signals.HasIntegrationTests);
    }

    [TestMethod]
    public void IntegrationTestsRequireAMatchingScriptAndBaseline()
    {
        Assert.IsFalse(RiskSignalCollector.HasIntegrationTests([new("Test/SqlDom/TestScripts/X170.sql", "added", 1, 0, null)]));
        Assert.IsFalse(RiskSignalCollector.HasIntegrationTests([
            new("Test/SqlDom/TestScripts/X170.sql", "added", 1, 0, null),
            new("Test/SqlDom/Baselines170/Y170.sql", "added", 1, 0, null)]));
        Assert.IsFalse(RiskSignalCollector.HasIntegrationTests([
            new("Test/SqlDom/TestScripts/X170.sql", "removed", 0, 1, null),
            new("Test/SqlDom/Baselines170/X170.sql", "removed", 0, 1, null)]));
        Assert.IsTrue(RiskSignalCollector.HasIntegrationTests([
            new("Test/SqlDom/TestScripts/X170.sql", "modified", 1, 0, null),
            new("Test/SqlDom/Baselines170/X170.sql", "added", 1, 0, null)]));
    }

    [TestMethod]
    public void AddedLinesFollowHunkHeadersAndIgnoreRemovedLines()
    {
        CollectionAssert.AreEqual(new[] { 2, 3, 5 }, RiskSignalCollector.AddedLines(Patch).ToArray());
        CollectionAssert.AreEqual(new[] { 10, 21 }, RiskSignalCollector.AddedLines(
            "@@ -8,2 +10,1 @@\n+a\n-b\n\\ No newline at end of file\n@@ -20 +20,2 @@\n x\n+y").ToArray());
    }

    [TestMethod]
    public void CoverageCountsOnlyInstrumentableAddedLinesUsingClassLevelHits()
    {
        RiskSignals signals = RiskSignalCollector.Collect([Code()], Review(), Evidence()).Signals!;
        // Line 2 is covered (class-level hits win over method-level), line 3 is not, line 5 is not instrumented.
        Assert.AreEqual(1, signals.CoveredAddedLines);
        Assert.AreEqual(2, signals.InstrumentableAddedLines);
        RiskSignals notApplicable = RiskSignalCollector.Collect(
            [new("adversarial-review/src/X.cs", "added", 2, 0, "@@ -0,0 +1,2 @@\n+a\n+b")], Review(), Evidence()).Signals!;
        Assert.AreEqual(0, notApplicable.InstrumentableAddedLines);
    }

    [TestMethod]
    public void WarningsCountChangedFilesOnlyAndFindingsSeparateBlockers()
    {
        RiskSignals signals = RiskSignalCollector.Collect([Code()], Review(
            [Finding("l1", "low"), Finding("l2", "low"), Finding("m1", "medium"), Finding("fp", "medium", "false-positive"),
             Finding("i1", "info"), Finding("h1", "high")]), Evidence()).Signals!;
        // One Semgrep result in A.cs plus one distinct Roslyn warning (summary repeat and generated file ignored).
        Assert.AreEqual(2, signals.NewStaticWarnings);
        Assert.AreEqual(2, signals.LowFindings);
        Assert.AreEqual(1, signals.MediumFindings);
        Assert.IsTrue(signals.HasUnresolvedHighOrCriticalFinding);
    }

    [TestMethod]
    public void MissingOrUnreliableInputsAreReportedInsteadOfDefaulted()
    {
        foreach (var (files, review, evidence, reason) in new (PullRequestFile[], AuditReport, Dictionary<string, byte[]>, string)[]
        {
            ([Code()], Review(), Evidence(coverage: false), "coverage report is missing"),
            ([Code()], Review(), Evidence(analysis: false), "Static-analysis reports"),
            ([Code() with { Patch = null }], Review(), Evidence(), "patch"),
            ([Code()], Review(outcome: "failed"), Evidence(), "did not complete"),
            ([Code()], Review(), new(Evidence()) { ["test-ubuntu/guid/coverage.cobertura.xml"] = Encoding.UTF8.GetBytes("<coverage><!DOCTYPE x>") }, "malformed")
        })
        {
            RiskSignalResult result = RiskSignalCollector.Collect(files, review, evidence);
            Assert.IsNull(result.Signals, reason);
            StringAssert.Contains(string.Join(" ", result.Missing), reason);
        }
    }
}

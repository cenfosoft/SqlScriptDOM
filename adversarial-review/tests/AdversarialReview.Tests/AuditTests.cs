using System.Text.Json;
using Microsoft.VisualStudio.TestTools.UnitTesting;

namespace AdversarialReview.Tests;

[TestClass]
public sealed class AuditTests
{
    private static Finding Fixture() => Contracts.Read<Finding>(
        File.ReadAllText(Path.Combine(AppContext.BaseDirectory, "fixtures/finding.valid.json")), "finding");

    private static AuditReport Report(Finding[]? findings = null) => AuditEngine.Create(
        FoundationTests.Run(), FoundationTests.Sha, FoundationTests.Checks(), findings ?? [],
        [FoundationTests.Round(1, "first")], 1);

    [TestMethod]
    public void CompleteAdvisoryReportNeverInventsNumericRisk()
    {
        Contracts.Read<AuditReport>(File.ReadAllText(Path.Combine(AppContext.BaseDirectory, "fixtures/audit.valid.json")), "audit-report");
        AuditReport report = Report();
        Assert.AreEqual("complete", report.Status);
        Assert.IsNull(report.RiskScore);
        Assert.AreEqual("report-only", report.Mode);
        string json = Contracts.Write(report, "audit-report");
        Assert.AreEqual(report.Status, Contracts.Read<AuditReport>(json, "audit-report").Status);
        StringAssert.Contains(ReportRenderer.Render(report), "Not a merge gate");
    }

    [TestMethod]
    public void MissingFailedAndStaleEvidenceCannotProduceClearance()
    {
        AuditReport missing = AuditEngine.Create(FoundationTests.Run(), FoundationTests.Sha, [], [], [], 3);
        Assert.AreEqual("incomplete", missing.Status);
        Assert.IsTrue(missing.Blockers.Length >= 7);
        AuditReport stale = AuditEngine.Create(FoundationTests.Run(), FoundationTests.OtherSha,
            FoundationTests.Checks(), [], [FoundationTests.Round(1, "first")], 1);
        Assert.AreEqual("stale", stale.Status);
        AuditReport failed = AuditEngine.Create(FoundationTests.Run(), FoundationTests.Sha,
            FoundationTests.Checks(), [], [FoundationTests.Round(1, "first") with { Outcome = "failed" }], 1);
        Assert.AreEqual("incomplete", failed.Status);
    }

    [TestMethod]
    public void HighSeverityFindingsEscalateWithoutInventingNumericRisk()
    {
        var report = Report([Fixture()]);
        Assert.AreEqual("incomplete", report.Status);
        Assert.IsTrue(AuditEngine.RequiresHumanReview(report));
        Assert.IsTrue(report.Blockers.Length >= 1);
        StringAssert.Contains(report.Blockers[0], "Unresolved high");
        Assert.IsNull(report.RiskScore);
        StringAssert.Contains(ReportRenderer.Render(report), "Human Review Required");
    }

    [TestMethod]
    public void CompleteStatusCannotHideUnresolvedHighRisk()
    {
        AuditReport report = Report([Fixture()]);
        Assert.ThrowsException<ContractException>(() =>
            Contracts.Write(report with { Status = "complete" }, "audit-report"));
    }

    [TestMethod]
    public void PreviouslyValidV10CompleteReportRemainsReadableAndEscalatesWhenRendered()
    {
        AuditReport legacy = Report([Fixture()]) with { Status = "complete", Blockers = [] };
        string json = JsonSerializer.Serialize(legacy, Contracts.JsonOptions);

        AuditReport read = Contracts.Read<AuditReport>(json, "audit-report");

        Assert.AreEqual("complete", read.Status);
        Assert.ThrowsException<ContractException>(() =>
            Contracts.Write(read, "audit-report"));
        StringAssert.Contains(ReportRenderer.Render(read), "Human Review Required");
    }

    [TestMethod]
    public void RejectsCrossShaDuplicateAndUnsafeEvidence()
    {
        Finding finding = Fixture();
        Assert.ThrowsException<ContractException>(() => Report([finding with { HeadSha = FoundationTests.OtherSha }]));
        Assert.ThrowsException<ContractException>(() => Report([finding, finding]));
        foreach (string path in new[] { "../secret", "/etc/passwd", "C:/secret", "a/../b", "a\\b", "a//b" })
            Assert.ThrowsException<ContractException>(() => Report([finding with
            { Evidence = [new("file", path, "unsafe", 1, 2)] }]));
        Assert.ThrowsException<ContractException>(() => Report([finding with
        { Evidence = [new("file", "path.cs", "bad range", 10, 2)] }]));
    }

    [TestMethod]
    public void RejectsForgedCompleteReportAndUnsupportedNumericScore()
    {
        AuditReport report = Report();
        Assert.ThrowsException<ContractException>(() =>
            Contracts.Write(report with { Rounds = [] }, "audit-report"));
        Assert.ThrowsException<ContractException>(() =>
            Contracts.Write(report with { RiskScore = 0 }, "audit-report"));
        Assert.ThrowsException<ContractException>(() =>
            Contracts.Write(report with { Checks = [] }, "audit-report"));
    }

    [TestMethod]
    public void OutputEscapesUntrustedMarkdownMentionsAndSecrets()
    {
        Finding fixture = Fixture() with
        {
            Title = "@allancascante <script> [click](https://evil.invalid)",
            Description = "secret-value ghp_12345678901234567890\n| forged table"
        };
        string output = ReportRenderer.Render(Report([fixture]), ["secret-value"]);
        Assert.IsFalse(output.Contains("@allancascante", StringComparison.Ordinal));
        Assert.IsFalse(output.Contains("<script>", StringComparison.Ordinal));
        Assert.IsFalse(output.Contains("[click]", StringComparison.Ordinal));
        Assert.IsFalse(output.Contains("secret-value", StringComparison.Ordinal));
        Assert.IsFalse(output.Contains("ghp_12345678901234567890", StringComparison.Ordinal));
        StringAssert.Contains(output, "REDACTED");
        StringAssert.StartsWith(output, ReportRenderer.Marker);
        string path = fixture.Evidence[0].Reference;
        string redactedLink = ReportRenderer.Render(Report([fixture]), [path]);
        Assert.IsFalse(redactedLink.Contains(path, StringComparison.Ordinal));
        Assert.IsFalse(redactedLink.Contains("/blob/", StringComparison.Ordinal));
    }

    [TestMethod]
    public void RenderingIsDeterministicAndBounded()
    {
        Finding fixture = Fixture();
        Finding[] findings = Enumerable.Range(0, 100).Select(n => fixture with
        { Id = $"finding-{n}", Description = new string('x', 1000) }).ToArray();
        string first = ReportRenderer.Render(Report(findings));
        string reordered = ReportRenderer.Render(Report(findings.Reverse().ToArray()));
        Assert.AreEqual(first, reordered);
        Assert.IsTrue(first.Length <= ReportRenderer.MaximumCommentCharacters);
        StringAssert.Contains(first, "Additional findings omitted");
    }
}

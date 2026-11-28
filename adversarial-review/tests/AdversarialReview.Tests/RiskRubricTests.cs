using Microsoft.VisualStudio.TestTools.UnitTesting;

namespace AdversarialReview.Tests;

[TestClass]
public sealed class RiskRubricTests
{
    private static RiskRubric Rubric() => RiskRubric.Parse(
        File.ReadAllText(Path.Combine(AppContext.BaseDirectory, "config/risk-rubric.json")));

    private static RiskSignals Signals() => new(0, 0, 100, 100, 0, 0, 0, false, false);

    [TestMethod]
    public void ChangedLinesCountAdditionsAndDeletionsAtBoundaries()
    {
        RiskRubric rubric = Rubric();
        foreach (var (added, deleted, expected) in new[]
        {
            (0, 0, "low"), (50, 50, "low"), (50, 51, "mid"),
            (500, 500, "mid"), (500, 501, "high")
        })
            Assert.AreEqual(expected, rubric.Classify(Signals() with
            { AddedCodeLines = added, DeletedCodeLines = deleted }).ChangedLines);
    }

    [TestMethod]
    public void CoverageDistinguishesThresholdsNotApplicableAndMissing()
    {
        RiskRubric rubric = Rubric();
        foreach (var (covered, total, expected) in new[]
        {
            (1000, 1000, "low"), (999, 1000, "mid"), (900, 1000, "mid"),
            (801, 1000, "mid"), (800, 1000, "high"), (0, 0, "not-applicable")
        })
            Assert.AreEqual(expected, rubric.Classify(Signals() with
            { CoveredAddedLines = covered, InstrumentableAddedLines = total }).Coverage);
        Assert.ThrowsException<ContractException>(() => rubric.Classify(Signals() with { CoveredAddedLines = null }));
        Assert.ThrowsException<ContractException>(() => rubric.Classify(Signals() with { CoveredAddedLines = 101 }));
    }

    [TestMethod]
    public void FindingAndWarningFactorsUseHighestApplicableClassification()
    {
        RiskRubric rubric = Rubric();
        RiskFactors low = rubric.Classify(Signals() with { LowFindings = 2, NewStaticWarnings = 1 });
        Assert.AreEqual("low", low.AdversarialFindings);
        Assert.AreEqual("low", low.StaticWarnings);
        RiskFactors mid = rubric.Classify(Signals() with
        { LowFindings = 3, MediumFindings = 1, NewStaticWarnings = 2 });
        Assert.AreEqual("mid", mid.AdversarialFindings);
        Assert.AreEqual("mid", mid.StaticWarnings);
        RiskFactors high = rubric.Classify(Signals() with
        { LowFindings = 3, MediumFindings = 2, NewStaticWarnings = 3, HasUnresolvedHighOrCriticalFinding = true });
        Assert.AreEqual("high", high.AdversarialFindings);
        Assert.AreEqual("high", high.StaticWarnings);
        Assert.IsTrue(high.HasUnresolvedHighOrCriticalFinding);
    }

    [TestMethod]
    public void ConfigurableBoundariesApplyAndInvalidInputsFailClosed()
    {
        RiskRubric rubric = Rubric() with { ChangedLinesLowMaximum = 50, CoverageHighMaximumPercent = 90 };
        RiskFactors factors = rubric.Classify(Signals() with
        { AddedCodeLines = 51, CoveredAddedLines = 90 });
        Assert.AreEqual("mid", factors.ChangedLines);
        Assert.AreEqual("high", factors.Coverage);
        Assert.ThrowsException<ContractException>(() => (rubric with
        { ChangedLinesMidMaximum = 50 }).Classify(Signals()));
        Assert.ThrowsException<ContractException>(() => rubric.Classify(Signals() with { LowFindings = -1 }));
    }
}

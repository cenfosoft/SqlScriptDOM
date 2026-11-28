namespace AdversarialReview;

public sealed record RiskRubric(
    string SchemaVersion, string Version,
    int ChangedLinesLowMaximum, int ChangedLinesMidMaximum,
    decimal CoverageLowMinimumPercent, decimal CoverageHighMaximumPercent,
    int LowFindingsLowMaximum, int MediumFindingsMidMaximum,
    int WarningsLowMaximum, int WarningsMidMaximum)
{
    public static RiskRubric Parse(string json)
    {
        RiskRubric rubric = Contracts.Read<RiskRubric>(json, "risk-rubric");
        rubric.Validate();
        return rubric;
    }

    public RiskFactors Classify(RiskSignals signals)
    {
        ArgumentNullException.ThrowIfNull(signals);
        Validate();
        if (signals.AddedCodeLines < 0 || signals.DeletedCodeLines < 0
            || signals.LowFindings < 0 || signals.MediumFindings < 0
            || signals.NewStaticWarnings < 0)
            throw new ContractException("Risk signal counts must not be negative.");
        if (signals.CoveredAddedLines is null || signals.InstrumentableAddedLines is null)
            throw new ContractException("Added-line coverage evidence is missing.");
        if (signals.CoveredAddedLines < 0 || signals.InstrumentableAddedLines < 0
            || signals.CoveredAddedLines > signals.InstrumentableAddedLines)
            throw new ContractException("Added-line coverage counts are invalid.");

        long changedLines = (long)signals.AddedCodeLines + signals.DeletedCodeLines;
        string changed = changedLines <= ChangedLinesLowMaximum ? "low" :
            changedLines <= ChangedLinesMidMaximum ? "mid" : "high";
        string coverage = signals.InstrumentableAddedLines == 0 ? "not-applicable" :
            ClassifyCoverage(100m * signals.CoveredAddedLines.Value / signals.InstrumentableAddedLines.Value);
        string findings = signals.MediumFindings > MediumFindingsMidMaximum ? "high" :
            signals.MediumFindings > 0 || signals.LowFindings > LowFindingsLowMaximum ? "mid" : "low";
        string warnings = signals.NewStaticWarnings <= WarningsLowMaximum ? "low" :
            signals.NewStaticWarnings <= WarningsMidMaximum ? "mid" : "high";

        return new(Version, changed, coverage, findings, warnings, signals.HasIntegrationTests,
            signals.HasUnresolvedHighOrCriticalFinding);
    }

    private string ClassifyCoverage(decimal percent) =>
        percent >= CoverageLowMinimumPercent ? "low" :
        percent <= CoverageHighMaximumPercent ? "high" : "mid";

    private void Validate()
    {
        if (SchemaVersion != "1.0" || string.IsNullOrWhiteSpace(Version)
            || Version.Length > 64 || Version.Any(c => !char.IsAsciiLetterOrDigit(c) && c is not ('.' or '_' or '-')))
            throw new ContractException("Risk rubric version is invalid.");
        if (ChangedLinesLowMaximum < 0 || ChangedLinesMidMaximum <= ChangedLinesLowMaximum
            || CoverageHighMaximumPercent < 0 || CoverageLowMinimumPercent > 100
            || CoverageHighMaximumPercent >= CoverageLowMinimumPercent
            || LowFindingsLowMaximum < 0 || MediumFindingsMidMaximum < 1
            || WarningsLowMaximum < 0 || WarningsMidMaximum <= WarningsLowMaximum)
            throw new ContractException("Risk rubric boundaries are invalid.");
    }
}

public sealed record RiskSignals(
    int AddedCodeLines, int DeletedCodeLines, int? CoveredAddedLines, int? InstrumentableAddedLines,
    int LowFindings, int MediumFindings, int NewStaticWarnings, bool? HasIntegrationTests,
    bool HasUnresolvedHighOrCriticalFinding);

public sealed record RiskFactors(
    string RubricVersion, string ChangedLines, string Coverage, string AdversarialFindings,
    string StaticWarnings, bool? HasIntegrationTests, bool HasUnresolvedHighOrCriticalFinding);

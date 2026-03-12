namespace AutomationTools.Coverage;

public sealed record CoverageFileInfo(string? Name, long? Size, string? ContentType);

public sealed record CoverageCalcRequest(
    string? TeamId,
    CoverageFileInfo? RequirementsPdf,
    string? RequirementsFigmaLink,
    string? AutomationReportLink,
    string? AutomationRepoLink,
    CoveragePlanSummary? CoverageData);

namespace AutomationTools.Coverage;

public sealed record CoverageAutomationReportSummary(
    int TotalTests,
    IReadOnlyList<CoverageAutomationTestCase> Tests);

public sealed record CoverageAutomationTestCase(
    string Title,
    string? FullName,
    string? Status,
    string? Uuid,
    IReadOnlyList<string> Steps);

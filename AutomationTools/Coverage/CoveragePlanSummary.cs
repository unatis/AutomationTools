namespace AutomationTools.Coverage;

public sealed record CoveragePlanSummary(
    int PlanId,
    int? SuiteId,
    int TotalTests,
    IReadOnlyList<CoverageSuiteSummary> Suites);

public sealed record CoverageSuiteSummary(
    int SuiteId,
    string Name,
    IReadOnlyList<CoverageTestCaseSummary> Tests);

public sealed record CoverageTestCaseSummary(
    int WorkItemId,
    string Title,
    string Steps);

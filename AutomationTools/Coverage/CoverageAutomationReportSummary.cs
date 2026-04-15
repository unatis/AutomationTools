using System.Text.Json.Serialization;

namespace AutomationTools.Coverage;

public sealed class CoverageAutomationReportSummary
{
    public int Version { get; set; } = 2;
    public string Source { get; set; } = "allure-zip";
    public int TotalTests { get; set; }
    public IReadOnlyList<CoverageAutomationTestCase> Tests { get; set; } = Array.Empty<CoverageAutomationTestCase>();
    public IReadOnlyList<CoverageAutomationCategoryIndex> CategoryIndex { get; set; } = Array.Empty<CoverageAutomationCategoryIndex>();

    // Older persisted reports included a fully duplicated category tree.
    // Ignore it on write, but allow old JSON files to deserialize cleanly.
    [JsonIgnore]
    public IReadOnlyList<CoverageAutomationCategory> Categories { get; set; } = Array.Empty<CoverageAutomationCategory>();
}

public sealed class CoverageAutomationTestCase
{
    public string Title { get; set; } = string.Empty;
    public string? FullName { get; set; }
    public string? Status { get; set; }
    public string? Uuid { get; set; }
    public IReadOnlyList<string> Steps { get; set; } = Array.Empty<string>();
    public string CategoryPath { get; set; } = "Uncategorized";
    public IReadOnlyList<string> CategoryParts { get; set; } = Array.Empty<string>();
    public string EmbeddingText { get; set; } = string.Empty;
}

public sealed class CoverageAutomationCategoryIndex
{
    public string Name { get; set; } = string.Empty;
    public int Count { get; set; }
}

public sealed class CoverageAutomationCategory
{
    public string Name { get; set; } = string.Empty;
    public IReadOnlyList<CoverageAutomationTestCase> Tests { get; set; } = Array.Empty<CoverageAutomationTestCase>();
}

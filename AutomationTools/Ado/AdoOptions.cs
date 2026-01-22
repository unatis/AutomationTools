namespace AutomationTools.Ado;

public sealed class AdoOptions
{
    public const string SectionName = "Ado";

    public string BaseProjectUrl { get; set; } = "https://dev.azure.com/InfraEdgeSys/InfraEdge";

    // Work Item Tracking API version (create/update Test Case)
    public string WitApiVersion { get; set; } = "7.1";

    // Test Plan API version (add Test Case to suite)
    public string TestPlanApiVersion { get; set; } = "7.1-preview.1";

    // Test API version (fallback for suite operations on some orgs/projects)
    public string TestApiVersion { get; set; } = "7.1-preview.1";

    // Backward-compat (if someone already configured ApiVersion, we'll use it only when the specific versions are not set)
    public string? ApiVersion { get; set; }
    public string ProjectName { get; set; } = "InfraEdge";

    // Where new/updated Test Cases should live
    public string AreaPath { get; set; } = @"InfraEdge\InfraEdge\WeTryHarder";
    public string IterationPath { get; set; } = "InfraEdge";

    public int PlanId { get; set; } = 11937;
    public int SuiteId { get; set; } = 12115;

    /// <summary>
    /// Personal Access Token (DO NOT store in appsettings.json). Prefer env var ADO_PAT.
    /// </summary>
    public string? Pat { get; set; }
}



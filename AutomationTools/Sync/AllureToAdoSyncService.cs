using System.Net;
using AutomationTools.Ado;
using AutomationTools.Allure;

namespace AutomationTools.Sync;

public sealed class AllureToAdoSyncService
{
    private readonly AdoClient _ado;
    private readonly AdoOptions _adoOptions;

    public AllureToAdoSyncService(AdoClient ado, AdoOptions adoOptions)
    {
        _ado = ado;
        _adoOptions = adoOptions;
    }

    public async Task<SyncSummary> SyncFromAllureResults(
        IReadOnlyList<AllureResult> allureResults,
        int? planId,
        int? suiteId,
        int? configurationId,
        ApiVersionOverrides? apiVersions,
        CancellationToken ct)
    {
        var effectivePlanId = planId is > 0 ? planId.Value : _adoOptions.PlanId;
        var effectiveSuiteId = suiteId is > 0 ? suiteId.Value : _adoOptions.SuiteId;

        var summary = new SyncSummary("(allure results)", effectivePlanId, effectiveSuiteId);

        foreach (var allure in allureResults)
        {
            ct.ThrowIfCancellationRequested();
            var src = allure.Uuid ?? allure.Name ?? "(allure item)";
            await SyncOne(allure, source: src, effectivePlanId, effectiveSuiteId, configurationId, apiVersions, summary, ct);
        }

        return summary;
    }

    private async Task SyncOne(
        AllureResult allure,
        string source,
        int effectivePlanId,
        int effectiveSuiteId,
        int? configurationId,
        ApiVersionOverrides? apiVersions,
        SyncSummary summary,
        CancellationToken ct)
    {
        // Always create/update a Test Case, even if there is no "1234_" prefix.
        // If there IS an ID prefix, try to update by that ID; if not found (or wrong type), create new.
        var title = allure.Name?.Trim();
        if (string.IsNullOrWhiteSpace(title))
            title = allure.FullName?.Trim();

        if (string.IsNullOrWhiteSpace(title))
        {
            summary.Errors.Add(new SyncError(source, "Missing test name (both name and fullName are empty)."));
            return;
        }

        var hasId = AllureParsing.TryParseTestCaseIdAndTitle(allure, out var parsedId, out _);
        var descriptionHtml = BuildDescriptionHtml(allure);
        var flatSteps = AllureParsing.FlattenSteps(allure.Steps).Select(s => s.Action);
        var stepsXml = StepsXmlBuilder.BuildStepsXml(flatSteps);

        int testCaseIdToUpdateOrCreate;
        bool created;
        bool addedToSuite = false;

        try
        {
            if (hasId)
            {
                var wi = await _ado.TryGetWorkItemById(parsedId, ct, apiVersions?.WitApiVersion);
                if (wi is not null && string.Equals(wi.WorkItemType, "Test Case", StringComparison.OrdinalIgnoreCase))
                {
                    await _ado.UpdateTestCase(wi.Id, descriptionHtml, stepsXml, ct, apiVersions?.WitApiVersion);
                    testCaseIdToUpdateOrCreate = wi.Id;
                    created = false;
                }
                else
                {
                    testCaseIdToUpdateOrCreate = await _ado.CreateTestCase(title, descriptionHtml, stepsXml, ct, apiVersions?.WitApiVersion);
                    created = true;
                }
            }
            else
            {
                testCaseIdToUpdateOrCreate = await _ado.CreateTestCase(title, descriptionHtml, stepsXml, ct, apiVersions?.WitApiVersion);
                created = true;
            }

            // Track create/update regardless of whether suite-add succeeds.
            if (created) summary.Created++;
            else summary.Updated++;

            try
            {
                await _ado.AddTestCaseToSuite(
                    planId: effectivePlanId,
                    suiteId: effectiveSuiteId,
                    testCaseId: testCaseIdToUpdateOrCreate,
                    ct: ct,
                    testPlanApiVersionOverride: apiVersions?.TestPlanApiVersion,
                    configurationId: configurationId);
                addedToSuite = true;
            }
            catch (Exception ex)
            {
                // Keep the created/updated item in the summary, but record the suite-add failure.
                summary.Errors.Add(new SyncError(
                    source,
                    $"Failed to add Test Case {testCaseIdToUpdateOrCreate} to suite {effectiveSuiteId} (plan {effectivePlanId}): {ex.Message}"));
                addedToSuite = false;
            }

            summary.Items.Add(new SyncItemResult(
                Source: source,
                Title: title,
                WorkItemId: testCaseIdToUpdateOrCreate,
                Action: created ? SyncAction.Created : SyncAction.Updated,
                AddedToSuite: addedToSuite));
        }
        catch (HttpRequestException ex) when (ex.StatusCode == HttpStatusCode.Unauthorized || ex.StatusCode == HttpStatusCode.Forbidden)
        {
            summary.Errors.Add(new SyncError(source, "ADO auth failed (check PAT permissions / ADO_PAT env var)."));
        }
        catch (Exception ex)
        {
            summary.Errors.Add(new SyncError(source, ex.Message));
        }
    }

    private static string BuildDescriptionHtml(AllureResult allure)
    {
        var desc = allure.Description ?? string.Empty;
        var fullName = allure.FullName ?? string.Empty;

        static string H(string s) => WebUtility.HtmlEncode(s).Replace("\r\n", "\n").Replace("\n", "<br/>");

        var parts = new List<string>();
        if (!string.IsNullOrWhiteSpace(desc))
            parts.Add($"<div><b>Description</b><br/>{H(desc)}</div>");
        if (!string.IsNullOrWhiteSpace(fullName))
            parts.Add($"<div><b>FullName</b><br/>{H(fullName)}</div>");

        if (parts.Count == 0)
            return "<div></div>";

        return string.Join("<hr/>", parts);
    }
}

public sealed record SyncSummary(string AllureResultsPath, int PlanId, int SuiteId)
{
    public int Created { get; set; }
    public int Updated { get; set; }
    public int Skipped { get; set; }

    public List<SyncItemResult> Items { get; } = new();
    public List<SyncError> Errors { get; } = new();
}

public enum SyncAction
{
    Created,
    Updated
}

public sealed record SyncItemResult(
    string Source,
    string Title,
    int WorkItemId,
    SyncAction Action,
    bool AddedToSuite);

public sealed record SyncError(string Source, string Message);

public sealed record ApiVersionOverrides(
    string? WitApiVersion,
    string? TestPlanApiVersion,
    string? TestApiVersion);



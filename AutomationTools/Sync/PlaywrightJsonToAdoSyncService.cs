using System.Net;
using System.Text.Json;
using AutomationTools.Ado;
using AutomationTools.Playwright;

namespace AutomationTools.Sync;

public sealed class PlaywrightJsonToAdoSyncService
{
    private readonly AdoClient _ado;
    private readonly AdoOptions _adoOptions;

    private static readonly JsonSerializerOptions JsonOptions = new()
    {
        PropertyNameCaseInsensitive = true
    };

    public PlaywrightJsonToAdoSyncService(AdoClient ado, AdoOptions adoOptions)
    {
        _ado = ado;
        _adoOptions = adoOptions;
    }

    public PlaywrightReport ParseReportJson(string json)
    {
        var report = JsonSerializer.Deserialize<PlaywrightReport>(json, JsonOptions);
        return report ?? throw new InvalidOperationException("Failed to parse Playwright JSON report.");
    }

    public async Task<SyncSummary> SyncFromPlaywrightReport(
        PlaywrightReport report,
        int? planId,
        int? suiteId,
        int? configurationId,
        ApiVersionOverrides? apiVersions,
        CancellationToken ct)
    {
        var effectivePlanId = planId is > 0 ? planId.Value : _adoOptions.PlanId;
        var effectiveSuiteId = suiteId is > 0 ? suiteId.Value : _adoOptions.SuiteId;

        var summary = new SyncSummary("(playwright json)", effectivePlanId, effectiveSuiteId);

        foreach (var (spec, suitePath) in PlaywrightJsonParsing.TraverseSpecs(report.Suites))
        {
            if (spec.Tests is null || spec.Tests.Count == 0)
                continue;

            foreach (var test in spec.Tests)
            {
                ct.ThrowIfCancellationRequested();

                var result = test.Results?.LastOrDefault();
                if (result is null)
                {
                    summary.Errors.Add(new SyncError(spec.File ?? spec.Title ?? "(spec)", "No test results found."));
                    continue;
                }

                var title = BuildTitle(spec, suitePath);
                if (string.IsNullOrWhiteSpace(title))
                {
                    summary.Errors.Add(new SyncError(spec.File ?? "(spec)", "Missing test title."));
                    continue;
                }

                var descriptionHtml = BuildDescriptionHtml(spec, test, result);
                var flatSteps = PlaywrightJsonParsing.FlattenSteps(result.Steps);
                if (flatSteps.Count == 0)
                    flatSteps = new List<string> { title };

                var stepsXml = StepsXmlBuilder.BuildStepsXml(flatSteps);

                await SyncOne(
                    title,
                    descriptionHtml,
                    stepsXml,
                    source: spec.File ?? title,
                    effectivePlanId,
                    effectiveSuiteId,
                    configurationId,
                    apiVersions,
                    summary,
                    ct);
            }
        }

        return summary;
    }

    private static string BuildTitle(PlaywrightSpec spec, string? suitePath)
    {
        var baseTitle = spec.Title?.Trim();
        if (string.IsNullOrWhiteSpace(baseTitle))
            return string.Empty;

        if (string.IsNullOrWhiteSpace(suitePath))
            return baseTitle;

        return $"{suitePath} — {baseTitle}";
    }

    private static string BuildDescriptionHtml(PlaywrightSpec spec, PlaywrightTest test, PlaywrightResult result)
    {
        static string H(string s) => WebUtility.HtmlEncode(s).Replace("\r\n", "\n").Replace("\n", "<br/>");

        var parts = new List<string>();

        if (!string.IsNullOrWhiteSpace(spec.File))
            parts.Add($"<div><b>File</b><br/>{H(spec.File)}:{spec.Line}:{spec.Column}</div>");

        if (!string.IsNullOrWhiteSpace(test.ProjectName))
            parts.Add($"<div><b>Project</b><br/>{H(test.ProjectName)}</div>");

        if (!string.IsNullOrWhiteSpace(result.Status))
            parts.Add($"<div><b>Status</b><br/>{H(result.Status)}</div>");

        if (result.Duration > 0)
            parts.Add($"<div><b>Duration</b><br/>{result.Duration} ms</div>");

        var msg = result.Error?.Message;
        if (!string.IsNullOrWhiteSpace(msg))
        {
            msg = PlaywrightJsonParsing.StripAnsi(msg);
            parts.Add($"<div><b>Error</b><br/>{H(msg)}</div>");
        }

        if (parts.Count == 0)
            return "<div></div>";

        return string.Join("<hr/>", parts);
    }

    private async Task SyncOne(
        string title,
        string descriptionHtml,
        string stepsXml,
        string source,
        int effectivePlanId,
        int effectiveSuiteId,
        int? configurationId,
        ApiVersionOverrides? apiVersions,
        SyncSummary summary,
        CancellationToken ct)
    {
        var hasId = PlaywrightJsonParsing.TryParseTestCaseIdAndTitle(title, out var parsedId, out var cleanTitle);
        if (hasId)
            title = cleanTitle;

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
}

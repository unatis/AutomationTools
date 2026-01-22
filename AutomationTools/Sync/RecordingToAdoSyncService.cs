using System.Net;
using System.Text;
using System.Text.Json;
using System.Text.RegularExpressions;
using AutomationTools.Ado;
using AutomationTools.Recording;

namespace AutomationTools.Sync;

public sealed class RecordingToAdoSyncService
{
    private static readonly Regex NameWithIdRegex = new(@"^(?<id>\d+?)_(?<title>.+)$", RegexOptions.Compiled);

    private readonly AdoClient _ado;
    private readonly AdoOptions _adoOptions;

    public RecordingToAdoSyncService(AdoClient ado, AdoOptions adoOptions)
    {
        _ado = ado;
        _adoOptions = adoOptions;
    }

    public async Task<SyncSummary> SyncFromRecording(
        RecordingPayload payload,
        int? planId,
        int? suiteId,
        int? configurationId,
        ApiVersionOverrides? apiVersions,
        CancellationToken ct)
    {
        var effectivePlanId = planId is > 0 ? planId.Value : _adoOptions.PlanId;
        var effectiveSuiteId = suiteId is > 0 ? suiteId.Value : _adoOptions.SuiteId;

        var summary = new SyncSummary("(recording)", effectivePlanId, effectiveSuiteId);

        var test = payload.Test;
        if (test is null)
        {
            summary.Errors.Add(new SyncError("(recording)", "Missing 'test' object."));
            return summary;
        }

        var source = test.Id ?? "(recording)";
        var title = (test.Name ?? string.Empty).Trim();
        if (string.IsNullOrWhiteSpace(title))
        {
            summary.Errors.Add(new SyncError(source, "Missing test name (test.name is empty)."));
            return summary;
        }

        var stepsText = MapStepsToActionLines(test.Steps);
        if (stepsText.Count == 0)
        {
            summary.Errors.Add(new SyncError(source, "Recording has no steps (test.steps is empty)."));
            return summary;
        }

        var descriptionHtml = BuildDescriptionHtml(payload, test);
        var stepsXml = StepsXmlBuilder.BuildStepsXml(stepsText);

        int testCaseId;
        bool created;

        try
        {
            // Optional update mode: if title starts with "12345_", try to update that Test Case ID.
            if (TryParseIdPrefix(title, out var parsedId, out var cleanedTitle))
            {
                var wi = await _ado.TryGetWorkItemById(parsedId, ct, apiVersions?.WitApiVersion);
                if (wi is not null && string.Equals(wi.WorkItemType, "Test Case", StringComparison.OrdinalIgnoreCase))
                {
                    await _ado.UpdateTestCase(wi.Id, descriptionHtml, stepsXml, ct, apiVersions?.WitApiVersion);
                    testCaseId = wi.Id;
                    created = false;
                }
                else
                {
                    testCaseId = await _ado.CreateTestCase(cleanedTitle, descriptionHtml, stepsXml, ct, apiVersions?.WitApiVersion);
                    created = true;
                }
            }
            else
            {
                testCaseId = await _ado.CreateTestCase(title, descriptionHtml, stepsXml, ct, apiVersions?.WitApiVersion);
                created = true;
            }

            if (created) summary.Created++;
            else summary.Updated++;

            bool addedToSuite;
            try
            {
                await _ado.AddTestCaseToSuite(
                    planId: effectivePlanId,
                    suiteId: effectiveSuiteId,
                    testCaseId: testCaseId,
                    ct: ct,
                    testPlanApiVersionOverride: apiVersions?.TestPlanApiVersion,
                    configurationId: configurationId);
                addedToSuite = true;
            }
            catch (Exception ex)
            {
                summary.Errors.Add(new SyncError(
                    source,
                    $"Failed to add Test Case {testCaseId} to suite {effectiveSuiteId} (plan {effectivePlanId}): {ex.Message}"));
                addedToSuite = false;
            }

            summary.Items.Add(new SyncItemResult(
                Source: source,
                Title: title,
                WorkItemId: testCaseId,
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

        return summary;
    }

    private static bool TryParseIdPrefix(string name, out int id, out string cleanedTitle)
    {
        id = 0;
        cleanedTitle = name;
        var m = NameWithIdRegex.Match(name.Trim());
        if (!m.Success)
            return false;

        if (!int.TryParse(m.Groups["id"].Value, out id))
            return false;

        cleanedTitle = m.Groups["title"].Value.Trim();
        if (string.IsNullOrWhiteSpace(cleanedTitle))
            cleanedTitle = name.Trim();

        return true;
    }

    private static List<string> MapStepsToActionLines(List<RecordingStep>? steps)
    {
        if (steps is null || steps.Count == 0)
            return new List<string>();

        static string S(string? v) => (v ?? string.Empty).Trim();

        static string BestLocator(RecordingLocators? loc)
        {
            if (loc is null) return string.Empty;
            if (!string.IsNullOrWhiteSpace(loc.Css)) return $"css={loc.Css}";
            if (!string.IsNullOrWhiteSpace(loc.Aria)) return $"aria={loc.Aria}";
            if (!string.IsNullOrWhiteSpace(loc.XPath)) return $"xpath={loc.XPath}";
            return string.Empty;
        }

        static (string accName, string text, string tag, string labelText) ReadMeta(JsonElement? meta)
        {
            if (meta is null) return (string.Empty, string.Empty, string.Empty, string.Empty);
            if (meta.Value.ValueKind != JsonValueKind.Object) return (string.Empty, string.Empty, string.Empty, string.Empty);

            string Get(string prop)
            {
                if (!meta.Value.TryGetProperty(prop, out var el)) return string.Empty;
                return el.ValueKind == JsonValueKind.String ? (el.GetString() ?? string.Empty) : string.Empty;
            }

            return (Get("accName").Trim(), Get("text").Trim(), Get("tag").Trim(), Get("labelText").Trim());
        }

        static bool ReadMetaModifier(JsonElement? meta)
        {
            if (meta is null) return false;
            return meta.Value.ValueKind switch
            {
                JsonValueKind.True => true,
                JsonValueKind.False => false,
                _ => false
            };
        }

        var lines = new List<string>(steps.Count);

        for (int i = 0; i < steps.Count; i++)
        {
            var st = steps[i];
            var type = S(st.Type).ToLowerInvariant();
            var url = S(st.Url);
            var locator = BestLocator(st.Locators);

            var (accName, text, tag, labelText) = ReadMeta(st.Meta);
            var target = !string.IsNullOrWhiteSpace(accName) ? accName
                : !string.IsNullOrWhiteSpace(labelText) ? labelText
                : !string.IsNullOrWhiteSpace(text) ? text
                : !string.IsNullOrWhiteSpace(tag) ? tag
                : "(element)";

            var sb = new StringBuilder();

            switch (type)
            {
                case "navigation":
                    sb.Append("Navigate: ").Append(url);
                    break;
                case "click":
                    sb.Append("Click: ").Append(target);
                    break;
                case "input":
                    sb.Append("Input: ").Append(target);
                    if (!string.IsNullOrWhiteSpace(st.Value))
                        sb.Append(" = \"").Append(st.Value).Append('"');
                    if (!string.IsNullOrWhiteSpace(st.CommittedBy))
                        sb.Append(" (committedBy=").Append(st.CommittedBy).Append(')');
                    break;
                case "select":
                    sb.Append("Select: ").Append(target);
                    if (!string.IsNullOrWhiteSpace(st.Value))
                        sb.Append(" value=\"").Append(st.Value).Append('"');
                    if (!string.IsNullOrWhiteSpace(st.OptionText))
                        sb.Append(" option=\"").Append(st.OptionText).Append('"');
                    break;
                case "scroll":
                    sb.Append("Scroll: x=").Append(st.X?.ToString() ?? "0")
                        .Append(" y=").Append(st.Y?.ToString() ?? "0");
                    break;
                case "keydown":
                    sb.Append("KeyDown: ").Append(S(st.Key));
                    sb.Append(" ctrl=").Append(st.Ctrl is true);
                    sb.Append(" meta=").Append(ReadMetaModifier(st.Meta));
                    sb.Append(" alt=").Append(st.Alt is true);
                    sb.Append(" shift=").Append(st.Shift is true);
                    break;
                default:
                    sb.Append("Step: ").Append(type);
                    break;
            }

            if (!string.IsNullOrWhiteSpace(locator))
                sb.Append(" | ").Append(locator);
            if (!string.IsNullOrWhiteSpace(url) && type != "navigation")
                sb.Append(" | url=").Append(url);

            lines.Add(sb.ToString());
        }

        return lines;
    }

    private static string BuildDescriptionHtml(RecordingPayload payload, RecordingTest test)
    {
        static string H(string s) => WebUtility.HtmlEncode(s).Replace("\r\n", "\n").Replace("\n", "<br/>");

        var parts = new List<string>();

        if (!string.IsNullOrWhiteSpace(test.StartUrl))
            parts.Add($"<div><b>StartUrl</b><br/>{H(test.StartUrl!)}</div>");
        if (!string.IsNullOrWhiteSpace(test.Id))
            parts.Add($"<div><b>RecordingId</b><br/>{H(test.Id!)}</div>");
        if (payload.ExportedAt is > 0)
            parts.Add($"<div><b>ExportedAt</b><br/>{H(payload.ExportedAt.Value.ToString())}</div>");

        if (parts.Count == 0)
            return "<div></div>";

        return string.Join("<hr/>", parts);
    }
}



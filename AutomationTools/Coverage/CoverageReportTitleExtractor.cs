using System.Text.Json;

namespace AutomationTools.Coverage;

public static class CoverageReportTitleExtractor
{
    public static IReadOnlyList<string> ExtractTitles(string reportJson)
    {
        if (string.IsNullOrWhiteSpace(reportJson))
            return Array.Empty<string>();

        using var doc = JsonDocument.Parse(reportJson);
        var root = doc.RootElement;

        if (root.ValueKind == JsonValueKind.Array)
            return ExtractAllureTitlesFromArray(root);

        if (root.ValueKind != JsonValueKind.Object)
            return Array.Empty<string>();

        if (root.TryGetProperty("suites", out var suitesEl) && suitesEl.ValueKind == JsonValueKind.Array)
            return ExtractPlaywrightTitlesFromSuites(suitesEl);

        if (root.TryGetProperty("results", out var resultsEl) && resultsEl.ValueKind == JsonValueKind.Array)
            return ExtractAllureTitlesFromArray(resultsEl);

        return Array.Empty<string>();
    }

    private static IReadOnlyList<string> ExtractAllureTitlesFromArray(JsonElement arrayEl)
    {
        var titles = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
        foreach (var item in arrayEl.EnumerateArray())
        {
            if (item.ValueKind != JsonValueKind.Object) continue;

            if (TryGetString(item, "name", out var name))
                titles.Add(name);
            else if (TryGetString(item, "fullName", out var full))
                titles.Add(full);
        }

        return titles.ToList();
    }

    private static IReadOnlyList<string> ExtractPlaywrightTitlesFromSuites(JsonElement suitesEl)
    {
        var titles = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
        foreach (var suite in suitesEl.EnumerateArray())
            ExtractPlaywrightTitlesFromSuite(suite, titles);

        return titles.ToList();
    }

    private static void ExtractPlaywrightTitlesFromSuite(JsonElement suiteEl, HashSet<string> titles)
    {
        if (suiteEl.ValueKind != JsonValueKind.Object) return;

        if (suiteEl.TryGetProperty("specs", out var specsEl) && specsEl.ValueKind == JsonValueKind.Array)
        {
            foreach (var spec in specsEl.EnumerateArray())
            {
                if (spec.ValueKind != JsonValueKind.Object) continue;
                if (TryGetString(spec, "title", out var title))
                    titles.Add(title);
            }
        }

        if (suiteEl.TryGetProperty("suites", out var suitesEl) && suitesEl.ValueKind == JsonValueKind.Array)
        {
            foreach (var child in suitesEl.EnumerateArray())
                ExtractPlaywrightTitlesFromSuite(child, titles);
        }
    }

    private static bool TryGetString(JsonElement obj, string prop, out string value)
    {
        value = string.Empty;
        if (!obj.TryGetProperty(prop, out var el) || el.ValueKind != JsonValueKind.String)
            return false;

        value = el.GetString() ?? string.Empty;
        return !string.IsNullOrWhiteSpace(value);
    }
}

using System.Text.RegularExpressions;

namespace AutomationTools.Playwright;

public static class PlaywrightJsonParsing
{
    private static readonly Regex NameWithIdRegex = new(@"^(?<id>\d+?)_(?<title>.+)$", RegexOptions.Compiled);
    private static readonly Regex AnsiRegex = new(@"\x1B\[[0-?]*[ -/]*[@-~]", RegexOptions.Compiled);

    public static IEnumerable<(PlaywrightSpec Spec, string? SuitePath)> TraverseSpecs(IEnumerable<PlaywrightSuite> suites)
    {
        foreach (var item in TraverseSpecsInternal(suites, prefix: null))
            yield return item;
    }

    private static IEnumerable<(PlaywrightSpec Spec, string? SuitePath)> TraverseSpecsInternal(
        IEnumerable<PlaywrightSuite> suites,
        string? prefix)
    {
        foreach (var suite in suites)
        {
            var current = string.IsNullOrWhiteSpace(suite.Title)
                ? prefix
                : string.IsNullOrWhiteSpace(prefix)
                    ? suite.Title
                    : $"{prefix} / {suite.Title}";

            if (suite.Specs is { Count: > 0 })
            {
                foreach (var spec in suite.Specs)
                    yield return (spec, current);
            }

            if (suite.Suites is { Count: > 0 })
            {
                foreach (var item in TraverseSpecsInternal(suite.Suites, current))
                    yield return item;
            }
        }
    }

    public static IReadOnlyList<string> FlattenSteps(IEnumerable<PlaywrightStep>? steps)
    {
        var result = new List<string>();
        if (steps is null) return result;

        foreach (var step in steps)
            FlattenStepInner(step, depth: 0, result);

        return result;
    }

    private static void FlattenStepInner(PlaywrightStep step, int depth, List<string> output)
    {
        var name = step.Title?.Trim();
        if (string.IsNullOrWhiteSpace(name))
            name = "(unnamed step)";

        if (depth > 0)
            name = new string(' ', depth * 2) + "- " + name;

        output.Add(name);

        if (step.Steps is null || step.Steps.Count == 0)
            return;

        foreach (var child in step.Steps)
            FlattenStepInner(child, depth + 1, output);
    }

    public static bool TryParseTestCaseIdAndTitle(string? title, out int testCaseId, out string cleanTitle)
    {
        testCaseId = 0;
        cleanTitle = string.Empty;

        var name = title?.Trim();
        if (string.IsNullOrWhiteSpace(name))
            return false;

        var m = NameWithIdRegex.Match(name);
        if (!m.Success)
            return false;

        if (!int.TryParse(m.Groups["id"].Value, out testCaseId))
            return false;

        cleanTitle = m.Groups["title"].Value.Trim();
        if (string.IsNullOrWhiteSpace(cleanTitle))
            cleanTitle = name;

        return true;
    }

    public static string StripAnsi(string input)
    {
        if (string.IsNullOrEmpty(input))
            return input ?? string.Empty;

        return AnsiRegex.Replace(input, string.Empty);
    }
}

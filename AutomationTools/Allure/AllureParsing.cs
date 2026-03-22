using System.Text;
using System.Text.Json;
using System.Text.RegularExpressions;

namespace AutomationTools.Allure;

public static class AllureParsing
{
    private static readonly JsonSerializerOptions JsonOptions = new()
    {
        PropertyNameCaseInsensitive = true
    };

    private static readonly Regex NameWithIdRegex = new(@"^(?<id>\d+?)_(?<title>.+)$", RegexOptions.Compiled);

    public static AllureResult ParseResultJson(string json)
    {
        var result = JsonSerializer.Deserialize<AllureResult>(json, JsonOptions);
        return result ?? throw new InvalidOperationException("Failed to parse Allure JSON.");
    }

    public static IReadOnlyList<string> ExtractTitles(IEnumerable<AllureResult> results)
    {
        var titles = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
        foreach (var result in results)
        {
            var name = result.Name?.Trim();
            if (!string.IsNullOrWhiteSpace(name))
            {
                titles.Add(name);
                continue;
            }

            var fullName = result.FullName?.Trim();
            if (!string.IsNullOrWhiteSpace(fullName))
                titles.Add(fullName);
        }

        return titles.ToList();
    }

    public static bool TryParseTestCaseIdAndTitle(AllureResult result, out int testCaseId, out string title)
    {
        testCaseId = 0;
        title = string.Empty;

        var name = result.Name?.Trim();
        if (string.IsNullOrWhiteSpace(name))
            return false;

        var m = NameWithIdRegex.Match(name);
        if (!m.Success)
            return false;

        if (!int.TryParse(m.Groups["id"].Value, out testCaseId))
            return false;

        title = m.Groups["title"].Value.Trim();
        if (string.IsNullOrWhiteSpace(title))
            title = name;

        return true;
    }

    public static IReadOnlyList<FlattenedStep> FlattenSteps(IEnumerable<AllureStep>? steps)
    {
        var result = new List<FlattenedStep>();
        if (steps is null) return result;

        foreach (var s in steps)
            FlattenStepInner(s, depth: 0, result);

        return result;
    }

    private static void FlattenStepInner(AllureStep step, int depth, List<FlattenedStep> output)
    {
        var action = BuildActionText(step, depth);
        if (!string.IsNullOrWhiteSpace(action))
            output.Add(new FlattenedStep(action));

        if (step.Steps is null || step.Steps.Count == 0)
            return;

        foreach (var child in step.Steps)
            FlattenStepInner(child, depth + 1, output);
    }

    private static string BuildActionText(AllureStep step, int depth)
    {
        var name = step.Name?.Trim() ?? string.Empty;
        if (string.IsNullOrWhiteSpace(name))
            name = "(unnamed step)";

        var sb = new StringBuilder();
        if (depth > 0)
            sb.Append(new string(' ', depth * 2)).Append("- ");

        sb.Append(name);

        if (step.Parameters is { Count: > 0 })
        {
            sb.Append(" (");
            for (int i = 0; i < step.Parameters.Count; i++)
            {
                var p = step.Parameters[i];
                if (i > 0) sb.Append(", ");
                sb.Append(p.Name?.Trim() ?? "param");
                sb.Append('=');
                sb.Append(p.Value?.Trim() ?? string.Empty);
            }
            sb.Append(')');
        }

        return sb.ToString();
    }
}

public readonly record struct FlattenedStep(string Action);




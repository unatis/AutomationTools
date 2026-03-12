using System.Text;

namespace AutomationTools.Coverage;

public static class CoveragePromptBuilder
{
    public static string Build(CoverageCalcRequest request, IReadOnlyList<string>? reportTitles)
    {
        var sb = new StringBuilder();
        sb.AppendLine("You are a QA coverage assistant.");
        sb.AppendLine("Provide a concise coverage summary and next steps based on the inputs.");
        sb.AppendLine();
        sb.AppendLine($"TeamId: {request.TeamId ?? "(empty)"}");
        sb.AppendLine($"Requirements Figma: {request.RequirementsFigmaLink ?? "(empty)"}");
        sb.AppendLine($"Automation report: {request.AutomationReportLink ?? "(empty)"}");
        sb.AppendLine($"Automation repo: {request.AutomationRepoLink ?? "(empty)"}");

        if (request.RequirementsPdf is not null)
        {
            var pdf = request.RequirementsPdf;
            sb.AppendLine($"Requirements PDF: {pdf.Name ?? "(unnamed)"} ({pdf.Size ?? 0} bytes, {pdf.ContentType ?? "unknown"})");
        }
        else
        {
            sb.AppendLine("Requirements PDF: (not provided)");
        }

        if (request.CoverageData is not null)
        {
            sb.AppendLine();
            sb.AppendLine($"Coverage data: {request.CoverageData.TotalTests} tests across {request.CoverageData.Suites.Count} suites.");
            sb.AppendLine("ADO test titles:");

            var suites = request.CoverageData.Suites;
            for (var i = 0; i < suites.Count; i++)
            {
                var suite = suites[i];
                for (var j = 0; j < suite.Tests.Count; j++)
                {
                    var title = suite.Tests[j].Title;
                    if (!string.IsNullOrWhiteSpace(title))
                        sb.AppendLine($"- {title}");
                }
            }
        }

        sb.AppendLine();
        if (reportTitles is { Count: > 0 })
        {
            sb.AppendLine("Automation report test titles:");
            foreach (var title in reportTitles)
            {
                if (!string.IsNullOrWhiteSpace(title))
                    sb.AppendLine($"- {title}");
            }
        }
        else
        {
            sb.AppendLine("Automation report test titles: (not provided)");
        }

        return sb.ToString().Trim();
    }
}

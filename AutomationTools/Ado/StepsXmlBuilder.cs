using System.Text;
using System.Xml;

namespace AutomationTools.Ado;

public static class StepsXmlBuilder
{
    // Minimal XML for Microsoft.VSTS.TCM.Steps.
    // Uses "parameterizedString" nodes as expected by Azure DevOps Test Case steps.
    public static string BuildStepsXml(IEnumerable<string> actions)
    {
        var actionsList = actions.Where(a => !string.IsNullOrWhiteSpace(a)).ToList();

        var settings = new XmlWriterSettings
        {
            OmitXmlDeclaration = true,
            ConformanceLevel = ConformanceLevel.Fragment,
            Encoding = Encoding.UTF8,
            NewLineHandling = NewLineHandling.None
        };

        var sb = new StringBuilder();
        using (var xw = XmlWriter.Create(sb, settings))
        {
            xw.WriteStartElement("steps");
            xw.WriteAttributeString("id", "0");
            xw.WriteAttributeString("last", actionsList.Count.ToString());

            for (int i = 0; i < actionsList.Count; i++)
            {
                var stepId = (i + 1).ToString();

                xw.WriteStartElement("step");
                xw.WriteAttributeString("id", stepId);
                xw.WriteAttributeString("type", "ActionStep");

                xw.WriteStartElement("parameterizedString");
                xw.WriteAttributeString("isformatted", "true");
                xw.WriteString(actionsList[i]);
                xw.WriteEndElement(); // parameterizedString (action)

                xw.WriteStartElement("parameterizedString");
                xw.WriteAttributeString("isformatted", "true");
                xw.WriteString(string.Empty); // expected result (optional)
                xw.WriteEndElement(); // parameterizedString (expected)

                xw.WriteEndElement(); // step
            }

            xw.WriteEndElement(); // steps
        }

        return sb.ToString();
    }
}




using System.IO.Compression;
using System.Text;
using System.Text.Json;
using System.Text.Json.Serialization;

namespace AutomationTools.TestGenerator;

public static class TestGeneratorService
{
    public static byte[] GenerateZipFromJson(string json, string baseName)
    {
        var rec = JsonSerializer.Deserialize<Recording>(json, new JsonSerializerOptions
        {
            PropertyNameCaseInsensitive = true
        });

        if (rec == null || rec.Steps == null || rec.Steps.Count == 0)
            throw new InvalidOperationException("No steps found.");

        var locatorsCode = GenerateLocators(rec, baseName);
        var pageCode = GeneratePage(rec, baseName);
        var testCode = GenerateTest(rec, baseName);

        using var ms = new MemoryStream();
        using (var zip = new ZipArchive(ms, ZipArchiveMode.Create, leaveOpen: true))
        {
            WriteEntry(zip, $"{baseName}Mapper.cs", locatorsCode);
            WriteEntry(zip, $"{baseName}Page.cs", pageCode);
            WriteEntry(zip, $"{baseName}Test.cs", testCode);
        }

        return ms.ToArray();
    }

    public static byte[] GenerateZipFromJsonTs(string json, string baseName)
    {
        var rec = JsonSerializer.Deserialize<Recording>(json, new JsonSerializerOptions
        {
            PropertyNameCaseInsensitive = true
        });

        if (rec == null || rec.Steps == null || rec.Steps.Count == 0)
            throw new InvalidOperationException("No steps found.");

        var locatorsCode = GenerateTsLocators(rec, baseName);
        var pageCode = GenerateTsPage(rec, baseName);
        var testCode = GenerateTsTest(rec, baseName);

        using var ms = new MemoryStream();
        using (var zip = new ZipArchive(ms, ZipArchiveMode.Create, leaveOpen: true))
        {
            WriteEntry(zip, $"{baseName}Mapper.ts", locatorsCode);
            WriteEntry(zip, $"{baseName}Page.ts", pageCode);
            WriteEntry(zip, $"{baseName}Test.ts", testCode);
        }

        return ms.ToArray();
    }

    private static void WriteEntry(ZipArchive zip, string name, string content)
    {
        var entry = zip.CreateEntry(name, CompressionLevel.Optimal);
        using var es = entry.Open();
        using var sw = new StreamWriter(es, Encoding.UTF8);
        sw.Write(content);
    }

    private static string GenerateLocators(Recording rec, string baseName)
    {
        var sb = new StringBuilder();
        sb.AppendLine("using OpenQA.Selenium;");
        sb.AppendLine();
        sb.AppendLine("namespace GeneratedTests;");
        sb.AppendLine();
        sb.AppendLine($"public class {baseName}Mapper");
        sb.AppendLine("{");
        sb.AppendLine($"    public {baseName}Page Page {{ get; }}");
        sb.AppendLine();
        sb.AppendLine($"    public {baseName}Mapper({baseName}Page page)");
        sb.AppendLine("    {");
        sb.AppendLine("        Page = page;");
        sb.AppendLine("    }");
        sb.AppendLine();

        var used = new HashSet<string>(StringComparer.OrdinalIgnoreCase);

        for (int i = 0; i < rec.Steps.Count; i++)
        {
            var step = rec.Steps[i];
            if (!StepNeedsElement(step)) continue;

            var fieldName = MakeUnique(used, ToPascal(StepFriendlyName(step) ?? $"{step.Type}_{i + 1}"));
            var locator = PickLocator(step);

            if (locator.Kind == LocatorKind.None)
            {
                sb.AppendLine($"    // {fieldName}: NO LOCATOR (step {i + 1})");
                continue;
            }

            sb.AppendLine($"    // Step {i + 1}: {step.Type} | url={step.Url}");
            sb.AppendLine($"    public By {fieldName} {{ get; }} = {locator.ByExpression};");
            sb.AppendLine();
        }

        sb.AppendLine("}");
        return sb.ToString();
    }

    private static string GenerateTsLocators(Recording rec, string baseName)
    {
        var sb = new StringBuilder();
        sb.AppendLine($"export class {baseName}Mapper {{");

        var used = new HashSet<string>(StringComparer.OrdinalIgnoreCase);

        for (int i = 0; i < rec.Steps.Count; i++)
        {
            var step = rec.Steps[i];
            if (!StepNeedsElement(step)) continue;

            var fieldName = MakeUnique(used, ToPascal(StepFriendlyName(step) ?? $"{step.Type}_{i + 1}"));
            var selector = PickLocatorSelector(step);

            sb.AppendLine($"  // Step {i + 1}: {step.Type} | url={step.Url}");
            if (string.IsNullOrWhiteSpace(selector))
            {
                sb.AppendLine($"  // {fieldName}: NO LOCATOR (step {i + 1})");
                continue;
            }

            sb.AppendLine($"  readonly {fieldName} = \"{Esc(selector)}\";");
            sb.AppendLine();
        }

        sb.AppendLine("}");
        return sb.ToString();
    }

    private static string GeneratePage(Recording rec, string baseName)
    {
        var sb = new StringBuilder();
        sb.AppendLine("using System;");
        sb.AppendLine("using OpenQA.Selenium;");
        sb.AppendLine("using OpenQA.Selenium.Support.UI;");
        sb.AppendLine("using SeleniumExtras.WaitHelpers;");
        sb.AppendLine();
        sb.AppendLine("namespace GeneratedTests;");
        sb.AppendLine();
        sb.AppendLine($"public class {baseName}Page");
        sb.AppendLine("{");
        sb.AppendLine("    private readonly IWebDriver _driver;");
        sb.AppendLine("    private readonly WebDriverWait _wait;");
        sb.AppendLine($"    public {baseName}Mapper Map {{ get; }}");
        sb.AppendLine();
        sb.AppendLine($"    public {baseName}Page(IWebDriver driver, WebDriverWait wait)");
        sb.AppendLine("    {");
        sb.AppendLine("        _driver = driver;");
        sb.AppendLine("        _wait = wait;");
        sb.AppendLine($"        Map = new {baseName}Mapper(this);");
        sb.AppendLine("    }");
        sb.AppendLine();
        sb.AppendLine("    public void GoTo(string url)");
        sb.AppendLine("    {");
        sb.AppendLine("        _driver.Navigate().GoToUrl(url);");
        sb.AppendLine("        WaitForDocumentReady();");
        sb.AppendLine("    }");
        sb.AppendLine();
        sb.AppendLine("    public void EnsureUrl(string url)");
        sb.AppendLine("    {");
        sb.AppendLine("        if (!_driver.Url.StartsWith(url, StringComparison.OrdinalIgnoreCase))");
        sb.AppendLine("        {");
        sb.AppendLine("            GoTo(url);");
        sb.AppendLine("        }");
        sb.AppendLine("    }");
        sb.AppendLine();

        var usedMethods = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
        var locatorFields = BuildStepToLocatorFieldMap(rec);

        for (int i = 0; i < rec.Steps.Count; i++)
        {
            var step = rec.Steps[i];
            var nextStep = (i + 1 < rec.Steps.Count) ? rec.Steps[i + 1] : null;
            var expectedNextUrl = GetExpectedNextUrl(step, nextStep);

            var friendly = StepFriendlyName(step) ?? $"{step.Type}_{i + 1}";
            var actionPrefix = step.Type switch
            {
                "click" => "Click",
                "input" => "Input",
                "select" => "Select",
                "scroll" => "Scroll",
                "navigate" => "Navigate",
                "setViewport" => "Viewport",
                _ => "Step"
            };

            var methodName = MakeUnique(usedMethods, actionPrefix + ToPascal(friendly));

            sb.AppendLine($"    // Step {i + 1}: {step.Type} | url={step.Url}");
            sb.AppendLine($"    public void {methodName}()");
            sb.AppendLine("    {");
            if (string.Equals(step.Type, "navigate", StringComparison.OrdinalIgnoreCase))
            {
                sb.AppendLine($"        EnsureUrl(\"{Esc(step.Url)}\");");
            }

            switch (step.Type)
            {
                case "click":
                    if (locatorFields.TryGetValue(i, out var clickField))
                    {
                        sb.AppendLine($"        Click(Map.{clickField}, expectedNextUrl: {(expectedNextUrl != null ? $"\"{Esc(expectedNextUrl)}\"" : "null")});");
                    }
                    else
                    {
                        sb.AppendLine("        // No locator for this click step");
                    }
                    break;

                case "input":
                    if (locatorFields.TryGetValue(i, out var inputField))
                    {
                        sb.AppendLine($"        Input(Map.{inputField}, value: \"{Esc(step.Value)}\");");
                    }
                    else sb.AppendLine("        // No locator for this input step");
                    break;

                case "select":
                    if (locatorFields.TryGetValue(i, out var selectField))
                    {
                        sb.AppendLine($"        Select(Map.{selectField}, value: \"{Esc(step.Value)}\", optionText: \"{Esc(step.OptionText)}\");");
                    }
                    else sb.AppendLine("        // No locator for this select step");
                    break;

                case "scroll":
                    sb.AppendLine($"        ScrollTo(x: {(step.OffsetX ?? 0).ToString(System.Globalization.CultureInfo.InvariantCulture)}, y: {(step.OffsetY ?? 0).ToString(System.Globalization.CultureInfo.InvariantCulture)});");
                    break;

                case "navigate":
                    break;

                case "setViewport":
                    sb.AppendLine("        // Viewport change step (no-op in generator)");
                    break;

                default:
                    sb.AppendLine($"        // Unsupported step type: {step.Type}");
                    break;
            }

            sb.AppendLine("    }");
            sb.AppendLine();
        }

        sb.AppendLine("    private void Click(By by, string? expectedNextUrl)");
        sb.AppendLine("    {");
        sb.AppendLine("        var el = _wait.Until(ExpectedConditions.ElementToBeClickable(by));");
        sb.AppendLine("        el.Click();");
        sb.AppendLine("        if (!string.IsNullOrWhiteSpace(expectedNextUrl))");
        sb.AppendLine("        {");
        sb.AppendLine("            _wait.Until(d => d.Url.StartsWith(expectedNextUrl, StringComparison.OrdinalIgnoreCase));");
        sb.AppendLine("            WaitForDocumentReady();");
        sb.AppendLine("        }");
        sb.AppendLine("    }");
        sb.AppendLine();
        sb.AppendLine("    private void Input(By by, string value)");
        sb.AppendLine("    {");
        sb.AppendLine("        var el = _wait.Until(ExpectedConditions.ElementIsVisible(by));");
        sb.AppendLine("        el.Clear();");
        sb.AppendLine("        el.SendKeys(value ?? string.Empty);");
        sb.AppendLine("    }");
        sb.AppendLine();
        sb.AppendLine("    private void Select(By by, string value, string optionText)");
        sb.AppendLine("    {");
        sb.AppendLine("        var el = _wait.Until(ExpectedConditions.ElementExists(by));");
        sb.AppendLine("        var sel = new SelectElement(el);");
        sb.AppendLine("        if (!string.IsNullOrWhiteSpace(value)) sel.SelectByValue(value);");
        sb.AppendLine("        else if (!string.IsNullOrWhiteSpace(optionText)) sel.SelectByText(optionText);");
        sb.AppendLine("    }");
        sb.AppendLine();
        sb.AppendLine("    private void ScrollTo(double x, double y)");
        sb.AppendLine("    {");
        sb.AppendLine("        ((IJavaScriptExecutor)_driver).ExecuteScript(\"window.scrollTo(arguments[0], arguments[1]);\", x, y);");
        sb.AppendLine("    }");
        sb.AppendLine();
        sb.AppendLine("    private void WaitForDocumentReady()");
        sb.AppendLine("    {");
        sb.AppendLine("        _wait.Until(d => ((IJavaScriptExecutor)d).ExecuteScript(\"return document.readyState\").ToString() == \"complete\");");
        sb.AppendLine("    }");
        sb.AppendLine("}");
        return sb.ToString();
    }

    private static string GenerateTsPage(Recording rec, string baseName)
    {
        var sb = new StringBuilder();
        sb.AppendLine("import { Page } from \"@playwright/test\";");
        sb.AppendLine($"import {{ {baseName}Mapper }} from \"./{baseName}Mapper\";");
        sb.AppendLine();
        sb.AppendLine($"export class {baseName}Page {{");
        sb.AppendLine("  constructor(private readonly page: Page) {");
        sb.AppendLine($"    this.map = new {baseName}Mapper();");
        sb.AppendLine("  }");
        sb.AppendLine();
        sb.AppendLine($"  readonly map: {baseName}Mapper;");
        sb.AppendLine();
        sb.AppendLine("  async goTo(url: string) {");
        sb.AppendLine("    await this.page.goto(url);");
        sb.AppendLine("  }");
        sb.AppendLine();
        sb.AppendLine("  async ensureUrl(url: string) {");
        sb.AppendLine("    const current = this.page.url();");
        sb.AppendLine("    if (!current.startsWith(url)) {");
        sb.AppendLine("      await this.goTo(url);");
        sb.AppendLine("    }");
        sb.AppendLine("  }");
        sb.AppendLine();

        var usedMethods = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
        var locatorFields = BuildStepToLocatorFieldMapTs(rec);

        for (int i = 0; i < rec.Steps.Count; i++)
        {
            var step = rec.Steps[i];
            var nextStep = (i + 1 < rec.Steps.Count) ? rec.Steps[i + 1] : null;
            var expectedNextUrl = GetExpectedNextUrl(step, nextStep);

            var friendly = StepFriendlyName(step) ?? $"{step.Type}_{i + 1}";
            var actionPrefix = step.Type switch
            {
                "click" => "Click",
                "input" => "Input",
                "select" => "Select",
                "scroll" => "Scroll",
                "navigate" => "Navigate",
                "setViewport" => "Viewport",
                _ => "Step"
            };

            var methodName = MakeUnique(usedMethods, actionPrefix + ToPascal(friendly));

            sb.AppendLine($"  // Step {i + 1}: {step.Type} | url={step.Url}");
            sb.AppendLine($"  async {methodName}() {{");
            if (string.Equals(step.Type, "navigate", StringComparison.OrdinalIgnoreCase))
            {
                if (!string.IsNullOrWhiteSpace(step.Url))
                    sb.AppendLine($"    await this.ensureUrl(\"{Esc(step.Url)}\");");
                else
                    sb.AppendLine("    // Navigate step without url");
            }

            switch (step.Type)
            {
                case "click":
                    if (locatorFields.TryGetValue(i, out var clickField))
                    {
                        sb.AppendLine($"    await this.click(this.map.{clickField}, {(expectedNextUrl != null ? $"\"{Esc(expectedNextUrl)}\"" : "null")});");
                    }
                    else
                    {
                        sb.AppendLine("    // No locator for this click step");
                    }
                    break;

                case "input":
                    if (locatorFields.TryGetValue(i, out var inputField))
                    {
                        sb.AppendLine($"    await this.input(this.map.{inputField}, \"{Esc(step.Value)}\");");
                    }
                    else sb.AppendLine("    // No locator for this input step");
                    break;

                case "select":
                    if (locatorFields.TryGetValue(i, out var selectField))
                    {
                        sb.AppendLine($"    await this.select(this.map.{selectField}, \"{Esc(step.Value)}\", \"{Esc(step.OptionText)}\");");
                    }
                    else sb.AppendLine("    // No locator for this select step");
                    break;

                case "scroll":
                    sb.AppendLine($"    await this.scrollTo({(step.OffsetX ?? 0).ToString(System.Globalization.CultureInfo.InvariantCulture)}, {(step.OffsetY ?? 0).ToString(System.Globalization.CultureInfo.InvariantCulture)});");
                    break;

                case "navigate":
                    break;

                case "setViewport":
                    if (step.Width is > 0 && step.Height is > 0)
                    {
                        sb.AppendLine($"    await this.page.setViewportSize({{ width: {step.Width.Value}, height: {step.Height.Value} }});");
                    }
                    else
                    {
                        sb.AppendLine("    // Viewport change step (no size provided)");
                    }
                    break;

                default:
                    sb.AppendLine($"    // Unsupported step type: {step.Type}");
                    break;
            }

            sb.AppendLine("  }");
            sb.AppendLine();
        }

        sb.AppendLine("  private async click(selector: string, expectedNextUrl?: string | null) {");
        sb.AppendLine("    await this.page.locator(selector).click();");
        sb.AppendLine("    if (expectedNextUrl) {");
        sb.AppendLine("      await this.page.waitForURL(url => url.toString().startsWith(expectedNextUrl));");
        sb.AppendLine("    }");
        sb.AppendLine("  }");
        sb.AppendLine();
        sb.AppendLine("  private async input(selector: string, value?: string) {");
        sb.AppendLine("    await this.page.locator(selector).fill(value ?? \"\");");
        sb.AppendLine("  }");
        sb.AppendLine();
        sb.AppendLine("  private async select(selector: string, value?: string, optionText?: string) {");
        sb.AppendLine("    const locator = this.page.locator(selector);");
        sb.AppendLine("    if (value) await locator.selectOption({ value });");
        sb.AppendLine("    else if (optionText) await locator.selectOption({ label: optionText });");
        sb.AppendLine("  }");
        sb.AppendLine();
        sb.AppendLine("  private async scrollTo(x: number, y: number) {");
        sb.AppendLine("    await this.page.evaluate(({ x, y }) => window.scrollTo(x, y), { x, y });");
        sb.AppendLine("  }");
        sb.AppendLine("}");
        return sb.ToString();
    }

    private static string GenerateTest(Recording rec, string baseName)
    {
        var sb = new StringBuilder();
        sb.AppendLine("using System;");
        sb.AppendLine("using NUnit.Framework;");
        sb.AppendLine("using OpenQA.Selenium;");
        sb.AppendLine("using OpenQA.Selenium.Chrome;");
        sb.AppendLine("using OpenQA.Selenium.Support.UI;");
        sb.AppendLine();
        sb.AppendLine("namespace GeneratedTests;");
        sb.AppendLine();
        sb.AppendLine("[TestFixture]");
        sb.AppendLine($"public class {baseName}Test");
        sb.AppendLine("{");
        sb.AppendLine("    private IWebDriver _driver = default!;");
        sb.AppendLine("    private WebDriverWait _wait = default!;");
        sb.AppendLine();
        sb.AppendLine("    [SetUp]");
        sb.AppendLine("    public void SetUp()");
        sb.AppendLine("    {");
        sb.AppendLine("        var options = new ChromeOptions();");
        sb.AppendLine("        // options.AddArgument(\"--headless=new\");");
        sb.AppendLine("        _driver = new ChromeDriver(options);");
        sb.AppendLine("        _driver.Manage().Window.Maximize();");
        sb.AppendLine("        _wait = new WebDriverWait(_driver, TimeSpan.FromSeconds(15));");
        sb.AppendLine("    }");
        sb.AppendLine();
        sb.AppendLine("    [TearDown]");
        sb.AppendLine("    public void TearDown()");
        sb.AppendLine("    {");
        sb.AppendLine("        try { _driver?.Quit(); } catch { }");
        sb.AppendLine("        try { _driver?.Dispose(); } catch { }");
        sb.AppendLine("    }");
        sb.AppendLine();
        sb.AppendLine("    [Test]");
        sb.AppendLine("    public void Run_Recording()");
        sb.AppendLine("    {");
        sb.AppendLine($"        var page = new {baseName}Page(_driver, _wait);");
        var startUrl = GetStartUrl(rec);
        if (!string.IsNullOrWhiteSpace(startUrl))
        {
            sb.AppendLine($"        page.GoTo(\"{Esc(startUrl)}\");");
        }
        sb.AppendLine();

        var usedMethods = new HashSet<string>(StringComparer.OrdinalIgnoreCase);

        for (int i = 0; i < rec.Steps.Count; i++)
        {
            var step = rec.Steps[i];
            var friendly = StepFriendlyName(step) ?? $"{step.Type}_{i + 1}";
            var actionPrefix = step.Type switch
            {
                "click" => "Click",
                "input" => "Input",
                "select" => "Select",
                "scroll" => "Scroll",
                "navigate" => "Navigate",
                "setViewport" => "Viewport",
                _ => "Step"
            };
            var methodName = MakeUnique(usedMethods, actionPrefix + ToPascal(friendly));
            sb.AppendLine($"        page.{methodName}();");
        }

        sb.AppendLine("    }");
        sb.AppendLine("}");
        return sb.ToString();
    }

    private static string GenerateTsTest(Recording rec, string baseName)
    {
        var sb = new StringBuilder();
        sb.AppendLine("import { test } from \"@playwright/test\";");
        sb.AppendLine($"import {{ {baseName}Page }} from \"./{baseName}Page\";");
        sb.AppendLine();
        sb.AppendLine("test(\"Run recording\", async ({ page }) => {");
        sb.AppendLine($"  const p = new {baseName}Page(page);");

        var startUrl = GetStartUrl(rec);
        if (!string.IsNullOrWhiteSpace(startUrl))
        {
            sb.AppendLine($"  await p.goTo(\"{Esc(startUrl)}\");");
        }

        sb.AppendLine();

        var usedMethods = new HashSet<string>(StringComparer.OrdinalIgnoreCase);

        for (int i = 0; i < rec.Steps.Count; i++)
        {
            var step = rec.Steps[i];
            var friendly = StepFriendlyName(step) ?? $"{step.Type}_{i + 1}";
            var actionPrefix = step.Type switch
            {
                "click" => "Click",
                "input" => "Input",
                "select" => "Select",
                "scroll" => "Scroll",
                "navigate" => "Navigate",
                "setViewport" => "Viewport",
                _ => "Step"
            };
            var methodName = MakeUnique(usedMethods, actionPrefix + ToPascal(friendly));
            sb.AppendLine($"  await p.{methodName}();");
        }

        sb.AppendLine("});");
        return sb.ToString();
    }

    private static Dictionary<int, string> BuildStepToLocatorFieldMap(Recording rec)
    {
        var map = new Dictionary<int, string>();
        var used = new HashSet<string>(StringComparer.OrdinalIgnoreCase);

        for (int i = 0; i < rec.Steps.Count; i++)
        {
            var step = rec.Steps[i];
            if (!StepNeedsElement(step)) continue;

            var name = MakeUnique(used, ToPascal(StepFriendlyName(step) ?? $"{step.Type}_{i + 1}"));
            var loc = PickLocator(step);
            if (loc.Kind == LocatorKind.None) continue;

            map[i] = name;
        }

        return map;
    }

    private static Dictionary<int, string> BuildStepToLocatorFieldMapTs(Recording rec)
    {
        var map = new Dictionary<int, string>();
        var used = new HashSet<string>(StringComparer.OrdinalIgnoreCase);

        for (int i = 0; i < rec.Steps.Count; i++)
        {
            var step = rec.Steps[i];
            if (!StepNeedsElement(step)) continue;

            var name = MakeUnique(used, ToPascal(StepFriendlyName(step) ?? $"{step.Type}_{i + 1}"));
            var selector = PickLocatorSelector(step);
            if (string.IsNullOrWhiteSpace(selector)) continue;

            map[i] = name;
        }

        return map;
    }

    private static bool StepNeedsElement(Step step)
        => step.Type is "click" or "input" or "select";

    private enum LocatorKind { None, Css, XPath }

    private readonly record struct LocatorPick(LocatorKind Kind, string ByExpression);

    private static LocatorPick PickLocator(Step step)
    {
        if (step.Selectors == null)
            return new LocatorPick(LocatorKind.None, "");

        foreach (var group in step.Selectors)
        {
            var s = group?.FirstOrDefault();
            if (string.IsNullOrWhiteSpace(s)) continue;

            if (s.StartsWith("xpath//", StringComparison.OrdinalIgnoreCase))
            {
                var xp = s.Substring("xpath".Length);
                return new LocatorPick(LocatorKind.XPath, $"By.XPath(\"{Esc(xp)}\")");
            }

            if (s.StartsWith("aria/", StringComparison.OrdinalIgnoreCase))
            {
                var label = s.Substring("aria/".Length);
                return new LocatorPick(LocatorKind.XPath, $"By.XPath(\"//*[@aria-label=\\\"{Esc(label)}\\\" or normalize-space(.)=\\\"{Esc(label)}\\\"]\")");
            }

            if (s.StartsWith("text/", StringComparison.OrdinalIgnoreCase))
            {
                var text = s.Substring("text/".Length);
                return new LocatorPick(LocatorKind.XPath, $"By.XPath(\"//*[normalize-space(.)=\\\"{Esc(text)}\\\"]\")");
            }

            if (s.StartsWith("pierce/", StringComparison.OrdinalIgnoreCase))
            {
                var css = s.Substring("pierce/".Length);
                return new LocatorPick(LocatorKind.Css, $"By.CssSelector(\"{Esc(css)}\")");
            }

            if (s.StartsWith("css=", StringComparison.OrdinalIgnoreCase))
            {
                var css = s.Substring("css=".Length);
                return new LocatorPick(LocatorKind.Css, $"By.CssSelector(\"{Esc(css)}\")");
            }

            return new LocatorPick(LocatorKind.Css, $"By.CssSelector(\"{Esc(s)}\")");
        }

        return new LocatorPick(LocatorKind.None, "");
    }

    private static string? PickLocatorSelector(Step step)
    {
        if (step.Selectors == null)
            return null;

        foreach (var group in step.Selectors)
        {
            var s = group?.FirstOrDefault();
            if (string.IsNullOrWhiteSpace(s)) continue;

            if (s.StartsWith("xpath//", StringComparison.OrdinalIgnoreCase))
            {
                var xp = s.Substring("xpath".Length);
                return "xpath=" + xp;
            }

            if (s.StartsWith("aria/", StringComparison.OrdinalIgnoreCase))
            {
                var label = s.Substring("aria/".Length);
                return "aria/" + label;
            }

            if (s.StartsWith("text/", StringComparison.OrdinalIgnoreCase))
            {
                var text = s.Substring("text/".Length);
                return "text=" + text;
            }

            if (s.StartsWith("pierce/", StringComparison.OrdinalIgnoreCase))
            {
                var css = s.Substring("pierce/".Length);
                return "css=" + css;
            }

            if (s.StartsWith("css=", StringComparison.OrdinalIgnoreCase))
            {
                var css = s.Substring("css=".Length);
                return "css=" + css;
            }

            return s;
        }

        return null;
    }

    private static string? StepFriendlyName(Step step)
    {
        if (!string.IsNullOrWhiteSpace(step.Target))
            return step.Target;

        var s = FirstSelectorString(step);
        if (string.IsNullOrWhiteSpace(s)) return null;

        s = s.Replace("aria/", "")
            .Replace("text/", "")
            .Replace("xpath//", "")
            .Replace("pierce/", "")
            .Replace("css=", "")
            .Trim();

        return string.IsNullOrWhiteSpace(s) ? null : s;
    }

    private static string ToPascal(string s)
    {
        var chars = s.Replace("_", " ").Replace("-", " ").Trim();

        var parts = chars.Split(' ', StringSplitOptions.RemoveEmptyEntries);
        var sb = new StringBuilder();
        foreach (var p in parts)
        {
            var clean = new string(p.Where(char.IsLetterOrDigit).ToArray());
            if (clean.Length == 0) continue;
            sb.Append(char.ToUpperInvariant(clean[0]));
            if (clean.Length > 1) sb.Append(clean.Substring(1));
        }
        var res = sb.ToString();
        if (string.IsNullOrWhiteSpace(res)) res = "Step";
        if (char.IsDigit(res[0])) res = "_" + res;
        return res;
    }

    private static string MakeUnique(HashSet<string> used, string baseName)
    {
        var name = baseName;
        var i = 2;
        while (!used.Add(name))
        {
            name = baseName + i;
            i++;
        }
        return name;
    }

    private static string Esc(string? s)
        => (s ?? "").Replace("\\", "\\\\").Replace("\"", "\\\"");

    private static string? FirstSelectorString(Step step)
    {
        if (step.Selectors == null) return null;
        foreach (var group in step.Selectors)
        {
            var s = group?.FirstOrDefault();
            if (!string.IsNullOrWhiteSpace(s))
                return s;
        }
        return null;
    }

    private static string GetStartUrl(Recording rec)
    {
        var nav = rec.Steps.FirstOrDefault(s =>
            string.Equals(s.Type, "navigate", StringComparison.OrdinalIgnoreCase) &&
            !string.IsNullOrWhiteSpace(s.Url));
        if (nav?.Url is { Length: > 0 }) return nav.Url;

        var asserted = rec.Steps
            .SelectMany(s => s.AssertedEvents ?? new List<AssertedEvent>())
            .FirstOrDefault(e => string.Equals(e.Type, "navigation", StringComparison.OrdinalIgnoreCase) &&
                                 !string.IsNullOrWhiteSpace(e.Url));
        if (asserted?.Url is { Length: > 0 }) return asserted.Url;

        return rec.Steps.FirstOrDefault(s => !string.IsNullOrWhiteSpace(s.Url))?.Url ?? string.Empty;
    }

    private static string? GetExpectedNextUrl(Step step, Step? nextStep)
    {
        var asserted = step.AssertedEvents?.FirstOrDefault(e =>
            string.Equals(e.Type, "navigation", StringComparison.OrdinalIgnoreCase) &&
            !string.IsNullOrWhiteSpace(e.Url));
        if (asserted?.Url is { Length: > 0 }) return asserted.Url;

        if (nextStep != null &&
            string.Equals(nextStep.Type, "navigate", StringComparison.OrdinalIgnoreCase) &&
            !string.IsNullOrWhiteSpace(nextStep.Url))
        {
            return nextStep.Url;
        }

        return null;
    }
}

public class Recording
{
    [JsonPropertyName("title")] public string? Title { get; set; }
    [JsonPropertyName("steps")] public List<Step> Steps { get; set; } = new();
}

public class Step
{
    [JsonPropertyName("type")] public string Type { get; set; } = "";
    [JsonPropertyName("url")] public string? Url { get; set; }
    [JsonPropertyName("target")] public string? Target { get; set; }

    [JsonPropertyName("selectors")] public List<List<string>>? Selectors { get; set; }

    [JsonPropertyName("width")] public int? Width { get; set; }
    [JsonPropertyName("height")] public int? Height { get; set; }
    [JsonPropertyName("deviceScaleFactor")] public double? DeviceScaleFactor { get; set; }
    [JsonPropertyName("isMobile")] public bool? IsMobile { get; set; }
    [JsonPropertyName("hasTouch")] public bool? HasTouch { get; set; }
    [JsonPropertyName("isLandscape")] public bool? IsLandscape { get; set; }

    [JsonPropertyName("offsetX")] public double? OffsetX { get; set; }
    [JsonPropertyName("offsetY")] public double? OffsetY { get; set; }

    [JsonPropertyName("value")] public string? Value { get; set; }
    [JsonPropertyName("optionText")] public string? OptionText { get; set; }

    [JsonPropertyName("assertedEvents")] public List<AssertedEvent>? AssertedEvents { get; set; }
}

public class AssertedEvent
{
    [JsonPropertyName("type")] public string? Type { get; set; }
    [JsonPropertyName("url")] public string? Url { get; set; }
    [JsonPropertyName("title")] public string? Title { get; set; }
}

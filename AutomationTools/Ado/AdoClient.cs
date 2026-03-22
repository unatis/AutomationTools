using System.Net;
using System.Net.Http.Headers;
using System.Text;
using System.Text.Json;

namespace AutomationTools.Ado;

public sealed class AdoClient
{
    private readonly HttpClient _http;
    private readonly AdoOptions _options;

    public AdoClient(HttpClient http, AdoOptions options)
    {
        _http = http;
        _options = options;
    }

    public async Task<AdoWorkItem?> TryGetWorkItemById(int id, CancellationToken ct, string? witApiVersionOverride = null)
    {
        var url = $"_apis/wit/workitems/{id}?api-version={GetWitApiVersion(witApiVersionOverride)}";
        using var req = new HttpRequestMessage(HttpMethod.Get, url);
        using var resp = await _http.SendAsync(req, ct);

        if (resp.StatusCode == HttpStatusCode.NotFound)
            return null;

        await EnsureSuccessOrThrow(resp, url, "TryGetWorkItemById", ct);

        var json = await resp.Content.ReadAsStringAsync(ct);
        return ParseWorkItem(json);
    }

    public async Task<TestCaseDetails?> GetTestCaseDetails(int id, CancellationToken ct, string? witApiVersionOverride = null)
    {
        var fields = Uri.EscapeDataString("System.Title,Microsoft.VSTS.TCM.Steps");
        var url = $"_apis/wit/workitems/{id}?fields={fields}&api-version={GetWitApiVersion(witApiVersionOverride)}";
        using var req = new HttpRequestMessage(HttpMethod.Get, url);
        using var resp = await _http.SendAsync(req, ct);

        if (resp.StatusCode == HttpStatusCode.NotFound)
            return null;

        await EnsureSuccessOrThrow(resp, url, "GetTestCaseDetails", ct);

        var json = await resp.Content.ReadAsStringAsync(ct);
        return ParseTestCaseDetails(json);
    }

    public async Task<int?> TryFindTestCaseIdByExactTitle(string title, CancellationToken ct, string? witApiVersionOverride = null)
    {
        // WIQL query:
        // https://learn.microsoft.com/en-us/rest/api/azure/devops/wit/wiql/query-by-wiql
        var safeTitle = EscapeWiqlString(title);
        var safeProject = EscapeWiqlString(_options.ProjectName);

        var wiql = new
        {
            query =
                "Select [System.Id] From WorkItems " +
                $"Where [System.TeamProject] = '{safeProject}' " +
                "And [System.WorkItemType] = 'Test Case' " +
                $"And [System.Title] = '{safeTitle}'"
        };

        var url = $"_apis/wit/wiql?api-version={GetWitApiVersion(witApiVersionOverride)}";
        using var req = new HttpRequestMessage(HttpMethod.Post, url);
        req.Content = new StringContent(JsonSerializer.Serialize(wiql), Encoding.UTF8, "application/json");

        using var resp = await _http.SendAsync(req, ct);
        await EnsureSuccessOrThrow(resp, url, "WIQL", ct);

        var json = await resp.Content.ReadAsStringAsync(ct);
        using var doc = JsonDocument.Parse(json);

        if (!doc.RootElement.TryGetProperty("workItems", out var workItems) || workItems.ValueKind != JsonValueKind.Array)
            return null;

        var first = workItems.EnumerateArray().FirstOrDefault();
        if (first.ValueKind != JsonValueKind.Object)
            return null;

        if (!first.TryGetProperty("id", out var idEl) || idEl.ValueKind != JsonValueKind.Number)
            return null;

        return idEl.GetInt32();
    }

    public async Task<int> CreateTestCase(string title, string descriptionHtml, string stepsXml, CancellationToken ct, string? witApiVersionOverride = null)
    {
        var url = $"_apis/wit/workitems/$Test%20Case?api-version={GetWitApiVersion(witApiVersionOverride)}";
        var patch = new object[]
        {
            new { op = "add", path = "/fields/System.Title", value = title },
            new { op = "add", path = "/fields/System.Description", value = descriptionHtml },
            new { op = "add", path = "/fields/Microsoft.VSTS.TCM.Steps", value = stepsXml },
            new { op = "add", path = "/fields/System.AreaPath", value = _options.AreaPath },
            new { op = "add", path = "/fields/System.IterationPath", value = _options.IterationPath },
        };

        using var req = new HttpRequestMessage(HttpMethod.Post, url);
        req.Content = new StringContent(JsonSerializer.Serialize(patch), Encoding.UTF8, "application/json-patch+json");

        using var resp = await _http.SendAsync(req, ct);
        await EnsureSuccessOrThrow(resp, url, "CreateTestCase", ct);

        var json = await resp.Content.ReadAsStringAsync(ct);
        var wi = ParseWorkItem(json);
        if (wi is null)
            throw new InvalidOperationException("Failed to parse created work item.");
        return wi.Id;
    }

    public async Task UpdateTestCase(int id, string descriptionHtml, string stepsXml, CancellationToken ct, string? witApiVersionOverride = null)
    {
        var url = $"_apis/wit/workitems/{id}?api-version={GetWitApiVersion(witApiVersionOverride)}";
        var patch = new object[]
        {
            new { op = "replace", path = "/fields/System.Description", value = descriptionHtml },
            new { op = "replace", path = "/fields/Microsoft.VSTS.TCM.Steps", value = stepsXml },
            new { op = "replace", path = "/fields/System.AreaPath", value = _options.AreaPath },
            new { op = "replace", path = "/fields/System.IterationPath", value = _options.IterationPath },
        };

        using var req = new HttpRequestMessage(HttpMethod.Patch, url);
        req.Content = new StringContent(JsonSerializer.Serialize(patch), Encoding.UTF8, "application/json-patch+json");

        using var resp = await _http.SendAsync(req, ct);
        await EnsureSuccessOrThrow(resp, url, "UpdateTestCase", ct);
    }

    public async Task AddTestCaseToSuite(
        int planId,
        int suiteId,
        int testCaseId,
        CancellationToken ct,
        string? testPlanApiVersionOverride = null,
        int? configurationId = null)
    {
        // For static suites, Azure DevOps uses ".../Suites/{suiteId}/TestCase" (singular) and expects the id in JSON body.
        // Example:
        // POST .../_apis/testplan/Plans/{planId}/Suites/{suiteId}/TestCase?api-version=7.1
        // Body: { "workItem": { "id": 123 } }   (some variants accept { "testCaseIds": [123] })
        var url = $"_apis/testplan/Plans/{planId}/Suites/{suiteId}/TestCase?api-version={GetTestPlanApiVersion(testPlanApiVersionOverride)}";

        async Task<bool> SuiteContainsTestCase()
        {
            var items = await ListSuiteTestCases(planId, suiteId, ct, testPlanApiVersionOverride);
            return items.Any(x => x.WorkItemId == testCaseId);
        }

        async Task VerifyAddedOrThrow(string successContext, HttpResponseMessage? resp = null)
        {
            // ADO can return 200/201 but the suite list may be eventually consistent. Retry a bit.
            for (int i = 0; i < 6; i++)
            {
                if (await SuiteContainsTestCase())
                    return;

                await Task.Delay(500, ct);
            }

            var body = resp is null ? string.Empty : await SafeReadBody(resp, ct);
            throw new InvalidOperationException(
                $"{successContext} but Test Case was not found in suite after verification. " +
                $"PlanId={planId} SuiteId={suiteId} TestCaseId={testCaseId}. Url={url}. Body={body}");
        }

        static async Task<string> SafeReadBody(HttpResponseMessage resp, CancellationToken ct)
        {
            try { return await resp.Content.ReadAsStringAsync(ct); }
            catch { return string.Empty; }
        }

        static string ConflictHint(int suiteId) =>
            $"ADO returned 409 Conflict while adding Test Case to Suite {suiteId}. " +
            "This often happens if the suite is not a Static suite (e.g., requirement-based or query-based).";

        // Some orgs require point assignments (configuration) to create Suite Test Cases properly.
        // If caller didn't specify configurationId, try to pick the first available configuration.
        int? effectiveConfigId = configurationId;
        if (effectiveConfigId is null)
        {
            try
            {
                var configs = await ListTestPlanConfigurations(ct, testPlanApiVersionOverride);
                effectiveConfigId = configs.FirstOrDefault()?.Id;
            }
            catch
            {
                // If listing configs fails, continue with legacy bodies.
                effectiveConfigId = null;
            }
        }

        // Try body shape C (preferred): array with pointAssignments
        if (effectiveConfigId is not null)
        {
            var bodyC = new[]
            {
                new
                {
                    workItem = new { id = testCaseId },
                    pointAssignments = new[] { new { configurationId = effectiveConfigId.Value } }
                }
            };

            using var reqC = new HttpRequestMessage(HttpMethod.Post, url);
            reqC.Content = new StringContent(JsonSerializer.Serialize(bodyC), Encoding.UTF8, "application/json");
            using var respC = await _http.SendAsync(reqC, ct);

            if (respC.IsSuccessStatusCode)
            {
                await VerifyAddedOrThrow("AddTestCaseToSuite succeeded (body shape C)", respC);
                return;
            }

            if (respC.StatusCode == HttpStatusCode.Conflict)
            {
                if (await SuiteContainsTestCase())
                    return;

                var body = await SafeReadBody(respC, ct);
                throw new InvalidOperationException(
                    $"{ConflictHint(suiteId)} PlanId={planId} SuiteId={suiteId} TestCaseId={testCaseId}. Url={url}. Body={body}");
            }

            // If C fails with 404/400 etc, fall through to legacy bodies below.
        }

        // Try body shape A (legacy)
        var bodyA = new { workItem = new { id = testCaseId } };
        using (var req = new HttpRequestMessage(HttpMethod.Post, url))
        {
            req.Content = new StringContent(JsonSerializer.Serialize(bodyA), Encoding.UTF8, "application/json");
            using var resp = await _http.SendAsync(req, ct);

            if (resp.IsSuccessStatusCode)
            {
                await VerifyAddedOrThrow("AddTestCaseToSuite succeeded (body shape A)", resp);
                return;
            }

            if (resp.StatusCode == HttpStatusCode.Conflict)
            {
                // Treat as success only if the test case is actually present in this suite.
                if (await SuiteContainsTestCase())
                    return;

                var body = await SafeReadBody(resp, ct);
                throw new InvalidOperationException(
                    $"{ConflictHint(suiteId)} PlanId={planId} SuiteId={suiteId} TestCaseId={testCaseId}. Url={url}. Body={body}");
            }

            // Try body shape B for compatibility
            if (resp.StatusCode == HttpStatusCode.NotFound || resp.StatusCode == HttpStatusCode.BadRequest)
            {
                var bodyB = new { testCaseIds = new[] { testCaseId } };
                using var req2 = new HttpRequestMessage(HttpMethod.Post, url);
                req2.Content = new StringContent(JsonSerializer.Serialize(bodyB), Encoding.UTF8, "application/json");

                using var resp2 = await _http.SendAsync(req2, ct);
                if (resp2.IsSuccessStatusCode)
                {
                    await VerifyAddedOrThrow("AddTestCaseToSuite succeeded (body shape B)", resp2);
                    return;
                }

                if (resp2.StatusCode == HttpStatusCode.Conflict)
                {
                    if (await SuiteContainsTestCase())
                        return;

                    var body2 = await SafeReadBody(resp2, ct);
                    throw new InvalidOperationException(
                        $"{ConflictHint(suiteId)} PlanId={planId} SuiteId={suiteId} TestCaseId={testCaseId}. Url={url}. Body={body2}");
                }

                await EnsureSuccessOrThrow(resp2, url, "AddTestCaseToSuite", ct);
                return;
            }

            await EnsureSuccessOrThrow(resp, url, "AddTestCaseToSuite", ct);
        }
    }

    public async Task<IReadOnlyList<TestPlanConfiguration>> ListTestPlanConfigurations(
        CancellationToken ct,
        string? testPlanApiVersionOverride = null)
    {
        var url = $"_apis/testplan/configurations?api-version={GetTestPlanApiVersion(testPlanApiVersionOverride)}";
        using var req = new HttpRequestMessage(HttpMethod.Get, url);
        using var resp = await _http.SendAsync(req, ct);
        await EnsureSuccessOrThrow(resp, url, "ListTestPlanConfigurations", ct);

        var json = await resp.Content.ReadAsStringAsync(ct);
        using var doc = JsonDocument.Parse(json);

        if (!doc.RootElement.TryGetProperty("value", out var valueEl) || valueEl.ValueKind != JsonValueKind.Array)
            return Array.Empty<TestPlanConfiguration>();

        var list = new List<TestPlanConfiguration>();
        foreach (var item in valueEl.EnumerateArray())
        {
            if (item.ValueKind != JsonValueKind.Object) continue;
            if (!item.TryGetProperty("id", out var idEl) || idEl.ValueKind != JsonValueKind.Number) continue;
            var id = idEl.GetInt32();
            var name = item.TryGetProperty("name", out var nameEl) && nameEl.ValueKind == JsonValueKind.String
                ? (nameEl.GetString() ?? string.Empty)
                : string.Empty;
            list.Add(new TestPlanConfiguration(id, name));
        }

        return list;
    }

    public async Task<IReadOnlyList<TestPlanItem>> ListTestPlans(
        CancellationToken ct,
        string? testPlanApiVersionOverride = null)
    {
        var url = $"_apis/testplan/plans?api-version={GetTestPlanApiVersion(testPlanApiVersionOverride)}";
        using var req = new HttpRequestMessage(HttpMethod.Get, url);
        using var resp = await _http.SendAsync(req, ct);
        await EnsureSuccessOrThrow(resp, url, "ListTestPlans", ct);

        var json = await resp.Content.ReadAsStringAsync(ct);
        using var doc = JsonDocument.Parse(json);

        if (!doc.RootElement.TryGetProperty("value", out var valueEl) || valueEl.ValueKind != JsonValueKind.Array)
            return Array.Empty<TestPlanItem>();

        var list = new List<TestPlanItem>();
        foreach (var item in valueEl.EnumerateArray())
        {
            if (item.ValueKind != JsonValueKind.Object) continue;
            if (!item.TryGetProperty("id", out var idEl) || idEl.ValueKind != JsonValueKind.Number) continue;
            var id = idEl.GetInt32();
            var name = item.TryGetProperty("name", out var nameEl) && nameEl.ValueKind == JsonValueKind.String
                ? (nameEl.GetString() ?? string.Empty)
                : string.Empty;
            list.Add(new TestPlanItem(id, name));
        }

        return list;
    }

    public async Task<IReadOnlyList<TestSuiteItem>> ListTestSuites(
        int planId,
        CancellationToken ct,
        string? testPlanApiVersionOverride = null)
    {
        var url = $"_apis/testplan/plans/{planId}/suites?api-version={GetTestPlanApiVersion(testPlanApiVersionOverride)}";
        using var req = new HttpRequestMessage(HttpMethod.Get, url);
        using var resp = await _http.SendAsync(req, ct);
        await EnsureSuccessOrThrow(resp, url, "ListTestSuites", ct);

        var json = await resp.Content.ReadAsStringAsync(ct);
        using var doc = JsonDocument.Parse(json);

        if (!doc.RootElement.TryGetProperty("value", out var valueEl) || valueEl.ValueKind != JsonValueKind.Array)
            return Array.Empty<TestSuiteItem>();

        var list = new List<TestSuiteItem>();
        foreach (var item in valueEl.EnumerateArray())
        {
            if (item.ValueKind != JsonValueKind.Object) continue;
            if (!item.TryGetProperty("id", out var idEl) || idEl.ValueKind != JsonValueKind.Number) continue;
            var id = idEl.GetInt32();
            var name = item.TryGetProperty("name", out var nameEl) && nameEl.ValueKind == JsonValueKind.String
                ? (nameEl.GetString() ?? string.Empty)
                : string.Empty;
            list.Add(new TestSuiteItem(id, name));
        }

        return list;
    }

    public async Task<IReadOnlyList<SuiteTestCase>> ListSuiteTestCases(int planId, int suiteId, CancellationToken ct, string? testPlanApiVersionOverride = null)
    {
        var url = $"_apis/testplan/Plans/{planId}/Suites/{suiteId}/TestCase?api-version={GetTestPlanApiVersion(testPlanApiVersionOverride)}";
        using var req = new HttpRequestMessage(HttpMethod.Get, url);
        using var resp = await _http.SendAsync(req, ct);
        await EnsureSuccessOrThrow(resp, url, "ListSuiteTestCases", ct);

        var json = await resp.Content.ReadAsStringAsync(ct);
        using var doc = JsonDocument.Parse(json);

        if (!doc.RootElement.TryGetProperty("value", out var valueEl) || valueEl.ValueKind != JsonValueKind.Array)
            return Array.Empty<SuiteTestCase>();

        var list = new List<SuiteTestCase>();
        foreach (var item in valueEl.EnumerateArray())
        {
            if (item.ValueKind != JsonValueKind.Object) continue;
            if (!item.TryGetProperty("workItem", out var wiEl) || wiEl.ValueKind != JsonValueKind.Object) continue;
            if (!wiEl.TryGetProperty("id", out var idEl) || idEl.ValueKind != JsonValueKind.Number) continue;

            var id = idEl.GetInt32();
            var title = wiEl.TryGetProperty("name", out var nameEl) && nameEl.ValueKind == JsonValueKind.String
                ? (nameEl.GetString() ?? string.Empty)
                : string.Empty;

            list.Add(new SuiteTestCase(id, title));
        }

        return list;
    }

    public async Task<string> GetSuiteTestCasesRaw(int planId, int suiteId, CancellationToken ct, string? testPlanApiVersionOverride = null)
    {
        var url = $"_apis/testplan/Plans/{planId}/Suites/{suiteId}/TestCase?api-version={GetTestPlanApiVersion(testPlanApiVersionOverride)}";
        using var req = new HttpRequestMessage(HttpMethod.Get, url);
        using var resp = await _http.SendAsync(req, ct);
        await EnsureSuccessOrThrow(resp, url, "GetSuiteTestCasesRaw", ct);
        return await resp.Content.ReadAsStringAsync(ct);
    }

    private static async Task EnsureSuccessOrThrow(HttpResponseMessage resp, string relativeUrl, string operation, CancellationToken ct)
    {
        if (resp.IsSuccessStatusCode)
            return;

        var body = string.Empty;
        try { body = await resp.Content.ReadAsStringAsync(ct); } catch { /* ignore */ }

        // Important: include URL and response body to debug ADO 404 vs auth vs bad endpoint.
        throw new InvalidOperationException(
            $"{operation} failed: {(int)resp.StatusCode} {resp.ReasonPhrase}. Url={relativeUrl}. Body={body}");
    }

    private string GetWitApiVersion(string? overrideValue) =>
        !string.IsNullOrWhiteSpace(overrideValue) ? overrideValue! : (_options.WitApiVersion ?? _options.ApiVersion ?? "7.1");

    private string GetTestPlanApiVersion(string? overrideValue) =>
        !string.IsNullOrWhiteSpace(overrideValue) ? overrideValue! : (_options.TestPlanApiVersion ?? _options.ApiVersion ?? "7.1");

    private string GetTestApiVersion(string? overrideValue) =>
        !string.IsNullOrWhiteSpace(overrideValue) ? overrideValue! : (_options.TestApiVersion ?? _options.ApiVersion ?? "7.1-preview.1");

    public static void ConfigureHttpClient(HttpClient http, AdoOptions options)
    {
        http.BaseAddress = new Uri(options.BaseProjectUrl.TrimEnd('/') + "/");

        var pat = options.Pat ?? Environment.GetEnvironmentVariable("ADO_PAT");
        if (string.IsNullOrWhiteSpace(pat))
            throw new InvalidOperationException("Missing PAT. Set env var ADO_PAT or configure Ado:Pat securely.");

        var authToken = Convert.ToBase64String(Encoding.ASCII.GetBytes($":{pat}"));
        http.DefaultRequestHeaders.Authorization = new AuthenticationHeaderValue("Basic", authToken);
        http.DefaultRequestHeaders.Accept.Add(new MediaTypeWithQualityHeaderValue("application/json"));
    }

    private static string EscapeWiqlString(string value) => value.Replace("'", "''");

    private static AdoWorkItem? ParseWorkItem(string json)
    {
        using var doc = JsonDocument.Parse(json);
        var root = doc.RootElement;

        if (!root.TryGetProperty("id", out var idEl) || idEl.ValueKind != JsonValueKind.Number)
            return null;

        var id = idEl.GetInt32();

        var workItemType = string.Empty;
        if (root.TryGetProperty("fields", out var fieldsEl) && fieldsEl.ValueKind == JsonValueKind.Object)
        {
            if (fieldsEl.TryGetProperty("System.WorkItemType", out var witEl) && witEl.ValueKind == JsonValueKind.String)
                workItemType = witEl.GetString() ?? string.Empty;
        }

        return new AdoWorkItem(id, workItemType);
    }

    private static TestCaseDetails? ParseTestCaseDetails(string json)
    {
        using var doc = JsonDocument.Parse(json);
        var root = doc.RootElement;

        if (!root.TryGetProperty("id", out var idEl) || idEl.ValueKind != JsonValueKind.Number)
            return null;

        var id = idEl.GetInt32();
        var title = string.Empty;
        var steps = string.Empty;

        if (root.TryGetProperty("fields", out var fieldsEl) && fieldsEl.ValueKind == JsonValueKind.Object)
        {
            if (fieldsEl.TryGetProperty("System.Title", out var titleEl) && titleEl.ValueKind == JsonValueKind.String)
                title = titleEl.GetString() ?? string.Empty;

            if (fieldsEl.TryGetProperty("Microsoft.VSTS.TCM.Steps", out var stepsEl) && stepsEl.ValueKind == JsonValueKind.String)
                steps = stepsEl.GetString() ?? string.Empty;
        }

        return new TestCaseDetails(id, title, steps);
    }
}

public sealed record AdoWorkItem(int Id, string WorkItemType);
public sealed record TestCaseDetails(int Id, string Title, string Steps);
public sealed record SuiteTestCase(int WorkItemId, string Title);
public sealed record TestPlanConfiguration(int Id, string Name);
public sealed record TestPlanItem(int Id, string Name);
public sealed record TestSuiteItem(int Id, string Name);



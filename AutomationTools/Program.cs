using Microsoft.AspNetCore.Mvc;
using Microsoft.Extensions.Caching.Memory;
using Microsoft.Extensions.Options;
using AutomationTools.Ado;
using AutomationTools.Sync;
using AutomationTools.Allure;
using AutomationTools.Playwright;
using AutomationTools.Recording;
using AutomationTools.TestGenerator;
using AutomationTools.Ai;
using AutomationTools.Coverage;
using System.Text;
using System.Text.Json;
using System.IO.Compression;
using System.Text.RegularExpressions;

var builder = WebApplication.CreateBuilder(args);

// Read ONLY appsettings.json (ignore appsettings.{Environment}.json)
builder.Configuration.Sources.Clear();
builder.Configuration
    .AddJsonFile("appsettings.json", optional: false, reloadOnChange: true)
    .AddEnvironmentVariables()
    .AddCommandLine(args);

builder.Services.Configure<AdoOptions>(builder.Configuration.GetSection(AdoOptions.SectionName));
builder.Services.AddSingleton(sp => sp.GetRequiredService<IOptions<AdoOptions>>().Value);

builder.Services.Configure<AiOptions>(builder.Configuration.GetSection(AiOptions.SectionName));
builder.Services.AddSingleton(sp => sp.GetRequiredService<IOptions<AiOptions>>().Value);

builder.Services.AddHttpClient<AdoClient>((sp, http) =>
{
    var opts = sp.GetRequiredService<AdoOptions>();
    AdoClient.ConfigureHttpClient(http, opts);
});

builder.Services.AddHttpClient<GroqClient>((sp, http) =>
{
    var apiKey = Environment.GetEnvironmentVariable("GROQ_API_KEY");
    GroqClient.ConfigureHttpClient(http, apiKey);
});

builder.Services.AddHttpClient<LocalEmbeddingClient>((sp, http) =>
{
    var opts = sp.GetRequiredService<AiOptions>();
    LocalEmbeddingClient.ConfigureHttpClient(http, opts);
});

builder.Services.AddSingleton<AllureToAdoSyncService>();
builder.Services.AddSingleton<RecordingToAdoSyncService>();
builder.Services.AddSingleton<PlaywrightJsonToAdoSyncService>();

builder.Services.AddEndpointsApiExplorer();
builder.Services.AddSwaggerGen();
builder.Services.AddMemoryCache();

var app = builder.Build();

app.UseSwagger();
app.UseSwaggerUI();
app.UseStaticFiles();

// Friendly default route so browser launch doesn't 404.
app.MapGet("/", () => Results.Redirect("/main.html"));
app.MapGet("/ui", () => Results.Redirect("/main.html"));
app.MapGet("/health", () => Results.Ok(new { ok = true }));
app.MapGet("/debug/ado", (AdoOptions ado) =>
    Results.Ok(new
    {
        ado.BaseProjectUrl,
        ado.ProjectName,
        ado.WitApiVersion,
        ado.TestPlanApiVersion,
        ado.TestApiVersion,
        ado.AreaPath,
        ado.IterationPath,
        ado.PlanId,
        ado.SuiteId
    }));

app.MapGet("/debug/configurations", async (
    string? testPlanApiVersion,
    AdoClient client,
    CancellationToken ct) =>
{
    var configs = await client.ListTestPlanConfigurations(ct, testPlanApiVersion);
    return Results.Ok(new { count = configs.Count, items = configs });
});

app.MapGet("/debug/testplans", async (
    string? testPlanApiVersion,
    AdoClient client,
    CancellationToken ct) =>
{
    var plans = await client.ListTestPlans(ct, testPlanApiVersion);
    return Results.Ok(new { count = plans.Count, items = plans });
});

app.MapGet("/debug/testsuites", async (
    int planId,
    string? testPlanApiVersion,
    AdoClient client,
    CancellationToken ct) =>
{
    var suites = await client.ListTestSuites(planId, ct, testPlanApiVersion);
    return Results.Ok(new { planId, count = suites.Count, items = suites });
});

app.MapGet("/debug/suite-testcases", async (
    int? planId,
    int? suiteId,
    string? testPlanApiVersion,
    AdoOptions ado,
    AdoClient client,
    CancellationToken ct) =>
{
    var p = planId is > 0 ? planId.Value : ado.PlanId;
    var s = suiteId is > 0 ? suiteId.Value : ado.SuiteId;

    var items = await client.ListSuiteTestCases(p, s, ct, testPlanApiVersion);
    return Results.Ok(new { planId = p, suiteId = s, count = items.Count, items });
});

app.MapGet("/debug/suite-testcases-raw", async (
    int? planId,
    int? suiteId,
    string? testPlanApiVersion,
    AdoOptions ado,
    AdoClient client,
    CancellationToken ct) =>
{
    var p = planId is > 0 ? planId.Value : ado.PlanId;
    var s = suiteId is > 0 ? suiteId.Value : ado.SuiteId;

    var raw = await client.GetSuiteTestCasesRaw(p, s, ct, testPlanApiVersion);
    // return truncated to avoid huge payloads in dev tools
    var truncated = raw.Length > 8000 ? raw[..8000] : raw;
    return Results.Ok(new { planId = p, suiteId = s, raw = truncated });
});

// Sync from raw Allure JSON sent in request body (object or array of objects).
// planId/suiteId are provided via query string, e.g. POST /sync?planId=11937&suiteId=12115
app.MapPost("/sync", async (
    HttpRequest http,
    int? planId,
    int? suiteId,
    int? configurationId,
    string? witApiVersion,
    string? testPlanApiVersion,
    string? testApiVersion,
    AllureToAdoSyncService sync,
    CancellationToken ct) =>
{
    using var doc = await JsonDocument.ParseAsync(http.Body, cancellationToken: ct);

    List<AllureResult> results;
    if (doc.RootElement.ValueKind == JsonValueKind.Object)
    {
        var one = doc.RootElement.Deserialize<AllureResult>();
        results = one is null ? new() : new() { one };
    }
    else if (doc.RootElement.ValueKind == JsonValueKind.Array)
    {
        results = doc.RootElement.Deserialize<List<AllureResult>>() ?? new();
    }
    else
    {
        return Results.BadRequest("Body must be an Allure result.json object or an array of such objects.");
    }

    var summary = await sync.SyncFromAllureResults(
        allureResults: results,
        planId: planId,
        suiteId: suiteId,
        configurationId: configurationId,
        apiVersions: new ApiVersionOverrides(witApiVersion, testPlanApiVersion, testApiVersion),
        ct: ct);

    return Results.Ok(summary);
});

// Sync from browser recording JSON (test.name + test.steps[]).
// planId/suiteId/configurationId are provided via query string, e.g.:
// POST /sync-recording?planId=11937&suiteId=29357&configurationId=2
app.MapPost("/sync-recording", async (
    [FromBody] RecordingPayload payload,
    int? planId,
    int? suiteId,
    int? configurationId,
    string? witApiVersion,
    string? testPlanApiVersion,
    string? testApiVersion,
    RecordingToAdoSyncService sync,
    CancellationToken ct) =>
{
    var summary = await sync.SyncFromRecording(
        payload: payload,
        planId: planId,
        suiteId: suiteId,
        configurationId: configurationId,
        apiVersions: new ApiVersionOverrides(witApiVersion, testPlanApiVersion, testApiVersion),
        ct: ct);

    return Results.Ok(summary);
});

// Legacy endpoint: sync from a local folder containing *-result.json files
app.MapPost("/sync-folder", async ([FromBody] SyncFolderRequest request, AllureToAdoSyncService sync, CancellationToken ct) =>
{
    var summary = await sync.SyncFromAllureResultsDirectory(
        allureResultsPath: request.AllureResultsPath,
        planId: request.PlanId,
        suiteId: request.SuiteId,
        configurationId: null,
        apiVersions: null,
        ct: ct);

    return Results.Ok(summary);
});

// Sync from Playwright JSON report (results.json) sent in request body.
// planId/suiteId/configurationId are provided via query string, e.g.:
// POST /sync-playwright-json?planId=11937&suiteId=29357&configurationId=2
app.MapPost("/sync-playwright-json", async (
    HttpRequest http,
    int? planId,
    int? suiteId,
    int? configurationId,
    string? witApiVersion,
    string? testPlanApiVersion,
    string? testApiVersion,
    PlaywrightJsonToAdoSyncService sync,
    CancellationToken ct) =>
{
    using var doc = await JsonDocument.ParseAsync(http.Body, cancellationToken: ct);
    if (doc.RootElement.ValueKind != JsonValueKind.Object)
        return Results.BadRequest("Body must be a Playwright JSON report object.");

    PlaywrightReport report;
    try
    {
        var json = doc.RootElement.GetRawText();
        report = sync.ParseReportJson(json);
    }
    catch (Exception ex)
    {
        return Results.BadRequest($"Failed to parse Playwright JSON: {ex.Message}");
    }

    var summary = await sync.SyncFromPlaywrightReport(
        report: report,
        planId: planId,
        suiteId: suiteId,
        configurationId: configurationId,
        apiVersions: new ApiVersionOverrides(witApiVersion, testPlanApiVersion, testApiVersion),
        ct: ct);

    return Results.Ok(summary);
});

app.MapPost("/test-generator", async (HttpRequest req) =>
{
    using var doc = await JsonDocument.ParseAsync(req.Body);
    var root = doc.RootElement;

    if (root.ValueKind != JsonValueKind.Object)
        return Results.BadRequest("Body must be JSON object.");

    if (!root.TryGetProperty("json", out var jsonEl) || jsonEl.ValueKind != JsonValueKind.String)
        return Results.BadRequest("Missing 'json' string.");

    var json = jsonEl.GetString() ?? string.Empty;
    if (string.IsNullOrWhiteSpace(json))
        return Results.BadRequest("JSON payload is empty.");

    var baseName = "Generated";
    if (root.TryGetProperty("baseName", out var nameEl) && nameEl.ValueKind == JsonValueKind.String)
    {
        var candidate = nameEl.GetString();
        if (!string.IsNullOrWhiteSpace(candidate))
            baseName = candidate!.Trim();
    }

    try
    {
        var zipBytes = TestGeneratorService.GenerateZipFromJson(json, baseName);
        var fileName = $"{baseName}.zip";
        return Results.File(zipBytes, "application/zip", fileName);
    }
    catch (Exception ex)
    {
        return Results.BadRequest(ex.Message);
    }
});

app.MapPost("/test-generator-ts", async (HttpRequest req) =>
{
    using var doc = await JsonDocument.ParseAsync(req.Body);
    var root = doc.RootElement;

    if (root.ValueKind != JsonValueKind.Object)
        return Results.BadRequest("Body must be JSON object.");

    if (!root.TryGetProperty("json", out var jsonEl) || jsonEl.ValueKind != JsonValueKind.String)
        return Results.BadRequest("Missing 'json' string.");

    var json = jsonEl.GetString() ?? string.Empty;
    if (string.IsNullOrWhiteSpace(json))
        return Results.BadRequest("JSON payload is empty.");

    var baseName = "Generated";
    if (root.TryGetProperty("baseName", out var nameEl) && nameEl.ValueKind == JsonValueKind.String)
    {
        var candidate = nameEl.GetString();
        if (!string.IsNullOrWhiteSpace(candidate))
            baseName = candidate!.Trim();
    }

    try
    {
        var zipBytes = TestGeneratorService.GenerateZipFromJsonTs(json, baseName);
        var fileName = $"{baseName}.zip";
        return Results.File(zipBytes, "application/zip", fileName);
    }
    catch (Exception ex)
    {
        return Results.BadRequest(ex.Message);
    }
});

app.MapPost("/coverage/calc", async (
    [FromBody] CoverageCalcRequest request,
    GroqClient groq,
    IMemoryCache cache,
    CancellationToken ct) =>
{
    var apiKey = Environment.GetEnvironmentVariable("GROQ_API_KEY");
    if (string.IsNullOrWhiteSpace(apiKey))
        return Results.BadRequest("GROQ_API_KEY is not set.");

    var reportKey = string.IsNullOrWhiteSpace(request.TeamId) ? null : $"coverage:report:{request.TeamId}";
    var reportTitles = reportKey is null ? null : cache.Get<IReadOnlyList<string>>(reportKey);
    var prompt = CoveragePromptBuilder.Build(request, reportTitles);

    try
    {
        var result = await groq.ChatAsync(prompt, systemPrompt: null, ct);
        return Results.Ok(new { result });
    }
    catch (Exception ex)
    {
        return Results.BadRequest(ex.Message);
    }
});

var webRootPath = string.IsNullOrWhiteSpace(app.Environment.WebRootPath)
    ? Path.Combine(app.Environment.ContentRootPath, "wwwroot")
    : app.Environment.WebRootPath;

app.MapPost("/coverage/report-cache", (
    [FromBody] CoverageReportCacheRequest request,
    IMemoryCache cache) =>
{
    if (string.IsNullOrWhiteSpace(request.TeamId))
        return Results.BadRequest("TeamId is required.");

    if (string.IsNullOrWhiteSpace(request.ReportJson))
        return Results.BadRequest("ReportJson is required.");

    IReadOnlyList<string> titles;
    try
    {
        titles = CoverageReportTitleExtractor.ExtractTitles(request.ReportJson);
    }
    catch (Exception ex)
    {
        return Results.BadRequest($"Failed to parse report JSON: {ex.Message}");
    }

    var key = $"coverage:report:{request.TeamId}";
    cache.Set(key, titles, TimeSpan.FromMinutes(10));
    return Results.Ok(new { ok = true, count = titles.Count });
});

app.MapPost("/coverage/report-zip", async (
    HttpRequest http,
    string? teamId,
    IMemoryCache cache,
    CancellationToken ct) =>
{
    if (string.IsNullOrWhiteSpace(teamId))
        return Results.BadRequest("TeamId is required.");

    if (!http.HasFormContentType)
        return Results.BadRequest("Expected multipart/form-data with a ZIP file.");

    var form = await http.ReadFormAsync(ct);
    var file = form.Files.FirstOrDefault();
    if (file is null || file.Length == 0)
        return Results.BadRequest("ZIP file is required.");

    var results = new List<AllureResult>();
    try
    {
        using var archive = new ZipArchive(file.OpenReadStream(), ZipArchiveMode.Read, leaveOpen: false);
        foreach (var entry in archive.Entries)
        {
            if (!entry.Name.EndsWith("-result.json", StringComparison.OrdinalIgnoreCase))
                continue;

            using var stream = entry.Open();
            using var reader = new StreamReader(stream, Encoding.UTF8, detectEncodingFromByteOrderMarks: true);
            var json = await reader.ReadToEndAsync();
            var result = AllureParsing.ParseResultJson(json);
            results.Add(result);
        }
    }
    catch (Exception ex)
    {
        return Results.BadRequest($"Failed to parse ZIP: {ex.Message}");
    }

    if (results.Count == 0)
        return Results.BadRequest("ZIP does not contain any *-result.json files.");

    var titles = AllureParsing.ExtractTitles(results);
    var tests = results
        .Select(r =>
        {
            var title = r.Name?.Trim();
            if (string.IsNullOrWhiteSpace(title))
                title = r.FullName?.Trim();

            var steps = AllureParsing.FlattenSteps(r.Steps)
                .Select(s => s.Action)
                .Where(s => !string.IsNullOrWhiteSpace(s))
                .ToList();

            return new CoverageAutomationTestCase(
                Title: title ?? string.Empty,
                FullName: string.IsNullOrWhiteSpace(r.FullName) ? null : r.FullName.Trim(),
                Status: string.IsNullOrWhiteSpace(r.Status) ? null : r.Status.Trim(),
                Uuid: string.IsNullOrWhiteSpace(r.Uuid) ? null : r.Uuid.Trim(),
                Steps: steps);
        })
        .Where(t => !string.IsNullOrWhiteSpace(t.Title))
        .ToList();

    var report = new CoverageAutomationReportSummary(tests.Count, tests);
    var key = $"coverage:report:{teamId}";
    cache.Set(key, titles, TimeSpan.FromMinutes(10));
    cache.Set($"coverage:report-model:{teamId}", report, TimeSpan.FromMinutes(10));
    await PersistCoverageReportJson(report, teamId, webRootPath, ct);

    return Results.Ok(new { ok = true, count = titles.Count, report });
});

app.MapGet("/coverage/embedding-report", async (
    int planId,
    string teamId,
    string? testPlanApiVersion,
    string? witApiVersion,
    bool? force,
    int? topK,
    double? minScore,
    bool? llm,
    int? llmMaxMatches,
    int? llmMaxGaps,
    AdoClient client,
    IMemoryCache cache,
    AiOptions aiOptions,
    LocalEmbeddingClient embeddings,
    GroqClient groq,
    CancellationToken ct) =>
{
    if (string.IsNullOrWhiteSpace(teamId))
        return Results.BadRequest("teamId is required.");

    var report = await LoadCoverageReportSummary(teamId, webRootPath, cache, ct);
    var plan = await GetCoverageSummary(
        planId,
        testPlanApiVersion,
        witApiVersion,
        force == true,
        client,
        cache,
        webRootPath,
        ct);

    var embeddingModel = aiOptions.EmbeddingModel ?? string.Empty;
    var effectiveTopK = topK is > 0 ? topK.Value : 5;
    var effectiveMinScore = minScore is > 0 ? minScore.Value : 0.75;

    var adoSources = BuildAdoEmbeddingSources(plan);
    var reportSources = BuildAllureEmbeddingSources(report);

    var embeddingsDir = Path.Combine(webRootPath, "coverage");
    var adoEmbeddingsPath = Path.Combine(embeddingsDir, $"embeddings-ado-plan-{planId}.json");
    var reportEmbeddingsPath = Path.Combine(embeddingsDir, $"embeddings-allure-{teamId}.json");

    var adoEmbeddings = await GetOrCreateEmbeddingsFile(
        adoEmbeddingsPath,
        embeddingModel,
        adoSources,
        embeddings,
        force == true,
        ct);

    var reportEmbeddings = await GetOrCreateEmbeddingsFile(
        reportEmbeddingsPath,
        embeddingModel,
        reportSources,
        embeddings,
        force == true,
        ct);

    var result = BuildEmbeddingReport(
        plan,
        report,
        adoEmbeddings,
        reportEmbeddings,
        teamId,
        embeddingModel,
        effectiveMinScore,
        effectiveTopK);

    await PersistCoverageEmbeddingReport(result, planId, teamId, webRootPath, ct);

    if (llm == true)
    {
        var apiKey = Environment.GetEnvironmentVariable("GROQ_API_KEY");
        if (string.IsNullOrWhiteSpace(apiKey))
            return Results.BadRequest("GROQ_API_KEY is not set.");

        var maxMatches = llmMaxMatches is > 0 ? llmMaxMatches.Value : 20;
        var maxGaps = llmMaxGaps is > 0 ? llmMaxGaps.Value : 20;
        var llmReport = await BuildLlmEmbeddingReport(
            plan,
            report,
            result,
            groq,
            maxMatches,
            maxGaps,
            ct);

        await PersistCoverageEmbeddingLlmReport(llmReport, planId, teamId, webRootPath, ct);
        return Results.Ok(new { report = result, llm = llmReport });
    }

    return Results.Ok(result);
});

app.MapGet("/coverage/pipeline", async (
    int planId,
    string teamId,
    int? sourceWorkItemId,
    int? topK,
    int? maxCandidates,
    double? minScore,
    bool? llm,
    int? llmMaxSources,
    AdoClient client,
    IMemoryCache cache,
    AiOptions aiOptions,
    LocalEmbeddingClient embeddings,
    GroqClient groq,
    CancellationToken ct) =>
{
    if (string.IsNullOrWhiteSpace(teamId))
        return Results.BadRequest("teamId is required.");

    var report = await LoadCoverageReportSummary(teamId, webRootPath, cache, ct);
    var plan = await GetCoverageSummary(
        planId,
        testPlanApiVersion: null,
        witApiVersion: null,
        force: false,
        client,
        cache,
        webRootPath,
        ct);

    var adoEntities = NormalizeAdoPlan(plan);
    var allureEntities = NormalizeAllureReport(report);

    if (sourceWorkItemId is > 0)
        adoEntities = adoEntities.Where(e => e.Id == sourceWorkItemId.Value.ToString()).ToList();

    var embeddingModel = aiOptions.EmbeddingModel ?? string.Empty;
    var effectiveTopK = topK is > 0 ? topK.Value : 5;
    var effectiveMaxCandidates = maxCandidates is > 0 ? maxCandidates.Value : 5;
    var effectiveMinScore = minScore is > 0 ? minScore.Value : 0.75;
    var effectiveLlmMaxSources = llmMaxSources is > 0 ? llmMaxSources.Value : 5;

    var embeddingDir = Path.Combine(webRootPath, "coverage");
    var adoEmbeddingsPath = Path.Combine(embeddingDir, $"embeddings-normalized-ado-plan-{planId}.json");
    var allureEmbeddingsPath = Path.Combine(embeddingDir, $"embeddings-normalized-allure-{teamId}.json");

    var adoEmbeddingSources = adoEntities.Select(e =>
        new CoverageEmbeddingSource(e.Id, BuildTextForEmbedding(e))).ToList();
    var allureEmbeddingSources = allureEntities.Select(e =>
        new CoverageEmbeddingSource(e.Id, BuildTextForEmbedding(e))).ToList();

    var adoEmbeddings = await GetOrCreateEmbeddingsFile(
        adoEmbeddingsPath,
        embeddingModel,
        adoEmbeddingSources,
        embeddings,
        force: false,
        ct);

    var allureEmbeddings = await GetOrCreateEmbeddingsFile(
        allureEmbeddingsPath,
        embeddingModel,
        allureEmbeddingSources,
        embeddings,
        force: false,
        ct);

    var allureVectors = allureEmbeddings.Items.Select(i => NormalizeVector(i.Vector)).ToList();
    var allureMap = allureEntities.ToDictionary(e => e.Id, e => e);

    var results = new List<CoveragePipelineResult>();
    var llmEnabled = llm == true;
    var llmSourcesUsed = 0;

    foreach (var source in adoEntities)
    {
        var ruleMatches = FindRuleMatches(source, allureEntities);
        var strongRule = ruleMatches.Any(m => m.Score >= 0.99);

        var vectorMatches = strongRule
            ? new List<VectorMatch>()
            : FindVectorMatches(source, adoEmbeddings, allureEmbeddings, effectiveTopK, effectiveMinScore);

        var candidates = SelectCandidates(source, allureEntities, ruleMatches, vectorMatches, effectiveMaxCandidates);

        CoverageDecision? decision = null;
        if (llmEnabled && llmSourcesUsed < effectiveLlmMaxSources)
        {
            var payload = BuildLlmPayload(source, candidates);
            decision = await CompareWithLlm(payload, groq, ct);
            llmSourcesUsed++;
        }

        results.Add(new CoveragePipelineResult(source, candidates, ruleMatches, vectorMatches, decision));
    }

    return Results.Ok(new { count = results.Count, results });
});

app.MapGet("/coverage/plan-summary", async (
    int planId,
    string? testPlanApiVersion,
    string? witApiVersion,
    bool? force,
    AdoClient client,
    IMemoryCache cache,
    CancellationToken ct) =>
{
    var summary = await GetCoverageSummary(
        planId,
        testPlanApiVersion,
        witApiVersion,
        force == true,
        client,
        cache,
        webRootPath,
        ct);
    return Results.Ok(summary);
});

app.MapGet("/coverage/export", async (
    int planId,
    string? testPlanApiVersion,
    string? witApiVersion,
    bool? force,
    AdoClient client,
    IMemoryCache cache,
    CancellationToken ct) =>
{
    var summary = await GetCoverageSummary(
        planId,
        testPlanApiVersion,
        witApiVersion,
        force == true,
        client,
        cache,
        webRootPath,
        ct);
    var json = JsonSerializer.Serialize(summary, new JsonSerializerOptions { WriteIndented = true });
    var bytes = Encoding.UTF8.GetBytes(json);
    var fileName = $"coverage-plan-{planId}.json";
    return Results.File(bytes, "application/json", fileName);
});

static async Task<CoveragePlanSummary> GetCoverageSummary(
    int planId,
    string? testPlanApiVersion,
    string? witApiVersion,
    bool force,
    AdoClient client,
    IMemoryCache cache,
    string webRootPath,
    CancellationToken ct)
{
    var cacheKey = $"coverage:plan:{planId}:tp:{testPlanApiVersion ?? ""}:wit:{witApiVersion ?? ""}";
    if (!force && cache.TryGetValue(cacheKey, out CoveragePlanSummary cached))
        return cached;

    var suites = await client.ListTestSuites(planId, ct, testPlanApiVersion);
    var suiteSummaries = new List<CoverageSuiteSummary>();
    var totalTests = 0;

    foreach (var suite in suites)
    {
        ct.ThrowIfCancellationRequested();

        var tests = await client.ListSuiteTestCases(planId, suite.Id, ct, testPlanApiVersion);
        var testSummaries = new List<CoverageTestCaseSummary>();

        foreach (var t in tests)
        {
            ct.ThrowIfCancellationRequested();

            var details = await client.GetTestCaseDetails(t.WorkItemId, ct, witApiVersion);
            var title = details?.Title ?? t.Title;
            var stepsRaw = details?.Steps ?? string.Empty;
            var steps = NormalizeText(StripHtml(stepsRaw));

            testSummaries.Add(new CoverageTestCaseSummary(t.WorkItemId, title, steps));
        }

        totalTests += testSummaries.Count;
        suiteSummaries.Add(new CoverageSuiteSummary(suite.Id, suite.Name, testSummaries));
    }

    var summary = new CoveragePlanSummary(planId, totalTests, suiteSummaries);
    cache.Set(cacheKey, summary, TimeSpan.FromMinutes(10));
    await PersistCoveragePlanJson(summary, planId, webRootPath, ct);
    return summary;
}

static async Task PersistCoveragePlanJson(
    CoveragePlanSummary summary,
    int planId,
    string webRootPath,
    CancellationToken ct)
{
    var coverageDir = Path.Combine(webRootPath, "coverage");
    Directory.CreateDirectory(coverageDir);

    var json = JsonSerializer.Serialize(summary, new JsonSerializerOptions { WriteIndented = true });
    var planFile = Path.Combine(coverageDir, $"coverage-plan-{planId}.json");
    var latestFile = Path.Combine(coverageDir, "coverage-plan.json");

    await File.WriteAllTextAsync(planFile, json, ct);
    await File.WriteAllTextAsync(latestFile, json, ct);
}

static async Task PersistCoverageReportJson(
    CoverageAutomationReportSummary report,
    string teamId,
    string webRootPath,
    CancellationToken ct)
{
    var coverageDir = Path.Combine(webRootPath, "coverage");
    Directory.CreateDirectory(coverageDir);

    var json = JsonSerializer.Serialize(report, new JsonSerializerOptions { WriteIndented = true });
    var reportFile = Path.Combine(coverageDir, $"coverage-report-{teamId}.json");
    var latestFile = Path.Combine(coverageDir, "coverage-report.json");

    await File.WriteAllTextAsync(reportFile, json, ct);
    await File.WriteAllTextAsync(latestFile, json, ct);
}

static async Task<CoverageAutomationReportSummary> LoadCoverageReportSummary(
    string teamId,
    string webRootPath,
    IMemoryCache cache,
    CancellationToken ct)
{
    var cacheKey = $"coverage:report-model:{teamId}";
    if (cache.TryGetValue(cacheKey, out CoverageAutomationReportSummary cached))
        return cached;

    var coverageDir = Path.Combine(webRootPath, "coverage");
    var reportFile = Path.Combine(coverageDir, $"coverage-report-{teamId}.json");
    if (!File.Exists(reportFile))
    {
        var latestFile = Path.Combine(coverageDir, "coverage-report.json");
        if (!File.Exists(latestFile))
            throw new InvalidOperationException($"Coverage report file not found for teamId '{teamId}'. Upload Allure ZIP first.");

        reportFile = latestFile;
    }

    var json = await File.ReadAllTextAsync(reportFile, ct);
    var report = JsonSerializer.Deserialize<CoverageAutomationReportSummary>(json, new JsonSerializerOptions
    {
        PropertyNameCaseInsensitive = true
    });

    if (report is null)
        throw new InvalidOperationException("Failed to deserialize coverage report.");

    cache.Set(cacheKey, report, TimeSpan.FromMinutes(10));
    return report;
}

static IReadOnlyList<CoverageEmbeddingSource> BuildAdoEmbeddingSources(CoveragePlanSummary plan)
{
    var sources = new List<CoverageEmbeddingSource>();
    foreach (var suite in plan.Suites)
    {
        foreach (var test in suite.Tests)
        {
            var text = NormalizeText($"{suite.Name}\n{test.Title}\n{test.Steps}");
            sources.Add(new CoverageEmbeddingSource(test.WorkItemId.ToString(), text));
        }
    }

    return sources;
}

static IReadOnlyList<CoverageEmbeddingSource> BuildAllureEmbeddingSources(CoverageAutomationReportSummary report)
{
    var sources = new List<CoverageEmbeddingSource>();
    var ids = BuildAllureIds(report.Tests);

    for (var i = 0; i < report.Tests.Count; i++)
    {
        var t = report.Tests[i];
        var steps = t.Steps is { Count: > 0 } ? string.Join("\n", t.Steps) : string.Empty;
        var text = NormalizeText($"{t.Title}\n{t.FullName ?? string.Empty}\n{steps}");
        sources.Add(new CoverageEmbeddingSource(ids[i], text));
    }

    return sources;
}

static IReadOnlyList<string> BuildAllureIds(IReadOnlyList<CoverageAutomationTestCase> tests)
{
    var ids = new List<string>(tests.Count);
    var used = new HashSet<string>(StringComparer.OrdinalIgnoreCase);

    for (var i = 0; i < tests.Count; i++)
    {
        var t = tests[i];
        var id = !string.IsNullOrWhiteSpace(t.Uuid)
            ? t.Uuid!
            : !string.IsNullOrWhiteSpace(t.FullName)
                ? t.FullName!
                : !string.IsNullOrWhiteSpace(t.Title)
                    ? t.Title
                    : $"idx:{i}";

        var baseId = id;
        var suffix = 1;
        while (!used.Add(id))
        {
            id = $"{baseId}:{suffix}";
            suffix++;
        }

        ids.Add(id);
    }

    return ids;
}

static async Task<CoverageEmbeddingFile> GetOrCreateEmbeddingsFile(
    string filePath,
    string model,
    IReadOnlyList<CoverageEmbeddingSource> sources,
    LocalEmbeddingClient embeddings,
    bool force,
    CancellationToken ct)
{
    if (string.IsNullOrWhiteSpace(model))
        throw new InvalidOperationException("Embedding model is not configured.");

    if (!force && File.Exists(filePath))
    {
        var existingJson = await File.ReadAllTextAsync(filePath, ct);
        var existing = JsonSerializer.Deserialize<CoverageEmbeddingFile>(existingJson, new JsonSerializerOptions
        {
            PropertyNameCaseInsensitive = true
        });

        if (existing is not null
            && string.Equals(existing.Model, model, StringComparison.OrdinalIgnoreCase)
            && existing.Items.Count == sources.Count
            && existing.Items.Select(i => i.Id).SequenceEqual(sources.Select(s => s.Id))
            && existing.Items.Select(i => i.Text).SequenceEqual(sources.Select(s => s.Text)))
        {
            return existing;
        }
    }

    if (sources.Count == 0)
    {
        var emptyFile = new CoverageEmbeddingFile(model, Array.Empty<CoverageEmbeddingItem>());
        var emptyJson = JsonSerializer.Serialize(emptyFile, new JsonSerializerOptions { WriteIndented = true });
        Directory.CreateDirectory(Path.GetDirectoryName(filePath)!);
        await File.WriteAllTextAsync(filePath, emptyJson, ct);
        return emptyFile;
    }

    var vectors = await EmbedSourcesWithChunking(embeddings, sources, ct);
    if (vectors.Count != sources.Count)
        throw new InvalidOperationException("Embedding count does not match input count.");

    var items = sources.Zip(vectors, (s, v) => new CoverageEmbeddingItem(s.Id, s.Text, v)).ToList();
    var file = new CoverageEmbeddingFile(model, items);
    var json = JsonSerializer.Serialize(file, new JsonSerializerOptions { WriteIndented = true });

    Directory.CreateDirectory(Path.GetDirectoryName(filePath)!);
    await File.WriteAllTextAsync(filePath, json, ct);
    return file;
}

static async Task<IReadOnlyList<float[]>> EmbedInBatches(
    LocalEmbeddingClient embeddings,
    IReadOnlyList<string> inputs,
    CancellationToken ct)
{
    const int batchSize = 64;
    var output = new List<float[]>(inputs.Count);

    for (var i = 0; i < inputs.Count; i += batchSize)
    {
        var batch = inputs.Skip(i).Take(batchSize).ToList();
        var vectors = await embeddings.EmbedAsync(batch, ct);
        output.AddRange(vectors);
    }

    return output;
}

static async Task<IReadOnlyList<float[]>> EmbedSourcesWithChunking(
    LocalEmbeddingClient embeddings,
    IReadOnlyList<CoverageEmbeddingSource> sources,
    CancellationToken ct)
{
    const int maxChunkChars = 2000;
    var allChunks = new List<string>();
    var sourceChunkRanges = new List<(int Start, int Count)>(sources.Count);

    foreach (var source in sources)
    {
        var chunks = SplitTextIntoChunks(source.Text, maxChunkChars);
        var start = allChunks.Count;
        allChunks.AddRange(chunks);
        sourceChunkRanges.Add((start, chunks.Count));
    }

    if (allChunks.Count == 0)
        return Array.Empty<float[]>();

    var chunkVectors = await EmbedInBatches(embeddings, allChunks, ct);
    if (chunkVectors.Count != allChunks.Count)
        throw new InvalidOperationException("Chunk embedding count does not match input count.");

    var output = new List<float[]>(sources.Count);
    foreach (var (start, count) in sourceChunkRanges)
    {
        if (count == 0)
        {
            output.Add(Array.Empty<float>());
            continue;
        }

        var vectors = chunkVectors.Skip(start).Take(count).ToList();
        output.Add(AverageVectors(vectors));
    }

    return output;
}

static IReadOnlyList<string> SplitTextIntoChunks(string text, int maxChars)
{
    if (string.IsNullOrWhiteSpace(text))
        return Array.Empty<string>();

    var chunks = new List<string>();
    var start = 0;
    var length = text.Length;

    while (start < length)
    {
        var remaining = length - start;
        var take = Math.Min(maxChars, remaining);
        var end = start + take;

        if (end < length)
        {
            var lastSpace = text.LastIndexOf(' ', end, take);
            if (lastSpace > start + 50)
                end = lastSpace;
        }

        var chunk = text[start..end].Trim();
        if (!string.IsNullOrWhiteSpace(chunk))
            chunks.Add(chunk);

        start = end;
    }

    return chunks;
}

static float[] AverageVectors(IReadOnlyList<float[]> vectors)
{
    if (vectors.Count == 0)
        return Array.Empty<float>();

    var size = vectors.Max(v => v.Length);
    if (size == 0)
        return Array.Empty<float>();

    var sum = new double[size];
    foreach (var vec in vectors)
    {
        var len = vec.Length;
        for (var i = 0; i < size; i++)
            sum[i] += i < len ? vec[i] : 0;
    }

    var output = new float[size];
    for (var i = 0; i < size; i++)
        output[i] = (float)(sum[i] / vectors.Count);

    return output;
}

static CoverageEmbeddingReport BuildEmbeddingReport(
    CoveragePlanSummary plan,
    CoverageAutomationReportSummary report,
    CoverageEmbeddingFile adoEmbeddings,
    CoverageEmbeddingFile reportEmbeddings,
    string teamId,
    string model,
    double minScore,
    int topK)
{
    var adoMeta = BuildAdoMeta(plan);
    var allureMeta = BuildAllureMeta(report);

    if (adoEmbeddings.Items.Count != adoMeta.Count || reportEmbeddings.Items.Count != allureMeta.Count)
        throw new InvalidOperationException("Embedding metadata mismatch.");

    var reportVectors = reportEmbeddings.Items.Select(i => NormalizeVector(i.Vector)).ToList();
    var usedAllure = new HashSet<int>();
    var matches = new List<CoverageEmbeddingMatch>();
    var unmatchedAdo = new List<CoverageEmbeddingUnmatchedAdo>();

    if (reportVectors.Count == 0)
    {
        unmatchedAdo.AddRange(adoMeta.Select(a => new CoverageEmbeddingUnmatchedAdo(a.WorkItemId, a.Title, a.Suite)));
        return new CoverageEmbeddingReport(
            plan.PlanId,
            teamId,
            adoMeta.Count,
            0,
            0,
            0,
            model,
            minScore,
            topK,
            matches,
            unmatchedAdo,
            new List<CoverageEmbeddingUnmatchedAllure>());
    }

    for (var i = 0; i < adoEmbeddings.Items.Count; i++)
    {
        var adoVector = NormalizeVector(adoEmbeddings.Items[i].Vector);
        var candidates = new List<(int Index, double Score)>(reportVectors.Count);

        for (var j = 0; j < reportVectors.Count; j++)
        {
            var score = Dot(adoVector, reportVectors[j]);
            candidates.Add((j, score));
        }

        var best = candidates
            .OrderByDescending(c => c.Score)
            .Take(topK)
            .FirstOrDefault(c => !usedAllure.Contains(c.Index));

        if (best.Score >= minScore && best.Index >= 0)
        {
            usedAllure.Add(best.Index);
            var ado = adoMeta[i];
            var allure = allureMeta[best.Index];
            matches.Add(new CoverageEmbeddingMatch(
                ado.WorkItemId,
                ado.Title,
                ado.Suite,
                allure.Title,
                allure.FullName,
                allure.Uuid,
                best.Score));
        }
        else
        {
            var ado = adoMeta[i];
            unmatchedAdo.Add(new CoverageEmbeddingUnmatchedAdo(ado.WorkItemId, ado.Title, ado.Suite));
        }
    }

    var unmatchedAllure = allureMeta
        .Where((_, index) => !usedAllure.Contains(index))
        .Select(a => new CoverageEmbeddingUnmatchedAllure(a.Title, a.FullName, a.Uuid))
        .ToList();

    var matchedCount = matches.Count;
    var totalAdo = adoMeta.Count;
    var totalAllure = allureMeta.Count;
    var coveragePercent = totalAdo == 0 ? 0 : Math.Round((double)matchedCount / totalAdo * 100, 2);

    return new CoverageEmbeddingReport(
        plan.PlanId,
        teamId,
        totalAdo,
        totalAllure,
        matchedCount,
        coveragePercent,
        model,
        minScore,
        topK,
        matches,
        unmatchedAdo,
        unmatchedAllure);
}

static IReadOnlyList<(int WorkItemId, string Title, string Suite)> BuildAdoMeta(CoveragePlanSummary plan)
{
    var output = new List<(int WorkItemId, string Title, string Suite)>();
    foreach (var suite in plan.Suites)
    {
        foreach (var test in suite.Tests)
            output.Add((test.WorkItemId, test.Title, suite.Name));
    }

    return output;
}

static IReadOnlyList<(string Title, string? FullName, string? Uuid)> BuildAllureMeta(CoverageAutomationReportSummary report)
{
    var output = new List<(string Title, string? FullName, string? Uuid)>();
    foreach (var test in report.Tests)
        output.Add((test.Title, test.FullName, test.Uuid));

    return output;
}

static float[] NormalizeVector(float[] vector)
{
    var sum = 0.0;
    for (var i = 0; i < vector.Length; i++)
        sum += vector[i] * vector[i];

    var norm = Math.Sqrt(sum);
    if (norm <= 0)
        return vector;

    var output = new float[vector.Length];
    for (var i = 0; i < vector.Length; i++)
        output[i] = (float)(vector[i] / norm);

    return output;
}

static double Dot(float[] a, float[] b)
{
    var len = Math.Min(a.Length, b.Length);
    double sum = 0;
    for (var i = 0; i < len; i++)
        sum += a[i] * b[i];

    return sum;
}

static string NormalizeText(string text)
{
    if (string.IsNullOrWhiteSpace(text))
        return string.Empty;

    var collapsed = Regex.Replace(text, @"\s+", " ").Trim();
    return collapsed.ToLowerInvariant();
}

static string StripHtml(string html)
{
    if (string.IsNullOrWhiteSpace(html))
        return string.Empty;

    var withoutTags = Regex.Replace(html, "<.*?>", " ");
    return withoutTags;
}

static async Task PersistCoverageEmbeddingReport(
    CoverageEmbeddingReport report,
    int planId,
    string teamId,
    string webRootPath,
    CancellationToken ct)
{
    var coverageDir = Path.Combine(webRootPath, "coverage");
    Directory.CreateDirectory(coverageDir);

    var json = JsonSerializer.Serialize(report, new JsonSerializerOptions { WriteIndented = true });
    var reportFile = Path.Combine(coverageDir, $"coverage-embedding-report-{planId}-{teamId}.json");
    var latestFile = Path.Combine(coverageDir, "coverage-embedding-report.json");

    await File.WriteAllTextAsync(reportFile, json, ct);
    await File.WriteAllTextAsync(latestFile, json, ct);
}

static async Task PersistCoverageEmbeddingLlmReport(
    CoverageEmbeddingLlmReport report,
    int planId,
    string teamId,
    string webRootPath,
    CancellationToken ct)
{
    var coverageDir = Path.Combine(webRootPath, "coverage");
    Directory.CreateDirectory(coverageDir);

    var json = JsonSerializer.Serialize(report, new JsonSerializerOptions { WriteIndented = true });
    var reportFile = Path.Combine(coverageDir, $"coverage-embedding-llm-report-{planId}-{teamId}.json");
    var latestFile = Path.Combine(coverageDir, "coverage-embedding-llm-report.json");

    await File.WriteAllTextAsync(reportFile, json, ct);
    await File.WriteAllTextAsync(latestFile, json, ct);
}

static async Task<CoverageEmbeddingLlmReport> BuildLlmEmbeddingReport(
    CoveragePlanSummary plan,
    CoverageAutomationReportSummary report,
    CoverageEmbeddingReport embeddingReport,
    GroqClient groq,
    int maxMatches,
    int maxGaps,
    CancellationToken ct)
{
    var matchResult = await ReviewMatchesWithLlm(
        plan,
        report,
        embeddingReport.Matches.Take(maxMatches).ToList(),
        groq,
        ct);

    var gapResult = await ExplainGapsWithLlm(
        plan,
        report,
        embeddingReport.UnmatchedAdo.Take(maxGaps).ToList(),
        embeddingReport.UnmatchedAllure.Take(maxGaps).ToList(),
        groq,
        ct);

    var tokenUsage = new CoverageLlmTokenUsage(
        matchResult.Usage.PromptTokens + gapResult.Usage.PromptTokens,
        matchResult.Usage.CompletionTokens + gapResult.Usage.CompletionTokens,
        matchResult.Usage.TotalTokens + gapResult.Usage.TotalTokens);

    return new CoverageEmbeddingLlmReport(
        embeddingReport.PlanId,
        embeddingReport.TeamId,
        "groq",
        tokenUsage,
        matchResult.Reviews,
        gapResult.Explanations,
        gapResult.MissingTests);
}

static async Task<(IReadOnlyList<CoverageLlmMatchReview> Reviews, CoverageLlmTokenUsage Usage)> ReviewMatchesWithLlm(
    CoveragePlanSummary plan,
    CoverageAutomationReportSummary report,
    IReadOnlyList<CoverageEmbeddingMatch> matches,
    GroqClient groq,
    CancellationToken ct)
{
    if (matches.Count == 0)
        return (Array.Empty<CoverageLlmMatchReview>(), new CoverageLlmTokenUsage(0, 0, 0));

    var adoLookup = BuildAdoDetails(plan);
    var allureLookup = BuildAllureDetails(report);

    var items = matches.Select(m =>
    {
        adoLookup.TryGetValue(m.WorkItemId, out var ado);

        var allure = allureLookup.FirstOrDefault(a => string.Equals(a.Title, m.AllureTitle, StringComparison.OrdinalIgnoreCase));
        if (string.IsNullOrWhiteSpace(allure.Title))
            allure = allureLookup.FirstOrDefault(a => string.Equals(a.Uuid, m.AllureUuid, StringComparison.OrdinalIgnoreCase));
        if (string.IsNullOrWhiteSpace(allure.Title))
            allure = allureLookup.FirstOrDefault(a => string.Equals(a.FullName, m.AllureFullName, StringComparison.OrdinalIgnoreCase));

        return new
        {
            workItemId = m.WorkItemId,
            adoTitle = m.AdoTitle,
            adoSteps = ado.Steps ?? string.Empty,
            allureTitle = m.AllureTitle,
            allureSteps = allure.Steps ?? string.Empty,
            score = m.Score
        };
    }).ToList();

    var payload = JsonSerializer.Serialize(new { matches = items }, new JsonSerializerOptions { WriteIndented = false });
    var prompt = $@"You are a QA analyst. For each pair decide if they truly match.
Return JSON only with schema:
{{ ""reviews"": [{{ ""workItemId"": 123, ""adoTitle"": ""..."", ""allureTitle"": ""..."", ""decision"": ""match|no_match|uncertain"", ""confidence"": 0.0, ""reason"": ""..."" }}] }}

Input:
{payload}";

    var response = await groq.ChatWithUsageAsync(prompt, systemPrompt: "Return strict JSON only.", ct);
    var reviews = ParseLlmReviews(response.Content);
    var usage = new CoverageLlmTokenUsage(response.PromptTokens, response.CompletionTokens, response.TotalTokens);
    return (reviews, usage);
}

static async Task<(IReadOnlyList<CoverageLlmGapExplanation> Explanations, IReadOnlyList<CoverageLlmMissingTest> MissingTests, CoverageLlmTokenUsage Usage)> ExplainGapsWithLlm(
    CoveragePlanSummary plan,
    CoverageAutomationReportSummary report,
    IReadOnlyList<CoverageEmbeddingUnmatchedAdo> unmatchedAdo,
    IReadOnlyList<CoverageEmbeddingUnmatchedAllure> unmatchedAllure,
    GroqClient groq,
    CancellationToken ct)
{
    if (unmatchedAdo.Count == 0 && unmatchedAllure.Count == 0)
        return (Array.Empty<CoverageLlmGapExplanation>(), Array.Empty<CoverageLlmMissingTest>(), new CoverageLlmTokenUsage(0, 0, 0));

    var adoLookup = BuildAdoDetails(plan);

    var input = new
    {
        unmatchedAdo = unmatchedAdo.Select(u =>
        {
            adoLookup.TryGetValue(u.WorkItemId, out var ado);
            return new
            {
                workItemId = u.WorkItemId,
                title = u.AdoTitle,
                suite = u.AdoSuite,
            steps = ado.Steps ?? string.Empty
            };
        }),
        unmatchedAllure = unmatchedAllure.Select(u => new
        {
            title = u.Title,
            fullName = u.FullName,
            uuid = u.Uuid
        })
    };

    var payload = JsonSerializer.Serialize(input, new JsonSerializerOptions { WriteIndented = false });
    var prompt = $@"You are a QA analyst. Explain gaps and suggest missing tests.
Return JSON only with schema:
{{
  ""gapExplanations"": [
    {{ ""kind"": ""ado_unmatched"", ""workItemId"": 123, ""adoTitle"": ""..."", ""reason"": ""..."" }},
    {{ ""kind"": ""allure_unmatched"", ""allureTitle"": ""..."", ""allureUuid"": ""..."", ""reason"": ""..."" }}
  ],
  ""missingTests"": [
    {{ ""kind"": ""ado_unmatched"", ""workItemId"": 123, ""title"": ""..."", ""steps"": [""...""] }},
    {{ ""kind"": ""allure_unmatched"", ""title"": ""..."", ""steps"": [""...""] }}
  ]
}}

Input:
{payload}";

    var response = await groq.ChatWithUsageAsync(prompt, systemPrompt: "Return strict JSON only.", ct);
    var (gaps, missingTests) = ParseLlmGaps(response.Content);
    var usage = new CoverageLlmTokenUsage(response.PromptTokens, response.CompletionTokens, response.TotalTokens);
    return (gaps, missingTests, usage);
}

static IReadOnlyList<CoverageLlmMatchReview> ParseLlmReviews(string json)
{
    try
    {
        using var doc = JsonDocument.Parse(json);
        if (!doc.RootElement.TryGetProperty("reviews", out var reviewsEl) || reviewsEl.ValueKind != JsonValueKind.Array)
            return Array.Empty<CoverageLlmMatchReview>();

        var list = new List<CoverageLlmMatchReview>();
        foreach (var item in reviewsEl.EnumerateArray())
        {
            var workItemId = item.TryGetProperty("workItemId", out var idEl) && idEl.TryGetInt32(out var idVal) ? idVal : 0;
            var adoTitle = item.TryGetProperty("adoTitle", out var adoTitleEl) ? adoTitleEl.GetString() ?? string.Empty : string.Empty;
            var allureTitle = item.TryGetProperty("allureTitle", out var allureTitleEl) ? allureTitleEl.GetString() : null;
            var decision = item.TryGetProperty("decision", out var decisionEl) ? decisionEl.GetString() ?? "uncertain" : "uncertain";
            var confidence = item.TryGetProperty("confidence", out var confEl) && confEl.TryGetDouble(out var confVal) ? confVal : 0;
            var reason = item.TryGetProperty("reason", out var reasonEl) ? reasonEl.GetString() ?? string.Empty : string.Empty;

            list.Add(new CoverageLlmMatchReview(workItemId, adoTitle, allureTitle, decision, confidence, reason));
        }

        return list;
    }
    catch
    {
        return Array.Empty<CoverageLlmMatchReview>();
    }
}

static (IReadOnlyList<CoverageLlmGapExplanation>, IReadOnlyList<CoverageLlmMissingTest>) ParseLlmGaps(string json)
{
    try
    {
        using var doc = JsonDocument.Parse(json);
        var gapExplanations = new List<CoverageLlmGapExplanation>();
        var missingTests = new List<CoverageLlmMissingTest>();

        if (doc.RootElement.TryGetProperty("gapExplanations", out var gapsEl) && gapsEl.ValueKind == JsonValueKind.Array)
        {
            foreach (var item in gapsEl.EnumerateArray())
            {
                var kind = item.TryGetProperty("kind", out var kindEl) ? kindEl.GetString() ?? string.Empty : string.Empty;
                int? workItemId = item.TryGetProperty("workItemId", out var idEl) && idEl.TryGetInt32(out var idVal) ? idVal : null;
                var adoTitle = item.TryGetProperty("adoTitle", out var adoEl) ? adoEl.GetString() : null;
                var allureTitle = item.TryGetProperty("allureTitle", out var allureTitleEl) ? allureTitleEl.GetString() : null;
                var allureUuid = item.TryGetProperty("allureUuid", out var allureUuidEl) ? allureUuidEl.GetString() : null;
                var reason = item.TryGetProperty("reason", out var reasonEl) ? reasonEl.GetString() ?? string.Empty : string.Empty;
                gapExplanations.Add(new CoverageLlmGapExplanation(kind, workItemId, adoTitle, allureTitle, allureUuid, reason));
            }
        }

        if (doc.RootElement.TryGetProperty("missingTests", out var missingEl) && missingEl.ValueKind == JsonValueKind.Array)
        {
            foreach (var item in missingEl.EnumerateArray())
            {
                var kind = item.TryGetProperty("kind", out var kindEl) ? kindEl.GetString() ?? string.Empty : string.Empty;
                int? workItemId = item.TryGetProperty("workItemId", out var idEl) && idEl.TryGetInt32(out var idVal) ? idVal : null;
                var title = item.TryGetProperty("title", out var titleEl) ? titleEl.GetString() ?? string.Empty : string.Empty;
                var steps = new List<string>();
                if (item.TryGetProperty("steps", out var stepsEl) && stepsEl.ValueKind == JsonValueKind.Array)
                {
                    foreach (var step in stepsEl.EnumerateArray())
                        steps.Add(step.GetString() ?? string.Empty);
                }

                missingTests.Add(new CoverageLlmMissingTest(kind, workItemId, title, steps));
            }
        }

        return (gapExplanations, missingTests);
    }
    catch
    {
        return (Array.Empty<CoverageLlmGapExplanation>(), Array.Empty<CoverageLlmMissingTest>());
    }
}

static Dictionary<int, (string Title, string Suite, string Steps)> BuildAdoDetails(CoveragePlanSummary plan)
{
    var dict = new Dictionary<int, (string Title, string Suite, string Steps)>();
    foreach (var suite in plan.Suites)
    {
        foreach (var test in suite.Tests)
        {
            var steps = NormalizeText(test.Steps);
            dict[test.WorkItemId] = (test.Title, suite.Name, steps);
        }
    }

    return dict;
}

static List<(string Title, string? FullName, string? Uuid, string Steps)> BuildAllureDetails(CoverageAutomationReportSummary report)
{
    var list = new List<(string Title, string? FullName, string? Uuid, string Steps)>();
    foreach (var test in report.Tests)
    {
        var steps = test.Steps is { Count: > 0 } ? NormalizeText(string.Join("\n", test.Steps)) : string.Empty;
        list.Add((test.Title, test.FullName, test.Uuid, steps));
    }

    return list;
}

static IReadOnlyList<NormalizedEntity> NormalizeAdoPlan(CoveragePlanSummary plan)
{
    var output = new List<NormalizedEntity>();
    foreach (var suite in plan.Suites)
    {
        foreach (var test in suite.Tests)
        {
            var (actions, assertions) = SplitActionsAndAssertions(test.Steps);
            output.Add(new NormalizedEntity(
                test.WorkItemId.ToString(),
                "manual_test",
                test.Title,
                suite.Name,
                Screen: null,
                actions,
                assertions,
                Behaviors: Array.Empty<string>(),
                new Dictionary<string, string> { { "workItemId", test.WorkItemId.ToString() } }));
        }
    }

    return output;
}

static IReadOnlyList<NormalizedEntity> NormalizeAllureReport(CoverageAutomationReportSummary report)
{
    var output = new List<NormalizedEntity>();
    foreach (var test in report.Tests)
    {
        var steps = test.Steps is { Count: > 0 } ? string.Join("\n", test.Steps) : string.Empty;
        var (actions, assertions) = SplitActionsAndAssertions(steps);
        var id = !string.IsNullOrWhiteSpace(test.Uuid)
            ? test.Uuid!
            : !string.IsNullOrWhiteSpace(test.FullName)
                ? test.FullName!
                : test.Title;

        output.Add(new NormalizedEntity(
            id,
            "automation_test",
            test.Title,
            Feature: null,
            Screen: null,
            actions,
            assertions,
            Behaviors: Array.Empty<string>(),
            new Dictionary<string, string> { { "uuid", test.Uuid ?? string.Empty } }));
    }

    return output;
}

static (IReadOnlyList<string> Actions, IReadOnlyList<string> Assertions) SplitActionsAndAssertions(string text)
{
    var lines = text
        .Split(new[] { '\r', '\n' }, StringSplitOptions.RemoveEmptyEntries)
        .Select(l => l.Trim())
        .Where(l => !string.IsNullOrWhiteSpace(l))
        .ToList();

    if (lines.Count == 0)
        return (Array.Empty<string>(), Array.Empty<string>());

    var actions = new List<string>();
    var assertions = new List<string>();

    foreach (var line in lines)
    {
        var lower = line.ToLowerInvariant();
        if (lower.StartsWith("verify ")
            || lower.StartsWith("check ")
            || lower.StartsWith("ensure ")
            || lower.StartsWith("assert "))
        {
            assertions.Add(line);
        }
        else
        {
            actions.Add(line);
        }
    }

    return (actions, assertions);
}

static IReadOnlyList<RuleMatch> FindRuleMatches(
    NormalizedEntity source,
    IReadOnlyList<NormalizedEntity> targets)
{
    var matches = new List<RuleMatch>();
    var sourceId = source.Id;
    var normalizedSourceTitle = NormalizeTitle(source.Title);

    foreach (var target in targets)
    {
        if (string.Equals(sourceId, target.Id, StringComparison.OrdinalIgnoreCase))
        {
            matches.Add(new RuleMatch(source.Id, target.Id, "exact_id", 1.0));
            continue;
        }

        if (TitleContainsId(target.Title, sourceId))
            matches.Add(new RuleMatch(source.Id, target.Id, "id_in_title", 0.98));

        var normalizedTargetTitle = NormalizeTitle(target.Title);
        if (!string.IsNullOrWhiteSpace(normalizedSourceTitle)
            && normalizedSourceTitle == normalizedTargetTitle)
        {
            matches.Add(new RuleMatch(source.Id, target.Id, "title_exact", 0.95));
        }

        if (!string.IsNullOrWhiteSpace(source.Feature)
            && !string.IsNullOrWhiteSpace(target.Feature)
            && string.Equals(source.Feature, target.Feature, StringComparison.OrdinalIgnoreCase))
        {
            var overlap = KeywordOverlap(source, target);
            if (overlap >= 0.6)
                matches.Add(new RuleMatch(source.Id, target.Id, "feature_keyword_overlap", overlap));
        }
    }

    return matches;
}

static IReadOnlyList<VectorMatch> FindVectorMatches(
    NormalizedEntity source,
    CoverageEmbeddingFile sourceEmbeddings,
    CoverageEmbeddingFile targetEmbeddings,
    int topK,
    double minScore)
{
    var sourceItem = sourceEmbeddings.Items.FirstOrDefault(i => i.Id == source.Id);
    if (sourceItem is null)
        return Array.Empty<VectorMatch>();

    var sourceVector = NormalizeVector(sourceItem.Vector);
    var matches = new List<VectorMatch>();

    foreach (var target in targetEmbeddings.Items)
    {
        var score = Dot(sourceVector, NormalizeVector(target.Vector));
        if (score >= minScore)
            matches.Add(new VectorMatch(source.Id, target.Id, score));
    }

    return matches
        .OrderByDescending(m => m.Score)
        .Take(topK)
        .ToList();
}

static IReadOnlyList<CandidateEntity> SelectCandidates(
    NormalizedEntity source,
    IReadOnlyList<NormalizedEntity> targets,
    IReadOnlyList<RuleMatch> ruleMatches,
    IReadOnlyList<VectorMatch> vectorMatches,
    int maxCandidates)
{
    var targetLookup = targets.ToDictionary(t => t.Id, t => t);
    var combined = new Dictionary<string, CandidateEntity>(StringComparer.OrdinalIgnoreCase);

    foreach (var rule in ruleMatches)
    {
        if (!targetLookup.TryGetValue(rule.TargetId, out var target))
            continue;

        combined[rule.TargetId] = new CandidateEntity(target, "rule", rule.Score);
    }

    foreach (var vector in vectorMatches)
    {
        if (!targetLookup.TryGetValue(vector.TargetId, out var target))
            continue;

        if (combined.TryGetValue(vector.TargetId, out var existing))
        {
            combined[vector.TargetId] = new CandidateEntity(
                existing.Entity,
                "rule+embedding",
                Math.Max(existing.Score, vector.Score));
        }
        else
        {
            combined[vector.TargetId] = new CandidateEntity(target, "embedding", vector.Score);
        }
    }

    return combined.Values
        .OrderByDescending(c => c.Score)
        .Take(maxCandidates)
        .ToList();
}

static LlmComparePayload BuildLlmPayload(NormalizedEntity source, IReadOnlyList<CandidateEntity> candidates)
{
    var sourcePayload = new LlmSourcePayload(
        source.Id,
        source.Type,
        source.Title,
        source.Feature,
        source.Screen,
        TruncateList(source.Actions, 10),
        TruncateList(source.Assertions, 10),
        TruncateList(source.Behaviors, 10));

    var candidatePayloads = candidates.Select(c =>
        new LlmCandidatePayload(
            c.Entity.Id,
            c.Entity.Type,
            c.MatchOrigin,
            c.Score,
            c.Entity.Title,
            c.Entity.Feature,
            c.Entity.Screen,
            TruncateList(c.Entity.Actions, 10),
            TruncateList(c.Entity.Assertions, 10),
            TruncateList(c.Entity.Behaviors, 10)))
        .ToList();

    return new LlmComparePayload(sourcePayload, candidatePayloads);
}

static async Task<CoverageDecision> CompareWithLlm(
    LlmComparePayload payload,
    GroqClient groq,
    CancellationToken ct)
{
    var json = JsonSerializer.Serialize(payload, new JsonSerializerOptions { WriteIndented = false });
    var prompt = $@"You are a QA analyst. Compare source vs candidates and decide coverage.
Return JSON only:
{{ ""bestMatchId"": ""id-or-null"", ""coverage"": ""full|partial|none"", ""confidence"": 0.0, ""coveredSourceSteps"": [1], ""missingSourceSteps"": [2], ""notes"": [""...""] }}

Input:
{json}";

    var response = await groq.ChatAsync(prompt, systemPrompt: "Return strict JSON only.", ct);
    return ParseCoverageDecision(payload.Source.Id, response);
}

static CoverageDecision ParseCoverageDecision(string sourceId, string json)
{
    try
    {
        using var doc = JsonDocument.Parse(json);
        var root = doc.RootElement;
        var bestMatchId = root.TryGetProperty("bestMatchId", out var bestEl) ? bestEl.GetString() : null;
        var coverage = root.TryGetProperty("coverage", out var covEl) ? covEl.GetString() ?? "none" : "none";
        var confidence = root.TryGetProperty("confidence", out var confEl) && confEl.TryGetDouble(out var confVal) ? confVal : 0;
        var covered = ReadIntArray(root, "coveredSourceSteps");
        var missing = ReadIntArray(root, "missingSourceSteps");
        var notes = ReadStringArray(root, "notes");
        return new CoverageDecision(sourceId, bestMatchId, coverage, confidence, covered, missing, notes);
    }
    catch
    {
        return new CoverageDecision(sourceId, null, "none", 0, Array.Empty<int>(), Array.Empty<int>(), Array.Empty<string>());
    }
}

static IReadOnlyList<int> ReadIntArray(JsonElement root, string name)
{
    if (!root.TryGetProperty(name, out var arr) || arr.ValueKind != JsonValueKind.Array)
        return Array.Empty<int>();

    var list = new List<int>();
    foreach (var item in arr.EnumerateArray())
    {
        if (item.TryGetInt32(out var val))
            list.Add(val);
    }

    return list;
}

static IReadOnlyList<string> ReadStringArray(JsonElement root, string name)
{
    if (!root.TryGetProperty(name, out var arr) || arr.ValueKind != JsonValueKind.Array)
        return Array.Empty<string>();

    var list = new List<string>();
    foreach (var item in arr.EnumerateArray())
        list.Add(item.GetString() ?? string.Empty);

    return list;
}

static string BuildTextForEmbedding(NormalizedEntity entity)
{
    var sb = new StringBuilder();
    sb.AppendLine(entity.Type);
    if (!string.IsNullOrWhiteSpace(entity.Feature))
        sb.AppendLine($"feature: {entity.Feature}");
    if (!string.IsNullOrWhiteSpace(entity.Screen))
        sb.AppendLine($"screen: {entity.Screen}");
    sb.AppendLine($"title: {entity.Title}");
    if (entity.Actions.Count > 0)
        sb.AppendLine("actions: " + string.Join("; ", entity.Actions));
    if (entity.Assertions.Count > 0)
        sb.AppendLine("assertions: " + string.Join("; ", entity.Assertions));
    if (entity.Behaviors.Count > 0)
        sb.AppendLine("behaviors: " + string.Join("; ", entity.Behaviors));
    return NormalizeText(sb.ToString());
}

static IReadOnlyList<string> TruncateList(IReadOnlyList<string> items, int maxItems)
{
    if (items.Count <= maxItems)
        return items;

    return items.Take(maxItems).ToList();
}

static string NormalizeTitle(string title)
{
    if (string.IsNullOrWhiteSpace(title))
        return string.Empty;

    var lowered = title.ToLowerInvariant();
    var cleaned = Regex.Replace(lowered, @"[^\p{L}\p{N}]+", " ");
    return Regex.Replace(cleaned, @"\s+", " ").Trim();
}

static bool TitleContainsId(string title, string id)
{
    if (string.IsNullOrWhiteSpace(title) || string.IsNullOrWhiteSpace(id))
        return false;

    var pattern = $@"(?:^|[_\-\s]){Regex.Escape(id)}(?:$|[_\-\s])";
    return Regex.IsMatch(title, pattern, RegexOptions.IgnoreCase);
}

static double KeywordOverlap(NormalizedEntity a, NormalizedEntity b)
{
    var aTokens = TokenizeKeywords(a);
    var bTokens = TokenizeKeywords(b);
    if (aTokens.Count == 0 || bTokens.Count == 0)
        return 0;

    var overlap = aTokens.Intersect(bTokens).Count();
    var union = aTokens.Union(bTokens).Count();
    return union == 0 ? 0 : (double)overlap / union;
}

static HashSet<string> TokenizeKeywords(NormalizedEntity entity)
{
    var combined = string.Join(" ", entity.Actions.Concat(entity.Assertions).Concat(entity.Behaviors));
    var normalized = NormalizeTitle(combined);
    return normalized.Split(' ', StringSplitOptions.RemoveEmptyEntries).ToHashSet();
}

app.Run();

public sealed record SyncFolderRequest(string AllureResultsPath, int? PlanId, int? SuiteId);

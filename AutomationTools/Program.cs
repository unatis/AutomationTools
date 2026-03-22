using Microsoft.AspNetCore.Mvc;
using Microsoft.Extensions.Caching.Memory;
using Microsoft.Extensions.Options;
using AutomationTools.Ado;
using AutomationTools.Sync;
using AutomationTools.Allure;
using AutomationTools.TestGenerator;
using AutomationTools.Ai;
using AutomationTools.Coverage;
using System.Text;
using System.Text.Json;
using System.IO.Compression;
using System.Text.RegularExpressions;
using System.Collections.Concurrent;

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
    var opts = sp.GetRequiredService<AiOptions>();
    if (opts.LlmTimeoutSeconds > 0)
        http.Timeout = TimeSpan.FromSeconds(opts.LlmTimeoutSeconds);
    var apiKey = Environment.GetEnvironmentVariable("GROQ_API_KEY");
    GroqClient.ConfigureHttpClient(http, apiKey);
});

builder.Services.AddHttpClient<LocalLlmClient>((sp, http) =>
{
    var opts = sp.GetRequiredService<AiOptions>();
    if (opts.LlmTimeoutSeconds > 0)
        http.Timeout = TimeSpan.FromSeconds(opts.LlmTimeoutSeconds);
    LocalLlmClient.ConfigureHttpClient(http, opts);
});

builder.Services.AddHttpClient<LocalEmbeddingClient>((sp, http) =>
{
    var opts = sp.GetRequiredService<AiOptions>();
    if (opts.EmbeddingTimeoutSeconds > 0)
        http.Timeout = TimeSpan.FromSeconds(opts.EmbeddingTimeoutSeconds);
    LocalEmbeddingClient.ConfigureHttpClient(http, opts);
});

builder.Services.AddSingleton<AllureToAdoSyncService>();

builder.Services.AddEndpointsApiExplorer();
builder.Services.AddSwaggerGen();
builder.Services.AddMemoryCache();

var app = builder.Build();

app.UseSwagger();
app.UseSwaggerUI();
app.UseStaticFiles();

var RequirementsJobs = new ConcurrentDictionary<string, RequirementsJob>();

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

// Sync from Allure results ZIP (multipart): extracts *-result.json entries and syncs to ADO.
// POST /sync-allure-zip?planId=11937&suiteId=29357&configurationId=2
app.MapPost("/sync-allure-zip", async (
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
            var json = await reader.ReadToEndAsync(ct);
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

    var summary = await sync.SyncFromAllureResults(
        allureResults: results,
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
    AiOptions aiOptions,
    GroqClient groq,
    LocalLlmClient localLlm,
    IMemoryCache cache,
    CancellationToken ct) =>
{
    if (IsGroqProvider(aiOptions))
    {
        var apiKey = Environment.GetEnvironmentVariable("GROQ_API_KEY");
        if (string.IsNullOrWhiteSpace(apiKey))
            return Results.BadRequest("GROQ_API_KEY is not set.");
    }

    var reportKey = string.IsNullOrWhiteSpace(request.TeamId) ? null : $"coverage:report:{request.TeamId}";
    var reportTitles = reportKey is null ? null : cache.Get<IReadOnlyList<string>>(reportKey);
    var prompt = CoveragePromptBuilder.Build(request, reportTitles);

    try
    {
        var result = await ChatAsync(aiOptions, groq, localLlm, prompt, systemPrompt: null, ct);
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

app.MapPost("/requirements/test-cases", async (
    [FromBody] RequirementsTestCasesRequest request,
    AiOptions aiOptions,
    GroqClient groq,
    LocalLlmClient localLlm,
    CancellationToken ct) =>
{
    if (string.IsNullOrWhiteSpace(request.Text))
        return Results.BadRequest("Text is required.");

    var requirements = ExtractRequirementsFromText(request.Text);
    if (requirements.Count == 0)
        return Results.BadRequest("No requirements found in the text.");

    var indexed = requirements.Select((text, index) => new RequirementItem($"R{index + 1}", text)).ToList();
    var (testCases, notApplicable) = await GenerateTestCasesAsync(
        indexed,
        aiOptions,
        groq,
        localLlm,
        ct,
        progress: null);

    return Results.Ok(new { testCases, notApplicable });
});

app.MapPost("/requirements/test-cases/async", async (
    [FromBody] RequirementsTestCasesRequest request,
    AiOptions aiOptions,
    GroqClient groq,
    LocalLlmClient localLlm,
    CancellationToken ct) =>
{
    if (string.IsNullOrWhiteSpace(request.Text))
        return Results.BadRequest("Text is required.");

    var requirements = ExtractRequirementsFromText(request.Text);
    if (requirements.Count == 0)
        return Results.BadRequest("No requirements found in the text.");

    var indexed = requirements.Select((text, index) => new RequirementItem($"R{index + 1}", text)).ToList();
    var job = RequirementsJob.Create(indexed.Count);
    RequirementsJobs[job.Id] = job;

    _ = Task.Run(async () =>
    {
        try
        {
            job.Status = "running";
            var (testCases, notApplicable) = await GenerateTestCasesAsync(
                indexed,
                aiOptions,
                groq,
                localLlm,
                CancellationToken.None,
                (completed, total) =>
                {
                    job.Completed = completed;
                    job.Total = total;
                });
            job.TestCases = testCases;
            job.NotApplicable = notApplicable;
            job.Status = "completed";
            try
            {
                var webRootPath = string.IsNullOrWhiteSpace(app.Environment.WebRootPath)
                    ? Path.Combine(app.Environment.ContentRootPath, "wwwroot")
                    : app.Environment.WebRootPath;
                await PersistRequirementsTestCasesJson(job, webRootPath, CancellationToken.None);
            }
            catch (Exception ex)
            {
                app.Logger.LogError(ex, "Failed to persist requirements test cases for job {JobId}.", job.Id);
            }
        }
        catch (Exception ex)
        {
            job.Error = ex.Message;
            job.Status = "failed";
        }
    });

    return Results.Ok(new { jobId = job.Id });
});

app.MapGet("/requirements/test-cases/async/{jobId}", (string jobId) =>
{
    if (!RequirementsJobs.TryGetValue(jobId, out var job))
        return Results.NotFound();

    return Results.Ok(new
    {
        jobId = job.Id,
        status = job.Status,
        total = job.Total,
        completed = job.Completed,
        testCases = job.TestCases,
        notApplicable = job.NotApplicable,
        error = job.Error
    });
});

app.MapGet("/coverage/embedding-report", async (
    int planId,
    string teamId,
    string? testPlanApiVersion,
    string? witApiVersion,
    bool? force,
    int? topK,
    double? minScore,
    double? dupMinScore,
    bool? llm,
    int? llmMaxMatches,
    int? llmMaxGaps,
    AdoClient client,
    IMemoryCache cache,
    AiOptions aiOptions,
    LocalEmbeddingClient embeddings,
    GroqClient groq,
    LocalLlmClient localLlm,
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

    var duplicates = await BuildDuplicateReport(
        plan,
        report,
        teamId,
        embeddingModel,
        dupMinScore,
        force == true,
        embeddings,
        webRootPath,
        ct);

    await PersistCoverageEmbeddingReport(result, planId, teamId, webRootPath, ct);

    if (llm == true)
    {
        if (IsGroqProvider(aiOptions))
        {
            var apiKey = Environment.GetEnvironmentVariable("GROQ_API_KEY");
            if (string.IsNullOrWhiteSpace(apiKey))
                return Results.BadRequest("GROQ_API_KEY is not set.");
        }

        var maxAdo = llmMaxMatches is > 0 ? llmMaxMatches.Value : 0;
        var maxAllure = llmMaxGaps is > 0 ? llmMaxGaps.Value : 0;
        var llmCoverage = await BuildLlmCoverageFromUnmatched(
            plan,
            report,
            result,
            aiOptions,
            groq,
            localLlm,
            maxAdo,
            maxAllure,
            ct);

        await PersistCoverageEmbeddingLlmCoverageReport(llmCoverage, planId, teamId, webRootPath, ct);
        return Results.Ok(new { report = result, llmCoverage, duplicates });
    }

    return Results.Ok(new { report = result, duplicates });
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

static async Task PersistRequirementsTestCasesJson(
    RequirementsJob job,
    string webRootPath,
    CancellationToken ct)
{
    var coverageDir = Path.Combine(webRootPath, "coverage");
    Directory.CreateDirectory(coverageDir);

    var payload = new
    {
        jobId = job.Id,
        status = job.Status,
        total = job.Total,
        completed = job.Completed,
        testCases = job.TestCases ?? new List<GeneratedTestCase>(),
        notApplicable = job.NotApplicable ?? new List<NotApplicableItem>(),
        error = job.Error
    };
    var json = JsonSerializer.Serialize(payload, new JsonSerializerOptions { WriteIndented = true });
    var jobFile = Path.Combine(coverageDir, $"requirements-test-cases-{job.Id}.json");
    await File.WriteAllTextAsync(jobFile, json, ct);

    var latestJson = JsonSerializer.Serialize(payload.testCases, new JsonSerializerOptions { WriteIndented = true });
    var latestFile = Path.Combine(coverageDir, "requirements-test-cases-latest.json");
    await File.WriteAllTextAsync(latestFile, latestJson, ct);
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

static async Task PersistCoverageEmbeddingLlmCoverageReport(
    CoverageEmbeddingLlmCoverageReport report,
    int planId,
    string teamId,
    string webRootPath,
    CancellationToken ct)
{
    var coverageDir = Path.Combine(webRootPath, "coverage");
    Directory.CreateDirectory(coverageDir);

    var json = JsonSerializer.Serialize(report, new JsonSerializerOptions { WriteIndented = true });
    var reportFile = Path.Combine(coverageDir, $"coverage-embedding-llm-coverage-report-{planId}-{teamId}.json");
    var latestFile = Path.Combine(coverageDir, "coverage-embedding-llm-coverage-report.json");

    await File.WriteAllTextAsync(reportFile, json, ct);
    await File.WriteAllTextAsync(latestFile, json, ct);
}

static async Task<CoverageEmbeddingLlmCoverageReport> BuildLlmCoverageFromUnmatched(
    CoveragePlanSummary plan,
    CoverageAutomationReportSummary report,
    CoverageEmbeddingReport embeddingReport,
    AiOptions aiOptions,
    GroqClient groq,
    LocalLlmClient localLlm,
    int maxAdo,
    int maxAllure,
    CancellationToken ct)
{
    var unmatchedAdo = embeddingReport.UnmatchedAdo;
    var unmatchedAllure = embeddingReport.UnmatchedAllure;

    if (maxAdo > 0)
        unmatchedAdo = unmatchedAdo.Take(maxAdo).ToList();
    if (maxAllure > 0)
        unmatchedAllure = unmatchedAllure.Take(maxAllure).ToList();

    if (unmatchedAdo.Count == 0 || unmatchedAllure.Count == 0)
    {
        return new CoverageEmbeddingLlmCoverageReport(
            embeddingReport.PlanId,
            embeddingReport.TeamId,
            "groq",
            Array.Empty<CoverageEmbeddingMatch>(),
            unmatchedAdo.ToList(),
            unmatchedAllure.ToList());
    }

    var adoDetails = BuildAdoDetails(plan);
    var allureDetails = BuildAllureDetails(report);
    var candidates = BuildLlmAllureCandidates(unmatchedAllure, allureDetails);
    var candidateLookup = candidates.ToDictionary(c => c.Id, c => c);

    var matches = new List<CoverageEmbeddingMatch>();
    var matchedAdo = new HashSet<int>();
    var usedCandidates = new HashSet<string>(StringComparer.OrdinalIgnoreCase);

    const int batchSize = 20;
    var total = unmatchedAdo.Count;
    for (var i = 0; i < total; i += batchSize)
    {
        var batch = unmatchedAdo.Skip(i).Take(batchSize).ToList();
        var payload = BuildLlmCoveragePayload(batch, adoDetails, candidates);
        var response = await CompareUnmatchedWithLlm(aiOptions, payload, groq, localLlm, ct);
        var decisions = ParseLlmCoverageMatches(response);

        foreach (var d in decisions)
        {
            if (d.Decision != "match" || string.IsNullOrWhiteSpace(d.CandidateId))
                continue;

            if (matchedAdo.Contains(d.WorkItemId))
                continue;
            if (usedCandidates.Contains(d.CandidateId))
                continue;
            if (!candidateLookup.TryGetValue(d.CandidateId, out var candidate))
                continue;

            if (!adoDetails.TryGetValue(d.WorkItemId, out var ado))
                continue;

            matches.Add(new CoverageEmbeddingMatch(
                d.WorkItemId,
                ado.Title,
                ado.Suite,
                candidate.Title,
                candidate.FullName,
                candidate.Uuid,
                1.0));

            matchedAdo.Add(d.WorkItemId);
            usedCandidates.Add(d.CandidateId);
        }
    }

    var remainingAdo = unmatchedAdo.Where(a => !matchedAdo.Contains(a.WorkItemId)).ToList();
    var remainingAllure = candidates
        .Where(c => !usedCandidates.Contains(c.Id))
        .Select(c => new CoverageEmbeddingUnmatchedAllure(c.Title, c.FullName, c.Uuid))
        .ToList();

    return new CoverageEmbeddingLlmCoverageReport(
        embeddingReport.PlanId,
        embeddingReport.TeamId,
        "groq",
        matches,
        remainingAdo,
        remainingAllure);
}

static async Task<CoverageDuplicateReport> BuildDuplicateReport(
    CoveragePlanSummary plan,
    CoverageAutomationReportSummary report,
    string teamId,
    string embeddingModel,
    double? dupMinScore,
    bool force,
    LocalEmbeddingClient embeddings,
    string webRootPath,
    CancellationToken ct)
{
    var effectiveMinScore = dupMinScore is > 0 ? dupMinScore.Value : 0.9;

    var adoEntities = NormalizeAdoPlan(plan);
    var allureEntities = NormalizeAllureReport(report);

    var adoDuplicates = await FindDuplicatesForEntities(
        adoEntities,
        embeddingModel,
        effectiveMinScore,
        "ado",
        plan.PlanId.ToString(),
        force,
        embeddings,
        webRootPath,
        ct);

    var allureDuplicates = await FindDuplicatesForEntities(
        allureEntities,
        embeddingModel,
        effectiveMinScore,
        "allure",
        teamId,
        force,
        embeddings,
        webRootPath,
        ct);

    return new CoverageDuplicateReport(adoDuplicates, allureDuplicates);
}

static async Task<IReadOnlyList<CoverageDuplicateItem>> FindDuplicatesForEntities(
    IReadOnlyList<NormalizedEntity> entities,
    string embeddingModel,
    double minScore,
    string scope,
    string suffix,
    bool force,
    LocalEmbeddingClient embeddings,
    string webRootPath,
    CancellationToken ct)
{
    var duplicates = new List<CoverageDuplicateItem>();
    if (entities.Count == 0)
        return duplicates;

    var items = new List<(string Id, string Title, string Steps)>();
    var usedIds = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
    foreach (var entity in entities)
    {
        var baseId = entity.Id;
        var id = baseId;
        var suffixIndex = 1;
        while (!usedIds.Add(id))
        {
            id = $"{baseId}:{suffixIndex}";
            suffixIndex++;
        }

        items.Add((id, entity.Title, BuildStepsSignature(entity)));
    }
    var idToTitle = items.ToDictionary(i => i.Id, i => i.Title, StringComparer.OrdinalIgnoreCase);

    var firstBySteps = new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase);
    var duplicateIds = new HashSet<string>(StringComparer.OrdinalIgnoreCase);

    for (var i = 0; i < items.Count; i++)
    {
        var steps = items[i].Steps;
        if (string.IsNullOrWhiteSpace(steps))
            continue;

        var id = items[i].Id;
        if (firstBySteps.TryGetValue(steps, out var firstId))
        {
            duplicates.Add(new CoverageDuplicateItem(
                id,
                items[i].Title,
                firstId,
                "exact",
                1.0));
            duplicateIds.Add(id);
        }
        else
        {
            firstBySteps[steps] = id;
        }
    }

    if (minScore <= 0)
        return duplicates;

    var sources = new List<CoverageEmbeddingSource>();
    for (var i = 0; i < items.Count; i++)
    {
        var steps = items[i].Steps;
        if (string.IsNullOrWhiteSpace(steps))
            continue;

        sources.Add(new CoverageEmbeddingSource(items[i].Id, steps));
    }

    if (sources.Count == 0)
        return duplicates;

    var embeddingsDir = Path.Combine(webRootPath, "coverage");
    var embedPath = Path.Combine(embeddingsDir, $"embeddings-steps-{scope}-{suffix}.json");
    var embedFile = await GetOrCreateEmbeddingsFile(
        embedPath,
        embeddingModel,
        sources,
        embeddings,
        force,
        ct);

    var vectors = embedFile.Items.Select(i => NormalizeVector(i.Vector)).ToList();
    var ids = embedFile.Items.Select(i => i.Id).ToList();

    for (var i = 0; i < ids.Count; i++)
    {
        var idA = ids[i];
        if (duplicateIds.Contains(idA))
            continue;

        for (var j = i + 1; j < ids.Count; j++)
        {
            var idB = ids[j];
            if (duplicateIds.Contains(idB))
                continue;

            var score = Dot(vectors[i], vectors[j]);
            if (score < minScore)
                continue;

            duplicates.Add(new CoverageDuplicateItem(
                idB,
                idToTitle[idB],
                idA,
                "semantic",
                Math.Round(score, 4)));
            duplicateIds.Add(idB);
        }
    }

    return duplicates;
}

static string BuildStepsSignature(NormalizedEntity entity)
{
    var parts = entity.Actions.Concat(entity.Assertions).Where(p => !string.IsNullOrWhiteSpace(p)).ToList();
    if (parts.Count == 0)
        return string.Empty;

    return NormalizeText(string.Join("\n", parts));
}

static IReadOnlyList<LlmAllureCandidate> BuildLlmAllureCandidates(
    IReadOnlyList<CoverageEmbeddingUnmatchedAllure> unmatchedAllure,
    IReadOnlyList<(string Title, string? FullName, string? Uuid, string Steps)> allureDetails)
{
    var lookup = new Dictionary<string, (string Title, string? FullName, string? Uuid, string Steps)>(StringComparer.OrdinalIgnoreCase);
    foreach (var item in allureDetails)
    {
        var key = BuildAllureKey(item.Title, item.FullName, item.Uuid);
        if (!lookup.ContainsKey(key))
            lookup[key] = item;
    }

    var candidates = new List<LlmAllureCandidate>();
    for (var i = 0; i < unmatchedAllure.Count; i++)
    {
        var item = unmatchedAllure[i];
        var key = BuildAllureKey(item.Title, item.FullName, item.Uuid);
        var details = lookup.TryGetValue(key, out var found) ? found : (item.Title, item.FullName, item.Uuid, string.Empty);
        candidates.Add(new LlmAllureCandidate(
            Id: $"cand:{i}",
            Title: details.Title,
            FullName: details.FullName,
            Uuid: details.Uuid,
            Steps: details.Item4));
    }

    return candidates;
}

static string BuildAllureKey(string title, string? fullName, string? uuid)
{
    if (!string.IsNullOrWhiteSpace(uuid))
        return $"uuid:{uuid}";
    if (!string.IsNullOrWhiteSpace(fullName))
        return $"full:{fullName}";
    return $"title:{title}";
}

static LlmCoveragePayload BuildLlmCoveragePayload(
    IReadOnlyList<CoverageEmbeddingUnmatchedAdo> unmatchedAdo,
    Dictionary<int, (string Title, string Suite, string Steps)> adoDetails,
    IReadOnlyList<LlmAllureCandidate> candidates)
{
    var sources = unmatchedAdo.Select(a =>
    {
        adoDetails.TryGetValue(a.WorkItemId, out var details);
        return (object)new
        {
            workItemId = a.WorkItemId,
            title = a.AdoTitle,
            suite = a.AdoSuite,
            steps = details.Steps ?? string.Empty
        };
    }).ToList();

    var targetPayloads = candidates.Select(c => (object)new
    {
        id = c.Id,
        title = c.Title,
        fullName = c.FullName,
        uuid = c.Uuid,
        steps = c.Steps ?? string.Empty
    }).ToList();

    return new LlmCoveragePayload(sources, targetPayloads);
}

static async Task<string> CompareUnmatchedWithLlm(
    AiOptions aiOptions,
    LlmCoveragePayload payload,
    GroqClient groq,
    LocalLlmClient localLlm,
    CancellationToken ct)
{
    var json = JsonSerializer.Serialize(payload, new JsonSerializerOptions { WriteIndented = false });
    var prompt = $@"You are a QA analyst. For each ADO test decide the best matching automation test, or no match.
Return JSON only with schema:
{{ ""matches"": [{{ ""workItemId"": 123, ""candidateId"": ""cand:1"", ""decision"": ""match|no_match"", ""confidence"": 0.0, ""reason"": ""..."" }}] }}

Input:
{json}";

    return await ChatAsync(aiOptions, groq, localLlm, prompt, systemPrompt: "Return strict JSON only.", ct);
}

static IReadOnlyList<LlmCoverageDecision> ParseLlmCoverageMatches(string json)
{
    try
    {
        using var doc = JsonDocument.Parse(json);
        if (!doc.RootElement.TryGetProperty("matches", out var matchesEl) || matchesEl.ValueKind != JsonValueKind.Array)
            return Array.Empty<LlmCoverageDecision>();

        var list = new List<LlmCoverageDecision>();
        foreach (var item in matchesEl.EnumerateArray())
        {
            var workItemId = item.TryGetProperty("workItemId", out var idEl) && idEl.TryGetInt32(out var idVal) ? idVal : 0;
            var candidateId = item.TryGetProperty("candidateId", out var candEl) ? candEl.GetString() : null;
            var decision = item.TryGetProperty("decision", out var decEl) ? decEl.GetString() ?? "no_match" : "no_match";
            var confidence = item.TryGetProperty("confidence", out var confEl) && confEl.TryGetDouble(out var confVal) ? confVal : 0;
            var reason = item.TryGetProperty("reason", out var reasonEl) ? reasonEl.GetString() ?? string.Empty : string.Empty;
            if (workItemId > 0)
                list.Add(new LlmCoverageDecision(workItemId, candidateId, decision, confidence, reason));
        }

        return list;
    }
    catch
    {
        return Array.Empty<LlmCoverageDecision>();
    }
}


static async Task<CoverageEmbeddingLlmReport> BuildLlmEmbeddingReport(
    CoveragePlanSummary plan,
    CoverageAutomationReportSummary report,
    CoverageEmbeddingReport embeddingReport,
    AiOptions aiOptions,
    GroqClient groq,
    LocalLlmClient localLlm,
    int maxMatches,
    int maxGaps,
    CancellationToken ct)
{
    var matchResult = await ReviewMatchesWithLlm(
        plan,
        report,
        embeddingReport.Matches.Take(maxMatches).ToList(),
        aiOptions,
        groq,
        localLlm,
        ct);

    var gapResult = await ExplainGapsWithLlm(
        plan,
        report,
        embeddingReport.UnmatchedAdo.Take(maxGaps).ToList(),
        embeddingReport.UnmatchedAllure.Take(maxGaps).ToList(),
        aiOptions,
        groq,
        localLlm,
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
    AiOptions aiOptions,
    GroqClient groq,
    LocalLlmClient localLlm,
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

    var response = await ChatWithUsageAsync(aiOptions, groq, localLlm, prompt, systemPrompt: "Return strict JSON only.", ct);
    var reviews = ParseLlmReviews(response.Content);
    var usage = new CoverageLlmTokenUsage(response.PromptTokens, response.CompletionTokens, response.TotalTokens);
    return (reviews, usage);
}

static async Task<(IReadOnlyList<CoverageLlmGapExplanation> Explanations, IReadOnlyList<CoverageLlmMissingTest> MissingTests, CoverageLlmTokenUsage Usage)> ExplainGapsWithLlm(
    CoveragePlanSummary plan,
    CoverageAutomationReportSummary report,
    IReadOnlyList<CoverageEmbeddingUnmatchedAdo> unmatchedAdo,
    IReadOnlyList<CoverageEmbeddingUnmatchedAllure> unmatchedAllure,
    AiOptions aiOptions,
    GroqClient groq,
    LocalLlmClient localLlm,
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

    var response = await ChatWithUsageAsync(aiOptions, groq, localLlm, prompt, systemPrompt: "Return strict JSON only.", ct);
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
    var seen = new HashSet<int>();
    foreach (var suite in plan.Suites)
    {
        foreach (var test in suite.Tests)
        {
            if (!seen.Add(test.WorkItemId))
                continue;

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

static IReadOnlyList<string> ExtractRequirementsFromText(string text)
{
    if (string.IsNullOrWhiteSpace(text))
        return Array.Empty<string>();

    var blocks = Regex.Split(text, @"\r?\n\s*\r?\n")
        .Select(b => Regex.Replace(b, @"\s+", " ").Trim())
        .Where(b => !string.IsNullOrWhiteSpace(b))
        .ToList();

    var output = new List<string>();
    foreach (var block in blocks)
    {
        var cleaned = Regex.Replace(block, @"^\s*(?:[-*•]|\d+[.)])\s*", "");
        if (!string.IsNullOrWhiteSpace(cleaned))
            output.Add(cleaned);
    }

    if (output.Count == 0 && !string.IsNullOrWhiteSpace(text))
        output.Add(text.Trim());

    return output;
}

static async Task<(List<GeneratedTestCase> TestCases, List<NotApplicableItem> NotApplicable)> GenerateTestCasesAsync(
    List<RequirementItem> requirements,
    AiOptions aiOptions,
    GroqClient groq,
    LocalLlmClient localLlm,
    CancellationToken ct,
    Action<int, int>? progress)
{
    const int chunkSize = 3;
    var aggregatedTestCases = new List<GeneratedTestCase>();
    var aggregatedNotApplicable = new List<NotApplicableItem>();
    var completed = 0;
    var total = (int)Math.Ceiling(requirements.Count / (double)chunkSize);

    foreach (var chunk in requirements.Chunk(chunkSize))
    {
        var payload = new
        {
            requirements = chunk.Select(item => new
            {
                id = item.Id,
                text = item.Text
            }),
            rules = new
            {
                requiredTestTypes = new[] { "E2E", "Functional", "Security", "UI/UX", "Component" },
                mustInclude = new[] { "positive", "negative", "edge" },
                minPerRequirement = 3
            }
        };

        var payloadJson = JsonSerializer.Serialize(payload, new JsonSerializerOptions { WriteIndented = false });
        var prompt = $@"You are a QA analyst. Generate test cases from requirements.
Return JSON only with schema:
{{ ""testCases"": [{{ ""id"": ""TC-1"", ""requirementId"": ""R1"", ""type"": ""E2E|Functional|Security|UI/UX|Component"", ""title"": ""..."", ""preconditions"": [""...""], ""steps"": [""...""], ""expected"": ""..."", ""priority"": ""P1|P2|P3"" }}], ""notApplicable"": [{{ ""requirementId"": ""R1"", ""type"": ""Component"", ""reason"": ""..."" }}] }}
Rules:
- For each requirement, cover all required types when applicable.
- If a type is not applicable, add it to notApplicable with a reason.
- Ensure positive, negative, and edge coverage across the set.
Input JSON:
{payloadJson}";

        string? response = null;
        response = await ChatAsync(aiOptions, groq, localLlm, prompt, systemPrompt: "Return strict JSON only. No markdown. No extra text. If you cannot comply, return {}.", ct);
        var json = TryExtractJsonObject(response);
        if (json is null)
        {
            var repair = $@"Return ONLY a single JSON object that matches the required schema.
Do not add any explanations or markdown.
If no valid JSON can be produced, return {{}}.

Schema:
{{ ""testCases"": [{{ ""id"": ""TC-1"", ""requirementId"": ""R1"", ""type"": ""E2E|Functional|Security|UI/UX|Component"", ""title"": ""..."", ""preconditions"": [""...""], ""steps"": [""...""], ""expected"": ""..."", ""priority"": ""P1|P2|P3"" }}], ""notApplicable"": [{{ ""requirementId"": ""R1"", ""type"": ""Component"", ""reason"": ""..."" }}] }}

Original response:
{response}";
            var repaired = await ChatAsync(aiOptions, groq, localLlm, repair, systemPrompt: "Return strict JSON only.", ct);
            json = TryExtractJsonObject(repaired);
        }
        if (json is null)
            throw new InvalidOperationException("LLM returned invalid JSON.");

        var (testCases, notApplicable) = ParseLlmTestCases(json);
        aggregatedTestCases.AddRange(testCases);
        aggregatedNotApplicable.AddRange(notApplicable);

        completed++;
        progress?.Invoke(completed, total);
    }

    for (var i = 0; i < aggregatedTestCases.Count; i++)
        aggregatedTestCases[i].Id = $"TC-{i + 1}";

    return (aggregatedTestCases, aggregatedNotApplicable);
}

static (List<GeneratedTestCase> TestCases, List<NotApplicableItem> NotApplicable) ParseLlmTestCases(string json)
{
    var testCases = new List<GeneratedTestCase>();
    var notApplicable = new List<NotApplicableItem>();

    using var doc = JsonDocument.Parse(json);
    if (doc.RootElement.TryGetProperty("testCases", out var testCasesEl) && testCasesEl.ValueKind == JsonValueKind.Array)
    {
        foreach (var item in testCasesEl.EnumerateArray())
        {
            var testCase = new GeneratedTestCase
            {
                Id = GetString(item, "id"),
                RequirementId = GetString(item, "requirementId"),
                Type = GetString(item, "type"),
                Title = GetString(item, "title"),
                Preconditions = GetStringArray(item, "preconditions"),
                Steps = GetStringArray(item, "steps"),
                Expected = GetString(item, "expected"),
                Priority = GetString(item, "priority")
            };
            testCases.Add(testCase);
        }
    }

    if (doc.RootElement.TryGetProperty("notApplicable", out var notApplicableEl) && notApplicableEl.ValueKind == JsonValueKind.Array)
    {
        foreach (var item in notApplicableEl.EnumerateArray())
        {
            notApplicable.Add(new NotApplicableItem
            {
                RequirementId = GetString(item, "requirementId"),
                Type = GetString(item, "type"),
                Reason = GetString(item, "reason")
            });
        }
    }

    return (testCases, notApplicable);
}

static string? GetString(JsonElement element, string propertyName)
{
    if (element.TryGetProperty(propertyName, out var value) && value.ValueKind == JsonValueKind.String)
        return value.GetString();
    return null;
}

static List<string> GetStringArray(JsonElement element, string propertyName)
{
    var list = new List<string>();
    if (!element.TryGetProperty(propertyName, out var arr) || arr.ValueKind != JsonValueKind.Array)
        return list;
    foreach (var item in arr.EnumerateArray())
        list.Add(item.GetString() ?? string.Empty);
    return list;
}

static string? TryExtractJsonObject(string text)
{
    if (string.IsNullOrWhiteSpace(text))
        return null;

    text = text.Trim();
    if (text.StartsWith("{", StringComparison.Ordinal) && text.EndsWith("}", StringComparison.Ordinal))
        return text;

    var first = text.IndexOf('{');
    var last = text.LastIndexOf('}');
    if (first < 0 || last <= first)
        return null;

    var slice = text[first..(last + 1)].Trim();
    return slice.StartsWith("{", StringComparison.Ordinal) && slice.EndsWith("}", StringComparison.Ordinal)
        ? slice
        : null;
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

static bool IsGroqRateLimit(Exception ex)
{
    if (ex is InvalidOperationException inv && inv.Message.Contains("rate_limit_exceeded", StringComparison.OrdinalIgnoreCase))
        return true;

    if (ex.Message.Contains("429", StringComparison.OrdinalIgnoreCase))
        return true;

    return false;
}

static bool IsGroqProvider(AiOptions options)
{
    return string.Equals(options.Provider, "groq", StringComparison.OrdinalIgnoreCase);
}

static bool IsLocalProvider(AiOptions options)
{
    return string.Equals(options.Provider, "local", StringComparison.OrdinalIgnoreCase)
        || string.Equals(options.Provider, "ollama", StringComparison.OrdinalIgnoreCase);
}

static async Task<string> ChatAsync(
    AiOptions options,
    GroqClient groq,
    LocalLlmClient localLlm,
    string userPrompt,
    string? systemPrompt,
    CancellationToken ct)
{
    if (IsLocalProvider(options))
        return await localLlm.ChatAsync(userPrompt, systemPrompt, ct);

    return await groq.ChatAsync(userPrompt, systemPrompt, ct);
}

static async Task<GroqChatResult> ChatWithUsageAsync(
    AiOptions options,
    GroqClient groq,
    LocalLlmClient localLlm,
    string userPrompt,
    string? systemPrompt,
    CancellationToken ct)
{
    if (IsLocalProvider(options))
        return await localLlm.ChatWithUsageAsync(userPrompt, systemPrompt, ct);

    return await groq.ChatWithUsageAsync(userPrompt, systemPrompt, ct);
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

public sealed record RequirementsTestCasesRequest(string Text);

sealed record RequirementItem(string Id, string Text);

sealed class GeneratedTestCase
{
    public string? Id { get; set; }
    public string? RequirementId { get; set; }
    public string? Type { get; set; }
    public string? Title { get; set; }
    public List<string> Preconditions { get; set; } = new();
    public List<string> Steps { get; set; } = new();
    public string? Expected { get; set; }
    public string? Priority { get; set; }
}

sealed class NotApplicableItem
{
    public string? RequirementId { get; set; }
    public string? Type { get; set; }
    public string? Reason { get; set; }
}

sealed class RequirementsJob
{
    public string Id { get; private set; } = Guid.NewGuid().ToString("n");
    public string Status { get; set; } = "queued";
    public int Total { get; set; }
    public int Completed { get; set; }
    public List<GeneratedTestCase>? TestCases { get; set; }
    public List<NotApplicableItem>? NotApplicable { get; set; }
    public string? Error { get; set; }

    public static RequirementsJob Create(int total) => new RequirementsJob { Total = total };
}

sealed record LlmCoveragePayload(
    IReadOnlyList<object> UnmatchedAdo,
    IReadOnlyList<object> Candidates);

sealed record LlmAllureCandidate(
    string Id,
    string Title,
    string? FullName,
    string? Uuid,
    string Steps);

sealed record LlmCoverageDecision(
    int WorkItemId,
    string? CandidateId,
    string Decision,
    double Confidence,
    string Reason);

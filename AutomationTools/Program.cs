using Microsoft.AspNetCore.Mvc;
using Microsoft.Extensions.Caching.Memory;
using Microsoft.Extensions.Options;
using AutomationTools.Ado;
using AutomationTools.Sync;
using AutomationTools.Allure;
using AutomationTools.TestGenerator;
using AutomationTools.Ai;
using AutomationTools.Coverage;
using System.Net;
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

            var categoryParts = BuildAllureCategoryParts(r.Labels);
            var categoryPath = categoryParts.Count == 0 ? "Uncategorized" : string.Join(" / ", categoryParts);

            var steps = AllureParsing.FlattenSteps(r.Steps)
                .Select(s => s.Action)
                .Where(s => !string.IsNullOrWhiteSpace(s))
                .ToList();

            var testCase = new CoverageAutomationTestCase
            {
                Title = title ?? string.Empty,
                FullName = string.IsNullOrWhiteSpace(r.FullName) ? null : r.FullName.Trim(),
                Status = string.IsNullOrWhiteSpace(r.Status) ? null : r.Status.Trim(),
                Uuid = string.IsNullOrWhiteSpace(r.Uuid) ? null : r.Uuid.Trim(),
                Steps = steps,
                CategoryPath = categoryPath,
                CategoryParts = categoryParts
            };

            testCase.EmbeddingText = BuildAllureEmbeddingText(testCase);
            return testCase;
        })
        .Where(t => !string.IsNullOrWhiteSpace(t.Title))
        .ToList();

    var report = NormalizeCoverageReportSummary(new CoverageAutomationReportSummary
    {
        TotalTests = tests.Count,
        Tests = tests,
        CategoryIndex = BuildCoverageCategoryIndex(tests)
    });
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
    int? suiteId,
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
        suiteId,
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

    var adoItems = BuildCanonicalCoverageItemsFromAdo(plan);
    var allureItems = BuildCanonicalCoverageItemsFromAllure(report);

    var embeddingsDir = Path.Combine(webRootPath, "coverage");
    var selectionToken = BuildCoverageSelectionToken(planId, suiteId);

    var adoTitleEmbeddings = await GetOrCreateEmbeddingsFile(
        Path.Combine(embeddingsDir, $"embeddings-ado-title-plan-{selectionToken}.json"),
        embeddingModel,
        BuildEmbeddingSources(adoItems, item => item.Parts.TitleText),
        embeddings,
        force == true,
        ct);

    var adoCategoryEmbeddings = await GetOrCreateEmbeddingsFile(
        Path.Combine(embeddingsDir, $"embeddings-ado-category-plan-{selectionToken}.json"),
        embeddingModel,
        BuildEmbeddingSources(adoItems, item => item.Parts.CategoryText),
        embeddings,
        force == true,
        ct);

    var adoStepsEmbeddings = await GetOrCreateChunkEmbeddingsFile(
        Path.Combine(embeddingsDir, $"embeddings-ado-steps-plan-{selectionToken}.json"),
        embeddingModel,
        BuildEmbeddingSources(adoItems, item => item.Parts.StepsText),
        embeddings,
        force == true,
        ct);

    var allureTitleEmbeddings = await GetOrCreateEmbeddingsFile(
        Path.Combine(embeddingsDir, $"embeddings-allure-title-{teamId}.json"),
        embeddingModel,
        BuildEmbeddingSources(allureItems, item => item.Parts.TitleText),
        embeddings,
        force == true,
        ct);

    var allureCategoryEmbeddings = await GetOrCreateEmbeddingsFile(
        Path.Combine(embeddingsDir, $"embeddings-allure-category-{teamId}.json"),
        embeddingModel,
        BuildEmbeddingSources(allureItems, item => item.Parts.CategoryText),
        embeddings,
        force == true,
        ct);

    var allureStepsEmbeddings = await GetOrCreateChunkEmbeddingsFile(
        Path.Combine(embeddingsDir, $"embeddings-allure-steps-{teamId}.json"),
        embeddingModel,
        BuildEmbeddingSources(allureItems, item => item.Parts.StepsText),
        embeddings,
        force == true,
        ct);

    var result = BuildEmbeddingReport(
        plan,
        report,
        adoItems,
        allureItems,
        adoTitleEmbeddings,
        adoCategoryEmbeddings,
        adoStepsEmbeddings,
        allureTitleEmbeddings,
        allureCategoryEmbeddings,
        allureStepsEmbeddings,
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

    await PersistCoverageEmbeddingReport(result, planId, suiteId, teamId, webRootPath, ct);

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

        await PersistCoverageEmbeddingLlmCoverageReport(llmCoverage, planId, suiteId, teamId, webRootPath, ct);
        return Results.Ok(new { report = result, llmCoverage, duplicates });
    }

    return Results.Ok(new { report = result, duplicates });
});

app.MapGet("/coverage/plan-summary", async (
    int planId,
    int? suiteId,
    string? testPlanApiVersion,
    string? witApiVersion,
    bool? force,
    AdoClient client,
    IMemoryCache cache,
    CancellationToken ct) =>
{
    var summary = await GetCoverageSummary(
        planId,
        suiteId,
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
    int? suiteId,
    string? testPlanApiVersion,
    string? witApiVersion,
    bool? force,
    AdoClient client,
    IMemoryCache cache,
    CancellationToken ct) =>
{
    var summary = await GetCoverageSummary(
        planId,
        suiteId,
        testPlanApiVersion,
        witApiVersion,
        force == true,
        client,
        cache,
        webRootPath,
        ct);
    var json = JsonSerializer.Serialize(summary, new JsonSerializerOptions { WriteIndented = true });
    var bytes = Encoding.UTF8.GetBytes(json);
    var fileName = $"coverage-plan-{BuildCoverageSelectionToken(planId, suiteId)}.json";
    return Results.File(bytes, "application/json", fileName);
});


static async Task<CoveragePlanSummary> GetCoverageSummary(
    int planId,
    int? suiteId,
    string? testPlanApiVersion,
    string? witApiVersion,
    bool force,
    AdoClient client,
    IMemoryCache cache,
    string webRootPath,
    CancellationToken ct)
{
    var effectiveSuiteId = suiteId is > 0 ? suiteId.Value : (int?)null;
    var cacheKey = $"coverage:plan:{planId}:suite:{effectiveSuiteId?.ToString() ?? "all"}:tp:{testPlanApiVersion ?? ""}:wit:{witApiVersion ?? ""}";
    if (!force && cache.TryGetValue(cacheKey, out CoveragePlanSummary cached))
        return cached;

    var allSuites = await client.ListTestSuites(planId, ct, testPlanApiVersion);
    var suites = allSuites;
    if (effectiveSuiteId is > 0)
    {
        suites = CollectSuiteWithDescendants(allSuites, effectiveSuiteId.Value);
        if (suites.Count == 0)
            throw new InvalidOperationException($"Suite {effectiveSuiteId.Value} was not found in plan {planId}.");
    }

    var suiteSummaries = new List<CoverageSuiteSummary>();
    var totalTests = 0;
    var seenWorkItemIds = new HashSet<int>();

    foreach (var suite in suites)
    {
        ct.ThrowIfCancellationRequested();

        var tests = await client.ListSuiteTestCases(planId, suite.Id, ct, testPlanApiVersion);
        var testSummaries = new List<CoverageTestCaseSummary>();

        foreach (var t in tests)
        {
            ct.ThrowIfCancellationRequested();
            if (!seenWorkItemIds.Add(t.WorkItemId))
                continue;

            var details = await client.GetTestCaseDetails(t.WorkItemId, ct, witApiVersion);
            var title = details?.Title ?? t.Title;
            var stepsRaw = details?.Steps ?? string.Empty;
            var steps = NormalizeText(StripHtml(stepsRaw));

            testSummaries.Add(new CoverageTestCaseSummary(t.WorkItemId, title, steps));
        }

        totalTests += testSummaries.Count;
        suiteSummaries.Add(new CoverageSuiteSummary(suite.Id, suite.Name, testSummaries));
    }

    var summary = new CoveragePlanSummary(planId, effectiveSuiteId, totalTests, suiteSummaries);
    cache.Set(cacheKey, summary, TimeSpan.FromMinutes(10));
    await PersistCoveragePlanJson(summary, planId, effectiveSuiteId, webRootPath, ct);
    return summary;
}

static IReadOnlyList<TestSuiteItem> CollectSuiteWithDescendants(
    IReadOnlyList<TestSuiteItem> allSuites,
    int rootSuiteId)
{
    var byId = allSuites.ToDictionary(s => s.Id);
    var childrenByParent = allSuites
        .Where(s => s.ParentSuiteId is > 0)
        .GroupBy(s => s.ParentSuiteId!.Value)
        .ToDictionary(g => g.Key, g => g.OrderBy(s => s.Name, StringComparer.OrdinalIgnoreCase).ToList());

    var result = new List<TestSuiteItem>();
    var visited = new HashSet<int>();

    void Visit(int suiteId)
    {
        if (!visited.Add(suiteId))
            return;
        if (!byId.TryGetValue(suiteId, out var suite))
            return;

        result.Add(suite);
        if (!childrenByParent.TryGetValue(suiteId, out var children))
            return;

        foreach (var child in children)
            Visit(child.Id);
    }

    Visit(rootSuiteId);
    return result;
}

static async Task PersistCoveragePlanJson(
    CoveragePlanSummary summary,
    int planId,
    int? suiteId,
    string webRootPath,
    CancellationToken ct)
{
    var coverageDir = Path.Combine(webRootPath, "coverage");
    Directory.CreateDirectory(coverageDir);

    var json = JsonSerializer.Serialize(summary, new JsonSerializerOptions { WriteIndented = true });
    var planFile = Path.Combine(coverageDir, $"coverage-plan-{BuildCoverageSelectionToken(planId, suiteId)}.json");
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

    report = NormalizeCoverageReportSummary(report);
    cache.Set(cacheKey, report, TimeSpan.FromMinutes(10));
    return report;
}

static IReadOnlyList<string> BuildAllureCategoryParts(IReadOnlyList<AllureLabel>? labels)
{
    if (labels is null || labels.Count == 0)
        return Array.Empty<string>();

    string? GetLabel(string name) => labels
        .FirstOrDefault(l => string.Equals(l.Name, name, StringComparison.OrdinalIgnoreCase))
        ?.Value
        ?.Trim();

    return new[]
    {
        GetLabel("parentSuite"),
        GetLabel("suite"),
        GetLabel("subSuite")
    }
    .Where(p => !string.IsNullOrWhiteSpace(p))
    .Select(p => p!)
    .ToList();
}

static IReadOnlyList<CoverageAutomationCategoryIndex> BuildCoverageCategoryIndex(IReadOnlyList<CoverageAutomationTestCase> tests)
{
    return tests
        .GroupBy(t => string.IsNullOrWhiteSpace(t.CategoryPath) ? "Uncategorized" : t.CategoryPath)
        .OrderBy(g => g.Key, StringComparer.OrdinalIgnoreCase)
        .Select(g => new CoverageAutomationCategoryIndex
        {
            Name = g.Key,
            Count = g.Count()
        })
        .ToList();
}

static CoverageAutomationReportSummary NormalizeCoverageReportSummary(CoverageAutomationReportSummary report)
{
    var normalizedTests = report.Tests
        .Select(t =>
        {
            var categoryPath = string.IsNullOrWhiteSpace(t.CategoryPath) ? "Uncategorized" : t.CategoryPath.Trim();
            var categoryParts = t.CategoryParts is { Count: > 0 }
                ? t.CategoryParts.Where(p => !string.IsNullOrWhiteSpace(p)).Select(p => p.Trim()).ToList()
                : categoryPath.Split(" / ", StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries).ToList();

            var normalized = new CoverageAutomationTestCase
            {
                Title = t.Title,
                FullName = t.FullName,
                Status = t.Status,
                Uuid = t.Uuid,
                Steps = t.Steps ?? Array.Empty<string>(),
                CategoryPath = categoryPath,
                CategoryParts = categoryParts
            };

            normalized.EmbeddingText = string.IsNullOrWhiteSpace(t.EmbeddingText)
                ? BuildAllureEmbeddingText(normalized)
                : t.EmbeddingText;

            return normalized;
        })
        .ToList();

    var deduplicatedTests = DeduplicateCoverageTests(normalizedTests);

    return new CoverageAutomationReportSummary
    {
        Version = report.Version > 0 ? report.Version : 2,
        Source = string.IsNullOrWhiteSpace(report.Source) ? "allure-zip" : report.Source,
        TotalTests = deduplicatedTests.Count,
        Tests = deduplicatedTests,
        CategoryIndex = BuildCoverageCategoryIndex(deduplicatedTests)
    };
}

static IReadOnlyList<CoverageAutomationTestCase> DeduplicateCoverageTests(IReadOnlyList<CoverageAutomationTestCase> tests)
{
    var deduplicated = new List<CoverageAutomationTestCase>(tests.Count);
    var indexByKey = new Dictionary<string, int>(StringComparer.OrdinalIgnoreCase);

    foreach (var test in tests)
    {
        var key = BuildCoverageTestIdentityKey(test);
        if (!indexByKey.TryGetValue(key, out var existingIndex))
        {
            indexByKey[key] = deduplicated.Count;
            deduplicated.Add(test);
            continue;
        }

        if (ShouldPreferCoverageTest(test, deduplicated[existingIndex]))
            deduplicated[existingIndex] = test;
    }

    return deduplicated;
}

static string BuildCoverageTestIdentityKey(CoverageAutomationTestCase test)
{
    var categoryPath = string.IsNullOrWhiteSpace(test.CategoryPath) ? "Uncategorized" : test.CategoryPath.Trim();
    var identity = !string.IsNullOrWhiteSpace(test.FullName)
        ? test.FullName.Trim()
        : !string.IsNullOrWhiteSpace(test.Title)
            ? test.Title.Trim()
            : string.Empty;

    return $"{categoryPath}||{identity}";
}

static bool ShouldPreferCoverageTest(CoverageAutomationTestCase candidate, CoverageAutomationTestCase current)
{
    var candidateStepCount = candidate.Steps?.Count ?? 0;
    var currentStepCount = current.Steps?.Count ?? 0;
    if (candidateStepCount != currentStepCount)
        return candidateStepCount > currentStepCount;

    var candidateHasUuid = !string.IsNullOrWhiteSpace(candidate.Uuid);
    var currentHasUuid = !string.IsNullOrWhiteSpace(current.Uuid);
    if (candidateHasUuid != currentHasUuid)
        return candidateHasUuid;

    return false;
}

static string BuildAllureEmbeddingText(CoverageAutomationTestCase test)
{
    var parts = BuildCanonicalEmbeddingParts(
        test.Title,
        test.CategoryPath,
        test.FullName,
        test.Steps);
    return BuildCanonicalEmbeddingText(parts);
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

static IReadOnlyList<CanonicalCoverageItem> BuildCanonicalCoverageItemsFromAdo(CoveragePlanSummary plan)
{
    var items = new List<CanonicalCoverageItem>();
    foreach (var suite in plan.Suites)
    {
        foreach (var test in suite.Tests)
        {
            var parts = BuildCanonicalEmbeddingParts(
                test.Title,
                suite.Name,
                FullName: null,
                new[] { test.Steps });
            items.Add(new CanonicalCoverageItem(
                Id: test.WorkItemId.ToString(),
                WorkItemId: test.WorkItemId,
                Title: test.Title,
                CategoryPath: suite.Name,
                FullName: null,
                Uuid: null,
                Parts: parts));
        }
    }

    return items;
}

static IReadOnlyList<CanonicalCoverageItem> BuildCanonicalCoverageItemsFromAllure(CoverageAutomationReportSummary report)
{
    var items = new List<CanonicalCoverageItem>();
    var ids = BuildAllureIds(report.Tests);
    for (var i = 0; i < report.Tests.Count; i++)
    {
        var test = report.Tests[i];
        var parts = BuildCanonicalEmbeddingParts(
            test.Title,
            test.CategoryPath,
            test.FullName,
            test.Steps);
        items.Add(new CanonicalCoverageItem(
            Id: ids[i],
            WorkItemId: null,
            Title: test.Title,
            CategoryPath: test.CategoryPath,
            FullName: test.FullName,
            Uuid: test.Uuid,
            Parts: parts));
    }

    return items;
}

static CoverageEmbeddingParts BuildCanonicalEmbeddingParts(
    string? title,
    string? categoryPath,
    string? FullName,
    IEnumerable<string>? steps)
{
    var normalizedSteps = NormalizeStepsForEmbedding(string.Join("\n", steps ?? Array.Empty<string>()));
    return new CoverageEmbeddingParts(
        TitleText: NormalizeScalarText(title),
        CategoryText: NormalizeScalarText(categoryPath),
        FullNameText: NormalizeScalarText(FullName),
        StepsText: normalizedSteps);
}

static string BuildCanonicalEmbeddingText(CoverageEmbeddingParts parts)
{
    var sb = new StringBuilder();
    if (!string.IsNullOrWhiteSpace(parts.CategoryText))
        sb.AppendLine($"category: {parts.CategoryText}");
    if (!string.IsNullOrWhiteSpace(parts.TitleText))
        sb.AppendLine($"title: {parts.TitleText}");
    if (!string.IsNullOrWhiteSpace(parts.FullNameText))
        sb.AppendLine($"fullname: {parts.FullNameText}");
    if (!string.IsNullOrWhiteSpace(parts.StepsText))
    {
        sb.AppendLine("steps:");
        sb.AppendLine(parts.StepsText);
    }

    return sb.ToString().Trim();
}

static IReadOnlyList<CoverageEmbeddingSource> BuildEmbeddingSources(
    IReadOnlyList<CanonicalCoverageItem> items,
    Func<CanonicalCoverageItem, string> selector)
{
    return items
        .Select(item => new CoverageEmbeddingSource(item.Id, selector(item)))
        .ToList();
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

static async Task<CoverageChunkEmbeddingFile> GetOrCreateChunkEmbeddingsFile(
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
        var existing = JsonSerializer.Deserialize<CoverageChunkEmbeddingFile>(existingJson, new JsonSerializerOptions
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
        var emptyFile = new CoverageChunkEmbeddingFile(model, Array.Empty<CoverageChunkEmbeddingItem>());
        var emptyJson = JsonSerializer.Serialize(emptyFile, new JsonSerializerOptions { WriteIndented = true });
        Directory.CreateDirectory(Path.GetDirectoryName(filePath)!);
        await File.WriteAllTextAsync(filePath, emptyJson, ct);
        return emptyFile;
    }

    const int maxChunkChars = 800;
    var allChunks = new List<string>();
    var sourceChunkRanges = new List<(int Start, int Count)>(sources.Count);

    foreach (var source in sources)
    {
        var chunks = SplitTextIntoChunks(source.Text, maxChunkChars);
        var start = allChunks.Count;
        allChunks.AddRange(chunks);
        sourceChunkRanges.Add((start, chunks.Count));
    }

    var chunkVectors = allChunks.Count == 0
        ? Array.Empty<float[]>()
        : (await EmbedInBatches(embeddings, allChunks, ct)).ToArray();

    if (chunkVectors.Length != allChunks.Count)
        throw new InvalidOperationException("Chunk embedding count does not match input count.");

    var items = new List<CoverageChunkEmbeddingItem>(sources.Count);
    for (var i = 0; i < sources.Count; i++)
    {
        var (start, count) = sourceChunkRanges[i];
        var vectors = count == 0
            ? Array.Empty<float[]>()
            : chunkVectors.Skip(start).Take(count).ToArray();
        items.Add(new CoverageChunkEmbeddingItem(sources[i].Id, sources[i].Text, vectors));
    }

    var file = new CoverageChunkEmbeddingFile(model, items);
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
    IReadOnlyList<CanonicalCoverageItem> adoItems,
    IReadOnlyList<CanonicalCoverageItem> allureItems,
    CoverageEmbeddingFile adoTitleEmbeddings,
    CoverageEmbeddingFile adoCategoryEmbeddings,
    CoverageChunkEmbeddingFile adoStepsEmbeddings,
    CoverageEmbeddingFile allureTitleEmbeddings,
    CoverageEmbeddingFile allureCategoryEmbeddings,
    CoverageChunkEmbeddingFile allureStepsEmbeddings,
    string teamId,
    string model,
    double minScore,
    int topK)
{
    if (adoItems.Count == 0)
    {
        return new CoverageEmbeddingReport(
            plan.PlanId,
            plan.SuiteId,
            teamId,
            0,
            allureItems.Count,
            0,
            0,
            model,
            minScore,
            topK,
            Array.Empty<CoverageEmbeddingMatch>(),
            Array.Empty<CoverageEmbeddingUnmatchedAdo>(),
            allureItems.Select(a => new CoverageEmbeddingUnmatchedAllure(a.Title, a.FullName, a.Uuid)).ToList(),
            Array.Empty<CoverageEmbeddingCandidate>());
    }

    var scoreMatrix = BuildSimilarityMatrix(
        adoItems,
        allureItems,
        adoTitleEmbeddings,
        adoCategoryEmbeddings,
        adoStepsEmbeddings,
        allureTitleEmbeddings,
        allureCategoryEmbeddings,
        allureStepsEmbeddings);

    var assignment = SolveOptimalAssignment(scoreMatrix);
    var matches = new List<CoverageEmbeddingMatch>();
    var unmatchedAdo = new List<CoverageEmbeddingUnmatchedAdo>();
    var matchedAllure = new HashSet<int>();
    var candidates = BuildEmbeddingCandidates(adoItems, allureItems, scoreMatrix, topK);

    for (var adoIndex = 0; adoIndex < adoItems.Count; adoIndex++)
    {
        var ado = adoItems[adoIndex];
        var assignedAllure = adoIndex < assignment.Length ? assignment[adoIndex] : -1;
        if (assignedAllure >= 0 && assignedAllure < allureItems.Count)
        {
            var score = scoreMatrix[adoIndex][assignedAllure];
            if (score.FinalScore >= minScore)
            {
                var allure = allureItems[assignedAllure];
                matches.Add(new CoverageEmbeddingMatch(
                    ado.WorkItemId ?? 0,
                    ado.Title,
                    ado.CategoryPath,
                    allure.Title,
                    allure.FullName,
                    allure.Uuid,
                    score.FinalScore,
                    score.TitleScore,
                    score.CategoryScore,
                    score.StepsScore,
                    "global_assignment"));
                matchedAllure.Add(assignedAllure);
                continue;
            }
        }

        unmatchedAdo.Add(new CoverageEmbeddingUnmatchedAdo(
            ado.WorkItemId ?? 0,
            ado.Title,
            ado.CategoryPath));
    }

    var unmatchedAllure = allureItems
        .Where((_, index) => !matchedAllure.Contains(index))
        .Select(item => new CoverageEmbeddingUnmatchedAllure(item.Title, item.FullName, item.Uuid))
        .ToList();

    var matchedCount = matches.Count;
    var totalAdo = adoItems.Count;
    var totalAllure = allureItems.Count;
    var coveragePercent = totalAdo == 0 ? 0 : Math.Round((double)matchedCount / totalAdo * 100, 2);

    return new CoverageEmbeddingReport(
        plan.PlanId,
        plan.SuiteId,
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
        unmatchedAllure,
        candidates);
}

static CoverageSimilarityScore[][] BuildSimilarityMatrix(
    IReadOnlyList<CanonicalCoverageItem> adoItems,
    IReadOnlyList<CanonicalCoverageItem> allureItems,
    CoverageEmbeddingFile adoTitleEmbeddings,
    CoverageEmbeddingFile adoCategoryEmbeddings,
    CoverageChunkEmbeddingFile adoStepsEmbeddings,
    CoverageEmbeddingFile allureTitleEmbeddings,
    CoverageEmbeddingFile allureCategoryEmbeddings,
    CoverageChunkEmbeddingFile allureStepsEmbeddings)
{
    ValidateEmbeddingAlignment(adoItems, adoTitleEmbeddings.Items, "ADO title");
    ValidateEmbeddingAlignment(adoItems, adoCategoryEmbeddings.Items, "ADO category");
    ValidateChunkEmbeddingAlignment(adoItems, adoStepsEmbeddings.Items, "ADO steps");
    ValidateEmbeddingAlignment(allureItems, allureTitleEmbeddings.Items, "Allure title");
    ValidateEmbeddingAlignment(allureItems, allureCategoryEmbeddings.Items, "Allure category");
    ValidateChunkEmbeddingAlignment(allureItems, allureStepsEmbeddings.Items, "Allure steps");

    var adoTitleVectors = adoTitleEmbeddings.Items.Select(item => NormalizeVector(item.Vector)).ToArray();
    var adoCategoryVectors = adoCategoryEmbeddings.Items.Select(item => NormalizeVector(item.Vector)).ToArray();
    var allureTitleVectors = allureTitleEmbeddings.Items.Select(item => NormalizeVector(item.Vector)).ToArray();
    var allureCategoryVectors = allureCategoryEmbeddings.Items.Select(item => NormalizeVector(item.Vector)).ToArray();
    var matrix = new CoverageSimilarityScore[adoItems.Count][];

    for (var i = 0; i < adoItems.Count; i++)
    {
        matrix[i] = new CoverageSimilarityScore[allureItems.Count];
        for (var j = 0; j < allureItems.Count; j++)
        {
            var titleScore = Dot(adoTitleVectors[i], allureTitleVectors[j]);
            var categoryScore = Dot(adoCategoryVectors[i], allureCategoryVectors[j]);
            var stepsScore = CalculateChunkedSimilarity(
                adoStepsEmbeddings.Items[i].Vectors,
                allureStepsEmbeddings.Items[j].Vectors);
            var finalScore = (titleScore * 0.45) + (categoryScore * 0.15) + (stepsScore * 0.40);
            matrix[i][j] = new CoverageSimilarityScore(finalScore, titleScore, categoryScore, stepsScore);
        }
    }

    return matrix;
}

static void ValidateEmbeddingAlignment(
    IReadOnlyList<CanonicalCoverageItem> items,
    IReadOnlyList<CoverageEmbeddingItem> embeddedItems,
    string scope)
{
    if (items.Count != embeddedItems.Count)
        throw new InvalidOperationException($"{scope} embedding metadata mismatch.");

    for (var i = 0; i < items.Count; i++)
    {
        if (!string.Equals(items[i].Id, embeddedItems[i].Id, StringComparison.Ordinal))
            throw new InvalidOperationException($"{scope} embedding id mismatch at index {i}.");
    }
}

static void ValidateChunkEmbeddingAlignment(
    IReadOnlyList<CanonicalCoverageItem> items,
    IReadOnlyList<CoverageChunkEmbeddingItem> embeddedItems,
    string scope)
{
    if (items.Count != embeddedItems.Count)
        throw new InvalidOperationException($"{scope} embedding metadata mismatch.");

    for (var i = 0; i < items.Count; i++)
    {
        if (!string.Equals(items[i].Id, embeddedItems[i].Id, StringComparison.Ordinal))
            throw new InvalidOperationException($"{scope} embedding id mismatch at index {i}.");
    }
}

static IReadOnlyList<CoverageEmbeddingCandidate> BuildEmbeddingCandidates(
    IReadOnlyList<CanonicalCoverageItem> adoItems,
    IReadOnlyList<CanonicalCoverageItem> allureItems,
    CoverageSimilarityScore[][] scoreMatrix,
    int topK)
{
    var candidates = new List<CoverageEmbeddingCandidate>();
    var take = topK > 0 ? topK : 5;
    for (var i = 0; i < adoItems.Count; i++)
    {
        var ranked = Enumerable.Range(0, allureItems.Count)
            .Select(index => (Index: index, Score: scoreMatrix[i][index]))
            .OrderByDescending(item => item.Score.FinalScore)
            .ThenByDescending(item => item.Score.TitleScore)
            .Take(take)
            .ToList();

        for (var rank = 0; rank < ranked.Count; rank++)
        {
            var candidate = ranked[rank];
            var allure = allureItems[candidate.Index];
            candidates.Add(new CoverageEmbeddingCandidate(
                adoItems[i].WorkItemId ?? 0,
                adoItems[i].Title,
                adoItems[i].CategoryPath,
                allure.Title,
                allure.FullName,
                allure.Uuid,
                candidate.Score.FinalScore,
                candidate.Score.TitleScore,
                candidate.Score.CategoryScore,
                candidate.Score.StepsScore,
                rank + 1));
        }
    }

    return candidates;
}

static double CalculateChunkedSimilarity(
    IReadOnlyList<float[]> left,
    IReadOnlyList<float[]> right)
{
    if (left.Count == 0 || right.Count == 0)
        return 0;

    var leftVectors = left.Select(NormalizeVector).Where(v => v.Length > 0).ToList();
    var rightVectors = right.Select(NormalizeVector).Where(v => v.Length > 0).ToList();
    if (leftVectors.Count == 0 || rightVectors.Count == 0)
        return 0;

    static double AverageBestMatches(IReadOnlyList<float[]> source, IReadOnlyList<float[]> target)
    {
        double sum = 0;
        foreach (var sourceVector in source)
        {
            var best = 0.0;
            foreach (var targetVector in target)
                best = Math.Max(best, Dot(sourceVector, targetVector));
            sum += best;
        }

        return sum / source.Count;
    }

    var forward = AverageBestMatches(leftVectors, rightVectors);
    var backward = AverageBestMatches(rightVectors, leftVectors);
    return (forward + backward) / 2.0;
}

static int[] SolveOptimalAssignment(CoverageSimilarityScore[][] scoreMatrix)
{
    var rowCount = scoreMatrix.Length;
    var colCount = rowCount == 0 ? 0 : scoreMatrix[0].Length;
    var size = Math.Max(rowCount, colCount);
    if (size == 0)
        return Array.Empty<int>();

    var maxScore = 0.0;
    for (var i = 0; i < rowCount; i++)
    {
        for (var j = 0; j < colCount; j++)
            maxScore = Math.Max(maxScore, scoreMatrix[i][j].FinalScore);
    }

    var cost = new double[size + 1, size + 1];
    for (var i = 1; i <= size; i++)
    {
        for (var j = 1; j <= size; j++)
        {
            var score = (i <= rowCount && j <= colCount)
                ? scoreMatrix[i - 1][j - 1].FinalScore
                : 0.0;
            cost[i, j] = maxScore - score;
        }
    }

    var u = new double[size + 1];
    var v = new double[size + 1];
    var p = new int[size + 1];
    var way = new int[size + 1];

    for (var i = 1; i <= size; i++)
    {
        p[0] = i;
        var j0 = 0;
        var minv = new double[size + 1];
        var used = new bool[size + 1];
        for (var j = 0; j <= size; j++)
            minv[j] = double.PositiveInfinity;

        do
        {
            used[j0] = true;
            var i0 = p[j0];
            var delta = double.PositiveInfinity;
            var j1 = 0;
            for (var j = 1; j <= size; j++)
            {
                if (used[j])
                    continue;

                var cur = cost[i0, j] - u[i0] - v[j];
                if (cur < minv[j])
                {
                    minv[j] = cur;
                    way[j] = j0;
                }

                if (minv[j] < delta)
                {
                    delta = minv[j];
                    j1 = j;
                }
            }

            for (var j = 0; j <= size; j++)
            {
                if (used[j])
                {
                    u[p[j]] += delta;
                    v[j] -= delta;
                }
                else
                {
                    minv[j] -= delta;
                }
            }

            j0 = j1;
        } while (p[j0] != 0);

        do
        {
            var j1 = way[j0];
            p[j0] = p[j1];
            j0 = j1;
        } while (j0 != 0);
    }

    var assignment = Enumerable.Repeat(-1, rowCount).ToArray();
    for (var j = 1; j <= size; j++)
    {
        var row = p[j];
        if (row >= 1 && row <= rowCount)
            assignment[row - 1] = j <= colCount ? j - 1 : -1;
    }

    return assignment;
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

static string NormalizeText(string? text)
    => NormalizeScalarText(text);

static string NormalizeScalarText(string? text)
{
    if (string.IsNullOrWhiteSpace(text))
        return string.Empty;

    var collapsed = Regex.Replace(text, @"\s+", " ").Trim();
    return collapsed.ToLowerInvariant();
}

static string NormalizeStepsForEmbedding(string text)
{
    if (string.IsNullOrWhiteSpace(text))
        return string.Empty;

    var decoded = WebUtility.HtmlDecode(StripHtml(text));
    var lines = decoded
        .Replace("\r", "\n")
        .Split('\n', StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries)
        .Select(NormalizeStepLineForEmbedding)
        .Where(line => !string.IsNullOrWhiteSpace(line))
        .ToList();

    if (lines.Count == 0)
        return string.Empty;

    const int chunkSize = 20;
    if (lines.Count <= chunkSize)
        return string.Join("\n", lines);

    var summaries = new List<string>();
    for (var i = 0; i < lines.Count; i += chunkSize)
    {
        var chunk = lines.Skip(i).Take(chunkSize).ToList();
        if (chunk.Count == 0)
            continue;

        var first = chunk.First();
        var middle = chunk[chunk.Count / 2];
        var last = chunk.Last();
        summaries.Add($"chunk {i / chunkSize + 1}: first {first} middle {middle} last {last}");
    }

    return string.Join("\n", summaries);
}

static string NormalizeStepLineForEmbedding(string line)
{
    var normalized = NormalizeScalarText(line);
    if (string.IsNullOrWhiteSpace(normalized))
        return string.Empty;

    normalized = Regex.Replace(normalized, @"^\s*(?:step\s*)?\d+\s*[\.\):\-]\s*", "");
    normalized = Regex.Replace(normalized, @"^\s*[-*•]+\s*", "");

    var grouped = Regex.Match(
        normalized,
        @"^json steps \d+\-\d+\.\s*first:\s*(?<first>.+?)\s+last:\s*(?<last>.+?)(?:\|.*)?$",
        RegexOptions.IgnoreCase);
    if (grouped.Success)
    {
        var first = SummarizePayloadForEmbedding(grouped.Groups["first"].Value);
        var last = SummarizePayloadForEmbedding(grouped.Groups["last"].Value);
        return $"grouped first {first} last {last}";
    }

    return SummarizePayloadForEmbedding(normalized);
}

static string SummarizePayloadForEmbedding(string text)
{
    var normalized = NormalizeScalarText(text);
    if (string.IsNullOrWhiteSpace(normalized))
        return string.Empty;

    normalized = Regex.Replace(normalized, @"[{}\[\]""']", " ");
    normalized = Regex.Replace(normalized, @"[:;,=|]", " ");
    normalized = Regex.Replace(normalized, @"\\[rnqt]", " ");
    normalized = Regex.Replace(normalized, @"\b\d{4,}\b", "#");
    normalized = Regex.Replace(normalized, @"\s+", " ").Trim();

    const int maxTokens = 40;
    var tokens = normalized
        .Split(' ', StringSplitOptions.RemoveEmptyEntries)
        .Take(maxTokens)
        .ToArray();
    return string.Join(' ', tokens);
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
    int? suiteId,
    string teamId,
    string webRootPath,
    CancellationToken ct)
{
    var coverageDir = Path.Combine(webRootPath, "coverage");
    Directory.CreateDirectory(coverageDir);

    var json = JsonSerializer.Serialize(report, new JsonSerializerOptions { WriteIndented = true });
    var reportFile = Path.Combine(coverageDir, $"coverage-embedding-report-{BuildCoverageSelectionToken(planId, suiteId)}-{teamId}.json");
    var latestFile = Path.Combine(coverageDir, "coverage-embedding-report.json");

    await File.WriteAllTextAsync(reportFile, json, ct);
    await File.WriteAllTextAsync(latestFile, json, ct);
}

static async Task PersistCoverageEmbeddingLlmReport(
    CoverageEmbeddingLlmReport report,
    int planId,
    int? suiteId,
    string teamId,
    string webRootPath,
    CancellationToken ct)
{
    var coverageDir = Path.Combine(webRootPath, "coverage");
    Directory.CreateDirectory(coverageDir);

    var json = JsonSerializer.Serialize(report, new JsonSerializerOptions { WriteIndented = true });
    var reportFile = Path.Combine(coverageDir, $"coverage-embedding-llm-report-{BuildCoverageSelectionToken(planId, suiteId)}-{teamId}.json");
    var latestFile = Path.Combine(coverageDir, "coverage-embedding-llm-report.json");

    await File.WriteAllTextAsync(reportFile, json, ct);
    await File.WriteAllTextAsync(latestFile, json, ct);
}

static async Task PersistCoverageEmbeddingLlmCoverageReport(
    CoverageEmbeddingLlmCoverageReport report,
    int planId,
    int? suiteId,
    string teamId,
    string webRootPath,
    CancellationToken ct)
{
    var coverageDir = Path.Combine(webRootPath, "coverage");
    Directory.CreateDirectory(coverageDir);

    var json = JsonSerializer.Serialize(report, new JsonSerializerOptions { WriteIndented = true });
    var reportFile = Path.Combine(coverageDir, $"coverage-embedding-llm-coverage-report-{BuildCoverageSelectionToken(planId, suiteId)}-{teamId}.json");
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
            embeddingReport.SuiteId,
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
                1.0,
                1.0,
                1.0,
                1.0,
                "llm_review"));

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
        embeddingReport.SuiteId,
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
        BuildCoverageSelectionToken(plan.PlanId, plan.SuiteId),
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
        embeddingReport.SuiteId,
        embeddingReport.TeamId,
        "groq",
        tokenUsage,
        matchResult.Reviews,
        gapResult.Explanations,
        gapResult.MissingTests);
}

static string BuildCoverageSelectionToken(int planId, int? suiteId)
    => suiteId is > 0 ? $"{planId}-{suiteId.Value}" : planId.ToString();

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
            var steps = NormalizeStepsForEmbedding(test.Steps);
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
        var steps = test.Steps is { Count: > 0 } ? NormalizeStepsForEmbedding(string.Join("\n", test.Steps)) : string.Empty;
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
            Feature: test.CategoryPath,
            Screen: null,
            actions,
            assertions,
            Behaviors: Array.Empty<string>(),
            new Dictionary<string, string>
            {
                { "uuid", test.Uuid ?? string.Empty },
                { "categoryPath", test.CategoryPath ?? string.Empty }
            }));
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

sealed record CoverageEmbeddingParts(
    string TitleText,
    string CategoryText,
    string FullNameText,
    string StepsText);

sealed record CanonicalCoverageItem(
    string Id,
    int? WorkItemId,
    string Title,
    string CategoryPath,
    string? FullName,
    string? Uuid,
    CoverageEmbeddingParts Parts);

sealed record CoverageSimilarityScore(
    double FinalScore,
    double TitleScore,
    double CategoryScore,
    double StepsScore);

sealed record CoverageChunkEmbeddingItem(
    string Id,
    string Text,
    IReadOnlyList<float[]> Vectors);

sealed record CoverageChunkEmbeddingFile(
    string Model,
    IReadOnlyList<CoverageChunkEmbeddingItem> Items);

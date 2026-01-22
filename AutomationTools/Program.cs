using Microsoft.AspNetCore.Mvc;
using Microsoft.Extensions.Options;
using AutomationTools.Ado;
using AutomationTools.Sync;
using AutomationTools.Allure;
using AutomationTools.Recording;
using AutomationTools.TestGenerator;
using System.Text.Json;

var builder = WebApplication.CreateBuilder(args);

// Read ONLY appsettings.json (ignore appsettings.{Environment}.json)
builder.Configuration.Sources.Clear();
builder.Configuration
    .AddJsonFile("appsettings.json", optional: false, reloadOnChange: true)
    .AddEnvironmentVariables()
    .AddCommandLine(args);

builder.Services.Configure<AdoOptions>(builder.Configuration.GetSection(AdoOptions.SectionName));
builder.Services.AddSingleton(sp => sp.GetRequiredService<IOptions<AdoOptions>>().Value);

builder.Services.AddHttpClient<AdoClient>((sp, http) =>
{
    var opts = sp.GetRequiredService<AdoOptions>();
    AdoClient.ConfigureHttpClient(http, opts);
});

builder.Services.AddSingleton<AllureToAdoSyncService>();
builder.Services.AddSingleton<RecordingToAdoSyncService>();

builder.Services.AddEndpointsApiExplorer();
builder.Services.AddSwaggerGen();

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

app.Run();

public sealed record SyncFolderRequest(string AllureResultsPath, int? PlanId, int? SuiteId);

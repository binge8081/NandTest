using System.Text;
using System.Text.Json;
using Microsoft.Extensions.FileProviders;
using NandFlashTester.Api.Data;
using NandFlashTester.Api.Lots;
using NandFlashTester.Api.Materials;
using NandFlashTester.Api.Jobs;
using NandFlashTester.Api.Platform;
using NandFlashTester.Api.Identification;
using NandFlashTester.Api.Scripts;
using NandFlashTester.Core;
using NandFlashTester.Core.Devices;
using NandFlashTester.Core.Execution;
using NandFlashTester.Core.Management;
using NandFlashTester.Core.Materials;
using NandFlashTester.Core.Platform;
using NandFlashTester.Core.Patterns;
using NandFlashTester.Core.Lots;
using NandFlashTester.Core.Recipes;
using NandFlashTester.Core.Scope;

var builder = WebApplication.CreateBuilder(args);

var webRoot = builder.Configuration["WebRoot"];
if (string.IsNullOrWhiteSpace(webRoot))
{
    webRoot = Path.GetFullPath(Path.Combine(builder.Environment.ContentRootPath, "..", "..", "web", "dist"));
}

if (!string.Equals(builder.Environment.EnvironmentName, "Testing", StringComparison.Ordinal))
{
    builder.WebHost.UseUrls(builder.Configuration["Urls"] ?? "http://127.0.0.1:5080");
}

builder.Services.AddSingleton(sp =>
{
    var databasePath = ResolveDatabasePath(sp.GetRequiredService<IConfiguration>(), sp.GetRequiredService<IHostEnvironment>());
    return new SqliteJobStore(databasePath);
});
builder.Services.AddSingleton(sp =>
{
    var databasePath = ResolveDatabasePath(sp.GetRequiredService<IConfiguration>(), sp.GetRequiredService<IHostEnvironment>());
    return new SqliteSessionStore(databasePath);
});
builder.Services.AddSingleton(sp =>
{
    var databasePath = ResolveDatabasePath(sp.GetRequiredService<IConfiguration>(), sp.GetRequiredService<IHostEnvironment>());
    return new SqliteTraceStore(databasePath);
});
builder.Services.AddSingleton(sp =>
{
    var databasePath = ResolveDatabasePath(sp.GetRequiredService<IConfiguration>(), sp.GetRequiredService<IHostEnvironment>());
    return new SqliteScriptStore(databasePath);
});
builder.Services.AddSingleton(sp =>
{
    var databasePath = ResolveDatabasePath(sp.GetRequiredService<IConfiguration>(), sp.GetRequiredService<IHostEnvironment>());
    return new SqlitePatternStore(databasePath);
});
builder.Services.AddSingleton(sp =>
{
    var databasePath = ResolveDatabasePath(sp.GetRequiredService<IConfiguration>(), sp.GetRequiredService<IHostEnvironment>());
    return new SqliteLotStore(databasePath);
});
builder.Services.AddSingleton<IdentificationSession>();
builder.Services.AddSingleton<JobQueue>();
builder.Services.AddSingleton<IJobPacing, NoDelayPacing>();
builder.Services.AddSingleton<JobService>();
builder.Services.AddHostedService<JobWorker>();
builder.Services.AddCors(options =>
{
    options.AddPolicy("ui", policy =>
    {
        policy.WithOrigins("http://127.0.0.1:5173", "http://localhost:5173")
            .AllowAnyHeader()
            .AllowAnyMethod();
    });
});

var app = builder.Build();
app.Services.GetRequiredService<SqliteJobStore>().Initialize();
app.Services.GetRequiredService<SqliteSessionStore>().Initialize();
app.Services.GetRequiredService<SqliteTraceStore>().Initialize();
app.Services.GetRequiredService<SqliteScriptStore>().Initialize();
app.Services.GetRequiredService<SqlitePatternStore>().Initialize();
app.Services.GetRequiredService<SqliteLotStore>().Initialize();
app.UseCors("ui");

var bundledUi = Path.Combine(app.Environment.ContentRootPath, "wwwroot");
if (File.Exists(Path.Combine(bundledUi, "index.html")))
{
    webRoot = bundledUi;
}

var uiReady = File.Exists(Path.Combine(webRoot, "index.html"));
if (uiReady)
{
    var files = new PhysicalFileProvider(webRoot);
    app.UseDefaultFiles(new DefaultFilesOptions { FileProvider = files });
    app.UseStaticFiles(new StaticFileOptions { FileProvider = files });
}

var fixtureEnabled = PlatformModes.IsAutomatedTest(app.Configuration["Platform:Mode"]);

app.MapGet("/api/health", () => Results.Json(new
{
    status = "ok",
    service = "nand-platform",
    product = ProductIdentity.Name,
    framework = System.Runtime.InteropServices.RuntimeInformation.FrameworkDescription,
    hardwareAccepted = false,
    icdProvided = false,
    windowsExecutionVerified = false,
    schema = RecipeContract.SchemaId,
    identity = BuildInfo.Create(app.Environment.ContentRootPath, webRoot, fixtureEnabled ? PlatformModes.AutomatedTest : PlatformModes.Production)
}));
app.MapGet("/api/about", () => Results.Json(BuildInfo.Create(
    app.Environment.ContentRootPath,
    webRoot,
    fixtureEnabled ? PlatformModes.AutomatedTest : PlatformModes.Production)));

IdentificationRoutes.Map(app);

app.MapGet("/api/discovery", () => Results.Json(new
{
    connected = false,
    probed = false,
    devices = Array.Empty<object>(),
    reason = "发现与连接不可用：平台通信端点尚未提供。软件不会猜测地址，也不会发送探测命令。",
    next = "等待获准的通信说明给出端点后再连接。在此之前，依赖实机的执行保持禁用。"
}));

app.MapGet("/api/platform", () => Results.Json(SessionEndpoints.PlatformBody(fixtureEnabled)));
app.MapGet("/api/management/draft", () => Results.Json(new
{
    label = DraftReadOnlyProfile.Label,
    version = DraftReadOnlyProfile.Version,
    liveConnectAllowed = DraftReadOnlyProfile.LiveConnectAllowed,
    reason = DraftReadOnlyProfile.LiveConnectReason,
    operations = DraftReadOnlyProfile.Operations,
    notInThisDraft = DraftReadOnlyProfile.NotInThisDraft,
    hardwareAccepted = false,
    adopted = false
}));

app.MapPost("/api/management/draft/loopback", async (HttpRequest request) =>
{
    using var reader = new StreamReader(request.Body, Encoding.UTF8);
    var body = await reader.ReadToEndAsync();
    if (body.Contains("endpoint", StringComparison.OrdinalIgnoreCase) || body.Contains("tcp", StringComparison.OrdinalIgnoreCase))
    {
        return Results.Json(new
        {
            error = "live_connect_forbidden",
            message = DraftReadOnlyProfile.LiveConnectReason,
            liveConnectAllowed = false,
            hardwareAccepted = false
        }, statusCode: StatusCodes.Status400BadRequest);
    }

    var status = DraftMessages.LoopbackHelloThenStatus();
    return Results.Json(new
    {
        label = DraftReadOnlyProfile.Label,
        liveConnectAllowed = false,
        hardwareAccepted = false,
        safeToStartGrantsCommand = false,
        state = status.State,
        safeToStart = status.SafeToStart,
        safeToReset = status.SafeToReset,
        rateCount = status.RateCount,
        pending = DraftReadOnlyProfile.NotInThisDraft
    });
});

app.MapGet("/api/materials", () => Results.Json(MaterialEndpoints.View(DeviceMaterialModel.Unconfigured())));
app.MapPost("/api/materials/claim", (MaterialClaim claim) => Results.Json(MaterialEndpoints.View(DeviceMaterialModel.Bind(claim))));
app.MapPost("/api/materials/coverage", (CoverageRequest request) =>
{
    var temperature = DeviceMaterialModel.Temperature(request.SkuClass, request.Temperatures);
    return Results.Json(MaterialEndpoints.Coverage(request), statusCode: temperature.WithinCandidate ? StatusCodes.Status200OK : StatusCodes.Status400BadRequest);
});

app.MapPost("/api/patterns", async (HttpRequest request, SqlitePatternStore patterns, IConfiguration configuration) =>
{
    var quota = new HostPatternQuota(configuration.GetValue("Platform:PatternHostMaxBytes", HostPatternQuota.Default.MaxBytes));
    var data = await ReadDeclaredAsync(request, quota.MaxBytes);
    if (data is null)
    {
        return Results.Json(new { error = "pattern_rejected", message = $"读取前拒绝：必须先给出不超过 {quota.MaxBytes} 字节的长度。{HostPatternQuota.Role}" }, statusCode: StatusCodes.Status400BadRequest);
    }
    var pageSize = int.TryParse(request.Query["pageSize"], out var parsed) ? parsed : 0;
    var (resource, error) = PatternLibrary.Accept(request.Query["name"], request.Query["version"], pageSize, data, quota);
    if (resource is null)
    {
        return Results.Json(new { error = "pattern_rejected", message = error, hostQuotaIsDeviceSpecification = false }, statusCode: StatusCodes.Status400BadRequest);
    }

    patterns.Save(resource);
    var geometry = DeviceGeometry.Check(null, resource.PageSize);
    return Results.Json(new
    {
        id = resource.Id,
        name = resource.Name,
        version = resource.Version,
        sha256 = resource.Sha256,
        pageSize = resource.PageSize,
        pageCount = resource.PageCount,
        formulaNote = PatternLibrary.FormulaNote,
        hostQuotaBytes = quota.MaxBytes,
        hostQuotaIsDeviceSpecification = false,
        devicePageKnown = geometry.DevicePageKnown,
        deviceGeometryEnabled = geometry.RealDeviceEnabled,
        geometryReason = geometry.Reason,
        hardwareExecuted = false
    }, statusCode: StatusCodes.Status201Created);
});
app.MapGet("/api/patterns", (SqlitePatternStore patterns) => Results.Json(new
{
    formulaNote = PatternLibrary.FormulaNote,
    patterns = patterns.Catalog().Values.Select(resource => new
    {
        id = resource.Id,
        name = resource.Name,
        version = resource.Version,
        sha256 = resource.Sha256,
        pageSize = resource.PageSize,
        pageCount = resource.PageCount
    })
}));
app.MapGet("/api/scripts/example", () => Results.Json(ScriptEndpoints.Example()));
app.MapGet("/api/scripts/map", () => Results.Json(ScriptEndpoints.Map()));
app.MapPost("/api/scripts/check", (ScriptDocumentRequest request, SqlitePatternStore patterns) =>
{
    var (status, body) = ScriptEndpoints.Check(request.Body, patterns.Catalog());
    return Results.Json(body, statusCode: status);
});
app.MapPost("/api/scripts", (ScriptDocumentRequest request, SqliteScriptStore store) =>
{
    var (status, body) = ScriptEndpoints.Create(store, request);
    return Results.Json(body, statusCode: status);
});
app.MapGet("/api/scripts", (SqliteScriptStore store) => Results.Json(new { scripts = store.List().Select(ScriptEndpoints.Summary) }));
app.MapGet("/api/scripts/{id}", (string id, SqliteScriptStore store) =>
{
    var script = store.Get(id);
    return script is null
        ? Results.Json(new { error = "not_found", message = "脚本不存在。" }, statusCode: StatusCodes.Status404NotFound)
        : Results.Json(new { script = ScriptEndpoints.Summary(script), logs = store.Logs(id).Select(log => new { log.LogId, log.Kind, log.Line, log.Column, log.Message, log.HardwareExecuted }) });
});
app.MapPut("/api/scripts/{id}", (string id, ScriptDocumentRequest request, SqliteScriptStore store) =>
{
    var (status, body) = ScriptEndpoints.Save(store, id, request);
    return Results.Json(body, statusCode: status);
});
app.MapPost("/api/scripts/{id}/plan", (string id, SqliteScriptStore store, SqlitePatternStore patterns) =>
{
    var (status, body) = ScriptEndpoints.Plan(store, id, patterns.Catalog());
    return Results.Json(body, statusCode: status);
});
app.MapPost("/api/scripts/{id}/stop", (string id, SqliteScriptStore store) =>
{
    var (status, body) = ScriptEndpoints.Stop(store, id);
    return Results.Json(body, statusCode: status);
});
app.MapPost("/api/scripts/{id}/run", (string id, RealDeviceClaim claim, SqliteScriptStore store) =>
{
    var (status, body) = ScriptEndpoints.Run(store, id, claim.SkuSpecVerified);
    return Results.Json(body, statusCode: status);
});
app.MapGet("/api/scripts/{id}/export.csv", (string id, SqliteScriptStore store) =>
{
    var script = store.Get(id);
    return script is null
        ? Results.Json(new { error = "not_found" }, statusCode: StatusCodes.Status404NotFound)
        : Results.File(Encoding.UTF8.GetBytes(SqliteScriptStore.Csv(script, store.Logs(id), store.Snapshots().Where(item => item.ScriptId == id).ToArray())), "text/csv; charset=utf-8", $"{id}.csv");
});

app.MapGet("/api/meta", () => Results.Json(new
{
    simulationOnly = PhaseScope.SimulationOnly,
    claimsMeasuredThroughput = PhaseScope.ClaimsMeasuredThroughput,
    localDeliveryTarget = PhaseScope.LocalDeliveryTarget,
    localDeliveryPathWritten = PhaseScope.LocalDeliveryPathWritten,
    jsonEntryIsSslCompatible = PhaseScope.JsonEntryIsSslCompatible,
    hardwareAccepted = PhaseScope.HardwareAccepted,
    icdProvided = PhaseScope.IcdProvided,
    wireProtocolAssumed = PhaseScope.WireProtocolAssumed,
    windowsExecutionVerified = PhaseScope.WindowsExecutionVerified,
    oldBinaryCompatibility = PhaseScope.OldBinaryCompatibility,
    sslCompatibilityLayer = PhaseScope.SslCompatibilityLayer,
    reimplementationPolicy = PhaseScope.ReimplementationPolicy,
    expertEntryLocation = PhaseScope.ExpertEntryLocation,
    partialCount = PhaseScope.PartialCount,
    itemCount = PhaseScope.Items.Count,
    conflictsForBao = PhaseScope.ConflictsForBao,
    missingLegitimateSources = PhaseScope.MissingLegitimateSources,
    items = PhaseScope.Items
}));

app.MapPost("/api/sessions", (SqliteSessionStore store) =>
{
    var sessionId = SessionEndpoints.Open(store);
    return Results.Json(SessionEndpoints.ToDetail(store.Get(sessionId)!), statusCode: StatusCodes.Status201Created);
});

app.MapGet("/api/sessions", (SqliteSessionStore store, int? limit) =>
    Results.Json(new { sessions = store.List(Math.Clamp(limit ?? 50, 1, 100)).Select(SessionEndpoints.ToDetail) }));

app.MapGet("/api/sessions/{id}", (string id, SqliteSessionStore store) =>
{
    var session = store.Get(id);
    return session is null
        ? Results.Json(new { error = "not_found", message = "会话不存在。" }, statusCode: StatusCodes.Status404NotFound)
        : Results.Json(SessionEndpoints.ToDetail(session));
});

app.MapPost("/api/sessions/{id}/execute", async (string id, HttpRequest request, SqliteSessionStore store) =>
{
    var bytes = await ReadBodyAsync(request);
    if (bytes is null)
    {
        return Results.Json(new { error = "recipe_rejected", message = "配置超过 16 KiB，未发送。", executed = false, hardwareAccepted = false }, statusCode: StatusCodes.Status400BadRequest);
    }

    var (status, body) = await SessionEndpoints.Execute(store, id, bytes);
    return Results.Json(body, statusCode: status);
});

app.MapPost("/api/sessions/{id}/danger", (string id, HttpRequest request, SqliteSessionStore store) =>
{
    var (status, body) = SessionEndpoints.Danger(store, id, request.Query["intent"].FirstOrDefault());
    return Results.Json(body, statusCode: status);
});

app.MapPost("/api/sessions/{id}/cancel", (string id, SqliteSessionStore store) =>
{
    var (status, body) = SessionEndpoints.Cancel(store, id);
    return Results.Json(body, statusCode: status);
});

app.MapGet("/api/sessions/{id}/export.csv", (string id, SqliteSessionStore store) =>
{
    var session = store.Get(id);
    return session is null
        ? Results.Json(new { error = "not_found", message = "会话不存在。" }, statusCode: StatusCodes.Status404NotFound)
        : Results.File(Encoding.UTF8.GetBytes(SessionEndpoints.Csv(session)), "text/csv; charset=utf-8", $"{session.SessionId}.csv");
});

app.MapGet("/api/devices", () => fixtureEnabled
    ? Results.Json(new { devices = new[] { DevicePayload(SimulationDeviceCatalog.Resolve(null)) } })
    : Results.Json(new { error = "fixture_disabled", message = "生产模式不能进入或调用内部测试夹具。", executed = false, fallbackToFixture = false }, statusCode: StatusCodes.Status403Forbidden));

app.MapGet("/api/devices/{id}", (string id) => fixtureEnabled
    ? Results.Json(DevicePayload(SimulationDeviceCatalog.Resolve(id)))
    : Results.Json(new { error = "fixture_disabled", message = "生产模式不能进入或调用内部测试夹具。", executed = false, fallbackToFixture = false }, statusCode: StatusCodes.Status403Forbidden));

app.MapGet("/api/recipes/presets", () => Results.Json(new
{
    schema = RecipeContract.SchemaId,
    presets = RecipePresets.All.Select(preset => new { id = preset.Id, title = preset.Title, json = preset.Json })
}));

app.MapPost("/api/recipes/validate", async (HttpRequest request) =>
{
    var bytes = await ReadBodyAsync(request);
    if (bytes is null)
    {
        return Results.Json(new
        {
            ok = false,
            executed = false,
            errors = new[] { "配置超过 16 KiB，未执行。" }
        }, statusCode: StatusCodes.Status400BadRequest);
    }

    var result = RecipeValidator.Validate(bytes);
    return Results.Json(new
    {
        ok = result.Ok,
        executed = false,
        completeProfessionalScript = false,
        errors = result.Errors,
        canonical = result.Recipe?.CanonicalJson,
        sha256 = result.Recipe?.Sha256,
        seed = result.Recipe?.Seed,
        name = result.Recipe?.Name,
        capabilities = RecipeCapabilities.ForRecipe(result.Recipe)
    }, statusCode: result.Ok ? StatusCodes.Status200OK : StatusCodes.Status400BadRequest);
});

app.MapGet("/api/parameters", (SqliteTraceStore store) => Results.Json(new
{
    policyVersion = ParameterLibrary.PolicyVersion,
    active = ParameterLibrary.Active,
    imports = store.Imports(),
    realDeviceParametersEnabled = ParameterLibrary.Active.Where(entry => entry.Layer == SourceRules.DeviceLayer).All(entry => entry.Enabled) && ParameterLibrary.Active.Any(entry => entry.Layer == SourceRules.DeviceLayer && entry.Enabled)
}));

app.MapPost("/api/analysis/raw-bit-error", (RawBitErrorRequest request) =>
{
    var linked = RawBitError.HasMeasurementEvidence(0, request.EvidenceId);
    var report = RawBitError.Compute(request.Source, request.ComparedBits, request.ErrorBits, linked);
    return Results.Json(report);
});
app.MapPost("/api/policy/evaluate", (PolicyRequest request) => Results.Json(UnapprovedPolicy.Evaluate(request.Domain, request.Name)));
app.MapGet("/api/run-snapshots", (SqliteScriptStore store) => Results.Json(new
{
    hardwareRecovery = false,
    snapshots = store.Snapshots()
}));
app.MapPost("/api/sku-configs", (SkuConfigRequest request, SqliteLotStore store) =>
{
    var (status, body) = LotEndpoints.CreateConfig(store, request);
    return Results.Json(body, statusCode: status);
});
app.MapPost("/api/sku-configs/{id}/lock", (string id, int version, SqliteLotStore store) =>
{
    var (status, body) = LotEndpoints.Lock(store, id, version);
    return Results.Json(body, statusCode: status);
});
app.MapPost("/api/sku-configs/{id}/revise", (string id, SkuConfigRequest request, SqliteLotStore store) =>
{
    var (status, body) = LotEndpoints.Revise(store, id, request);
    return Results.Json(body, statusCode: status);
});
app.MapPost("/api/lots", (OpenLotRequest request, SqliteLotStore store) =>
{
    var (status, body) = LotEndpoints.Open(store, request);
    return Results.Json(body, statusCode: status);
});
app.MapGet("/api/lots/{id}", (string id, SqliteLotStore store) =>
{
    var (status, body) = LotEndpoints.Get(store, id);
    return Results.Json(body, statusCode: status);
});
app.MapPost("/api/lots/{id}/units", (string id, UnitLabelRequest request, SqliteLotStore store) =>
{
    var (status, body) = LotEndpoints.Record(store, id, request);
    return Results.Json(body, statusCode: status);
});
app.MapPost("/api/lots/{id}/units/{label}/retest", (string id, string label, SqliteLotStore store) =>
{
    var (status, body) = LotEndpoints.Retest(store, id, label);
    return Results.Json(body, statusCode: status);
});
app.MapPost("/api/lots/{id}/units/{label}/attempts/{attempt:int}/raw", async (string id, string label, int attempt, HttpRequest request, SqliteLotStore store) =>
{
    int limit;
    try
    {
        limit = store.RawReadLimit(id, label, attempt);
    }
    catch (LotRuleException ex)
    {
        return Results.Json(new { error = "lot_rule", message = ex.Message, executed = false }, statusCode: StatusCodes.Status400BadRequest);
    }

    var data = await ReadDeclaredAsync(request, limit);
    if (data is null)
    {
        return Results.Json(new { error = "lot_rule", message = $"读取前拒绝：必须先给出不超过 {limit} 字节的长度。{LotRules.RawQuotaRole}", executed = false }, statusCode: StatusCodes.Status400BadRequest);
    }

    var (status, body) = LotEndpoints.AttachRaw(store, id, label, attempt, data);
    return Results.Json(body, statusCode: status);
});
app.MapPost("/api/lots/{id}/close", (string id, SqliteLotStore store) =>
{
    var (status, body) = LotEndpoints.Close(store, id);
    return Results.Json(body, statusCode: status);
});
app.MapGet("/api/lots/{id}/export.csv", (string id, string? detail, SqliteLotStore store) =>
{
    try
    {
        return Results.File(Encoding.UTF8.GetBytes(LotEndpoints.Csv(store, id, detail)), "text/csv; charset=utf-8", $"{id}.csv");
    }
    catch (LotRuleException ex)
    {
        return Results.Json(new { error = "lot_rule", message = ex.Message }, statusCode: StatusCodes.Status400BadRequest);
    }
});

app.MapPost("/api/parameters/import", (ParameterImportRequest request, SqliteTraceStore store) =>
{
    var imported = ParameterLibrary.ImportDevice(request.Key, request.Version, request.DocumentId, request.Chapter, request.ProposedValue);
    var stored = store.AddImport(imported);
    var active = ParameterLibrary.Active.SingleOrDefault(entry => entry.Key == stored.Key && entry.Layer == SourceRules.DeviceLayer);
    return Results.Json(new
    {
        import = stored,
        activated = false,
        activeValue = active?.ValueText,
        hardwareAccepted = false
    });
});

app.MapPost("/api/runs", async (HttpRequest request, SqliteTraceStore store) =>
{
    var bytes = await ReadBodyAsync(request);
    if (bytes is null)
    {
        return Results.Json(new { executed = false, hardwareAccepted = false }, statusCode: StatusCodes.Status400BadRequest);
    }

    var result = RecipeValidator.Validate(bytes);
    if (!result.Ok || result.Recipe is null)
    {
        return Results.Json(new { executed = false, hardwareAccepted = false, errors = result.Errors }, statusCode: StatusCodes.Status400BadRequest);
    }

    var attempt = store.AddRefusal(result.Recipe.Name, result.Recipe.Sha256, result.Recipe.CanonicalJson, result.Recipe.Seed);
    return Results.Json(new
    {
        executed = false,
        hardwareAccepted = false,
        measuredErrorBits = (int?)null,
        attempt
    }, statusCode: StatusCodes.Status409Conflict);
});

app.MapGet("/api/runs", (SqliteTraceStore store) => Results.Json(new { runs = store.Attempts(), csvNote = "无实测错误数" }));

app.MapPost("/api/runs/{id}/cancel", (string id, SqliteTraceStore store) =>
{
    var changed = store.CancelBeforeStart(id);
    var attempt = store.GetAttempt(id);
    return attempt is null
        ? Results.Json(new { error = "not_found" }, statusCode: StatusCodes.Status404NotFound)
        : Results.Json(new { changed, attempt, hardwareAccepted = false, measuredErrorBits = (int?)null });
});

app.MapGet("/api/runs/export.csv", (SqliteTraceStore store) =>
    Results.File(Encoding.UTF8.GetBytes(SqliteTraceStore.Csv(store.Attempts())), "text/csv; charset=utf-8", "run-attempts.csv"));

app.MapPost("/api/jobs", async (HttpRequest request, JobService jobs) =>
{
    if (!fixtureEnabled)
    {
        return Results.Json(FixtureDisabled(), statusCode: StatusCodes.Status403Forbidden);
    }

    var bytes = await ReadBodyAsync(request);
    if (bytes is null)
    {
        return Results.Json(new
        {
            error = "recipe_rejected",
            message = "配置超过 16 KiB，未执行。",
            executed = false,
            details = Array.Empty<string>()
        }, statusCode: StatusCodes.Status400BadRequest);
    }

    var deviceId = request.Query["deviceId"].FirstOrDefault();
    var (status, body) = jobs.Create(bytes, deviceId);
    return Results.Json(body, statusCode: status);
});

app.MapGet("/api/jobs", (SqliteJobStore store, int? limit) =>
{
    if (!fixtureEnabled)
    {
        return Results.Json(FixtureDisabled(), statusCode: StatusCodes.Status403Forbidden);
    }

    var take = Math.Clamp(limit ?? 50, 1, 100);
    return Results.Json(new { jobs = store.List(take).Select(JobViews.ToSummary).ToList() });
});

app.MapGet("/api/jobs/{id}", (string id, SqliteJobStore store) =>
{
    if (!fixtureEnabled)
    {
        return Results.Json(FixtureDisabled(), statusCode: StatusCodes.Status403Forbidden);
    }

    var job = store.Get(id);
    return job is null ? Results.NotFound(new { error = "not_found", message = "任务不存在。" }) : Results.Json(JobViews.ToDetail(job));
});

app.MapPost("/api/jobs/{id}/cancel", (string id, JobService jobs) =>
{
    if (!fixtureEnabled)
    {
        return Results.Json(FixtureDisabled(), statusCode: StatusCodes.Status403Forbidden);
    }

    var (status, body) = jobs.Cancel(id);
    return Results.Json(body, statusCode: status);
});

app.MapGet("/api/jobs/{id}/export.csv", (string id, SqliteJobStore store) =>
{
    if (!fixtureEnabled)
    {
        return Results.Json(FixtureDisabled(), statusCode: StatusCodes.Status403Forbidden);
    }

    var job = store.Get(id);
    if (job is null)
    {
        return Results.NotFound(new { error = "not_found", message = "任务不存在。" });
    }

    var csv = CsvExport.Build(job);
    return Results.File(Encoding.UTF8.GetBytes(csv), "text/csv; charset=utf-8", $"{job.JobId}.csv");
});

if (!uiReady)
{
    app.MapGet("/", () => Results.Json(new
    {
        service = "nand-platform",
        product = ProductIdentity.Name,
        health = "/api/health",
        ui = "在 web 目录执行 npm run dev，或先 npm run build 再由本服务托管 web/dist"
    }));
}
else
{
    var files = new PhysicalFileProvider(webRoot);
    app.MapFallbackToFile("index.html", new StaticFileOptions { FileProvider = files });
}

app.Run();

static object DevicePayload(DeviceResolution resolution) => new
{
    deviceId = resolution.ActualDeviceId,
    requestedDeviceId = resolution.RequestedDeviceId,
    kind = SimIdentity.Kind,
    kindText = "模拟替身",
    manufacturer = SimIdentity.Manufacturer,
    model = SimIdentity.Model,
    disclaimer = SimIdentity.Disclaimer,
    substituted = resolution.Substituted,
    substitutionReason = resolution.Reason,
    idHex = SimIdentity.ToHex(SimIdentity.IdBytes),
    idLength = SimIdentity.IdBytes.Length,
    parameterPageLength = SimParameterPage.Length,
    parameterPageMagic = "SIM1",
    status = "SIM_READY",
    throughput = SimIdentity.Throughput,
    voltage = SimIdentity.Voltage,
    temperature = SimIdentity.Temperature,
    handler = "未实现",
    thermalChamber = "未实现",
    features = SimIdentity.Features
};

static object FixtureDisabled() => new
{
    error = "fixture_disabled",
    message = "生产模式不能进入或调用内部测试夹具。",
    executed = false,
    hardwareAccepted = false,
    fallbackToFixture = false
};

static async Task<byte[]?> ReadBodyAsync(HttpRequest request) => await ReadDeclaredAsync(request, RecipeValidator.MaxBytes);

static async Task<byte[]?> ReadDeclaredAsync(HttpRequest request, int maxBytes)
{
    if (request.ContentLength is null or <= 0 || request.ContentLength > maxBytes)
    {
        return null;
    }

    var data = new byte[(int)request.ContentLength.Value];
    await request.Body.ReadExactlyAsync(data);
    return data;
}

static string ResolveDatabasePath(IConfiguration configuration, IHostEnvironment environment)
{
    var databasePath = configuration["DatabasePath"];
    if (string.IsNullOrWhiteSpace(databasePath))
    {
        databasePath = "data/nand-platform.db";
    }

    if (!Path.IsPathRooted(databasePath))
    {
        databasePath = Path.GetFullPath(Path.Combine(environment.ContentRootPath, databasePath));
    }

    return databasePath;
}

public partial class Program;

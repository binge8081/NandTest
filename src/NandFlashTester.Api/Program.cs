using System.Text;
using System.Text.Json;
using Microsoft.Extensions.FileProviders;
using NandFlashTester.Api.Data;
using NandFlashTester.Api.Materials;
using NandFlashTester.Api.Jobs;
using NandFlashTester.Api.Platform;
using NandFlashTester.Core;
using NandFlashTester.Core.Devices;
using NandFlashTester.Core.Execution;
using NandFlashTester.Core.Management;
using NandFlashTester.Core.Materials;
using NandFlashTester.Core.Platform;
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
    schema = RecipeContract.SchemaId
}));

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
    return Results.Json(MaterialEndpoints.Coverage(request), statusCode: temperature.Accepted ? StatusCodes.Status200OK : StatusCodes.Status400BadRequest);
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
        return Results.Json(new { error = "recipe_rejected", message = "配方超过 16 KiB，未发送。", executed = false, hardwareAccepted = false }, statusCode: StatusCodes.Status400BadRequest);
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

app.MapGet("/api/devices", () =>
{
    var resolution = SimulationDeviceCatalog.Resolve(null);
    return Results.Json(new { devices = new[] { DevicePayload(resolution) } });
});

app.MapGet("/api/devices/{id}", (string id) => Results.Json(DevicePayload(SimulationDeviceCatalog.Resolve(id))));

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
            errors = new[] { "配方超过 16 KiB，未执行。" }
        }, statusCode: StatusCodes.Status400BadRequest);
    }

    var result = RecipeValidator.Validate(bytes);
    return Results.Json(new
    {
        ok = result.Ok,
        executed = false,
        errors = result.Errors,
        canonical = result.Recipe?.CanonicalJson,
        sha256 = result.Recipe?.Sha256,
        seed = result.Recipe?.Seed,
        name = result.Recipe?.Name
    }, statusCode: result.Ok ? StatusCodes.Status200OK : StatusCodes.Status400BadRequest);
});

app.MapPost("/api/jobs", async (HttpRequest request, JobService jobs) =>
{
    if (!fixtureEnabled)
    {
        return Results.Json(new
        {
            error = "fixture_disabled",
            message = "擦写读夹具只在自动化测试里启用。生产页面不把它当作实机。",
            executed = false,
            hardwareAccepted = false
        }, statusCode: StatusCodes.Status403Forbidden);
    }

    var bytes = await ReadBodyAsync(request);
    if (bytes is null)
    {
        return Results.Json(new
        {
            error = "recipe_rejected",
            message = "配方超过 16 KiB，未执行。",
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
    var take = Math.Clamp(limit ?? 50, 1, 100);
    return Results.Json(new { jobs = store.List(take).Select(JobViews.ToSummary).ToList() });
});

app.MapGet("/api/jobs/{id}", (string id, SqliteJobStore store) =>
{
    var job = store.Get(id);
    return job is null ? Results.NotFound(new { error = "not_found", message = "任务不存在。" }) : Results.Json(JobViews.ToDetail(job));
});

app.MapPost("/api/jobs/{id}/cancel", (string id, JobService jobs) =>
{
    var (status, body) = jobs.Cancel(id);
    return Results.Json(body, statusCode: status);
});

app.MapGet("/api/jobs/{id}/export.csv", (string id, SqliteJobStore store) =>
{
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

static async Task<byte[]?> ReadBodyAsync(HttpRequest request)
{
    using var buffer = new MemoryStream();
    var chunk = new byte[4096];
    while (true)
    {
        var read = await request.Body.ReadAsync(chunk);
        if (read == 0)
        {
            break;
        }

        if (buffer.Length + read > RecipeValidator.MaxBytes)
        {
            return null;
        }

        buffer.Write(chunk, 0, read);
    }

    return buffer.ToArray();
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

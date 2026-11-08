using System.Text;
using System.Text.Json;
using Microsoft.Extensions.FileProviders;
using NandFlashTester.Api.Data;
using NandFlashTester.Api.Jobs;
using NandFlashTester.Core.Devices;
using NandFlashTester.Core.Execution;
using NandFlashTester.Core.Recipes;
using NandFlashTester.Core.Scope;

var builder = WebApplication.CreateBuilder(args);

var webRoot = builder.Configuration["WebRoot"];
if (string.IsNullOrWhiteSpace(webRoot))
{
    webRoot = Path.GetFullPath(Path.Combine(builder.Environment.ContentRootPath, "..", "..", "web", "dist"));
}

builder.Services.AddSingleton(sp =>
{
    var config = sp.GetRequiredService<IConfiguration>();
    var environment = sp.GetRequiredService<IHostEnvironment>();
    var databasePath = config["DatabasePath"];
    if (string.IsNullOrWhiteSpace(databasePath))
    {
        databasePath = "data/nand-sim.db";
    }

    if (!Path.IsPathRooted(databasePath))
    {
        databasePath = Path.GetFullPath(Path.Combine(environment.ContentRootPath, databasePath));
    }

    return new SqliteJobStore(databasePath);
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
app.UseCors("ui");

var uiReady = File.Exists(Path.Combine(webRoot, "index.html"));
if (uiReady)
{
    var files = new PhysicalFileProvider(webRoot);
    app.UseDefaultFiles(new DefaultFilesOptions { FileProvider = files });
    app.UseStaticFiles(new StaticFileOptions { FileProvider = files });
}

app.MapGet("/api/health", () => Results.Json(new
{
    status = "ok",
    service = "nand-sim-closed-loop",
    framework = System.Runtime.InteropServices.RuntimeInformation.FrameworkDescription,
    simulationOnly = true,
    schema = RecipeContract.SchemaId
}));

app.MapGet("/api/meta", () => Results.Json(new
{
    simulationOnly = PhaseScope.SimulationOnly,
    claimsMeasuredThroughput = PhaseScope.ClaimsMeasuredThroughput,
    localDeliveryTarget = PhaseScope.LocalDeliveryTarget,
    localDeliveryPathWritten = PhaseScope.LocalDeliveryPathWritten,
    jsonEntryIsSslCompatible = PhaseScope.JsonEntryIsSslCompatible,
    reimplementationPolicy = PhaseScope.ReimplementationPolicy,
    expertEntryLocation = PhaseScope.ExpertEntryLocation,
    implementedSubsetCount = PhaseScope.ImplementedSubsetCount,
    itemCount = PhaseScope.Items.Count,
    items = PhaseScope.Items
}));

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
        service = "nand-sim-closed-loop",
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

public partial class Program;

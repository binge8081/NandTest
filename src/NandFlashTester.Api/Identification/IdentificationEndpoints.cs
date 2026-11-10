using NandFlashTester.Core.Identification;

namespace NandFlashTester.Api.Identification;

public sealed class IdentificationSession
{
    private readonly object _gate = new();
    private readonly string? _skuDirectory;
    private readonly string? _dutMapPath;
    private IdentificationReport? _report;
    private readonly Dictionary<string, ChipField> _chip = [];

    public IdentificationSession(IConfiguration configuration)
    {
        _skuDirectory = configuration["Identification:SkuDirectory"];
        _dutMapPath = configuration["Identification:DutMap"];
    }

    public object Overview()
    {
        var catalog = SkuCardLoader.LoadDirectory(_skuDirectory);
        var map = DutMapLoader.Load(_dutMapPath);
        lock (_gate)
        {
            return new
            {
                connected = false,
                probed = false,
                hardwareExecuted = false,
                wireBytesEmitted = 0,
                connectionReason = "未连接。平台通信端点尚未提供，软件不会猜测地址，也不会发送探测。",
                skuDirectory = catalog.Directory,
                skuErrors = catalog.Errors,
                cards = catalog.Cards.Select(Summary).ToList(),
                dutMap = new { map.Path, map.Source, map.Errors, targets = map.Targets.Select(target => new { target.Id, target.Label, target.Lane, target.Channel, target.Ce, target.Lun }) },
                wireGaps = WireProtocolGaps.All,
                report = _report,
                chip = ChipBody()
            };
        }
    }

    public (int Status, object Body) Decode(DecodeRequest? request)
    {
        var captures = request?.Captures ?? [];
        if (captures.Count is < 1 or > 64)
        {
            return (StatusCodes.Status400BadRequest, new { message = "需要 1 到 64 笔原始字节。" });
        }

        var parsed = new List<RawCapture>();
        foreach (var capture in captures)
        {
            if (!TryBytes(capture.Id00, "00h", out var id00, out var error)
                || !TryBytes(capture.Id20, "20h", out var id20, out error)
                || !TryBytes(capture.Id40, "40h", out var id40, out error)
                || !TryBytes(capture.UniqueId, "Unique ID", out var uid, out error)
                || !TryBytes(capture.ParameterPage, "参数页", out var page, out error))
            {
                return (StatusCodes.Status400BadRequest, new { message = error });
            }

            parsed.Add(new RawCapture(string.IsNullOrWhiteSpace(capture.Label) ? "导入" : capture.Label.Trim(), id00, id20, id40, uid, page));
        }

        var catalog = SkuCardLoader.LoadDirectory(_skuDirectory);
        var report = IdentificationEngine.Decode(catalog.Cards, parsed);
        lock (_gate)
        {
            _report = report;
        }

        return (StatusCodes.Status200OK, new { hardwareExecuted = false, skuErrors = catalog.Errors, report });
    }

    public object Run(RunRequest? request)
    {
        var catalog = SkuCardLoader.LoadDirectory(_skuDirectory);
        var map = DutMapLoader.Load(_dutMapPath);
        var targets = Select(map.Targets, request?.Targets);
        SkuCard? card = null;
        if (!string.IsNullOrWhiteSpace(request?.CardId))
        {
            card = catalog.Cards.FirstOrDefault(item => item.CardId == request.CardId);
            if (card is null)
            {
                var missing = IdentificationPlanBuilder.Build(null, targets);
                return IdentificationTransport.Execute(missing, connected: false) with
                {
                    Message = $"未连接。参数卡 {request.CardId} 不在已载入目录中。没有发送。"
                };
            }
        }
        else if (catalog.Cards.Count == 1)
        {
            card = catalog.Cards[0];
        }

        var plan = IdentificationPlanBuilder.Build(card, targets);
        var result = IdentificationTransport.Execute(plan, connected: false);
        if (map.Targets.Count == 0)
        {
            return result with { Message = result.Message + " DUT_MAP 也没有载入，不能列出 LUN。" };
        }

        if (catalog.Cards.Count > 1 && string.IsNullOrWhiteSpace(request?.CardId))
        {
            return result with { Message = result.Message + " 载入了多张参数卡，时序不能合并，请指定一张。" };
        }

        return result;
    }

    public object Samples() => new
    {
        hardwareExecuted = false,
        note = "这些字节是仓库里的合成示例，不是器件读出。",
        identified = new
        {
            label = "合成 ONFI",
            id00 = HexText.Format(SyntheticSamples.Id00),
            id20 = HexText.Format(SyntheticSamples.Id20),
            id40 = HexText.Format(SyntheticSamples.Id40),
            uniqueId = HexText.Format(SyntheticSamples.UniqueId()),
            parameterPage = HexText.Format(SyntheticSamples.ParameterPage())
        },
        partial = new { label = "合成缺口", id00 = "54", id20 = "", id40 = "", uniqueId = "", parameterPage = "" },
        unrecognized = new { label = "未识别", id00 = "11 00", id20 = "", id40 = "", uniqueId = "", parameterPage = "" }
    };

    public (int Status, object Body) Confirm()
    {
        lock (_gate)
        {
            if (_report is null)
            {
                return (StatusCodes.Status400BadRequest, new { message = "还没有解码结果，不能写入芯片定义。" });
            }

            _chip.Clear();
            foreach (var field in _report.Fields.Where(item => !item.Missing && !string.IsNullOrWhiteSpace(item.Value)))
            {
                _chip[field.Key] = new ChipField(field.Key, field.Label, field.Value, field.Source, false);
            }

            return (StatusCodes.Status200OK, new { confirmed = true, chip = ChipBody() });
        }
    }

    public (int Status, object Body) Override(OverrideRequest? request)
    {
        if (request?.Key is null || IdentificationSession.Labels.All(item => item.Key != request.Key))
        {
            return (StatusCodes.Status400BadRequest, new { message = "没有这个字段。" });
        }

        var label = Labels.Single(item => item.Key == request.Key).Label;
        lock (_gate)
        {
            if (string.IsNullOrWhiteSpace(request.Value))
            {
                _chip[request.Key] = new ChipField(request.Key, label, null, "手工清空", true);
            }
            else
            {
                _chip[request.Key] = new ChipField(request.Key, label, request.Value.Trim(), "手工填写", true);
            }

            return (StatusCodes.Status200OK, new { chip = ChipBody() });
        }
    }

    public static readonly (string Key, string Label)[] Labels =
    [
        ("vendor", "厂商"), ("model", "型号"), ("partNumber", "料号"), ("datasheetRevision", "资料版本"),
        ("interface", "接口"), ("timingModes", "时序模式"), ("pageDataBytes", "页大小"), ("spareBytes", "spare"),
        ("pagesPerBlock", "每块页数"), ("blocksPerLun", "每 LUN 块数"), ("lunsPerCe", "每 CE 的 LUN"),
        ("bitsPerCell", "每单元位数"), ("ecc", "ECC 需求"), ("voltage", "电压"), ("temperature", "温度规格"),
        ("uniqueId", "Unique ID")
    ];

    private object ChipBody() => new
    {
        fields = Labels.Select(item =>
        {
            _chip.TryGetValue(item.Key, out var field);
            return new
            {
                key = item.Key,
                label = item.Label,
                value = field?.Value,
                source = field?.Source,
                manual = field?.Manual ?? false,
                missing = string.IsNullOrWhiteSpace(field?.Value)
            };
        })
    };

    private static object Summary(SkuCard card) => new
    {
        card.CardId,
        card.Vendor,
        vendorLegalNameKnown = card.VendorLegalNameKnown,
        revision = card.DatasheetRevision,
        blocksAutoMatch = card.Conflicts.Any(item => item.BlocksAutoMatch),
        pending = card.Pending,
        conflicts = card.Conflicts.Select(item => new { item.Topic, item.Detail, item.BlocksAutoMatch })
    };

    private static List<DutTarget> Select(IReadOnlyList<DutTarget> map, IReadOnlyList<string>? requested)
    {
        if (requested is null || requested.Count == 0)
        {
            return [];
        }

        if (requested.Any(item => item.Equals("all", StringComparison.OrdinalIgnoreCase)))
        {
            return map.ToList();
        }

        return map.Where(target => requested.Contains(target.Id, StringComparer.Ordinal)).ToList();
    }

    private static bool TryBytes(string? text, string label, out byte[]? bytes, out string? error)
    {
        bytes = null;
        if (!HexText.TryParse(text, out var parsed, out error))
        {
            error = $"{label}：{error}";
            return false;
        }

        if (parsed.Length > 1024 * 1024)
        {
            error = $"{label} 超过 1 MiB，已拒绝。";
            return false;
        }

        bytes = parsed.Length == 0 ? null : parsed;
        return true;
    }
}

public sealed record ChipField(string Key, string Label, string? Value, string? Source, bool Manual);

public sealed class DecodeRequest
{
    public List<CaptureInput>? Captures { get; init; }
}

public sealed class CaptureInput
{
    public string? Label { get; init; }
    public string? Id00 { get; init; }
    public string? Id20 { get; init; }
    public string? Id40 { get; init; }
    public string? UniqueId { get; init; }
    public string? ParameterPage { get; init; }
}

public sealed class RunRequest
{
    public string? CardId { get; init; }
    public List<string>? Targets { get; init; }
}

public sealed class OverrideRequest
{
    public string? Key { get; init; }
    public string? Value { get; init; }
}

public static class IdentificationRoutes
{
    public static void Map(WebApplication app)
    {
        app.MapGet("/api/identification", (IdentificationSession session) => Results.Json(session.Overview()));
        app.MapGet("/api/identification/samples", (IdentificationSession session) => Results.Json(session.Samples()));
        app.MapPost("/api/identification/decode", (DecodeRequest request, IdentificationSession session) =>
        {
            var (status, body) = session.Decode(request);
            return Results.Json(body, statusCode: status);
        });
        app.MapPost("/api/identification/run", (RunRequest request, IdentificationSession session) => Results.Json(session.Run(request)));
        app.MapPost("/api/identification/chip/confirm", (IdentificationSession session) =>
        {
            var (status, body) = session.Confirm();
            return Results.Json(body, statusCode: status);
        });
        app.MapPost("/api/identification/chip/override", (OverrideRequest request, IdentificationSession session) =>
        {
            var (status, body) = session.Override(request);
            return Results.Json(body, statusCode: status);
        });
        app.MapGet("/api/identification/sku-card.schema.json", (IHostEnvironment environment) =>
        {
            var path = Path.Combine(environment.ContentRootPath, "schemas", "sku-card.schema.json");
            return File.Exists(path)
                ? Results.File(path, "application/schema+json")
                : Results.Json(new { message = "参数卡 schema 不在本目录。" }, statusCode: StatusCodes.Status404NotFound);
        });
    }
}

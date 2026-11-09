using System.Globalization;
using System.Text;
using NandFlashTester.Api.Data;
using NandFlashTester.Core.Lots;

namespace NandFlashTester.Api.Lots;

public sealed record SkuConfigRequest(string? Name, int RetestLimit, string? TemperatureCandidate, bool RawEnabled, int RawQuotaBytes);

public sealed record OpenLotRequest(string? ConfigId, int Version, int IndustrialPlanned, int TotalPlanned);

public sealed record UnitLabelRequest(string? Label);

public static class LotEndpoints
{
    public static (int Status, object Body) CreateConfig(SqliteLotStore store, SkuConfigRequest request)
    {
        try
        {
            return (StatusCodes.Status201Created, View(store.CreateConfig(request.Name ?? "", request.RetestLimit, request.TemperatureCandidate, request.RawEnabled, request.RawQuotaBytes)));
        }
        catch (LotRuleException ex)
        {
            return Reject(ex);
        }
    }

    public static (int Status, object Body) Lock(SqliteLotStore store, string configId, int version)
    {
        try
        {
            return (StatusCodes.Status200OK, View(store.Lock(configId, version)));
        }
        catch (LotRuleException ex)
        {
            return Reject(ex);
        }
    }

    public static (int Status, object Body) Revise(SqliteLotStore store, string configId, SkuConfigRequest request)
    {
        try
        {
            return (StatusCodes.Status201Created, View(store.Revise(configId, request.Name ?? "", request.RetestLimit, request.TemperatureCandidate, request.RawEnabled, request.RawQuotaBytes)));
        }
        catch (LotRuleException ex)
        {
            return Reject(ex);
        }
    }

    public static (int Status, object Body) Open(SqliteLotStore store, OpenLotRequest request)
    {
        try
        {
            var lot = store.OpenLot(request.ConfigId ?? "", request.Version, request.IndustrialPlanned, request.TotalPlanned);
            return (StatusCodes.Status201Created, Detail(store, lot));
        }
        catch (LotRuleException ex)
        {
            return Reject(ex);
        }
    }

    public static (int Status, object Body) Get(SqliteLotStore store, string lotId)
    {
        var lot = store.Lot(lotId);
        return lot is null
            ? (StatusCodes.Status404NotFound, new { error = "not_found", message = "批次不存在。" })
            : (StatusCodes.Status200OK, Detail(store, lot));
    }

    public static (int Status, object Body) Record(SqliteLotStore store, string lotId, UnitLabelRequest request)
    {
        try
        {
            store.Record(lotId, request.Label ?? "");
            return Get(store, lotId);
        }
        catch (LotRuleException ex)
        {
            return Reject(ex);
        }
    }

    public static (int Status, object Body) Retest(SqliteLotStore store, string lotId, string label)
    {
        try
        {
            store.Retest(lotId, label);
            return Get(store, lotId);
        }
        catch (LotRuleException ex)
        {
            return Reject(ex);
        }
    }

    public static (int Status, object Body) Close(SqliteLotStore store, string lotId)
    {
        try
        {
            store.Close(lotId);
            return Get(store, lotId);
        }
        catch (LotRuleException ex)
        {
            return Reject(ex);
        }
    }

    public static (int Status, object Body) AttachRaw(SqliteLotStore store, string lotId, string label, int attempt, byte[] data)
    {
        try
        {
            store.AttachRaw(lotId, label, attempt, data);
            return Get(store, lotId);
        }
        catch (LotRuleException ex)
        {
            return Reject(ex);
        }
    }

    public static string Csv(SqliteLotStore store, string lotId, string? detail)
    {
        var lot = store.Lot(lotId) ?? throw new LotRuleException("批次不存在。");
        var config = store.Configs(lot.ConfigId).Single(item => item.Version == lot.ConfigVersion);
        var attempts = store.Attempts(lotId);
        var mode = string.IsNullOrWhiteSpace(detail) ? "attempts" : detail.Trim();
        if (mode is not ("attempts" or "summary"))
        {
            throw new LotRuleException("报告明细只能是 attempts 或 summary。");
        }

        var builder = new StringBuilder();
        builder.Append('\uFEFF');
        builder.AppendLine("产品,批次,配置版本,配置哈希,复测上限,raw已打开,工业计划,总计划,片号,次数,父记录,换座,实测,发送字节,raw摘要,未覆盖来源,说明");
        if (mode == "summary")
        {
            builder.AppendLine(string.Join(',', Cells(
                Product(),
                lot.LotId,
                lot.ConfigVersion.ToString(CultureInfo.InvariantCulture),
                lot.ConfigSha256,
                config.RetestLimit.ToString(CultureInfo.InvariantCulture),
                config.RawEnabled ? "是" : "否",
                lot.IndustrialPlanned.ToString(CultureInfo.InvariantCulture),
                lot.TotalPlanned.ToString(CultureInfo.InvariantCulture),
                "",
                attempts.Count(item => item.Status == "recorded").ToString(CultureInfo.InvariantCulture),
                "",
                "",
                "否",
                "0",
                "",
                "",
                "批次汇总。合成计划数量不是器件规格或实测。未分档。")));
            return builder.ToString();
        }

        foreach (var attempt in attempts)
        {
            var uncovered = attempt.Status == "recorded"
                ? string.Join(' ', LotRules.DeriveUncovered(config.TemperatureCandidate, attempt.RawSha256 is not null).Select(item => $"{item.Code}:{item.SourceField}"))
                : "";
            builder.AppendLine(string.Join(',', Cells(
                Product(),
                lot.LotId,
                lot.ConfigVersion.ToString(CultureInfo.InvariantCulture),
                lot.ConfigSha256,
                config.RetestLimit.ToString(CultureInfo.InvariantCulture),
                config.RawEnabled ? "是" : "否",
                lot.IndustrialPlanned.ToString(CultureInfo.InvariantCulture),
                lot.TotalPlanned.ToString(CultureInfo.InvariantCulture),
                attempt.Label,
                attempt.AttemptIndex.ToString(CultureInfo.InvariantCulture),
                attempt.ParentAttemptId ?? "",
                attempt.SocketChangeRequired ? "是" : "否",
                attempt.Measured ? "是" : "否",
                attempt.BytesEmitted.ToString(CultureInfo.InvariantCulture),
                attempt.RawSha256 ?? "",
                uncovered,
                attempt.Status == "recorded"
                    ? "主机批次记录。不是器件规格或实测。未分档。"
                    : "这次记录没有提交。不计入复测次数。")));
        }

        return builder.ToString();
    }

    private static object Detail(SqliteLotStore store, StoredLot lot)
    {
        var config = store.Configs(lot.ConfigId).Single(item => item.Version == lot.ConfigVersion);
        var attempts = store.Attempts(lot.LotId).Select(attempt => new
        {
            attempt.AttemptId,
            attempt.Label,
            attempt.AttemptIndex,
            attempt.ParentAttemptId,
            attempt.Status,
            attempt.SocketChangeRequired,
            attempt.Measured,
            attempt.BytesEmitted,
            executedRateMtPerS = (int?)null,
            eccDefinition = (string?)null,
            chamberSamples = (int?)null,
            attempt.RawSha256,
            attempt.RawLength,
            grade = (string?)null,
            gradesAssigned = false,
            uncovered = attempt.Status == "recorded"
                ? LotRules.DeriveUncovered(config.TemperatureCandidate, attempt.RawSha256 is not null)
                : Array.Empty<UncoveredItem>()
        }).ToArray();
        return new
        {
            lot.LotId,
            lot.Status,
            lot.ConfigId,
            lot.ConfigVersion,
            lot.ConfigSha256,
            lot.IndustrialPlanned,
            lot.TotalPlanned,
            devicePermissionGranted = false,
            measured = false,
            gradesAssigned = false,
            config = View(config),
            attempts
        };
    }

    private static object View(StoredSkuConfig config) => new
    {
        config.ConfigId,
        config.Version,
        config.Name,
        config.RetestLimit,
        config.TemperatureCandidate,
        config.RawEnabled,
        config.RawQuotaBytes,
        config.Sha256,
        config.Status,
        devicePermissionGranted = false,
        gradesAssigned = false,
        rawQuotaRole = LotRules.RawQuotaRole
    };

    private static (int Status, object Body) Reject(LotRuleException ex) =>
        (StatusCodes.Status400BadRequest, new { error = "lot_rule", message = ex.Message, executed = false, gradesAssigned = false });

    private static string Product() => NandFlashTester.Core.ProductIdentity.Name;

    private static IEnumerable<string> Cells(params string[] values) => values.Select(Cell);

    private static string Cell(string value)
    {
        if (value.Contains('"') || value.Contains(',') || value.Contains('\n') || value.Contains('\r'))
        {
            return "\"" + value.Replace("\"", "\"\"", StringComparison.Ordinal) + "\"";
        }

        return value;
    }
}

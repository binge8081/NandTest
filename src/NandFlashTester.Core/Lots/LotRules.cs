using System.Globalization;
using System.Security.Cryptography;
using System.Text;

namespace NandFlashTester.Core.Lots;

public sealed class LotRuleException(string message) : Exception(message);

public sealed record UncoveredItem(string Code, string SourceField, string Reason);

public static class LotRules
{
    public const int RawHostMaxBytes = 64 * 1024;
    public const int PlanCountMax = 100_000;
    public const string RawQuotaRole = "主机 raw 限额，不是未批准的约 2TB/批保留建议。";
    public static readonly string[] TemperatureCandidates = ["commercial-0-70", "commercial-0-85", "industrial"];

    public static string Canonical(string name, int retestLimit, string temperatureCandidate, bool rawEnabled, int rawQuotaBytes) =>
        string.Join('|',
            name,
            retestLimit.ToString(CultureInfo.InvariantCulture),
            temperatureCandidate,
            rawEnabled ? "1" : "0",
            rawQuotaBytes.ToString(CultureInfo.InvariantCulture));

    public static string Hash(string canonical) => Convert.ToHexString(SHA256.HashData(Encoding.UTF8.GetBytes(canonical)));

    public static void ValidateName(string? name)
    {
        if (name is null || !System.Text.RegularExpressions.Regex.IsMatch(name, @"\A[\p{L}\p{N}_.-]{1,64}\z"))
        {
            throw new LotRuleException("名称只能包含字母、数字、点、下划线和短横线，长度 1 到 64。");
        }
    }

    public static (string Candidate, bool RawEnabled, int RawQuotaBytes) ValidateConfig(string? name, int retestLimit, string? temperatureCandidate, bool rawEnabled, int rawQuotaBytes)
    {
        ValidateName(name);
        if (retestLimit is not (1 or 2))
        {
            throw new LotRuleException("复测上限只能是 1 或 2。没有批准的五级阈值，这里不产生分档。");
        }

        var candidate = temperatureCandidate?.Trim() ?? "";
        if (candidate.Length > 0 && !TemperatureCandidates.Contains(candidate, StringComparer.Ordinal))
        {
            throw new LotRuleException("温度档只能留空，或选择 commercial-0-70、commercial-0-85、industrial。选择候选档不授予器件许可。");
        }

        if (!rawEnabled)
        {
            return (candidate, false, 0);
        }

        if (rawQuotaBytes is < 1 or > RawHostMaxBytes)
        {
            throw new LotRuleException($"打开 raw 时，主机限额必须在 1 到 {RawHostMaxBytes} 字节。{RawQuotaRole}");
        }

        return (candidate, true, rawQuotaBytes);
    }

    public static void ValidateShare(int industrialPlanned, int totalPlanned)
    {
        if (totalPlanned is < 1 or > PlanCountMax || industrialPlanned is < 0 or > PlanCountMax)
        {
            throw new LotRuleException($"计划片数必须在 0 到 {PlanCountMax}，总片数至少为 1。这是主机计划数量，不是实测。");
        }

        if (industrialPlanned > totalPlanned || industrialPlanned * 10L > totalPlanned)
        {
            throw new LotRuleException("工业计划比例超过 10%。这只校验计划数量，不启动温箱，也不授予器件许可。");
        }
    }

    public static void ValidateRaw(bool rawEnabled, int rawQuotaBytes, int length)
    {
        if (!rawEnabled)
        {
            throw new LotRuleException("raw 默认关闭。本批次配置没有打开主机 raw，本次不保存。");
        }

        if (length < 1 || length > rawQuotaBytes)
        {
            throw new LotRuleException($"raw 长度必须在 1 到本批次主机限额 {rawQuotaBytes} 字节。{RawQuotaRole}");
        }
    }

    public static IReadOnlyList<UncoveredItem> DeriveUncovered(string temperatureCandidate, bool rawStored)
    {
        var items = new List<UncoveredItem>
        {
            new("executed_rate", "executed_rate_mt_per_s", "实际执行速率字段为空，不能用设置值填入。"),
            new("ecc_uber", "ecc_definition", "没有 ECC 定义，UBER 未统计。")
        };
        if (temperatureCandidate.Length > 0)
        {
            items.Add(new("temperature_coverage", "chamber_samples", "温度档只是候选，温箱读数字段为空。"));
        }

        if (rawStored)
        {
            items.Add(new("raw_source", "raw_sha256", "主机保存了 raw 摘要，但没有实机来源，不能当实测。"));
        }

        return items;
    }
}

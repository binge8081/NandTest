namespace NandFlashTester.Core.Materials;

public enum SourceStatus
{
    NotProvided,
    CitedUnverified,
    Approved
}

public static class SourceRules
{
    public const string DeviceLayer = "device-datasheet";
    public const string PlatformIcdLayer = "platform-icd";
    public const string PlatformPolicyLayer = "platform-policy";

    public static SourceStatus FromCitation(string? documentId, string? chapter)
    {
        if (string.IsNullOrWhiteSpace(documentId) || string.IsNullOrWhiteSpace(chapter))
        {
            return SourceStatus.NotProvided;
        }

        return SourceStatus.CitedUnverified;
    }
}

public sealed record TracedField(
    string Name,
    string Layer,
    string? ValueText,
    string? DocumentId,
    string? Chapter,
    SourceStatus SourceStatus,
    bool Enabled,
    string Reason);

public sealed record MaterialClaim(
    string? PartNumber,
    string? Sku,
    string? DocumentId,
    string? Revision,
    string? Chapter,
    int? RatedMtPerS,
    int? VoltageMillivolts,
    int? PageSizeBytes);

public sealed record RateReport(
    int? RatedMtPerS,
    int? CharacterizationMtPerS,
    int? RequestedMtPerS,
    int MassProductionCapMtPerS,
    int? ActualCoveredMtPerS,
    IReadOnlyList<string> Uncovered);

public sealed record TemperatureReport(
    string SkuClass,
    string Layer,
    IReadOnlyList<int> RatedPoints,
    IReadOnlyList<int> RequestedPoints,
    IReadOnlyList<int> ActualCoveredPoints,
    IReadOnlyList<string> Uncovered,
    bool Accepted,
    string Reason);

public sealed record DeviceProfile(
    bool RealConfigEnabled,
    string? PartNumber,
    string? Sku,
    string? DatasheetRevision,
    string Reason,
    IReadOnlyList<TracedField> Fields,
    RateReport Rate)
{
    public TracedField Field(string name) => Fields.Single(field => field.Name == name);
}

public static class DeviceMaterialModel
{
    public const int WdMassProductionCapMtPerS = 2400;

    public static readonly string[] FieldNames =
    [
        "command-set",
        "address-organization",
        "timing",
        "voltage",
        "temperature-limits",
        "status-bits",
        "pass-fail-criteria"
    ];

    public static DeviceProfile Unconfigured() =>
        Build(null, null, null, null, null, SourceStatus.NotProvided, "没有已核实的器件 datasheet。真实规格未启用。");

    public static DeviceProfile Bind(MaterialClaim claim)
    {
        var status = SourceRules.FromCitation(claim.DocumentId, claim.Chapter);
        var reason = status == SourceStatus.CitedUnverified
            ? "只收到来源文字，资料内容未核对。真实规格仍然禁用。"
            : "器件 datasheet 未提供。真实规格未启用。";
        return Build(null, null, null, claim.DocumentId, claim.Chapter, status, reason);
    }

    public static RateReport Rate(int? requestedMtPerS)
    {
        var uncovered = new List<string>
        {
            "器件额定速率没有已核实 datasheet，不能用平台上限或请求值代替。",
            "表征速率未提供，不填写。",
            "实际覆盖尚未在新硬件上测量。"
        };
        if (requestedMtPerS is > WdMassProductionCapMtPerS)
        {
            uncovered.Insert(0, $"请求 {requestedMtPerS} MT/s 高于 WD 量产验收上限 {WdMassProductionCapMtPerS} MT/s，不计入量产覆盖。");
        }

        return new RateReport(
            null,
            null,
            requestedMtPerS,
            WdMassProductionCapMtPerS,
            null,
            uncovered);
    }

    public static TemperatureReport Temperature(string? skuClass, IReadOnlyList<int>? requested)
    {
        var points = requested ?? [];
        if (!Templates.TryGetValue(skuClass ?? "", out var rated))
        {
            return new TemperatureReport(
                skuClass ?? "",
                SourceRules.PlatformPolicyLayer,
                [],
                points,
                [],
                ["SKU 温度档未选择，或不是平台已定的 0–70、0–85、工业三档。器件 datasheet 温度原表未提供。"],
                false,
                "温度请求未接受。");
        }

        var outside = points.Where(point => !rated.Contains(point)).Distinct().ToArray();
        if (outside.Length > 0)
        {
            return new TemperatureReport(
                skuClass!,
                SourceRules.PlatformPolicyLayer,
                rated,
                points,
                [],
                outside.Select(point => $"{point} ℃ 超出该 SKU 平台温度档。").ToArray(),
                false,
                "超界温度已拒绝，没有启动测试。");
        }

        return new TemperatureReport(
            skuClass!,
            SourceRules.PlatformPolicyLayer,
            rated,
            points,
            [],
            rated.Select(point => $"{point} ℃ 尚无实际覆盖。").ToArray(),
            true,
            "请求落在平台温度档内。额定档是平台规则，不是某一料号的 datasheet 原表；实际覆盖仍为空。");
    }

    private static DeviceProfile Build(
        string? partNumber,
        string? sku,
        string? revision,
        string? documentId,
        string? chapter,
        SourceStatus status,
        string reason)
    {
        var fields = FieldNames.Select(name => new TracedField(
            name,
            SourceRules.DeviceLayer,
            null,
            documentId,
            chapter,
            status,
            false,
            reason)).ToArray();
        return new DeviceProfile(false, partNumber, sku, revision, reason, fields, Rate(null));
    }

    private static readonly Dictionary<string, int[]> Templates = new(StringComparer.Ordinal)
    {
        ["commercial-0-70"] = [0, 25, 70],
        ["commercial-0-85"] = [0, 25, 85],
        ["industrial"] = [-40, 25, 85]
    };
}

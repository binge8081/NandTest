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
    string? Vendor,
    int? RatedMtPerS,
    int? CharacterizationMtPerS,
    int? RequestedMtPerS,
    int? MassProductionCapMtPerS,
    bool CapApplies,
    int? ActualCoveredMtPerS,
    IReadOnlyList<string> Uncovered);

public sealed record TemperatureReport(
    string SkuClass,
    string Layer,
    IReadOnlyList<int> RatedPoints,
    IReadOnlyList<int> RequestedPoints,
    IReadOnlyList<int> ActualCoveredPoints,
    IReadOnlyList<string> Uncovered,
    bool WithinCandidate,
    bool DevicePermissionGranted,
    string Reason);

public sealed record RealDeviceDecision(bool Allowed, bool SkuSpecVerified, string Reason);

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

public sealed record GeometryCheck(bool RealDeviceEnabled, bool DevicePageKnown, bool MatchesVerifiedPage, string Reason);

public static class DeviceGeometry
{
    public static GeometryCheck Check(int? verifiedPageBytes, int declaredPageBytes)
    {
        if (verifiedPageBytes is null)
        {
            return new GeometryCheck(false, false, false, "器件页长未经 datasheet 核实，声明值不能当作物理规格，也不启用实机。");
        }

        return verifiedPageBytes == declaredPageBytes
            ? new GeometryCheck(false, true, true, "声明页长与已核实值一致，仍不因该项启用实机。")
            : new GeometryCheck(false, true, false, "声明页长与已核实器件页长不一致，不能用于实机。");
    }
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

    public static RateReport Rate(string? vendor, int? requestedMtPerS, bool partVerifiedAsWd)
    {
        var uncovered = new List<string>
        {
            "器件额定速率没有已核实 datasheet，不能用上限或请求值代替。",
            "表征速率未提供，不填写。",
            "实际覆盖尚未在新硬件上测量。"
        };
        var wd = partVerifiedAsWd && string.Equals(vendor?.Trim(), "WD", StringComparison.OrdinalIgnoreCase);
        if (!partVerifiedAsWd)
        {
            uncovered.Add("料号未经核实。2400 MT/s 只属于已核实的 WD 量产约束，不作为所有厂商的全局上限，请求中的厂商文字也不启用它。");
            return new RateReport(vendor, null, null, requestedMtPerS, null, false, null, uncovered);
        }

        if (!wd)
        {
            uncovered.Add("已核实料号不是 WD，不套用 2400 MT/s 量产约束。");
            return new RateReport(vendor, null, null, requestedMtPerS, null, false, null, uncovered);
        }

        if (requestedMtPerS is > WdMassProductionCapMtPerS)
        {
            uncovered.Insert(0, $"请求 {requestedMtPerS} MT/s 高于该 WD 料号的量产验收上限 {WdMassProductionCapMtPerS} MT/s，不计入该料号的量产覆盖。");
        }

        return new RateReport(vendor, null, null, requestedMtPerS, WdMassProductionCapMtPerS, true, null, uncovered);
    }

    public static RealDeviceDecision DecideRealDevice(bool clientClaimsSkuVerified) =>
        new(
            false,
            false,
            clientClaimsSkuVerified
                ? "请求声称 SKU 规格已核实，但本机没有已批准的 SKU 规格，不允许实机。"
                : "SKU 规格未核实，不允许实机。");

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
                ["温度档只是候选。未选择候选档，或器件 SKU 规格尚未核实。"],
                false,
                false,
                "未授予器件温度许可。");
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
                outside.Select(point => $"{point} ℃ 不在该候选温度档内。").ToArray(),
                false,
                false,
                "超出候选档，已拒绝。这不表示该候选档已获器件许可。");
        }

        return new TemperatureReport(
            skuClass!,
            SourceRules.PlatformPolicyLayer,
            rated,
            points,
            [],
            rated.Select(point => $"{point} ℃ 尚无实际覆盖。").ToArray(),
            true,
            false,
            "请求落在候选档内。SKU 规格未核实，不授予真实器件温度许可。");
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
        return new DeviceProfile(false, partNumber, sku, revision, reason, fields, Rate(null, null, false));
    }

    private static readonly Dictionary<string, int[]> Templates = new(StringComparer.Ordinal)
    {
        ["commercial-0-70"] = [0, 25, 70],
        ["commercial-0-85"] = [0, 25, 85],
        ["industrial"] = [-40, 25, 85]
    };
}

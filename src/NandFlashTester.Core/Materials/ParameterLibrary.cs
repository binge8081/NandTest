namespace NandFlashTester.Core.Materials;

public sealed record ParameterEntry(
    string Key,
    string Layer,
    string? Version,
    string SourceStatus,
    bool Enabled,
    string? ValueText,
    string Reason);

public sealed record ParameterImport(
    string Key,
    string? Version,
    string? DocumentId,
    string? Chapter,
    string? ProposedValue,
    string SourceStatus,
    bool Enabled,
    string Reason);

public static class ParameterLibrary
{
    public const string PolicyVersion = "platform-policy/1";

    public static IReadOnlyList<ParameterEntry> Active { get; } =
    [
        Candidate("wd-mass-production-cap-mt-s", "2400", "仅在料号被核实为 WD 并选用该约束时适用。未核实时不是所有厂商的全局上限，也不是器件许可。"),
        Candidate("sku-temperature-commercial-0-70", "0,25,70", "候选温度档。须在 SKU 规格核实后选择，不自动成为当前器件许可。"),
        Candidate("sku-temperature-commercial-0-85", "0,25,85", "候选温度档。须在 SKU 规格核实后选择，不自动成为当前器件许可。"),
        Candidate("sku-temperature-industrial", "-40,25,85", "候选温度档。须在 SKU 规格核实后选择，不自动成为当前器件许可。"),
        Device("command-set"),
        Device("address-organization"),
        Device("timing"),
        Device("voltage"),
        Device("temperature-limits"),
        Device("status-bits"),
        Device("pass-fail-criteria")
    ];

    public static ParameterImport ImportDevice(string? key, string? version, string? documentId, string? chapter, string? proposedValue)
    {
        var name = key?.Trim() ?? "";
        if (Active.All(entry => entry.Key != name || entry.Layer != SourceRules.DeviceLayer))
        {
            return new ParameterImport(name, version, documentId, chapter, null, "Rejected", false, "未知参数或不是器件资料层，未启用。");
        }

        var status = SourceRules.FromCitation(documentId, chapter);
        var reason = status == SourceStatus.CitedUnverified
            ? "来源文字未核对到资料内容，参数保持禁用，提议值不生效。"
            : "没有资料来源，参数保持禁用，提议值不生效。";
        var proposed = string.IsNullOrWhiteSpace(proposedValue) ? null : proposedValue.Trim();
        if (proposed is { Length: > 64 } || proposed?.Contains('/') == true || proposed?.Contains('\\') == true)
        {
            proposed = null;
            reason += " 提议值格式无效，未保存为生效值。";
        }

        return new ParameterImport(name, string.IsNullOrWhiteSpace(version) ? null : version.Trim(), documentId, chapter, proposed, status.ToString(), false, reason);
    }

    private static ParameterEntry Candidate(string key, string value, string reason) =>
        new(key, SourceRules.PlatformPolicyLayer, PolicyVersion, "Candidate", false, value, reason);

    private static ParameterEntry Device(string key) =>
        new(key, SourceRules.DeviceLayer, null, SourceStatus.NotProvided.ToString(), false, null, "器件 datasheet 未提供，该项禁用。");
}

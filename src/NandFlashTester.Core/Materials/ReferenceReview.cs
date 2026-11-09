namespace NandFlashTester.Core.Materials;

public sealed record FieldProvenance(
    string? PartNumber,
    string? Sku,
    string? DocumentVersion,
    string? Chapter,
    string? Pages);

public sealed record ObservedField(
    string Name,
    string Category,
    string? ValueText,
    string EvidenceStatus,
    bool ReferenceCheckRequested,
    FieldProvenance Provenance);

public sealed record FieldDecision(
    string Name,
    string Category,
    string? ValueText,
    string EvidenceStatus,
    bool ReferenceCheckAllowed,
    bool RealDeviceControl,
    bool OperatingPermission,
    bool IsQlc,
    FieldProvenance Provenance,
    string Reason);

public sealed record ReferenceAssessment(
    bool RealDeviceEnabled,
    bool VendorPermissionClaimed,
    bool CloudReleaseApproved,
    int ReferenceCheckCount,
    int DisabledCount,
    IReadOnlyList<FieldDecision> Fields)
{
    public FieldDecision Field(string name) => Fields.Single(field => field.Name == name);
}

public static class ReferenceReview
{
    public static ReferenceAssessment Assess(IReadOnlyList<ObservedField> fields)
    {
        var decisions = fields.Select(Decide).ToArray();
        return new ReferenceAssessment(
            false,
            false,
            false,
            decisions.Count(field => field.ReferenceCheckAllowed),
            decisions.Count(field => !field.ReferenceCheckAllowed),
            decisions);
    }

    private static FieldDecision Decide(ObservedField field)
    {
        var category = field.Category ?? "";
        var value = field.ValueText;
        if (category == "platform_policy_not_manufacturer")
        {
            value = null;
        }

        var qlc = string.Equals(value, "QLC", StringComparison.OrdinalIgnoreCase);
        var provenanceReady = !string.IsNullOrWhiteSpace(field.Provenance.PartNumber)
            && !string.IsNullOrWhiteSpace(field.Provenance.Sku)
            && !string.IsNullOrWhiteSpace(field.Provenance.DocumentVersion)
            && !string.IsNullOrWhiteSpace(field.Provenance.Chapter);
        var verified = field.EvidenceStatus is "verified_pdf" or "verified_synthetic";
        var blockedCategory = category is "hardware_measurement" or "platform_policy_not_manufacturer" or "device_private_operation";
        var reference = field.ReferenceCheckRequested
            && value is not null
            && verified
            && provenanceReady
            && !blockedCategory;
        var reason = category switch
        {
            "stress_rating_only_not_operating_permission" => "绝对最大只作应力边界，不是连续工作许可，也不启用实机。",
            "recommended_operating_not_absolute" => "推荐工作范围不是绝对最大，单份资料不启用实机。",
            "recommended_operating_case_surface" => "这是壳表面温度，不是环境设定，也不启用实机。",
            "optional_supply_recommended_not_enable_permission" => "可选电源的推荐值不是上电许可。",
            "device_rated_not_platform" => value is null ? "额定值为空，不用模拟值或平台上限补上。" : "额定值不写成平台量产约束，也不启用实机。",
            "hardware_measurement" => "实测栏保持为空，不用额定值代替。",
            "platform_policy_not_manufacturer" => "平台约束不从器件资料填入。",
            "device_identity" when value?.Contains("TLC", StringComparison.OrdinalIgnoreCase) == true && !qlc => "单元类型含 TLC，不改称 QLC。",
            _ => reference ? "只允许参考核对。实机控制保持关闭，也未宣称厂商许可。" : "资料不足或值为空，参考核对与实机控制都关闭。"
        };
        return new FieldDecision(
            field.Name,
            category,
            value,
            field.EvidenceStatus,
            reference,
            false,
            false,
            qlc,
            field.Provenance,
            reason);
    }
}

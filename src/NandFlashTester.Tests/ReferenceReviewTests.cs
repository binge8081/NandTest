using System.Text.Json;
using NandFlashTester.Core.Materials;

namespace NandFlashTester.Tests;

public class ReferenceReviewTests
{
    private static readonly FieldProvenance Synthetic = new("SYN-PART", "SYN-SKU", "SYN-DOC-1", "SYN-1", "p1");

    [Fact]
    public void Synthetic_layers_stay_separate_and_do_not_enable_a_device()
    {
        var assessment = ReferenceReview.Assess(
        [
            Observed("cell-type", "device_identity", "SYN-TLC", "verified_synthetic", true),
            Observed("work-voltage", "recommended_operating_not_absolute", "1.00/1.10/1.20", "verified_synthetic", true),
            Observed("abs-voltage", "stress_rating_only_not_operating_permission", "0.10/9.90", "verified_synthetic", true),
            Observed("case-temp", "recommended_operating_case_surface", "0/1", "verified_synthetic", true),
            Observed("optional-supply", "optional_supply_recommended_not_enable_permission", "0.20", "verified_synthetic", true),
            Observed("rated-rate", "device_rated_not_platform", null, "conflict_not_resolved_in_this_minimal_review", false),
            Observed("measured-rate", "hardware_measurement", null, "not_measured", false),
            Observed("platform-limit", "platform_policy_not_manufacturer", "999", "not_loaded_from_datasheet", false)
        ]);

        Assert.False(assessment.RealDeviceEnabled);
        Assert.False(assessment.VendorPermissionClaimed);
        Assert.False(assessment.CloudReleaseApproved);
        Assert.Equal(5, assessment.ReferenceCheckCount);
        Assert.All(assessment.Fields, field =>
        {
            Assert.False(field.RealDeviceControl);
            Assert.False(field.OperatingPermission);
        });

        var cell = assessment.Field("cell-type");
        Assert.Equal("SYN-TLC", cell.ValueText);
        Assert.False(cell.IsQlc);
        Assert.Contains("不改称 QLC", cell.Reason, StringComparison.Ordinal);
        Assert.Equal("SYN-DOC-1", cell.Provenance.DocumentVersion);
        Assert.Equal("SYN-1", cell.Provenance.Chapter);

        Assert.NotEqual(assessment.Field("work-voltage").Category, assessment.Field("abs-voltage").Category);
        Assert.Contains("不是绝对最大", assessment.Field("work-voltage").Reason, StringComparison.Ordinal);
        Assert.Contains("不是连续工作许可", assessment.Field("abs-voltage").Reason, StringComparison.Ordinal);
        Assert.Contains("壳表面", assessment.Field("case-temp").Reason, StringComparison.Ordinal);
        Assert.Contains("不是上电许可", assessment.Field("optional-supply").Reason, StringComparison.Ordinal);

        Assert.Null(assessment.Field("rated-rate").ValueText);
        Assert.False(assessment.Field("rated-rate").ReferenceCheckAllowed);
        Assert.Null(assessment.Field("measured-rate").ValueText);
        Assert.Null(assessment.Field("platform-limit").ValueText);
        Assert.False(assessment.Field("platform-limit").ReferenceCheckAllowed);
    }

    [Fact]
    public void Missing_provenance_disables_reference_check()
    {
        var assessment = ReferenceReview.Assess(
        [
            new ObservedField("cell-type", "device_identity", "SYN-TLC", "verified_synthetic", true, new FieldProvenance(null, null, null, null, null))
        ]);
        Assert.False(assessment.Field("cell-type").ReferenceCheckAllowed);
        Assert.False(assessment.RealDeviceEnabled);
    }

    [Fact]
    public void Private_reference_shape_does_not_enable_control_or_fill_empty_rates()
    {
        var path = Environment.GetEnvironmentVariable("NAND_PRIVATE_REFERENCE");
        if (string.IsNullOrWhiteSpace(path) || !File.Exists(path))
        {
            return;
        }

        using var document = JsonDocument.Parse(File.ReadAllText(path));
        var root = document.RootElement;
        Assert.False(root.GetProperty("real_device_execution_enabled").GetBoolean());
        Assert.False(root.GetProperty("permission_review").GetProperty("legal_compliance_confirmed").GetBoolean());
        Assert.False(root.GetProperty("permission_review").GetProperty("cloud_upload_enabled").GetBoolean());
        Assert.False(root.GetProperty("permission_review").GetProperty("explicit_cloud_prohibition_found_in_reviewed_pages").GetBoolean());
        Assert.False(root.GetProperty("permission_review").GetProperty("github_commit_allowed").GetBoolean());

        var observations = new List<ObservedField>();
        foreach (var field in root.GetProperty("fields").EnumerateArray())
        {
            var value = field.GetProperty("value");
            string? text = value.ValueKind switch
            {
                JsonValueKind.Null => null,
                JsonValueKind.String => value.GetString(),
                _ => "present"
            };
            var pages = field.GetProperty("pdf_physical_pages").GetArrayLength() == 0
                ? null
                : "present";
            observations.Add(new ObservedField(
                field.GetProperty("field").GetString()!,
                field.GetProperty("value_category").GetString()!,
                text,
                field.GetProperty("evidence_status").GetString()!,
                field.GetProperty("enabled_for_reference_validation").GetBoolean(),
                new FieldProvenance(
                    field.GetProperty("part_number").GetString(),
                    field.GetProperty("sku").GetString(),
                    field.GetProperty("document_version").GetString(),
                    field.GetProperty("printed_section_or_table").GetString(),
                    pages)));
            Assert.False(field.GetProperty("enabled_for_real_device_control").GetBoolean());
        }

        var assessment = ReferenceReview.Assess(observations);
        Assert.Equal(20, assessment.Fields.Count);
        Assert.Equal(15, assessment.ReferenceCheckCount);
        Assert.Equal(5, assessment.DisabledCount);
        Assert.False(assessment.RealDeviceEnabled);
        Assert.False(assessment.VendorPermissionClaimed);
        Assert.False(assessment.CloudReleaseApproved);
        Assert.All(assessment.Fields, item => Assert.False(item.RealDeviceControl));
        Assert.All(assessment.Fields, item => Assert.False(item.OperatingPermission));
        Assert.Null(assessment.Field("qualified_package_rate").ValueText);
        Assert.Null(assessment.Field("actual_execution_rate").ValueText);
        Assert.Null(assessment.Field("platform_production_rate_limit").ValueText);
        Assert.False(assessment.Field("cell_type").IsQlc);
        Assert.DoesNotContain("QLC", assessment.Field("cell_type").ValueText, StringComparison.OrdinalIgnoreCase);
        Assert.NotEqual(
            assessment.Field("vcc_recommended").Category,
            assessment.Field("vcc_absolute").Category);
    }

    private static ObservedField Observed(string name, string category, string? value, string evidence, bool reference) =>
        new(name, category, value, evidence, reference, Synthetic);
}

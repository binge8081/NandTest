using NandFlashTester.Core.Materials;

namespace NandFlashTester.Tests;

public class DeviceMaterialTests
{
    [Fact]
    public void Unconfigured_profile_has_no_datasheet_values()
    {
        var profile = DeviceMaterialModel.Unconfigured();
        Assert.False(profile.RealConfigEnabled);
        Assert.Null(profile.PartNumber);
        Assert.Null(profile.Sku);
        Assert.Null(profile.DatasheetRevision);
        Assert.All(profile.Fields, field =>
        {
            Assert.Equal(SourceRules.DeviceLayer, field.Layer);
            Assert.Null(field.ValueText);
            Assert.False(field.Enabled);
            Assert.Equal(SourceStatus.NotProvided, field.SourceStatus);
        });
        Assert.Null(profile.Rate.RatedMtPerS);
        Assert.Null(profile.Rate.ActualCoveredMtPerS);
        Assert.False(profile.Rate.CapApplies);
        Assert.Null(profile.Rate.MassProductionCapMtPerS);
        Assert.DoesNotContain("64", string.Join(' ', profile.Fields.Select(field => field.ValueText)));
    }

    [Fact]
    public void A_citation_string_does_not_verify_or_enable_real_specs()
    {
        var profile = DeviceMaterialModel.Bind(new MaterialClaim(
            "WD-EXAMPLE",
            "commercial-0-70",
            "datasheet-internal",
            "1.0",
            "4.2.1",
            3200,
            2700,
            16384));

        Assert.False(profile.RealConfigEnabled);
        Assert.Null(profile.PartNumber);
        Assert.Equal(SourceStatus.CitedUnverified, profile.Field("voltage").SourceStatus);
        Assert.Null(profile.Field("voltage").ValueText);
        Assert.Null(profile.Field("timing").ValueText);
        Assert.Null(profile.Field("address-organization").ValueText);
        Assert.Null(profile.Field("command-set").ValueText);
        Assert.NotEqual(SourceStatus.Approved, profile.Field("status-bits").SourceStatus);
        Assert.Null(profile.Rate.RatedMtPerS);
    }

    [Fact]
    public void Platform_policy_does_not_fill_the_device_layer()
    {
        var unverified = DeviceMaterialModel.Rate("WD", 3200, partVerifiedAsWd: false);
        Assert.Null(unverified.RatedMtPerS);
        Assert.Null(unverified.MassProductionCapMtPerS);
        Assert.False(unverified.CapApplies);
        Assert.Contains(unverified.Uncovered, item => item.Contains("不作为所有厂商", StringComparison.Ordinal));

        var otherVendor = DeviceMaterialModel.Rate("Kioxia", 1600, partVerifiedAsWd: true);
        Assert.Null(otherVendor.MassProductionCapMtPerS);
        Assert.False(otherVendor.CapApplies);
        Assert.Contains(otherVendor.Uncovered, item => item.Contains("不是 WD", StringComparison.Ordinal));

        var rate = DeviceMaterialModel.Rate("WD", 3200, partVerifiedAsWd: true);
        Assert.Null(rate.RatedMtPerS);
        Assert.Null(rate.CharacterizationMtPerS);
        Assert.Equal(3200, rate.RequestedMtPerS);
        Assert.Equal(2400, rate.MassProductionCapMtPerS);
        Assert.True(rate.CapApplies);
        Assert.Null(rate.ActualCoveredMtPerS);
        Assert.Contains(rate.Uncovered, item => item.Contains("2400", StringComparison.Ordinal) && item.Contains("不计入该料号的量产覆盖", StringComparison.Ordinal));

        var temperature = DeviceMaterialModel.Temperature("commercial-0-70", [0, 25, 100]);
        Assert.False(temperature.WithinCandidate);
        Assert.False(temperature.DevicePermissionGranted);
        Assert.Empty(temperature.ActualCoveredPoints);
        Assert.Equal(SourceRules.PlatformPolicyLayer, temperature.Layer);
        Assert.Contains(temperature.Uncovered, item => item.Contains("100", StringComparison.Ordinal));
    }

    [Fact]
    public void In_range_temperature_candidate_does_not_grant_device_permission()
    {
        var temperature = DeviceMaterialModel.Temperature("industrial", [-40, 25, 85]);
        Assert.True(temperature.WithinCandidate);
        Assert.False(temperature.DevicePermissionGranted);
        Assert.Contains("不授予真实器件温度许可", temperature.Reason, StringComparison.Ordinal);
        Assert.Equal([-40, 25, 85], temperature.RatedPoints);
        Assert.Equal([-40, 25, 85], temperature.RequestedPoints);
        Assert.Empty(temperature.ActualCoveredPoints);
        Assert.Equal(3, temperature.Uncovered.Count);
    }

    [Fact]
    public void A_client_claim_does_not_verify_the_sku_or_allow_real_device()
    {
        var denied = DeviceMaterialModel.DecideRealDevice(false);
        var claimed = DeviceMaterialModel.DecideRealDevice(true);
        Assert.False(denied.Allowed);
        Assert.False(denied.SkuSpecVerified);
        Assert.False(claimed.Allowed);
        Assert.False(claimed.SkuSpecVerified);
        Assert.Contains("不允许实机", claimed.Reason, StringComparison.Ordinal);

        var candidate = ParameterLibrary.Active.Single(entry => entry.Key == "sku-temperature-industrial");
        Assert.False(candidate.Enabled);
        Assert.Equal("Candidate", candidate.SourceStatus);
        var cap = ParameterLibrary.Active.Single(entry => entry.Key == "wd-mass-production-cap-mt-s");
        Assert.False(cap.Enabled);
        Assert.Equal("2400", cap.ValueText);
        Assert.Contains("不是所有厂商", cap.Reason, StringComparison.Ordinal);
    }
}

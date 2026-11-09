using NandFlashTester.Core.Materials;

namespace NandFlashTester.Api.Materials;

public sealed record CoverageRequest(int? RequestedMtPerS, string? Vendor, string? SkuClass, int[]? Temperatures, bool SkuSpecVerified);

public static class MaterialEndpoints
{
    public static object View(DeviceProfile profile) => new
    {
        realConfigEnabled = profile.RealConfigEnabled,
        partNumber = profile.PartNumber,
        sku = profile.Sku,
        datasheetRevision = profile.DatasheetRevision,
        reason = profile.Reason,
        hardwareAccepted = false,
        layers = new
        {
            deviceDatasheet = SourceRules.DeviceLayer,
            platformIcd = SourceRules.PlatformIcdLayer,
            platformPolicy = SourceRules.PlatformPolicyLayer
        },
        fields = profile.Fields.Select(field => new
        {
            name = field.Name,
            layer = field.Layer,
            value = field.ValueText,
            documentId = field.DocumentId,
            chapter = field.Chapter,
            sourceStatus = field.SourceStatus.ToString(),
            enabled = field.Enabled,
            reason = field.Reason
        }),
        rate = new
        {
            ratedMtPerS = profile.Rate.RatedMtPerS,
            characterizationMtPerS = profile.Rate.CharacterizationMtPerS,
            requestedMtPerS = profile.Rate.RequestedMtPerS,
            massProductionCapMtPerS = profile.Rate.MassProductionCapMtPerS,
            capApplies = profile.Rate.CapApplies,
            actualCoveredMtPerS = profile.Rate.ActualCoveredMtPerS,
            uncovered = profile.Rate.Uncovered,
            policySource = "2400 MT/s 只在料号被核实为 WD 时作为量产约束。当前没有已核实料号，所以上限未适用，也不是器件许可。"
        },
        temperatureTemplates = new
        {
            role = "candidate",
            devicePermissionGranted = false,
            realDeviceAllowed = false,
            reason = "三温只是未核实候选。未经 SKU 规格核实，不允许实机。"
        }
    };

    public static object Coverage(CoverageRequest request)
    {
        var rate = DeviceMaterialModel.Rate(request.Vendor, request.RequestedMtPerS, partVerifiedAsWd: false);
        var temperature = DeviceMaterialModel.Temperature(request.SkuClass, request.Temperatures);
        var realDevice = DeviceMaterialModel.DecideRealDevice(request.SkuSpecVerified);
        return new
        {
            hardwareAccepted = false,
            realConfigEnabled = false,
            realDeviceAllowed = realDevice.Allowed,
            skuSpecVerified = realDevice.SkuSpecVerified,
            clientClaimIgnored = true,
            rate = new
            {
                vendor = rate.Vendor,
                ratedMtPerS = rate.RatedMtPerS,
                characterizationMtPerS = rate.CharacterizationMtPerS,
                requestedMtPerS = rate.RequestedMtPerS,
                massProductionCapMtPerS = rate.MassProductionCapMtPerS,
                capApplies = rate.CapApplies,
                actualCoveredMtPerS = rate.ActualCoveredMtPerS,
                uncovered = rate.Uncovered
            },
            temperature = new
            {
                skuClass = temperature.SkuClass,
                layer = temperature.Layer,
                ratedPoints = temperature.RatedPoints,
                requestedPoints = temperature.RequestedPoints,
                actualCoveredPoints = temperature.ActualCoveredPoints,
                uncovered = temperature.Uncovered,
                withinCandidate = temperature.WithinCandidate,
                devicePermissionGranted = temperature.DevicePermissionGranted,
                reason = temperature.Reason
            }
        };
    }
}

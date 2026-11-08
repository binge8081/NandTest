using NandFlashTester.Core.Materials;

namespace NandFlashTester.Api.Materials;

public sealed record CoverageRequest(int? RequestedMtPerS, string? SkuClass, int[]? Temperatures);

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
            actualCoveredMtPerS = profile.Rate.ActualCoveredMtPerS,
            uncovered = profile.Rate.Uncovered,
            policySource = "平台规划已定 WD 量产验收上限 2400 MT/s。不是器件 datasheet 额定值。"
        }
    };

    public static object Coverage(CoverageRequest request)
    {
        var rate = DeviceMaterialModel.Rate(request.RequestedMtPerS);
        var temperature = DeviceMaterialModel.Temperature(request.SkuClass, request.Temperatures);
        return new
        {
            hardwareAccepted = false,
            realConfigEnabled = false,
            rate = new
            {
                ratedMtPerS = rate.RatedMtPerS,
                characterizationMtPerS = rate.CharacterizationMtPerS,
                requestedMtPerS = rate.RequestedMtPerS,
                massProductionCapMtPerS = rate.MassProductionCapMtPerS,
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
                accepted = temperature.Accepted,
                reason = temperature.Reason
            }
        };
    }
}

namespace NandFlashTester.Core.Execution;

public sealed record RawBitErrorReport(
    string Source,
    long? ComparedBits,
    long? ErrorBits,
    double? RawRate,
    string Unit,
    string Uber,
    string Ecc,
    bool ThresholdDecided,
    bool Measured,
    string Reason);

public static class RawBitError
{
    public const string UnitText = "错误位/已比较位";

    public static bool HasMeasurementEvidence(int bytesEmitted, string? evidenceId) =>
        bytesEmitted > 0 && !string.IsNullOrWhiteSpace(evidenceId);

    public static RawBitErrorReport Compute(string? source, long? comparedBits, long? errorBits, bool measurementEvidenceLinked = false)
    {
        var kind = source?.Trim() ?? "";
        if (kind == "measured" && !measurementEvidenceLinked)
        {
            return new RawBitErrorReport(kind, comparedBits, errorBits, null, UnitText, "未统计", "未统计", false, false,
                "来源字符串不能单独成为实测。没有关联的发送证据，保持未验证。UBER 未统计。");
        }

        var known = kind is "synthetic" or "isolated-test" or "measured";
        if (!known || comparedBits is null or <= 0 || errorBits is null or < 0 || errorBits > comparedBits)
        {
            return new RawBitErrorReport(kind, comparedBits, errorBits, null, UnitText, "未统计", "未统计", false, false,
                known ? "分母必须是已比较位数，且错误位数不能为负或大于分母。本次不计算比率。" : "没有合法计数来源，原始误码率未统计。");
        }

        var measured = kind == "measured" && measurementEvidenceLinked;
        return new RawBitErrorReport(
            kind,
            comparedBits,
            errorBits,
            errorBits.Value / (double)comparedBits.Value,
            UnitText,
            "未统计",
            "未统计",
            false,
            measured,
            measured
                ? "已关联发送证据。没有 ECC 定义，UBER 仍未统计，也不做通过判定。"
                : "这是合成或隔离测试计数，不是实机数据。UBER 未统计，也不做通过判定。");
    }
}

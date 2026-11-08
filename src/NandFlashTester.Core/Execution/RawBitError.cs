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

    public static RawBitErrorReport Compute(string? source, long? comparedBits, long? errorBits)
    {
        var kind = source?.Trim() ?? "";
        var known = kind is "synthetic" or "isolated-test" or "measured";
        if (!known || comparedBits is null or <= 0 || errorBits is null or < 0 || errorBits > comparedBits)
        {
            return new RawBitErrorReport(kind, comparedBits, errorBits, null, UnitText, "未统计", "未统计", false, false,
                known ? "分母必须是已比较位数，且错误位数不能为负或大于分母。本次不计算比率。" : "没有合法计数来源，原始误码率未统计。");
        }

        return new RawBitErrorReport(
            kind,
            comparedBits,
            errorBits,
            errorBits.Value / (double)comparedBits.Value,
            UnitText,
            "未统计",
            "未统计",
            false,
            kind == "measured",
            kind == "measured"
                ? "原始比率只使用调用方给出的已比较位数和错误位数。没有 ECC 定义，UBER 未统计，也不做通过判定。"
                : "这是合成或隔离测试计数，不是实机数据。UBER 未统计，也不做通过判定。");
    }
}

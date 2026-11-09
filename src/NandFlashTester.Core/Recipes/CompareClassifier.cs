using System.Globalization;

namespace NandFlashTester.Core.Recipes;

public sealed record CompareFinding(int Page, int ByteOffset, int Bit, string ExpectedHex, string ActualHex);

public sealed record CompareClass(
    string Class,
    int? BitCount,
    int? ByteCount,
    IReadOnlyList<CompareFinding> Findings,
    bool Measured)
{
    public static CompareClass NotExecuted() => new("not_executed", null, null, [], false);

    public static CompareClass Match() => new("match", 0, 0, [], false);
}

public static class CompareClassifier
{
    public const int MaxFindings = 32;

    public static string Text(string errorClass) => errorClass switch
    {
        "not_executed" => "未执行",
        "match" => "一致",
        "bit_mismatch" => "位不一致",
        _ => errorClass
    };

    public static CompareClass Classify(int page, ReadOnlySpan<byte> expected, ReadOnlySpan<byte> actual)
    {
        var findings = new List<CompareFinding>();
        var bits = 0;
        var bytes = 0;
        var length = Math.Min(expected.Length, actual.Length);
        for (var offset = 0; offset < length; offset++)
        {
            var xor = (byte)(expected[offset] ^ actual[offset]);
            if (xor == 0)
            {
                continue;
            }

            bytes++;
            for (var bit = 0; bit < 8; bit++)
            {
                if ((xor & (1 << bit)) == 0)
                {
                    continue;
                }

                bits++;
                if (findings.Count < MaxFindings)
                {
                    findings.Add(new CompareFinding(
                        page,
                        offset,
                        bit,
                        expected[offset].ToString("X2", CultureInfo.InvariantCulture),
                        actual[offset].ToString("X2", CultureInfo.InvariantCulture)));
                }
            }
        }

        var extra = Math.Abs(expected.Length - actual.Length);
        bits += extra * 8;
        bytes += extra;
        return bits == 0
            ? CompareClass.Match()
            : new CompareClass("bit_mismatch", bits, bytes, findings, false);
    }
}

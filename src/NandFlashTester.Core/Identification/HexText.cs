using System.Globalization;
using System.Text;

namespace NandFlashTester.Core.Identification;

public static class HexText
{
    public static string Format(ReadOnlySpan<byte> data)
    {
        var builder = new StringBuilder(data.Length * 3);
        for (var i = 0; i < data.Length; i++)
        {
            if (i > 0)
            {
                builder.Append(' ');
            }

            builder.Append(data[i].ToString("X2", CultureInfo.InvariantCulture));
        }

        return builder.ToString();
    }

    public static bool TryParse(string? text, out byte[] bytes, out string? error)
    {
        bytes = [];
        error = null;
        if (string.IsNullOrWhiteSpace(text))
        {
            return true;
        }

        var cleaned = text.Replace("0x", "", StringComparison.OrdinalIgnoreCase)
            .Replace("0X", "", StringComparison.OrdinalIgnoreCase)
            .Replace(",", " ")
            .Replace(";", " ")
            .Replace("\r", " ")
            .Replace("\n", " ")
            .Replace("\t", " ");
        var parts = cleaned.Split(' ', StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries);
        if (parts.Length == 1 && parts[0].Length > 2 && parts[0].Length % 2 == 0 && parts[0].All(Uri.IsHexDigit))
        {
            parts = SplitPairs(parts[0]);
        }

        var buffer = new byte[parts.Length];
        for (var i = 0; i < parts.Length; i++)
        {
            var token = parts[i];
            if (token.Length != 2 || !byte.TryParse(token, NumberStyles.HexNumber, CultureInfo.InvariantCulture, out buffer[i]))
            {
                error = $"第 {i + 1} 个字节不是两位十六进制：{token}";
                bytes = [];
                return false;
            }
        }

        bytes = buffer;
        return true;
    }

    private static string[] SplitPairs(string compact)
    {
        var pairs = new string[compact.Length / 2];
        for (var i = 0; i < pairs.Length; i++)
        {
            pairs[i] = compact.Substring(i * 2, 2);
        }

        return pairs;
    }
}

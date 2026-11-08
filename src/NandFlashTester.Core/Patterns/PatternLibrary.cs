using System.Security.Cryptography;
using System.Text.RegularExpressions;

namespace NandFlashTester.Core.Patterns;

public sealed record PatternResource(
    string Id,
    string Name,
    string Version,
    string Sha256,
    int PageSize,
    int PageCount,
    byte[] Data);

public static partial class PatternLibrary
{
    public const int MaxBytes = 64 * 1024;
    public const int MinPageSize = 16;
    public const int MaxPageSize = 4096;
    public const string FormulaNote = "INCRMT、RANDOM、ICROVL、RDMOVL 使用本机 PatternGenerator。源码只给出这些命令文本，没有字节公式，所以与旧逐字节等价尚未证明。已上传资源按声明页长切片，不读取脚本中的路径。";

    public static (PatternResource? Resource, string? Error) Accept(string? name, string? version, int pageSize, ReadOnlySpan<byte> data)
    {
        var safeName = (name ?? "").Trim();
        if (safeName.Contains('/') || safeName.Contains('\\') || safeName.Contains(':'))
        {
            return (null, "名称不能是路径。");
        }

        if (!NamePattern().IsMatch(safeName))
        {
            return (null, "名称需要 1 到 64 个字母、数字或连接符。");
        }

        var safeVersion = string.IsNullOrWhiteSpace(version) ? "1" : version.Trim();
        if (!NamePattern().IsMatch(safeVersion))
        {
            return (null, "版本需要 1 到 64 个字母、数字或连接符。");
        }

        if (pageSize is < MinPageSize or > MaxPageSize || pageSize % 16 != 0)
        {
            return (null, "页长度必须是 16 到 4096 之间的 16 的倍数。");
        }

        if (data.Length == 0 || data.Length > MaxBytes || data.Length % pageSize != 0)
        {
            return (null, "文件长度必须是页长度的整数倍，并且不超过 64 KiB。");
        }

        var bytes = data.ToArray();
        return (new PatternResource(
            "pat-" + Convert.ToHexString(SHA256.HashData(bytes))[..16].ToLowerInvariant(),
            safeName,
            safeVersion,
            Convert.ToHexString(SHA256.HashData(bytes)),
            pageSize,
            bytes.Length / pageSize,
            bytes), null);
    }

    public static byte[] Slice(PatternResource resource, int bufferIndex)
    {
        if (bufferIndex < 0 || bufferIndex >= resource.PageCount)
        {
            throw new ArgumentOutOfRangeException(nameof(bufferIndex), "缓冲序号超出已上传文件的页数。");
        }

        var offset = bufferIndex * resource.PageSize;
        return resource.Data[offset..(offset + resource.PageSize)];
    }

    [GeneratedRegex(@"\A[\p{L}\p{N}_.-]{1,64}\z", RegexOptions.CultureInvariant)]
    private static partial Regex NamePattern();
}

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

public sealed record HostPatternQuota(int MaxBytes)
{
    public static HostPatternQuota Default { get; } = new(64 * 1024);

    public const string Role = "主机图案限额，不是 NAND 或 SKU 的物理页长。";
}

public static partial class PatternLibrary
{
    public const string FormulaNote = "INCRMT、RANDOM、ICROVL、RDMOVL 使用本机 PatternGenerator。源码只给出这些命令文本，没有字节公式，所以与旧逐字节等价尚未证明。已上传资源按声明页长切片，不读取脚本中的路径。";

    public static (PatternResource? Resource, string? Error) Accept(string? name, string? version, int pageSize, ReadOnlySpan<byte> data, HostPatternQuota? quota = null)
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

        var limit = quota ?? HostPatternQuota.Default;
        if (pageSize <= 0)
        {
            return (null, "页长度必须是正整数。这不是器件规格校验。");
        }

        if (data.Length == 0 || data.Length > limit.MaxBytes || data.Length % pageSize != 0)
        {
            return (null, $"文件长度必须是页长度的整数倍，并且不超过主机限额 {limit.MaxBytes} 字节。{HostPatternQuota.Role}");
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

namespace NandFlashTester.Core.Patterns;

public enum PatternKind
{
    Increment,
    Constant,
    Prng
}

/// <summary>
/// 公开、可复现的页图案。地址按块号和页号展开，不读取外部文件，也不执行配方里的文本。
/// increment: byte = (seed + ((block * 65536 + page) * pageSize + offset)) mod 256
/// prng: 每页用 xorshift32，初值由种子、块号、页号混合；同一输入重复调用结果相同。
/// constant: 整页填同一个字节，种子仍记入配方以便追溯，但不参与字节计算。
/// </summary>
public static class PatternGenerator
{
    public const int MaxLength = 4096;

    public static byte[] Generate(PatternKind kind, int pageSize, uint seed, int block, int page, byte constantValue = 0)
    {
        if (pageSize <= 0 || pageSize > MaxLength)
        {
            throw new ArgumentOutOfRangeException(nameof(pageSize), "图案长度必须在 1 到 4096 之间，零长和越界已拒绝。");
        }

        if (block < 0 || page < 0)
        {
            throw new ArgumentOutOfRangeException(nameof(page), "块号和页号不能为负。");
        }

        var data = new byte[pageSize];
        switch (kind)
        {
            case PatternKind.Increment:
                for (var i = 0; i < pageSize; i++)
                {
                    var linear = (((long)block * 65536) + page) * pageSize + i;
                    data[i] = (byte)(((long)seed + linear) & 0xFF);
                }

                break;
            case PatternKind.Constant:
                Array.Fill(data, constantValue);
                break;
            case PatternKind.Prng:
                var state = Mix(seed, block, page);
                for (var i = 0; i < pageSize; i++)
                {
                    state = XorShift32(state);
                    data[i] = (byte)(state & 0xFF);
                }

                break;
            default:
                throw new ArgumentOutOfRangeException(nameof(kind), "未知图案类型。");
        }

        return data;
    }

    public static uint Mix(uint seed, int block, int page)
    {
        var mixed = seed ^ ((uint)block * 0x85EBCA6Bu) ^ ((uint)page * 0x9E3779B9u);
        return mixed == 0 ? 0x6D2B79F5u : mixed;
    }

    public static uint XorShift32(uint state)
    {
        var x = state;
        x ^= x << 13;
        x ^= x >> 17;
        x ^= x << 5;
        return x;
    }
}

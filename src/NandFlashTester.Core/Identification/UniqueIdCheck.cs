namespace NandFlashTester.Core.Identification;

public sealed record UniqueIdResult(bool Valid, int CopyIndex, byte[]? Uid, string Note);

public static class UniqueIdCheck
{
    public static UniqueIdResult Inspect(ReadOnlySpan<byte> data, int copies, int copyBytes, int uidBytes)
    {
        if (copies < 1 || copyBytes < uidBytes * 2 || uidBytes < 1)
        {
            return new UniqueIdResult(false, -1, null, "Unique ID 的份数或长度未给出，不套用默认值。");
        }

        if (data.Length < copyBytes)
        {
            return new UniqueIdResult(false, -1, null, "Unique ID 字节短于一份，不能做补码校验。");
        }

        var available = Math.Min(copies, data.Length / copyBytes);
        for (var i = 0; i < available; i++)
        {
            var slice = data.Slice(i * copyBytes, copyBytes);
            if (ComplementMatches(slice[..uidBytes], slice.Slice(uidBytes, uidBytes)))
            {
                return new UniqueIdResult(true, i, slice[..uidBytes].ToArray(), $"第 {i + 1} 份通过补码校验。");
            }
        }

        var tail = data.Length % copyBytes == 0 ? "" : " 末尾还有不足一份的字节，已忽略。";
        return new UniqueIdResult(false, -1, null, $"已检查 {available} 份，没有一份满足前 {uidBytes} 字节与后 {uidBytes} 字节按位互补。{tail}");
    }

    public static bool ComplementMatches(ReadOnlySpan<byte> uid, ReadOnlySpan<byte> complement)
    {
        if (uid.Length != complement.Length)
        {
            return false;
        }

        for (var i = 0; i < uid.Length; i++)
        {
            if ((byte)(uid[i] ^ complement[i]) != 0xFF)
            {
                return false;
            }
        }

        return true;
    }
}

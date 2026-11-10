namespace NandFlashTester.Core.Identification;

/// <summary>
/// CRC-16 used by the public ONFI parameter page.
/// Polynomial x^16 + x^15 + x^2 + 1 (0x8005), initial value 0x4F4E,
/// no input reflection, no output reflection, no final XOR.
/// ONFI Specification 4.2 and 5.0, parameter page integrity CRC. https://onfi.org/
/// </summary>
public static class Crc16
{
    public const ushort OnfiPolynomial = 0x8005;
    public const ushort OnfiInit = 0x4F4E;

    public const string OnfiCitation =
        "ONFI Specification 4.2 / 5.0，参数页完整性 CRC-16：多项式 x^16+x^15+x^2+1（0x8005），初值 0x4F4E，不反转，最终不异或。https://onfi.org/";

    public static ushort Compute(ReadOnlySpan<byte> data, ushort init, ushort polynomial, bool reflectIn, bool reflectOut, ushort xorOut)
    {
        var crc = init;
        foreach (var raw in data)
        {
            var value = reflectIn ? Reflect8(raw) : raw;
            crc ^= (ushort)(value << 8);
            for (var bit = 0; bit < 8; bit++)
            {
                if ((crc & 0x8000) != 0)
                {
                    crc = (ushort)((crc << 1) ^ polynomial);
                }
                else
                {
                    crc <<= 1;
                }
            }
        }

        if (reflectOut)
        {
            crc = Reflect16(crc);
        }

        return (ushort)(crc ^ xorOut);
    }

    public static ushort Onfi(ReadOnlySpan<byte> data) => Compute(data, OnfiInit, OnfiPolynomial, false, false, 0);

    public static bool StoredLittleEndianMatches(ReadOnlySpan<byte> page, int storedOffset, ushort crc)
    {
        if (storedOffset < 0 || storedOffset + 1 >= page.Length)
        {
            return false;
        }

        var stored = (ushort)(page[storedOffset] | (page[storedOffset + 1] << 8));
        return stored == crc;
    }

    private static byte Reflect8(byte value)
    {
        byte reflected = 0;
        for (var i = 0; i < 8; i++)
        {
            reflected = (byte)((reflected << 1) | (value & 1));
            value >>= 1;
        }

        return reflected;
    }

    private static ushort Reflect16(ushort value)
    {
        ushort reflected = 0;
        for (var i = 0; i < 16; i++)
        {
            reflected = (ushort)((reflected << 1) | (value & 1));
            value >>= 1;
        }

        return reflected;
    }
}

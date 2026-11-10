namespace NandFlashTester.Core.Identification;

public static class SyntheticSamples
{
    public static readonly byte[] Id00 = [0x53, 0xA1, 0x01, 0x10, 0x00, 0x00, 0x00, 0x00];
    public static readonly byte[] Id20 = [0x4F, 0x4E, 0x46, 0x49, 0x01, 0x00];
    public static readonly byte[] Id40 = [0x4A, 0x45, 0x44, 0x45, 0x43, 0x00];

    public static byte[] ParameterPage()
    {
        var copy = new byte[256];
        "ONFI"u8.CopyTo(copy);
        copy[5] = 0x02;
        copy[6] = 0x80;
        copy[7] = 0x20;
        "SYNTHETIC"u8.CopyTo(copy.AsSpan(32));
        "SYN-ONFI-DEMO"u8.CopyTo(copy.AsSpan(44));
        copy[64] = 0x53;
        copy[80] = 0x00;
        copy[81] = 0x10;
        copy[84] = 0x00;
        copy[85] = 0x01;
        copy[92] = 0x00;
        copy[93] = 0x01;
        copy[96] = 0x00;
        copy[97] = 0x04;
        copy[100] = 0x01;
        copy[102] = 0x03;
        copy[112] = 0x08;
        copy[116] = 0x01;
        copy[129] = 0x3F;
        copy[14] = 0x03;
        var crc = Crc16.Onfi(copy.AsSpan(0, 254));
        copy[254] = (byte)crc;
        copy[255] = (byte)(crc >> 8);
        var page = new byte[256 * 3];
        copy.CopyTo(page, 0);
        copy.CopyTo(page, 256);
        copy.CopyTo(page, 512);
        page[0] ^= 0xFF;
        return page;
    }

    public static byte[] UniqueId()
    {
        var uid = new byte[16];
        for (var i = 0; i < uid.Length; i++)
        {
            uid[i] = (byte)(i + 1);
        }

        var data = new byte[32 * 2];
        uid.CopyTo(data, 0);
        for (var i = 0; i < 16; i++)
        {
            data[16 + i] = (byte)~uid[i];
        }

        uid.CopyTo(data, 32);
        for (var i = 0; i < 16; i++)
        {
            data[48 + i] = uid[i];
        }

        return data;
    }
}

namespace NandFlashTester.Core.Identification;

public static class OnfiPublic
{
    public static ReadOnlySpan<byte> Signature => "ONFI"u8;
    public static ReadOnlySpan<byte> JedecIdSignature => "JEDEC"u8;
    public static ReadOnlySpan<byte> JedecPageSignature => "JESD"u8;
    public static ReadOnlySpan<byte> ExtendedSignature => "EPPS"u8;

    public const int CopyLength = 256;
    public const int MinimumCopies = 3;

    public const string CopyCitation =
        "ONFI Specification 4.2 / 5.0：参数页每份 256 字节，至少 3 份冗余。https://onfi.org/";

    public const string UniqueIdCitation =
        "ONFI Read Unique ID（EDh，地址 00h）：16 份，每份 32 字节，前 16 字节为 UID，后 16 字节为按位补码，异或应为 16 字节 FFh。";

    private static readonly (int Bit, string Name)[] RevisionBits =
    [
        (1, "1.0"), (2, "2.0"), (3, "2.1"), (4, "2.2"), (5, "2.3"),
        (6, "3.0"), (7, "3.1"), (8, "3.2"), (9, "4.0"), (10, "4.1"),
        (11, "4.2"), (12, "5.0")
    ];

    public static bool StartsWith(ReadOnlySpan<byte> data, ReadOnlySpan<byte> signature) =>
        data.Length >= signature.Length && data[..signature.Length].SequenceEqual(signature);

    public static IReadOnlyList<string> RevisionNames(ushort revision, out IReadOnlyList<int> unnamedBits)
    {
        var names = new List<string>();
        var unnamed = new List<int>();
        for (var bit = 0; bit < 16; bit++)
        {
            if ((revision & (1 << bit)) == 0)
            {
                continue;
            }

            var known = false;
            foreach (var item in RevisionBits)
            {
                if (item.Bit == bit)
                {
                    names.Add(item.Name);
                    known = true;
                    break;
                }
            }

            if (!known)
            {
                unnamed.Add(bit);
            }
        }

        unnamedBits = unnamed;
        return names;
    }

    public static IReadOnlyList<string> FeatureInterfaces(ushort features)
    {
        var names = new List<string>();
        if ((features & (1 << 5)) != 0)
        {
            names.Add("NV-DDR");
        }

        if ((features & (1 << 10)) != 0)
        {
            names.Add("NV-DDR2");
        }

        if ((features & (1 << 13)) != 0)
        {
            names.Add("NV-DDR3");
        }

        return names;
    }

    public static bool ExtendedParameterPage(ushort features) => (features & (1 << 7)) != 0;

    public static IReadOnlyList<int> SetBits(ReadOnlySpan<byte> data)
    {
        var bits = new List<int>();
        for (var i = 0; i < data.Length; i++)
        {
            for (var bit = 0; bit < 8; bit++)
            {
                if ((data[i] & (1 << bit)) != 0)
                {
                    bits.Add(i * 8 + bit);
                }
            }
        }

        return bits;
    }
}

public sealed record PageCopy(int Index, int Offset, byte[] Bytes, bool CrcValid, string CrcNote);

public sealed record DecodedParameterPage
{
    public required string Layout { get; init; }
    public required byte[] Bytes { get; init; }
    public required int CopyIndex { get; init; }
    public required string Acceptance { get; init; }
    public string? Manufacturer { get; init; }
    public string? Model { get; init; }
    public byte? JedecManufacturer { get; init; }
    public ushort? Revision { get; init; }
    public IReadOnlyList<string> RevisionNames { get; init; } = [];
    public IReadOnlyList<int> UnnamedRevisionBits { get; init; } = [];
    public ushort? Features { get; init; }
    public IReadOnlyList<string> Interfaces { get; init; } = [];
    public long? PageDataBytes { get; init; }
    public int? SpareBytes { get; init; }
    public long? PagesPerBlock { get; init; }
    public long? BlocksPerLun { get; init; }
    public int? Luns { get; init; }
    public int? BitsPerCell { get; init; }
    public int? EccBits { get; init; }
    public bool EccFieldDefined { get; init; }
    public IReadOnlyList<int> SdrTimingModes { get; init; } = [];
    public int? ExtendedLength { get; init; }
    public int? DeclaredCopies { get; init; }
    public string? ExtendedNote { get; init; }
    public IReadOnlyList<string> Notes { get; init; } = [];
}

public static class ParameterPages
{
    public static bool TrySelectOnfi(ReadOnlySpan<byte> data, out DecodedParameterPage? page, out string note)
    {
        page = null;
        note = "";
        if (!AnySignature(data, OnfiPublic.CopyLength, OnfiPublic.Signature))
        {
            note = "参数页没有 ONFI 签名。";
            return false;
        }

        var selected = SelectCopy(data, OnfiPublic.CopyLength, useOnfiCrc: true, cardCrc: null);
        if (selected is null || !OnfiPublic.StartsWith(selected.Bytes, OnfiPublic.Signature))
        {
            note = "ONFI 签名存在，但没有一份通过公开 CRC-16，未采用页内数值。";
            return false;
        }

        page = DecodeOnfi(selected, data);
        note = selected.CrcNote;
        return true;
    }

    public static bool TrySelectJedec(ReadOnlySpan<byte> data, CardCrc? cardCrc, bool acceptIdenticalCopies, out DecodedParameterPage? page, out string note)
    {
        page = null;
        note = "";
        if (!AnySignature(data, copyLength: 512, OnfiPublic.JedecPageSignature))
        {
            note = "参数页没有 JESD 签名。";
            return false;
        }

        const int copyLength = 512;
        if (data.Length < copyLength)
        {
            note = "JESD 签名存在，但长度不足 512 字节，未按公开组织块解析。";
            return false;
        }

        var selected = SelectCopy(data, copyLength, useOnfiCrc: false, cardCrc);
        if (selected is not null && !OnfiPublic.StartsWith(selected.Bytes, OnfiPublic.JedecPageSignature))
        {
            selected = null;
        }

        if (selected is null && acceptIdenticalCopies && CopiesIdentical(data, copyLength) && OnfiPublic.StartsWith(data, OnfiPublic.JedecPageSignature))
        {
            selected = new PageCopy(0, 0, data[..copyLength].ToArray(), false, "冗余副本逐字节一致（参数卡允许在没有 CRC 时采用；未做 CRC）");
        }

        if (selected is null)
        {
            note = "JESD 签名存在，但参数卡没有给出可用的 CRC，也没有允许采用一致的冗余副本，未采用页内数值。";
            return false;
        }

        page = DecodeJedec(selected);
        note = selected.CrcNote;
        return true;
    }

    public static PageCopy? SelectCopy(ReadOnlySpan<byte> data, int copyLength, bool useOnfiCrc, CardCrc? cardCrc)
    {
        var count = data.Length / copyLength;
        PageCopy? first = null;
        for (var i = 0; i < count; i++)
        {
            var slice = data.Slice(i * copyLength, copyLength).ToArray();
            if (TryCrc(slice, useOnfiCrc, cardCrc, out var crcNote))
            {
                first ??= new PageCopy(i, i * copyLength, slice, true, $"参数页副本 {i + 1}，{crcNote}");
            }
        }

        return first;
    }

    public static bool TryCrc(byte[] copy, bool useOnfiCrc, CardCrc? cardCrc, out string note)
    {
        if (useOnfiCrc)
        {
            var crc = Crc16.Onfi(copy.AsSpan(0, 254));
            if (Crc16.StoredLittleEndianMatches(copy, 254, crc))
            {
                note = Crc16.OnfiCitation;
                return true;
            }
        }

        if (cardCrc is { Mode: "card" } && cardCrc.Polynomial is ushort poly && cardCrc.Init is ushort init && cardCrc.CoverageStart is int start && cardCrc.CoverageEndExclusive is int end && cardCrc.StoredOffset is int stored)
        {
            if (start >= 0 && end <= copy.Length && start < end && stored + 1 < copy.Length)
            {
                var crc = Crc16.Compute(copy.AsSpan(start, end - start), init, poly, cardCrc.ReflectIn, cardCrc.ReflectOut, cardCrc.XorOut);
                if (Crc16.StoredLittleEndianMatches(copy, stored, crc))
                {
                    note = string.IsNullOrWhiteSpace(cardCrc.Citation) ? "参数卡给出的 CRC-16" : cardCrc.Citation;
                    return true;
                }
            }
        }

        note = "";
        return false;
    }

    private static bool AnySignature(ReadOnlySpan<byte> data, int copyLength, ReadOnlySpan<byte> signature)
    {
        if (data.Length < signature.Length)
        {
            return false;
        }

        if (data.Length < copyLength)
        {
            return OnfiPublic.StartsWith(data, signature);
        }

        for (var offset = 0; offset + copyLength <= data.Length; offset += copyLength)
        {
            if (OnfiPublic.StartsWith(data[offset..], signature))
            {
                return true;
            }
        }

        return false;
    }

    private static bool CopiesIdentical(ReadOnlySpan<byte> data, int copyLength)
    {
        var count = data.Length / copyLength;
        if (count < 2)
        {
            return false;
        }

        var first = data[..copyLength];
        for (var i = 1; i < count; i++)
        {
            if (!data.Slice(i * copyLength, copyLength).SequenceEqual(first))
            {
                return false;
            }
        }

        return true;
    }

    private static DecodedParameterPage DecodeOnfi(PageCopy copy, ReadOnlySpan<byte> all)
    {
        var bytes = copy.Bytes;
        var revision = ReadU16(bytes, 4);
        var names = OnfiPublic.RevisionNames(revision, out var unnamed);
        var features = ReadU16(bytes, 6);
        var notes = new List<string>();
        if (unnamed.Count > 0)
        {
            notes.Add("修订字含本程序所引用的 ONFI 5.0 表未命名的位：" + string.Join('、', unnamed) + "。这些位不命名。");
        }

        var ecc = bytes[112];
        var eccDefined = ecc != 0xFF;
        if (!eccDefined)
        {
            notes.Add("偏移 112 为 FFh。公开 ONFI 表示此字段不给出纠错位数，不换算成具体位数。");
        }

        var extendedLength = ReadU16(bytes, 12);
        int? declaredCopies = bytes[14];
        string? extendedNote = null;
            if (extendedLength > 0)
            {
                var start = bytes[14] * OnfiPublic.CopyLength;
                if (start > 0 && all.Length >= start + 6)
                {
                    var signatureOk = all.Slice(start + 2, 4).SequenceEqual(OnfiPublic.ExtendedSignature);
                var crcOk = false;
                if (all.Length >= start + extendedLength && extendedLength > 2)
                {
                    var crc = Crc16.Onfi(all.Slice(start + 2, extendedLength - 2));
                    crcOk = Crc16.StoredLittleEndianMatches(all[start..], 0, crc);
                }

                extendedNote = signatureOk
                    ? (crcOk ? "扩展参数页签名 EPPS，CRC-16 通过。扩展区的厂商段不在公开布局里自动展开。" : "扩展参数页签名是 EPPS，CRC-16 未通过，不读取其中的数值。")
                    : "参数页声明有扩展区，但 EPPS 签名未出现，不读取扩展区。";
            }
            else
            {
                extendedNote = "偏移 12 声明了扩展参数页，但本次字节没有覆盖该区域，不外推内容。";
            }
        }

        return new DecodedParameterPage
        {
            Layout = "onfi-public",
            Bytes = bytes,
            CopyIndex = copy.Index,
            Acceptance = copy.CrcNote,
            Manufacturer = Ascii(bytes, 32, 12),
            Model = Ascii(bytes, 44, 20),
            JedecManufacturer = bytes[64],
            Revision = revision,
            RevisionNames = names,
            UnnamedRevisionBits = unnamed,
            Features = features,
            Interfaces = OnfiPublic.FeatureInterfaces(features),
            PageDataBytes = ReadU32(bytes, 80),
            SpareBytes = ReadU16(bytes, 84),
            PagesPerBlock = ReadU32(bytes, 92),
            BlocksPerLun = ReadU32(bytes, 96),
            Luns = bytes[100],
            BitsPerCell = bytes[102],
            EccBits = eccDefined ? ecc : null,
            EccFieldDefined = eccDefined,
            SdrTimingModes = OnfiPublic.SetBits(bytes.AsSpan(129, 2)),
            ExtendedLength = extendedLength,
            DeclaredCopies = declaredCopies,
            ExtendedNote = extendedNote,
            Notes = notes
        };
    }

    private static DecodedParameterPage DecodeJedec(PageCopy copy)
    {
        var bytes = copy.Bytes;
        return new DecodedParameterPage
        {
            Layout = "jedec-public",
            Bytes = bytes,
            CopyIndex = copy.Index,
            Acceptance = copy.CrcNote,
            Manufacturer = Ascii(bytes, 32, 12),
            Model = Ascii(bytes, 44, 20),
            JedecManufacturer = bytes[64],
            PageDataBytes = ReadU32(bytes, 80),
            SpareBytes = ReadU16(bytes, 84),
            PagesPerBlock = ReadU32(bytes, 92),
            BlocksPerLun = ReadU32(bytes, 96),
            Luns = bytes[100],
            BitsPerCell = bytes[102],
            EccFieldDefined = false,
            Notes = ["JEDEC 参数页组织块偏移与公开 JESD230 中和 ONFI 对齐的字段一致。纠错位的偏移不从 ONFI 的 112 套用，须由参数卡给出。"]
        };
    }

    public static string Ascii(byte[] bytes, int offset, int length)
    {
        var slice = bytes.AsSpan(offset, length);
        var end = slice.Length;
        while (end > 0 && (slice[end - 1] == 0x20 || slice[end - 1] == 0x00))
        {
            end--;
        }

        return System.Text.Encoding.ASCII.GetString(slice[..end]);
    }

    public static ushort ReadU16(byte[] bytes, int offset) => (ushort)(bytes[offset] | (bytes[offset + 1] << 8));

    public static uint ReadU32(byte[] bytes, int offset) =>
        (uint)(bytes[offset] | (bytes[offset + 1] << 8) | (bytes[offset + 2] << 16) | (bytes[offset + 3] << 24));
}

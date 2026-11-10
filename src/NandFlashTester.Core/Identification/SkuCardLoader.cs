using System.Text.Json;
using System.Text.Json.Serialization;

namespace NandFlashTester.Core.Identification;

public sealed class SkuLoadResult
{
    public IReadOnlyList<SkuCard> Cards { get; init; } = [];
    public IReadOnlyList<string> Errors { get; init; } = [];
    public string Directory { get; init; } = "";
}

public static class SkuCardLoader
{
    public const string SchemaVersion = "1";
    public const int MaxCardBytes = 256 * 1024;
    public const int MaxCards = 64;

    private static readonly JsonSerializerOptions Json = new(JsonSerializerDefaults.Web)
    {
        UnmappedMemberHandling = JsonUnmappedMemberHandling.Disallow,
        ReadCommentHandling = JsonCommentHandling.Disallow,
        AllowTrailingCommas = false,
        NumberHandling = JsonNumberHandling.Strict
    };

    public static string ResolveDirectory(string? configured)
    {
        if (!string.IsNullOrWhiteSpace(configured))
        {
            return configured;
        }

        var env = Environment.GetEnvironmentVariable("NFA1000_SKU_DIR");
        if (!string.IsNullOrWhiteSpace(env))
        {
            return env;
        }

        var root = Environment.GetFolderPath(Environment.SpecialFolder.CommonApplicationData);
        if (string.IsNullOrWhiteSpace(root))
        {
            root = Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData);
        }

        return Path.Combine(string.IsNullOrWhiteSpace(root) ? "." : root, "NFA1000", "sku-cards");
    }

    public static SkuLoadResult LoadDirectory(string? directory)
    {
        var path = ResolveDirectory(directory);
        if (!Directory.Exists(path))
        {
            return new SkuLoadResult { Directory = path, Errors = [$"参数卡目录不存在：{path}"] };
        }

        var errors = new List<string>();
        var cards = new List<SkuCard>();
        foreach (var file in Directory.EnumerateFiles(path, "*.json").OrderBy(item => item, StringComparer.OrdinalIgnoreCase))
        {
            if (cards.Count >= MaxCards)
            {
                errors.Add($"参数卡超过 {MaxCards} 份，其余文件未载入。");
                break;
            }

            try
            {
                var info = new FileInfo(file);
                if (info.Length > MaxCardBytes)
                {
                    errors.Add($"{info.Name} 超过 {MaxCardBytes} 字节，未载入。");
                    continue;
                }

                cards.Add(Parse(File.ReadAllText(file), info.Name));
            }
            catch (Exception ex) when (ex is JsonException or InvalidDataException or InvalidOperationException)
            {
                errors.Add($"{Path.GetFileName(file)}：{ex.Message}");
            }
        }

        return new SkuLoadResult { Directory = path, Cards = cards, Errors = errors };
    }

    public static SkuCard Parse(string json, string name = "参数卡")
    {
        SkuCardDocument? document;
        try
        {
            document = JsonSerializer.Deserialize<SkuCardDocument>(json, Json);
        }
        catch (JsonException ex)
        {
            throw new InvalidDataException($"{name} 不符合参数卡结构：{ex.Message}");
        }

        if (document is null)
        {
            throw new InvalidDataException($"{name} 是空的。");
        }

        if (document.SchemaVersion != SchemaVersion)
        {
            throw new InvalidDataException($"{name} 的 schemaVersion 必须是 {SchemaVersion}。");
        }

        if (string.IsNullOrWhiteSpace(document.CardId) || document.CardId.Length > 64 || !document.CardId.All(ch => char.IsAsciiLetterOrDigit(ch) || ch is '-' or '_'))
        {
            throw new InvalidDataException($"{name} 的 cardId 只能用字母、数字、连字符。");
        }

        var parts = new List<SkuPart>();
        foreach (var part in document.Parts ?? [])
        {
            parts.Add(new SkuPart
            {
                PartNumber = BlankToNull(part.PartNumber),
                PartNumberKnown = part.PartNumberKnown,
                Notes = BlankToNull(part.Notes),
                Id00 = OptionalHex(part.Id00Hex, $"{name} 料号 ID"),
                ModelNames = part.ModelNames ?? []
            });
        }

        var addresses = new List<ReadIdAddress>();
        foreach (var address in document.Commands?.ReadId?.Addresses ?? [])
        {
            if (address.Address is not ("00" or "20" or "40"))
            {
                throw new InvalidDataException($"{name} 的 Read ID 地址只能是 00、20 或 40。");
            }

            addresses.Add(new ReadIdAddress
            {
                Address = address.Address,
                Supported = address.Supported,
                LengthBytes = address.LengthBytes,
                LengthUncertain = address.LengthUncertain,
                Expected = OptionalHex(address.ExpectedHex, $"{name} 地址 {address.Address}"),
                Signature = BlankToNull(address.Signature)
            });
        }

        var bitFields = new List<IdBitField>();
        foreach (var field in document.IdBitFields ?? [])
        {
            if (field.Shift is < 0 or > 7 || field.Mask == 0)
            {
                throw new InvalidDataException($"{name} 的 ID 位域掩码或移位无效。");
            }

            bitFields.Add(new IdBitField
            {
                Address = field.Address,
                ByteIndex = field.ByteIndex,
                Mask = field.Mask,
                Shift = field.Shift,
                Field = field.Field,
                Incomplete = field.Incomplete,
                Note = BlankToNull(field.Note),
                Maps = (field.Maps ?? []).Select(map => new BitValue { Value = map.Value, Text = BlankToNull(map.Text), Number = map.Number }).ToList()
            });
        }

        var crcDoc = document.Commands?.ParameterPage?.Crc;
        var crc = new CardCrc
        {
            Mode = crcDoc?.Mode ?? "none",
            Polynomial = crcDoc?.Polynomial is null ? null : checked((ushort)crcDoc.Polynomial.Value),
            Init = crcDoc?.Init is null ? null : checked((ushort)crcDoc.Init.Value),
            ReflectIn = crcDoc?.ReflectIn ?? false,
            ReflectOut = crcDoc?.ReflectOut ?? false,
            XorOut = crcDoc?.XorOut is null ? (ushort)0 : checked((ushort)crcDoc.XorOut.Value),
            CoverageStart = crcDoc?.CoverageStart,
            CoverageEndExclusive = crcDoc?.CoverageEndExclusive,
            StoredOffset = crcDoc?.StoredOffset,
            Citation = BlankToNull(crcDoc?.Citation)
        };
        if (crc.Mode is not ("none" or "onfi-public" or "card"))
        {
            throw new InvalidDataException($"{name} 的 CRC mode 只能是 none、onfi-public 或 card。");
        }

        byte? manufacturer = null;
        if (!string.IsNullOrWhiteSpace(document.ManufacturerIdHex))
        {
            var parsed = OptionalHex(document.ManufacturerIdHex, $"{name} 厂商字节");
            if (parsed is not { Length: 1 })
            {
                throw new InvalidDataException($"{name} 的厂商字节必须是一个字节。");
            }

            manufacturer = parsed[0];
        }

        return new SkuCard
        {
            CardId = document.CardId,
            Vendor = BlankToNull(document.Vendor),
            VendorLegalNameKnown = document.VendorLegalNameKnown,
            Family = BlankToNull(document.Family),
            DatasheetTitle = BlankToNull(document.Datasheet?.Title),
            DatasheetRevision = BlankToNull(document.Datasheet?.Revision),
            DatasheetStatus = BlankToNull(document.Datasheet?.Status),
            DatasheetPageNote = BlankToNull(document.Datasheet?.PageNote),
            ManufacturerId = manufacturer,
            Parts = parts,
            InterfaceStandard = BlankToNull(document.InterfaceClaim?.Standard),
            InterfaceVersion = BlankToNull(document.InterfaceClaim?.Version),
            DataInterfaces = document.InterfaceClaim?.DataInterfaces ?? [],
            InterfaceConflicts = document.InterfaceClaim?.Conflicts ?? [],
            CellType = BlankToNull(document.CellType),
            ResetRequiredFirst = document.Commands?.Reset?.RequiredFirst,
            ResetOpcodes = (document.Commands?.Reset?.Opcodes ?? []).Select(ParseOpcode).ToList(),
            ResetPorMaxMs = document.Commands?.Reset?.TPorMaxMs,
            ReadIdAddresses = addresses,
            UniqueIdSupported = document.Commands?.UniqueId?.Supported,
            UniqueIdOpcode = document.Commands?.UniqueId?.Opcode is null ? null : ParseOpcode(document.Commands.UniqueId.Opcode),
            UniqueIdLayout = document.Commands?.UniqueId?.Layout ?? "card",
            UniqueIdCopies = document.Commands?.UniqueId?.Copies,
            UniqueIdCopyBytes = document.Commands?.UniqueId?.CopyBytes,
            UniqueIdBytes = document.Commands?.UniqueId?.UidBytes,
            ParameterPageSupported = document.Commands?.ParameterPage?.Supported,
            ParameterPageOpcode = document.Commands?.ParameterPage?.Opcode is null ? (byte)0xEC : ParseOpcode(document.Commands.ParameterPage.Opcode),
            ParameterPageAddresses = (document.Commands?.ParameterPage?.Addresses ?? []).Select(ParseOpcode).ToList(),
            ParameterPageLayout = document.Commands?.ParameterPage?.Layout ?? "unknown",
            CopyLength = document.Commands?.ParameterPage?.CopyLength,
            MinCopies = document.Commands?.ParameterPage?.MinCopies,
            Crc = crc,
            AcceptIdenticalCopiesWithoutCrc = document.Commands?.ParameterPage?.AcceptIdenticalCopiesWithoutCrc ?? false,
            StatusProcedure = BlankToNull(document.Commands?.ParameterPage?.StatusProcedure),
            StatusPollCapUs = document.Commands?.ParameterPage?.StatusPollCapUs,
            TimingPhases = (document.Timings?.Phases ?? []).Select(phase => new TimingPhase
            {
                Name = phase.Name,
                TWhrMinNs = phase.TWhrMinNs,
                TWhrtMinNs = phase.TWhrtMinNs,
                TRrMinNs = phase.TRrMinNs,
                TAdlMinNs = phase.TAdlMinNs,
                TRstReadUs = phase.TRstReadUs,
                TPorMaxMs = phase.TPorMaxMs
            }).ToList(),
            ParameterPageBusy = BlankToNull(document.Timings?.ParameterPageBusy),
            ParameterPageBusyNote = BlankToNull(document.Timings?.ParameterPageBusyNote),
            Geometry = MapGeometry(document.Geometry),
            IdBitFields = bitFields,
            TimingModeFields = (document.TimingModeFields ?? []).Select(field => new TimingModeField { Name = field.Name, Offset = field.Offset, Length = field.Length }).ToList(),
            Conflicts = (document.Conflicts ?? []).Select(item => new CardConflict { Topic = item.Topic, Detail = item.Detail, BlocksAutoMatch = item.BlocksAutoMatch }).ToList(),
            Pending = (document.Pending ?? []).Select(item => new PendingItem { Field = item.Field, Reason = item.Reason }).ToList(),
            Vcc = BlankToNull(document.Electrical?.Vcc),
            Vccq = BlankToNull(document.Electrical?.Vccq),
            TemperatureText = BlankToNull(document.Temperature?.RangeText)
        };
    }

    private static GeometryClaim MapGeometry(GeometryDocument? geometry) => geometry is null
        ? new GeometryClaim()
        : new GeometryClaim
        {
            Source = string.IsNullOrWhiteSpace(geometry.Source) ? "unknown" : geometry.Source,
            PageDataBytes = geometry.PageDataBytes,
            SpareBytes = geometry.SpareBytes,
            SpareConflicts = geometry.SpareConflicts ?? [],
            PagesPerBlock = geometry.PagesPerBlock,
            BlocksPerLun = geometry.BlocksPerLun,
            LunsPerCe = geometry.LunsPerCe,
            BitsPerCell = geometry.BitsPerCell,
            EccBits = geometry.EccBits,
            EccCodewordBytes = geometry.EccCodewordBytes,
            Planes = geometry.Planes,
            PageNote = BlankToNull(geometry.PageNote)
        };

    private static byte[]? OptionalHex(string? text, string label)
    {
        if (string.IsNullOrWhiteSpace(text))
        {
            return null;
        }

        if (!HexText.TryParse(text, out var bytes, out var error) || bytes.Length == 0)
        {
            throw new InvalidDataException($"{label}：{error ?? "空字节"}");
        }

        return bytes;
    }

    private static byte ParseOpcode(string text)
    {
        if (!HexText.TryParse(text, out var bytes, out var error) || bytes.Length != 1)
        {
            throw new InvalidDataException($"命令字节必须是一个字节：{error ?? text}");
        }

        return bytes[0];
    }

    private static string? BlankToNull(string? text) => string.IsNullOrWhiteSpace(text) ? null : text.Trim();

    private sealed class SkuCardDocument
    {
        public required string SchemaVersion { get; init; }
        public required string CardId { get; init; }
        public string? Vendor { get; init; }
        public bool VendorLegalNameKnown { get; init; }
        public string? Family { get; init; }
        public DatasheetDocument? Datasheet { get; init; }
        public List<PartDocument>? Parts { get; init; }
        public string? ManufacturerIdHex { get; init; }
        public InterfaceDocument? InterfaceClaim { get; init; }
        public string? CellType { get; init; }
        public CommandsDocument? Commands { get; init; }
        public TimingsDocument? Timings { get; init; }
        public GeometryDocument? Geometry { get; init; }
        public List<BitFieldDocument>? IdBitFields { get; init; }
        public List<TimingModeDocument>? TimingModeFields { get; init; }
        public List<ConflictDocument>? Conflicts { get; init; }
        public List<PendingDocument>? Pending { get; init; }
        public ElectricalDocument? Electrical { get; init; }
        public TemperatureDocument? Temperature { get; init; }
    }

    private sealed class DatasheetDocument
    {
        public string? Title { get; init; }
        public string? Revision { get; init; }
        public string? Date { get; init; }
        public string? Status { get; init; }
        public string? PageNote { get; init; }
    }

    private sealed class PartDocument
    {
        public string? PartNumber { get; init; }
        public bool PartNumberKnown { get; init; }
        public string? Notes { get; init; }
        public string? Id00Hex { get; init; }
        public List<string>? ModelNames { get; init; }
    }

    private sealed class InterfaceDocument
    {
        public string? Standard { get; init; }
        public string? Version { get; init; }
        public List<string>? DataInterfaces { get; init; }
        public List<string>? Conflicts { get; init; }
    }

    private sealed class CommandsDocument
    {
        public ResetDocument? Reset { get; init; }
        public ReadIdDocument? ReadId { get; init; }
        public UniqueDocument? UniqueId { get; init; }
        public ParameterDocument? ParameterPage { get; init; }
    }

    private sealed class ResetDocument
    {
        public bool? RequiredFirst { get; init; }
        public List<string>? Opcodes { get; init; }
        public int? TPorMaxMs { get; init; }
    }

    private sealed class ReadIdDocument
    {
        public List<AddressDocument>? Addresses { get; init; }
    }

    private sealed class AddressDocument
    {
        public required string Address { get; init; }
        public bool? Supported { get; init; }
        public int? LengthBytes { get; init; }
        public bool LengthUncertain { get; init; }
        public string? ExpectedHex { get; init; }
        public string? Signature { get; init; }
    }

    private sealed class UniqueDocument
    {
        public bool? Supported { get; init; }
        public string? Opcode { get; init; }
        public string? Address { get; init; }
        public string? Layout { get; init; }
        public int? Copies { get; init; }
        public int? CopyBytes { get; init; }
        public int? UidBytes { get; init; }
    }

    private sealed class ParameterDocument
    {
        public bool? Supported { get; init; }
        public string? Opcode { get; init; }
        public List<string>? Addresses { get; init; }
        public string? Layout { get; init; }
        public int? CopyLength { get; init; }
        public int? MinCopies { get; init; }
        public CrcDocument? Crc { get; init; }
        public bool AcceptIdenticalCopiesWithoutCrc { get; init; }
        public string? StatusProcedure { get; init; }
        public int? StatusPollCapUs { get; init; }
    }

    private sealed class CrcDocument
    {
        public string? Mode { get; init; }
        public int? Polynomial { get; init; }
        public int? Init { get; init; }
        public bool ReflectIn { get; init; }
        public bool ReflectOut { get; init; }
        public int? XorOut { get; init; }
        public int? CoverageStart { get; init; }
        public int? CoverageEndExclusive { get; init; }
        public int? StoredOffset { get; init; }
        public string? Citation { get; init; }
    }

    private sealed class TimingsDocument
    {
        public List<PhaseDocument>? Phases { get; init; }
        public string? ParameterPageBusy { get; init; }
        public string? ParameterPageBusyNote { get; init; }
    }

    private sealed class PhaseDocument
    {
        public required string Name { get; init; }
        public int? TWhrMinNs { get; init; }
        public int? TWhrtMinNs { get; init; }
        public int? TRrMinNs { get; init; }
        public int? TAdlMinNs { get; init; }
        public int? TRstReadUs { get; init; }
        public int? TPorMaxMs { get; init; }
    }

    private sealed class GeometryDocument
    {
        public string? Source { get; init; }
        public long? PageDataBytes { get; init; }
        public int? SpareBytes { get; init; }
        public List<int>? SpareConflicts { get; init; }
        public long? PagesPerBlock { get; init; }
        public long? BlocksPerLun { get; init; }
        public int? LunsPerCe { get; init; }
        public int? BitsPerCell { get; init; }
        public int? EccBits { get; init; }
        public int? EccCodewordBytes { get; init; }
        public int? Planes { get; init; }
        public string? PageNote { get; init; }
    }

    private sealed class BitFieldDocument
    {
        public required string Address { get; init; }
        public int ByteIndex { get; init; }
        public byte Mask { get; init; }
        public int Shift { get; init; }
        public required string Field { get; init; }
        public bool Incomplete { get; init; }
        public string? Note { get; init; }
        public List<BitMapDocument>? Maps { get; init; }
    }

    private sealed class BitMapDocument
    {
        public int Value { get; init; }
        public string? Text { get; init; }
        public long? Number { get; init; }
    }

    private sealed class TimingModeDocument
    {
        public required string Name { get; init; }
        public int Offset { get; init; }
        public int Length { get; init; }
    }

    private sealed class ConflictDocument
    {
        public required string Topic { get; init; }
        public required string Detail { get; init; }
        public bool BlocksAutoMatch { get; init; }
    }

    private sealed class PendingDocument
    {
        public required string Field { get; init; }
        public required string Reason { get; init; }
    }

    private sealed class ElectricalDocument
    {
        public string? Vcc { get; init; }
        public string? Vccq { get; init; }
        public string? Note { get; init; }
    }

    private sealed class TemperatureDocument
    {
        public string? RangeText { get; init; }
        public string? Note { get; init; }
    }
}

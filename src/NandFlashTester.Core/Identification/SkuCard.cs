namespace NandFlashTester.Core.Identification;

public sealed class CardCrc
{
    public string Mode { get; init; } = "none";
    public ushort? Polynomial { get; init; }
    public ushort? Init { get; init; }
    public bool ReflectIn { get; init; }
    public bool ReflectOut { get; init; }
    public ushort XorOut { get; init; }
    public int? CoverageStart { get; init; }
    public int? CoverageEndExclusive { get; init; }
    public int? StoredOffset { get; init; }
    public string? Citation { get; init; }
}

public sealed class SkuPart
{
    public string? PartNumber { get; init; }
    public bool PartNumberKnown { get; init; }
    public string? Notes { get; init; }
    public byte[]? Id00 { get; init; }
    public IReadOnlyList<string> ModelNames { get; init; } = [];
}

public sealed class ReadIdAddress
{
    public required string Address { get; init; }
    public bool? Supported { get; init; }
    public int? LengthBytes { get; init; }
    public bool LengthUncertain { get; init; }
    public byte[]? Expected { get; init; }
    public string? Signature { get; init; }
}

public sealed class IdBitField
{
    public required string Address { get; init; }
    public int ByteIndex { get; init; }
    public byte Mask { get; init; }
    public int Shift { get; init; }
    public required string Field { get; init; }
    public bool Incomplete { get; init; }
    public string? Note { get; init; }
    public IReadOnlyList<BitValue> Maps { get; init; } = [];
}

public sealed class BitValue
{
    public int Value { get; init; }
    public string? Text { get; init; }
    public long? Number { get; init; }
}

public sealed class TimingPhase
{
    public required string Name { get; init; }
    public int? TWhrMinNs { get; init; }
    public int? TWhrtMinNs { get; init; }
    public int? TRrMinNs { get; init; }
    public int? TAdlMinNs { get; init; }
    public int? TRstReadUs { get; init; }
    public int? TPorMaxMs { get; init; }
}

public sealed class TimingModeField
{
    public required string Name { get; init; }
    public int Offset { get; init; }
    public int Length { get; init; }
}

public sealed class GeometryClaim
{
    public string Source { get; init; } = "unknown";
    public long? PageDataBytes { get; init; }
    public int? SpareBytes { get; init; }
    public IReadOnlyList<int> SpareConflicts { get; init; } = [];
    public long? PagesPerBlock { get; init; }
    public long? BlocksPerLun { get; init; }
    public int? LunsPerCe { get; init; }
    public int? BitsPerCell { get; init; }
    public int? EccBits { get; init; }
    public int? EccCodewordBytes { get; init; }
    public int? Planes { get; init; }
    public string? PageNote { get; init; }
}

public sealed class CardConflict
{
    public required string Topic { get; init; }
    public required string Detail { get; init; }
    public bool BlocksAutoMatch { get; init; }
}

public sealed class PendingItem
{
    public required string Field { get; init; }
    public required string Reason { get; init; }
}

public sealed class SkuCard
{
    public required string CardId { get; init; }
    public string? Vendor { get; init; }
    public bool VendorLegalNameKnown { get; init; }
    public string? Family { get; init; }
    public string? DatasheetTitle { get; init; }
    public string? DatasheetRevision { get; init; }
    public string? DatasheetStatus { get; init; }
    public string? DatasheetPageNote { get; init; }
    public byte? ManufacturerId { get; init; }
    public IReadOnlyList<SkuPart> Parts { get; init; } = [];
    public string? InterfaceStandard { get; init; }
    public string? InterfaceVersion { get; init; }
    public IReadOnlyList<string> DataInterfaces { get; init; } = [];
    public IReadOnlyList<string> InterfaceConflicts { get; init; } = [];
    public string? CellType { get; init; }
    public bool? ResetRequiredFirst { get; init; }
    public IReadOnlyList<byte> ResetOpcodes { get; init; } = [];
    public int? ResetPorMaxMs { get; init; }
    public IReadOnlyList<ReadIdAddress> ReadIdAddresses { get; init; } = [];
    public bool? UniqueIdSupported { get; init; }
    public byte? UniqueIdOpcode { get; init; }
    public string UniqueIdLayout { get; init; } = "card";
    public int? UniqueIdCopies { get; init; }
    public int? UniqueIdCopyBytes { get; init; }
    public int? UniqueIdBytes { get; init; }
    public bool? ParameterPageSupported { get; init; }
    public byte ParameterPageOpcode { get; init; } = 0xEC;
    public IReadOnlyList<byte> ParameterPageAddresses { get; init; } = [];
    public string ParameterPageLayout { get; init; } = "unknown";
    public int? CopyLength { get; init; }
    public int? MinCopies { get; init; }
    public CardCrc Crc { get; init; } = new();
    public bool AcceptIdenticalCopiesWithoutCrc { get; init; }
    public string? StatusProcedure { get; init; }
    public int? StatusPollCapUs { get; init; }
    public IReadOnlyList<TimingPhase> TimingPhases { get; init; } = [];
    public string? ParameterPageBusy { get; init; }
    public string? ParameterPageBusyNote { get; init; }
    public GeometryClaim Geometry { get; init; } = new();
    public IReadOnlyList<IdBitField> IdBitFields { get; init; } = [];
    public IReadOnlyList<TimingModeField> TimingModeFields { get; init; } = [];
    public IReadOnlyList<CardConflict> Conflicts { get; init; } = [];
    public IReadOnlyList<PendingItem> Pending { get; init; } = [];
    public string? Vcc { get; init; }
    public string? Vccq { get; init; }
    public string? TemperatureText { get; init; }

    public string DatasheetLabel => string.IsNullOrWhiteSpace(DatasheetRevision) ? "资料版本未给出" : DatasheetRevision;

    public TimingPhase? Phase(string name) => TimingPhases.FirstOrDefault(phase => phase.Name == name);

    public ReadIdAddress? Address(string address) => ReadIdAddresses.FirstOrDefault(item => item.Address.Equals(address, StringComparison.OrdinalIgnoreCase));
}

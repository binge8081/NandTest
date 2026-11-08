namespace NandFlashTester.Api.Materials;

public sealed record ParameterImportRequest(string? Key, string? Version, string? DocumentId, string? Chapter, string? ProposedValue);

public sealed record RawBitErrorRequest(string? Source, long? ComparedBits, long? ErrorBits);

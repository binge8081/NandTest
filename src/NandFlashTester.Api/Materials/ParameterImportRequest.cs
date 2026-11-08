namespace NandFlashTester.Api.Materials;

public sealed record ParameterImportRequest(string? Key, string? Version, string? DocumentId, string? Chapter, string? ProposedValue);

using CodeIndex.Models;

namespace CodeIndex.Indexer.Extensibility;

public sealed record ExtractionContext(
    string Language,
    string? FilePath,
    IReadOnlyList<SymbolRecord>? FileSymbols = null,
    IReadOnlyList<SymbolRecord>? WorkspaceSymbols = null,
    int? MaxReferenceCount = null);

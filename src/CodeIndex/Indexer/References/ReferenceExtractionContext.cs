using CodeIndex.Models;

namespace CodeIndex.Indexer;

/// <summary>
/// Immutable input for a language-specific reference extraction pass.
/// 言語別参照抽出 1 回分の不変入力。
/// </summary>
public sealed record ReferenceExtractionContext(
    long FileId,
    string Language,
    string Content,
    IReadOnlyList<SymbolRecord> Symbols,
    string? Path = null,
    IReadOnlyList<SymbolRecord>? WorkspaceSymbols = null,
    string? RequestedLanguage = null,
    CancellationToken CancellationToken = default,
    int? MaxReferenceCount = null,
    Action<ReferenceExtractionDiagnostic>? ReportDiagnostic = null);

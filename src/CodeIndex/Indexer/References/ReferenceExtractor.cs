using System.Text;
using System.Text.RegularExpressions;
using Regex = CodeIndex.Indexer.BoundedRegex;
using CodeIndex.Indexer.Extensibility;
using CodeIndex.Models;

namespace CodeIndex.Indexer;

public sealed record ReferenceExtractionDiagnostic(string Kind, string Message);

public sealed record ReferenceExtractionResult(
    List<ReferenceRecord> References,
    IReadOnlyList<ReferenceExtractionDiagnostic> Diagnostics);

internal readonly record struct ReferenceDedupeKey(
    long FileId,
    string Language,
    int LineNumber,
    int Column,
    string ReferenceKind,
    string ContainerKind,
    string ContainerName,
    string Name)
{
    public override string ToString() =>
        $"{FileId}:{Language}:{LineNumber}:{Column}:{ReferenceKind}:{ContainerKind}:{ContainerName}:{Name}";
}

internal sealed class ReferenceDedupeSet
{
    // Keep identity components as values/references already owned by extracted records.
    // Concatenating them into one string per candidate dominates allocations for dense
    // generated files with long qualified names.
    private readonly HashSet<ReferenceDedupeKey> _keys;

    internal ReferenceDedupeSet(int capacity = 0)
    {
        _keys = capacity > 0 ? new HashSet<ReferenceDedupeKey>(capacity) : [];
    }

    internal bool Add(ReferenceDedupeKey key) => _keys.Add(key);

    internal bool Contains(ReferenceDedupeKey key) => _keys.Contains(key);
}

/// <summary>
/// Extracts lightweight symbol references such as call sites.
/// 軽量なシンボル参照（呼び出し箇所など）を抽出する。
/// </summary>
public static partial class ReferenceExtractor
{
    public static List<ReferenceRecord> Extract(
        long fileId,
        string? lang,
        string content,
        IReadOnlyList<SymbolRecord> symbols,
        string? path = null,
        IReadOnlyList<SymbolRecord>? workspaceSymbols = null,
        CancellationToken cancellationToken = default,
        int? maxReferenceCount = null)
        => ExtractDetailed(
            fileId,
            lang,
            content,
            symbols,
            path,
            workspaceSymbols,
            cancellationToken,
            maxReferenceCount).References;

    internal static List<ReferenceRecord> ExtractNormalized(
        long fileId,
        string? lang,
        string content,
        bool hasOversizeLine,
        IReadOnlyList<SymbolRecord> symbols,
        string? path = null,
        IReadOnlyList<SymbolRecord>? workspaceSymbols = null,
        CancellationToken cancellationToken = default,
        int? maxReferenceCount = null,
        int? conflictMarkerLine = null,
        string? workspaceRoot = null)
        => ExtractDetailedNormalized(
            fileId,
            lang,
            content,
            hasOversizeLine,
            symbols,
            path,
            workspaceSymbols,
            cancellationToken,
            maxReferenceCount,
            conflictMarkerLine,
            workspaceRoot).References;

    public static ReferenceExtractionResult ExtractDetailed(
        long fileId,
        string? lang,
        string content,
        IReadOnlyList<SymbolRecord> symbols,
        string? path = null,
        IReadOnlyList<SymbolRecord>? workspaceSymbols = null,
        CancellationToken cancellationToken = default,
        int? maxReferenceCount = null)
        => ExtractDetailedCore(
            fileId,
            lang,
            content,
            contentIsNormalized: false,
            hasOversizeLine: null,
            conflictMarkerLine: null,
            symbols,
            path,
            workspaceSymbols,
            cancellationToken,
            maxReferenceCount,
            workspaceRoot: null,
            csharpStaticInterfaceMemberLookups: null);

    internal static ReferenceExtractionResult ExtractDetailedNormalized(
        long fileId,
        string? lang,
        string content,
        bool hasOversizeLine,
        IReadOnlyList<SymbolRecord> symbols,
        string? path = null,
        IReadOnlyList<SymbolRecord>? workspaceSymbols = null,
        CancellationToken cancellationToken = default,
        int? maxReferenceCount = null,
        int? conflictMarkerLine = null,
        string? workspaceRoot = null,
        CSharpStaticInterfaceMemberLookups? csharpStaticInterfaceMemberLookups = null)
        => ExtractDetailedCore(
            fileId,
            lang,
            content,
            contentIsNormalized: true,
            hasOversizeLine,
            conflictMarkerLine,
            symbols,
            path,
            workspaceSymbols,
            cancellationToken,
            maxReferenceCount,
            workspaceRoot,
            csharpStaticInterfaceMemberLookups);

    private static ReferenceExtractionResult ExtractDetailedCore(
        long fileId,
        string? lang,
        string content,
        bool contentIsNormalized,
        bool? hasOversizeLine,
        int? conflictMarkerLine,
        IReadOnlyList<SymbolRecord> symbols,
        string? path,
        IReadOnlyList<SymbolRecord>? workspaceSymbols,
        CancellationToken cancellationToken,
        int? maxReferenceCount,
        string? workspaceRoot,
        CSharpStaticInterfaceMemberLookups? csharpStaticInterfaceMemberLookups)
    {
        cancellationToken.ThrowIfCancellationRequested();
        var requestedLanguage = lang;
        if (!TryGetExtractor(lang, out var extractor, out var normalizedLanguage))
        {
            var pluginLanguage = NormalizePluginLanguage(lang);
            if (pluginLanguage == null || !ExtractorPluginRegistry.TryGetReferenceExtractor(pluginLanguage, workspaceRoot, path, out var pluginExtractor))
                return new ReferenceExtractionResult([], []);

            if (string.IsNullOrEmpty(content))
                return new ReferenceExtractionResult([], []);
            if (hasOversizeLine ?? ChunkSplitter.HasOversizeLine(content))
                return new ReferenceExtractionResult([], []);
            if (!contentIsNormalized)
            {
                content = FileIndexer.NormalizeContentForPrepass(content);
            }
            cancellationToken.ThrowIfCancellationRequested();

            var pluginReferences = pluginExtractor.Extract(
                fileId,
                content,
                new ExtractionContext(pluginLanguage, path, symbols, workspaceSymbols, maxReferenceCount));
            var references = CopyPluginReferencesWithinLimit(pluginReferences, maxReferenceCount, out var truncated);
            IReadOnlyList<ReferenceExtractionDiagnostic> diagnostics = truncated
                ? [
                    new ReferenceExtractionDiagnostic(
                        "plugin_reference_count_truncated",
                        $"Plugin reference extraction for language '{pluginLanguage}' produced {pluginReferences.Count:N0} references, exceeding the materialization budget of {maxReferenceCount!.Value:N0}; only the first {maxReferenceCount.Value:N0} references were retained."),
                ]
                : [];
            return new ReferenceExtractionResult(references, diagnostics);
        }

        var language = normalizedLanguage!;
        List<ReferenceExtractionDiagnostic>? builtInDiagnostics = null;
        void ReportDiagnostic(ReferenceExtractionDiagnostic diagnostic)
            => (builtInDiagnostics ??= []).Add(diagnostic);

        var extractionContext = new ReferenceExtractionContext(
            fileId,
            language,
            content,
            symbols,
            path,
            workspaceSymbols,
            requestedLanguage,
            cancellationToken,
            maxReferenceCount,
            ReportDiagnostic,
            contentIsNormalized,
            hasOversizeLine,
            conflictMarkerLine)
        {
            CSharpStaticInterfaceMemberLookups = csharpStaticInterfaceMemberLookups,
        };
        var builtInReferences = extractor.Extract(extractionContext);
        return new ReferenceExtractionResult(
            builtInReferences,
            builtInDiagnostics ?? (IReadOnlyList<ReferenceExtractionDiagnostic>)Array.Empty<ReferenceExtractionDiagnostic>());
    }

    private static List<ReferenceRecord> CopyPluginReferencesWithinLimit(
        IReadOnlyList<ReferenceRecord> references,
        int? maxReferenceCount,
        out bool truncated)
    {
        if (maxReferenceCount is not { } limit)
        {
            truncated = false;
            return CopyPluginReferences(references, references.Count);
        }

        if (limit <= 0)
        {
            truncated = references.Count > 0;
            return [];
        }

        var retainedCount = Math.Min(references.Count, limit);
        truncated = references.Count > retainedCount;
        return CopyPluginReferences(references, retainedCount);
    }

    private static List<ReferenceRecord> CopyPluginReferences(
        IReadOnlyList<ReferenceRecord> references,
        int count)
    {
        var copiedReferences = new List<ReferenceRecord>(count);
        for (var i = 0; i < count; i++)
            copiedReferences.Add(references[i]);
        return copiedReferences;
    }

    private sealed class BoundedReferenceList : List<ReferenceRecord>
    {
        internal BoundedReferenceList(int maxReferenceCount, int initialCapacity)
            : base(initialCapacity)
        {
            MaxReferenceCount = maxReferenceCount;
        }

        internal int MaxReferenceCount { get; }
    }

    internal static List<ReferenceRecord> CreateReferenceList(int? maxReferenceCount, int initialCapacity = 0)
    {
        var capacity = Math.Max(0, initialCapacity);
        if (maxReferenceCount is > 0)
            return new BoundedReferenceList(maxReferenceCount.Value, Math.Min(maxReferenceCount.Value, capacity));

        return capacity > 0 ? new List<ReferenceRecord>(capacity) : [];
    }

    private static int EstimateReferenceListInitialCapacity(int lineCount)
    {
        if (lineCount < ReferenceListInitialCapacityLineThreshold)
            return 0;

        return Math.Min(ReferenceListInitialCapacityMax, Math.Max(16, lineCount / 4));
    }

    private static ReferenceDedupeSet CreateReferenceSeenSet(int lineCount)
    {
        var capacity = EstimateReferenceListInitialCapacity(lineCount);
        return new ReferenceDedupeSet(capacity);
    }

    internal static bool ReferenceLimitReached(List<ReferenceRecord> references)
        => references is BoundedReferenceList bounded
            && bounded.Count >= bounded.MaxReferenceCount;

    internal static ReferenceMatchEnumerable EnumerateReferenceMatches(
        Regex regex,
        string input,
        List<ReferenceRecord> references) =>
        new(Regex.EnumerateMatches(regex, input), references);

    internal static ReferenceMatchEnumerable EnumerateReferenceMatches(
        IEnumerable<Match> matches,
        List<ReferenceRecord> references)
        => new(matches, references);

    internal readonly struct ReferenceMatchEnumerable(
        IEnumerable<Match> matches,
        List<ReferenceRecord> references)
    {
        public ReferenceMatchEnumerator GetEnumerator() => new(matches.GetEnumerator(), references);
    }

    internal struct ReferenceMatchEnumerator : IDisposable
    {
        private IEnumerator<Match>? _matches;
        private readonly BoundedReferenceList? _references;

        internal ReferenceMatchEnumerator(
            IEnumerator<Match> matches,
            List<ReferenceRecord> references)
        {
            _matches = matches;
            _references = references as BoundedReferenceList;
        }

        public readonly Match Current => _matches!.Current;

        public readonly bool MoveNext()
        {
            return (_references == null || _references.Count < _references.MaxReferenceCount)
                && _matches!.MoveNext();
        }

        public void Dispose()
        {
            _matches?.Dispose();
            _matches = null;
        }
    }

    internal static bool TryAddReference(List<ReferenceRecord> references, ReferenceRecord reference)
    {
        if (ReferenceLimitReached(references))
            return false;

        if (reference.SpanLength <= 0)
            reference.SpanLength = Math.Max(1, reference.SymbolName.Length);
        references.Add(reference);
        return true;
    }

    private sealed class BuiltInReferenceExtractor(string language) : IReferenceExtractor
    {
        public string Language { get; } = language;

        public List<ReferenceRecord> Extract(ReferenceExtractionContext request)
        {
            if (!string.Equals(request.Language, Language, StringComparison.Ordinal))
                throw new ArgumentException($"Extractor for '{Language}' cannot handle '{request.Language}'.", nameof(request));

            if (IsHdlReferenceLanguage(Language))
                return ExtractHdlReferences(request);

            return ExtractCore(request);
        }
    }


}

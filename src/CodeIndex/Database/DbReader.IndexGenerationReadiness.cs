using CodeIndex.Cli;
using CodeIndex.Indexer;
using CodeIndex.Models;
using Microsoft.Data.Sqlite;

namespace CodeIndex.Database;

internal sealed record PersistedIndexCompletion(
    bool IndexComplete,
    IReadOnlyList<string> IndexIncompleteReasons,
    bool MigrationInProgress,
    PersistedSymbolKindFilterPolicy SymbolKindFilterPolicy);

internal sealed record PersistedSymbolKindFilterPolicy(
    bool ProvenanceAvailable,
    IReadOnlyList<string> Include,
    IReadOnlyList<string> Exclude,
    long? SymbolsDropped)
{
    internal bool IsActive => Include.Count > 0 || Exclude.Count > 0;
}

internal sealed record PersistedIndexGenerationReadiness(
    bool GraphTableAvailable,
    bool GraphDataCurrent,
    bool IndexComplete,
    IReadOnlyList<string> IndexIncompleteReasons,
    bool ReferenceGraphComplete,
    IReadOnlyList<string> ReferenceGraphIncompleteReasons,
    ReferenceExtractionCapHitSummary ReferenceExtractionCapHits,
    bool MigrationInProgress,
    PersistedSymbolKindFilterPolicy SymbolKindFilterPolicy);

public partial class DbReader
{
    internal const string SymbolsOnlyIndexIncompleteReason = "symbols_only_references_omitted";
    internal const string SymbolsOnlyReferenceGraphIncompleteReason =
        DegradationReasonCodes.SymbolsOnlyGraphOmitted;
    internal const string BatchInProgressIncompleteReason = "batch_in_progress";
    internal const string SymbolKindFilterCoverageLimitedReason =
        DegradationReasonCodes.SymbolKindFilterCoverageLimited;
    internal const string SymbolKindFilterProvenanceUnavailableReason =
        DegradationReasonCodes.SymbolKindFilterProvenanceUnavailable;

    private static readonly string[] IndexOmissionIssueKinds =
    [
        "file_too_large",
        "symbol_count_exceeded",
        "reference_count_exceeded",
        .. ReferenceExtractor.ReferenceSafetyCapDiagnosticKinds,
    ];

    internal PersistedIndexGenerationReadiness GetPersistedIndexGenerationReadiness(
        ReferenceExtractionCapHitSummary? referenceExtractionCapHits = null,
        IReadOnlyDictionary<string, long>? indexedLanguages = null,
        bool? hdlGraphContractReady = null,
        SqliteTransaction? transaction = null)
    {
        var indexCompletion = GetPersistedIndexCompletion(transaction);
        var indexComplete = indexCompletion.IndexComplete;
        var indexIncompleteReasons = indexCompletion.IndexIncompleteReasons;
        var migrationInProgress = indexCompletion.MigrationInProgress;

        var capHits = referenceExtractionCapHits ?? GetReferenceExtractionCapHits();
        var languages = indexedLanguages ?? GetIndexedLanguageCounts();
        var dynamicReferenceGraphContractsCurrent =
            AreDynamicReferenceGraphContractsCurrent(languages);
        var symbolsOnlyGraphOmitted = ParseMetaBool(
            TryGetMetaStringInternal(DbContext.SymbolsOnlyGraphOmittedMetaKey)) == true;
        var referenceGraphIncompleteReasons = MergeDistinctReasons(capHits.Reasons);
        if (!_hasReferencesTable)
        {
            AddDistinctReason(
                referenceGraphIncompleteReasons,
                symbolsOnlyGraphOmitted
                    ? SymbolsOnlyReferenceGraphIncompleteReason
                    : DegradationReasonCodes.GraphTableMissing);
        }
        foreach (var reason in indexIncompleteReasons)
        {
            AddDistinctReason(
                referenceGraphIncompleteReasons,
                reason == SymbolsOnlyIndexIncompleteReason
                    ? SymbolsOnlyReferenceGraphIncompleteReason
                    : reason);
        }
        if (!dynamicReferenceGraphContractsCurrent)
        {
            AddDistinctReason(
                referenceGraphIncompleteReasons,
                DynamicReferenceGraphContractStaleReason);
        }

        var referenceGraphComplete = _hasReferencesTable
            && indexComplete
            && capHits.StateAvailable
            && capHits.HitCount == 0
            && dynamicReferenceGraphContractsCurrent;
        var hdlReady = hdlGraphContractReady
            ?? GetHdlGraphContractSignal(
                lang: null,
                pathPatterns: null,
                excludePathPatterns: null,
                excludeTests: false).Ready;

        return new PersistedIndexGenerationReadiness(
            _hasReferencesTable,
            _hasReferencesTable && indexComplete && referenceGraphComplete && hdlReady,
            indexComplete,
            indexIncompleteReasons,
            referenceGraphComplete,
            referenceGraphIncompleteReasons,
            capHits,
            migrationInProgress,
            indexCompletion.SymbolKindFilterPolicy);
    }

    internal PersistedIndexCompletion GetPersistedIndexCompletion(
        SqliteTransaction? transaction = null)
    {
        var indexCompleteness = TryGetMetaStringInternal(DbContext.IndexCompletenessMetaKey);
        var indexIncompleteReasons = MergeDistinctReasons(
            ParseMetaStringList(TryGetMetaStringInternal(DbContext.IndexIncompleteReasonsMetaKey)),
            ReadPersistedIndexOmissionReasons(
                _conn,
                _hasIssuesPhysicalTable,
                ParseMetaBool(TryGetMetaStringInternal(DbContext.SymbolsOnlyGraphOmittedMetaKey)) == true,
                transaction));
        var symbolKindFilterPolicy = GetPersistedSymbolKindFilterPolicy(transaction);
        if (!symbolKindFilterPolicy.ProvenanceAvailable)
        {
            AddDistinctReason(
                indexIncompleteReasons,
                SymbolKindFilterProvenanceUnavailableReason);
        }
        else if (symbolKindFilterPolicy.IsActive)
        {
            AddDistinctReason(
                indexIncompleteReasons,
                SymbolKindFilterCoverageLimitedReason);
        }
        var migrationInProgress = string.Equals(
            TryGetMetaStringInternal(DbContext.BatchInProgressMetaKey),
            "true",
            StringComparison.OrdinalIgnoreCase);
        if (migrationInProgress)
            AddDistinctReason(indexIncompleteReasons, BatchInProgressIncompleteReason);

        var explicitlyIncomplete = string.Equals(
            indexCompleteness,
            "incomplete",
            StringComparison.OrdinalIgnoreCase);
        if (explicitlyIncomplete && indexIncompleteReasons.Count == 0)
            AddDistinctReason(indexIncompleteReasons, DegradationReasonCodes.IndexIncomplete);
        var indexComplete = !migrationInProgress
            && !explicitlyIncomplete
            && indexIncompleteReasons.Count == 0;

        return new PersistedIndexCompletion(
            indexComplete,
            indexIncompleteReasons,
            migrationInProgress,
            symbolKindFilterPolicy);
    }

    internal PersistedSymbolKindFilterPolicy GetPersistedSymbolKindFilterPolicy(
        SqliteTransaction? transaction = null)
    {
        var signature = TryGetMetaStringInternal(DbContext.SymbolKindFilterMetaKey);
        var provenanceAvailable = SymbolKindFilter.TryParsePersistedSignature(
            signature,
            out var filter);
        var auditCurrent = string.Equals(
            TryGetMetaStringInternal(DbContext.SymbolKindFilterAuditVersionMetaKey),
            DbContext.SymbolKindFilterAuditVersion,
            StringComparison.Ordinal);
        long? symbolsDropped = null;
        if (provenanceAvailable
            && (!filter.IsActive || auditCurrent)
            && _fileColumns.Contains(DbContext.SymbolsDroppedByKindFilterColumn))
        {
            using var command = _conn.CreateCommand();
            command.Transaction = transaction;
            command.CommandText = $"""
                SELECT COALESCE(SUM(
                    CASE
                        WHEN {DbContext.SymbolsDroppedByKindFilterColumn} BETWEEN 0 AND 2147483647
                            THEN {DbContext.SymbolsDroppedByKindFilterColumn}
                        ELSE 0
                    END), 0)
                FROM files
                """;
            symbolsDropped = Convert.ToInt64(
                command.ExecuteScalar(),
                System.Globalization.CultureInfo.InvariantCulture);
        }
        else if (provenanceAvailable && !filter.IsActive)
        {
            // An unfiltered generation cannot have policy drops, even when the per-file
            // audit column predates the opened DB.
            // filter 無し generation なら、per-file audit 列が無い旧 DB でも policy drop は 0。
            symbolsDropped = 0;
        }

        return new PersistedSymbolKindFilterPolicy(
            provenanceAvailable,
            provenanceAvailable ? filter.Include : [],
            provenanceAvailable ? filter.Exclude : [],
            symbolsDropped);
    }

    internal static IReadOnlyList<string> ReadPersistedIndexOmissionReasons(
        SqliteConnection connection,
        bool hasIssuesTable,
        bool symbolsOnlyGraphOmitted,
        SqliteTransaction? transaction)
    {
        var reasons = new List<string>();
        if (symbolsOnlyGraphOmitted)
            reasons.Add(SymbolsOnlyIndexIncompleteReason);
        if (!hasIssuesTable)
            return reasons;

        using var command = connection.CreateCommand();
        command.Transaction = transaction;
        var parameterNames = new string[IndexOmissionIssueKinds.Length];
        for (var index = 0; index < IndexOmissionIssueKinds.Length; index++)
        {
            var parameterName = $"@omissionKind{index}";
            parameterNames[index] = parameterName;
            SqliteCommandPolicy.Add(command, parameterName, IndexOmissionIssueKinds[index]);
        }
        command.CommandText = $"""
            SELECT kind
            FROM file_issues
            WHERE kind IN ({string.Join(", ", parameterNames)})
            GROUP BY kind
            ORDER BY kind
            """;
        using var reader = command.ExecuteTrackedReader();
        while (reader.TrackedRead())
            AddDistinctReason(reasons, reader.GetString(0));
        return reasons;
    }

    private static List<string> MergeDistinctReasons(
        IReadOnlyList<string>? first,
        IReadOnlyList<string>? second = null)
    {
        var reasons = new List<string>();
        if (first != null)
        {
            foreach (var reason in first)
                AddDistinctReason(reasons, reason);
        }
        if (second != null)
        {
            foreach (var reason in second)
                AddDistinctReason(reasons, reason);
        }
        return reasons;
    }

    private static void AddDistinctReason(List<string> reasons, string reason)
    {
        if (!reasons.Contains(reason, StringComparer.Ordinal))
            reasons.Add(reason);
    }
}

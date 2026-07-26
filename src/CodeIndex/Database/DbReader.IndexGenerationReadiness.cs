using CodeIndex.Indexer;
using CodeIndex.Models;
using Microsoft.Data.Sqlite;

namespace CodeIndex.Database;

internal sealed record PersistedIndexGenerationReadiness(
    bool GraphTableAvailable,
    bool GraphDataCurrent,
    bool IndexComplete,
    IReadOnlyList<string> IndexIncompleteReasons,
    bool ReferenceGraphComplete,
    IReadOnlyList<string> ReferenceGraphIncompleteReasons,
    ReferenceExtractionCapHitSummary ReferenceExtractionCapHits,
    bool MigrationInProgress);

public partial class DbReader
{
    internal const string SymbolsOnlyIndexIncompleteReason = "symbols_only_references_omitted";
    internal const string SymbolsOnlyReferenceGraphIncompleteReason = "symbols_only_graph_omitted";
    internal const string BatchInProgressIncompleteReason = "batch_in_progress";

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
        var indexCompleteness = TryGetMetaStringInternal(DbContext.IndexCompletenessMetaKey);
        var indexIncompleteReasons = MergeDistinctReasons(
            ParseMetaStringList(TryGetMetaStringInternal(DbContext.IndexIncompleteReasonsMetaKey)),
            ReadPersistedIndexOmissionReasons(
                _conn,
                _hasIssuesTable,
                ParseMetaBool(TryGetMetaStringInternal(DbContext.SymbolsOnlyGraphOmittedMetaKey)) == true,
                transaction));
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
            migrationInProgress);
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

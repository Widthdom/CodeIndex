namespace CodeIndex.Database;

internal sealed record WorkspaceIndexHealthSnapshot(
    DateTime? IndexedAt,
    DateTime? LatestModified,
    bool GraphTableAvailable,
    bool GraphDataCurrent,
    bool ReferenceGraphComplete,
    bool IndexComplete,
    bool IndexNewerThanReader);

public partial class DbReader
{
    internal WorkspaceIndexHealthSnapshot GetWorkspaceIndexHealth()
        => RunInReadSnapshot(() =>
        {
            var freshness = GetWorkspaceFreshness();
            var batchInProgress = string.Equals(
                TryGetMetaStringInternal(DbContext.BatchInProgressMetaKey),
                "true",
                StringComparison.OrdinalIgnoreCase);
            var indexCompleteness = TryGetMetaStringInternal(DbContext.IndexCompletenessMetaKey);
            var indexComplete = !batchInProgress
                && !string.Equals(indexCompleteness, "incomplete", StringComparison.OrdinalIgnoreCase);
            var referenceExtractionCapHits = GetReferenceExtractionCapHits();
            var referenceGraphComplete = IsReferenceGraphComplete(referenceExtractionCapHits);
            var hdlGraphContractReady = !ScopeMayIncludeHdlFiles(
                lang: null,
                pathPatterns: null,
                excludePathPatterns: null,
                excludeTests: false)
                || _hdlGraphContractCurrent;
            var graphDataCurrent = _hasReferencesTable
                && indexComplete
                && referenceGraphComplete
                && hdlGraphContractReady;

            return new WorkspaceIndexHealthSnapshot(
                freshness.IndexedAt,
                freshness.LatestModified,
                _hasReferencesTable,
                graphDataCurrent,
                referenceGraphComplete,
                indexComplete,
                _indexNewerThanReader);
        });
}

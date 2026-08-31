namespace CodeIndex.Database;

internal sealed record WorkspaceIndexHealthSnapshot(
    DateTime? IndexedAt,
    DateTime? LatestModified,
    bool GraphTableAvailable,
    bool GraphDataCurrent,
    bool ReferenceGraphComplete,
    bool IndexComplete,
    bool IndexNewerThanReader,
    IReadOnlyList<string> IndexIncompleteReasons,
    PersistedSymbolKindFilterPolicy SymbolKindFilterPolicy);

public partial class DbReader
{
    internal WorkspaceIndexHealthSnapshot GetWorkspaceIndexHealth()
        => RunInReadSnapshot(transaction =>
        {
            var freshness = GetWorkspaceFreshness();
            var referenceExtractionCapHits = GetReferenceExtractionCapHits();
            var hdlGraphContractReady = !ScopeMayIncludeHdlFiles(
                lang: null,
                pathPatterns: null,
                excludePathPatterns: null,
                excludeTests: false)
                || _hdlGraphContractCurrent;
            var persistedReadiness = GetPersistedIndexGenerationReadiness(
                referenceExtractionCapHits,
                hdlGraphContractReady: hdlGraphContractReady,
                transaction: transaction);

            return new WorkspaceIndexHealthSnapshot(
                freshness.IndexedAt,
                freshness.LatestModified,
                persistedReadiness.GraphTableAvailable,
                persistedReadiness.GraphDataCurrent,
                persistedReadiness.ReferenceGraphComplete,
                persistedReadiness.IndexComplete,
                _indexNewerThanReader,
                persistedReadiness.IndexIncompleteReasons,
                persistedReadiness.SymbolKindFilterPolicy);
        });
}

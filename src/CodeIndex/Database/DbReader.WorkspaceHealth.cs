namespace CodeIndex.Database;

internal sealed record WorkspaceIndexHealthSnapshot(
    DateTime? IndexedAt,
    DateTime? LatestModified,
    bool GraphTableAvailable,
    bool GraphDataCurrent,
    bool ReferenceGraphComplete,
    bool IndexComplete,
    bool IndexNewerThanReader,
    string? IndexedHeadCommit,
    string? IndexedHeadCommitBranch,
    bool IndexedHeadCommitBranchStampPresent,
    string? WorkspaceVerifiedHeadSha,
    string? IndexedHeadSha,
    string? IndexedHeadBranch,
    bool IndexedHeadBranchStampPresent);

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
                TryGetMetaStringInternal(DbContext.IndexedHeadCommitMetaKey),
                TryGetMetaStringInternal(DbContext.IndexedHeadCommitBranchMetaKey),
                HasMetaKeyInternal(DbContext.IndexedHeadCommitBranchMetaKey),
                TryGetMetaStringInternal(DbContext.WorkspaceVerifiedHeadShaMetaKey),
                TryGetMetaStringInternal(DbContext.IndexedHeadShaMetaKey),
                TryGetMetaStringInternal(DbContext.IndexedHeadBranchMetaKey),
                HasMetaKeyInternal(DbContext.IndexedHeadBranchMetaKey));
        });
}

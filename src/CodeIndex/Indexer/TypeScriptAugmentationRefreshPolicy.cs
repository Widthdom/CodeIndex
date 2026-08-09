namespace CodeIndex.Indexer;

internal static class TypeScriptAugmentationRefreshPolicy
{
    internal static bool IsRefreshRequired(
        bool symbolsOnly,
        bool refreshRequested,
        bool dirtyScopeRequiresRefresh) =>
        !symbolsOnly && (refreshRequested || dirtyScopeRequiresRefresh);

    internal static bool ShouldRebuildReferences(
        bool symbolsOnly,
        bool canFinalize,
        bool refreshRequested,
        bool dirtyScopeRequiresRefresh,
        bool canStampReadyWithoutRebuild) =>
        canFinalize
        && IsRefreshRequired(symbolsOnly, refreshRequested, dirtyScopeRequiresRefresh)
        && !canStampReadyWithoutRebuild;
}

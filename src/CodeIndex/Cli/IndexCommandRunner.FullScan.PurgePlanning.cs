using CodeIndex.Database;
using CodeIndex.Indexer;

namespace CodeIndex.Cli;

public static partial class IndexCommandRunner
{
    private sealed record FullScanPurgePreparation(
        FilePurgePlan StaleFilePurgePlan,
        int Purged,
        IReadOnlySet<string>? RetainedPaths,
        IReadOnlyList<string> IndexedJavaScriptTypeScriptConfigPathsBeforePurge,
        bool HadCSharpStaticInterfaceContractsBeforePurge);

    private static FullScanPurgePreparation PlanFullScanStaleFiles(
        DbWriter writer,
        FileIndexer.ScanFilesResult scanResult,
        IReadOnlyList<FullScanFileTarget> fileTargets,
        bool scanHadErrors,
        bool startedWithNoIndexedFiles,
        bool symbolsOnly,
        bool deferCSharpMutationsForIncompleteScan,
        bool? priorCSharpStaticInterfaceSourceEvidence,
        bool priorFilterRetainedCSharpContractMembers,
        CancellationToken cancellationToken)
    {
        if (startedWithNoIndexedFiles)
        {
            return new FullScanPurgePreparation(
                FilePurgePlan.Empty,
                Purged: 0,
                RetainedPaths: null,
                IndexedJavaScriptTypeScriptConfigPathsBeforePurge: [],
                HadCSharpStaticInterfaceContractsBeforePurge: false);
        }

        var retainedPaths = new HashSet<string>(fileTargets.Count, StringComparer.Ordinal);
        foreach (var target in fileTargets)
            retainedPaths.Add(target.IndexPath);
        var indexedJavaScriptTypeScriptConfigPathsBeforePurge =
            writer.GetIndexedJavaScriptTypeScriptConfigPaths();

        FilePurgePlan staleFilePurgePlan;
        if (scanHadErrors)
        {
            retainedPaths.UnionWith(
                scanResult.ProbeFailedFilePaths.Select(FileIndexer.NormalizeIndexPath));
            var authoritativeDirectories = scanResult.ListedDirectories
                .Select(FileIndexer.NormalizeIndexPath)
                .ToHashSet(StringComparer.Ordinal);
            var attributePrunedDirectories = scanResult.AttributePrunedDirectories
                .Select(FileIndexer.NormalizeIndexPath)
                .ToHashSet(StringComparer.Ordinal);
            attributePrunedDirectories.UnionWith(
                scanResult.NestedRepositories.Select(FileIndexer.NormalizeIndexPath));
            var explicitlyRemovedPaths = scanResult.NonIndexablePaths
                .Select(FileIndexer.NormalizeIndexPath)
                .ToHashSet(StringComparer.Ordinal);
            staleFilePurgePlan = writer.PlanFilesOutsideRetainedSetWithinListedDirectories(
                retainedPaths,
                authoritativeDirectories,
                attributePrunedDirectories,
                explicitlyRemovedPaths,
                cancellationToken);
        }
        else
        {
            staleFilePurgePlan = writer.PlanFilesOutsideRetainedSet(
                retainedPaths,
                cancellationToken);
        }

        if (deferCSharpMutationsForIncompleteScan && staleFilePurgePlan.Count > 0)
        {
            // A fatal discovery gap makes the C# workspace non-authoritative. Keep every
            // prior row (including stale candidates) until a clean scan can rebuild implicit
            // implementation references from one complete source snapshot.
            // fatal discovery gap中はC# workspaceが不完全なため、clean scanまで既存rowを保持する。
            staleFilePurgePlan = FilePurgePlan.Empty;
        }

        var hadCSharpStaticInterfaceContractsBeforePurge = !symbolsOnly
            && staleFilePurgePlan.Count > 0
            && writer.HasCSharpFilesInFileIds(staleFilePurgePlan.FileIds, cancellationToken)
            && (priorCSharpStaticInterfaceSourceEvidence == true
                || writer.HasCSharpStaticInterfaceContractMembersInFileIds(
                    staleFilePurgePlan.FileIds,
                    includeInterfaceDeclarationsAsConservativeEvidence:
                        priorCSharpStaticInterfaceSourceEvidence == null
                        || !priorFilterRetainedCSharpContractMembers,
                    cancellationToken));

        return new FullScanPurgePreparation(
            staleFilePurgePlan,
            staleFilePurgePlan.Count,
            retainedPaths,
            indexedJavaScriptTypeScriptConfigPathsBeforePurge,
            hadCSharpStaticInterfaceContractsBeforePurge);
    }
}

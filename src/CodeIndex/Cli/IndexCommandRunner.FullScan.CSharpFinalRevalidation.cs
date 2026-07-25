using CodeIndex.Database;
using CodeIndex.Indexer;

namespace CodeIndex.Cli;

public static partial class IndexCommandRunner
{
    private sealed class FullScanCSharpFinalRevalidationContext
    {
        internal required DbWriter Writer { get; init; }
        internal required FileIndexer Indexer { get; init; }
        internal required IndexCommandOptions Options { get; init; }
        internal required string ProjectRoot { get; init; }
        internal required FullScanFileTarget[] FileTargets { get; init; }
        internal required IReadOnlyList<CSharpStaticInterfacePrepass.FileTarget>
            CSharpPrepassTargets
        { get; init; }
        internal required FilePurgePlan StaleFilePurgePlan { get; init; }
        internal required bool StartedWithNoIndexedFiles { get; init; }
        internal required bool CSharpIndexedProjectRootCompatible
        {
            get;
            init;
        }

        internal required int ExtractionParallelism { get; init; }
        internal required bool? PriorCSharpStaticInterfaceSourceEvidence
        {
            get;
            init;
        }

        internal required ReusableIndexedFileStatsSnapshot
            ReusableIndexedFileStats
        { get; init; }
        internal List<int>? ExtractionFileIndexes { get; init; }
        internal required int ExtractionWorkItemCount { get; init; }
        internal required bool UseFtsBulkLoad { get; init; }
        internal required CSharpStaticInterfaceWorkspaceSymbols CSharpWorkspace
        {
            get;
            init;
        }

        internal Dictionary<string, CSharpStaticInterfacePrepass.FileStatSnapshot>?
            CSharpWorkspaceFileSnapshots
        { get; init; }
        internal required bool ForceFullCSharpRefreshFromInvalidatedNoOp
        {
            get;
            init;
        }

        internal required bool PreservePriorPositiveCSharpSourceNoOp
        {
            get;
            init;
        }

        internal required bool CSharpSourceEvidenceForStamp { get; init; }
        internal required bool CSharpSourceEvidenceComplete { get; init; }
        internal required CancellationToken CancellationToken { get; init; }
        internal required Action<CSharpStaticInterfaceWorkspaceSymbols>
            DeferCSharpMutationsForIncompleteWorkspace
        { get; init; }
        internal required Func<string, bool>
            IsExistingCSharpSymbolPathNowNonCSharp
        { get; init; }
    }

    private sealed record FullScanCSharpFinalRevalidationResult(
        List<int>? ExtractionFileIndexes,
        int ExtractionWorkItemCount,
        bool UseFtsBulkLoad,
        CSharpStaticInterfaceWorkspaceSymbols CSharpWorkspace,
        Dictionary<string, CSharpStaticInterfacePrepass.FileStatSnapshot>?
            CSharpWorkspaceFileSnapshots,
        bool ForceFullCSharpRefreshFromInvalidatedNoOp,
        bool PreservePriorPositiveCSharpSourceNoOp,
        bool CSharpSourceEvidenceForStamp,
        bool CSharpSourceEvidenceComplete,
        int PromotedCSharpTargetCount,
        bool PromotedAllCSharpTargets);

    private static FullScanCSharpFinalRevalidationResult
        RevalidateFinalFullScanCSharpNoOp(
            FullScanCSharpFinalRevalidationContext context)
    {
        if (!context.PreservePriorPositiveCSharpSourceNoOp
            || (context.ExtractionWorkItemCount == 0
                && context.StaleFilePurgePlan.Count == 0))
        {
            return BuildUnchangedFinalCSharpRevalidationResult(context);
        }

        // The dirty-byte pass can be long on a mixed-language monorepo. Revalidate C#
        // once more at the final read-only boundary, then undo tentative stat skips
        // and promote every affected C# target if any source changed.
        // mixed-language dirty-byte pass後の最終read-only境界でC#を再statする。
        FullScanCSharpFinalStatRevalidationForTesting?.Invoke();
        var invalidatedCSharpFileIndexes = FindInvalidatedFinalCSharpTargets(
            context);
        if (invalidatedCSharpFileIndexes.Count == 0)
            return BuildUnchangedFinalCSharpRevalidationResult(context);

        return RebuildFinalFullScanCSharpWorkspace(
            context,
            invalidatedCSharpFileIndexes);
    }

    private static List<int> FindInvalidatedFinalCSharpTargets(
        FullScanCSharpFinalRevalidationContext context)
    {
        var invalidatedCSharpFileIndexes = new List<int>();
        for (var fileIndex = 0;
             fileIndex < context.FileTargets.Length;
             fileIndex++)
        {
            var target = context.FileTargets[fileIndex];
            if (target.Language != "csharp")
                continue;

            context.CancellationToken.ThrowIfCancellationRequested();
            if (IndexedFileStatReuse.TryGetReusableUnchangedFile(
                    context.ReusableIndexedFileStats,
                    target.FilePath,
                    target.IndexPath,
                    target.Language,
                    target.GeneratedExtractionSuppressed) == null)
            {
                invalidatedCSharpFileIndexes.Add(fileIndex);
            }
        }

        return invalidatedCSharpFileIndexes;
    }

    private static FullScanCSharpFinalRevalidationResult
        RebuildFinalFullScanCSharpWorkspace(
            FullScanCSharpFinalRevalidationContext context,
            IReadOnlyList<int> invalidatedCSharpFileIndexes)
    {
        var workspace = BuildStableFullScanCSharpWorkspace(
            context.ProjectRoot,
            context.CSharpPrepassTargets,
            out var workspaceFileSnapshots,
            () => CSharpStaticInterfacePrepass.BuildWorkspaceSymbols(
                context.Writer,
                context.Indexer,
                context.CSharpPrepassTargets,
                includeExistingSymbols:
                    context.CSharpIndexedProjectRootCompatible
                    && !context.Options.Rebuild
                    && !context.StartedWithNoIndexedFiles,
                canReuseExistingSymbolsWithoutRead: null,
                parallelism: context.ExtractionParallelism,
                excludedExistingFileIds:
                    context.StaleFilePurgePlan.FileIds,
                isExistingSymbolPathExcluded:
                    context.IsExistingCSharpSymbolPathNowNonCSharp,
                cancellationToken: context.CancellationToken),
            context.CancellationToken);
        if (!workspace.SourceContractEvidenceComplete)
        {
            var incompleteSourcePaths = workspace.IncompleteSourcePaths;
            context.DeferCSharpMutationsForIncompleteWorkspace(workspace);
            return new FullScanCSharpFinalRevalidationResult(
                context.ExtractionFileIndexes,
                context.ExtractionWorkItemCount,
                UseFtsBulkLoad: false,
                new CSharpStaticInterfaceWorkspaceSymbols(
                    [],
                    false,
                    SourceContractEvidenceComplete: false,
                    IncompleteSourcePaths: incompleteSourcePaths),
                workspaceFileSnapshots,
                context.ForceFullCSharpRefreshFromInvalidatedNoOp,
                PreservePriorPositiveCSharpSourceNoOp: false,
                CSharpSourceEvidenceForStamp: false,
                CSharpSourceEvidenceComplete: false,
                PromotedCSharpTargetCount: 0,
                PromotedAllCSharpTargets: false);
        }

        var requiresFullCSharpRefresh =
            context.PriorCSharpStaticInterfaceSourceEvidence == true
            || workspace.HasStaticInterfaceContracts;
        IReadOnlyList<int> csharpFileIndexesToRefresh;
        if (requiresFullCSharpRefresh)
        {
            workspace = workspace with
            {
                HasStaticInterfaceContracts = true,
            };
            var allCSharpFileIndexes =
                new List<int>(context.CSharpPrepassTargets.Count);
            for (var fileIndex = 0;
                 fileIndex < context.FileTargets.Length;
                 fileIndex++)
            {
                if (context.FileTargets[fileIndex].Language == "csharp")
                    allCSharpFileIndexes.Add(fileIndex);
            }

            csharpFileIndexesToRefresh = allCSharpFileIndexes;
        }
        else
        {
            // A previously authoritative negative workspace only needs the
            // stat-invalidated files when the raw fallback is still negative.
            // prior negative のraw fallbackもnegativeなら変更fileだけを更新する。
            csharpFileIndexesToRefresh = invalidatedCSharpFileIndexes;
        }

        var extractionFileIndexes = context.ExtractionFileIndexes
            ?? new List<int>(csharpFileIndexesToRefresh.Count);
        foreach (var fileIndex in csharpFileIndexesToRefresh)
            extractionFileIndexes.Add(fileIndex);
        extractionFileIndexes.Sort();
        return new FullScanCSharpFinalRevalidationResult(
            extractionFileIndexes,
            extractionFileIndexes.Count,
            UseFtsBulkLoad: false,
            workspace,
            workspaceFileSnapshots,
            requiresFullCSharpRefresh,
            PreservePriorPositiveCSharpSourceNoOp: false,
            workspace.HasSourceStaticInterfaceContracts,
            CSharpSourceEvidenceComplete: true,
            csharpFileIndexesToRefresh.Count,
            csharpFileIndexesToRefresh.Count
                == context.CSharpPrepassTargets.Count);
    }

    private static FullScanCSharpFinalRevalidationResult
        BuildUnchangedFinalCSharpRevalidationResult(
            FullScanCSharpFinalRevalidationContext context)
        => new(
            context.ExtractionFileIndexes,
            context.ExtractionWorkItemCount,
            context.UseFtsBulkLoad,
            context.CSharpWorkspace,
            context.CSharpWorkspaceFileSnapshots,
            context.ForceFullCSharpRefreshFromInvalidatedNoOp,
            context.PreservePriorPositiveCSharpSourceNoOp,
            context.CSharpSourceEvidenceForStamp,
            context.CSharpSourceEvidenceComplete,
            PromotedCSharpTargetCount: 0,
            PromotedAllCSharpTargets: false);
}

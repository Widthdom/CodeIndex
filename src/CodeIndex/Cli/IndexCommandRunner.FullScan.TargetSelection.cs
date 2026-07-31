using CodeIndex.Database;
using CodeIndex.Indexer;

namespace CodeIndex.Cli;

public static partial class IndexCommandRunner
{
    private sealed class FullScanTargetSelectionContext
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
        internal required bool CanSkipTargetsBeforeContentLoad { get; init; }
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
        internal required Action ThrowIfFullScanCancelled { get; init; }
        internal required Func<bool>
            GetDeferCSharpMutationsForIncompleteScan
        { get; init; }
        internal required Func<int, bool, IndexedFileStatReuseResult?>
            GetFullScanTargetStatMatch
        { get; init; }
        internal required Action<int, IndexedFileStatReuseResult>
            RecordFullScanTargetStatSkip
        { get; init; }
        internal required Action<CSharpStaticInterfaceWorkspaceSymbols>
            DeferCSharpMutationsForIncompleteWorkspace
        { get; init; }
        internal required Func<string, bool>
            IsExistingCSharpSymbolPathNowNonCSharp
        { get; init; }
    }

    private sealed class FullScanTargetSelectionState
    {
        internal List<int>? ExtractionFileIndexes { get; set; }
        internal int ExtractionWorkItemCount { get; set; }
        internal required CSharpStaticInterfaceWorkspaceSymbols CSharpWorkspace
        {
            get;
            set;
        }

        internal Dictionary<string, CSharpStaticInterfacePrepass.FileStatSnapshot>?
            CSharpWorkspaceFileSnapshots
        { get; set; }
        internal bool ForceFullCSharpRefreshFromInvalidatedNoOp { get; set; }
        internal bool PreservePriorPositiveCSharpSourceNoOp { get; set; }
        internal bool CSharpSourceEvidenceForStamp { get; set; }
        internal bool CSharpSourceEvidenceComplete { get; set; }
    }

    private sealed record FullScanTargetSelectionResult(
        List<int>? ExtractionFileIndexes,
        int ExtractionWorkItemCount,
        CSharpStaticInterfaceWorkspaceSymbols CSharpWorkspace,
        Dictionary<string, CSharpStaticInterfacePrepass.FileStatSnapshot>?
            CSharpWorkspaceFileSnapshots,
        bool ForceFullCSharpRefreshFromInvalidatedNoOp,
        bool PreservePriorPositiveCSharpSourceNoOp,
        bool CSharpSourceEvidenceForStamp,
        bool CSharpSourceEvidenceComplete);

    private static FullScanTargetSelectionResult
        PrepareFullScanExtractionTargets(
            FullScanTargetSelectionContext context)
    {
        context.ThrowIfFullScanCancelled();
        var state = new FullScanTargetSelectionState
        {
            CSharpWorkspace = context.CSharpWorkspace,
            CSharpWorkspaceFileSnapshots =
                context.CSharpWorkspaceFileSnapshots,
            ForceFullCSharpRefreshFromInvalidatedNoOp =
                context.ForceFullCSharpRefreshFromInvalidatedNoOp,
            PreservePriorPositiveCSharpSourceNoOp =
                context.PreservePriorPositiveCSharpSourceNoOp,
            CSharpSourceEvidenceForStamp =
                context.CSharpSourceEvidenceForStamp,
            CSharpSourceEvidenceComplete =
                context.CSharpSourceEvidenceComplete,
        };

        if (context.CanSkipTargetsBeforeContentLoad)
        {
            SelectReusableFullScanTargets(context, state);
        }
        else if (context.GetDeferCSharpMutationsForIncompleteScan())
        {
            SelectFullScanTargetsWithDeferredCSharp(context, state);
        }
        else
        {
            state.ExtractionWorkItemCount = context.FileTargets.Length;
        }

        return new FullScanTargetSelectionResult(
            state.ExtractionFileIndexes,
            state.ExtractionWorkItemCount,
            state.CSharpWorkspace,
            state.CSharpWorkspaceFileSnapshots,
            state.ForceFullCSharpRefreshFromInvalidatedNoOp,
            state.PreservePriorPositiveCSharpSourceNoOp,
            state.CSharpSourceEvidenceForStamp,
            state.CSharpSourceEvidenceComplete);
    }

    private static void SelectReusableFullScanTargets(
        FullScanTargetSelectionContext context,
        FullScanTargetSelectionState state)
    {
        var fileTargets = context.FileTargets;
        var statPreflightMatched = new bool[fileTargets.Length];
        var csharpNoOpHasInterveningWork =
            context.StaleFilePurgePlan.Count > 0;
        for (var fileIndex = 0; fileIndex < fileTargets.Length; fileIndex++)
        {
            context.ThrowIfFullScanCancelled();
            if (context.GetDeferCSharpMutationsForIncompleteScan()
                && fileTargets[fileIndex].Language == "csharp")
            {
                continue;
            }

            statPreflightMatched[fileIndex] =
                context.GetFullScanTargetStatMatch(
                    fileIndex,
                    true) != null;
            if (!statPreflightMatched[fileIndex])
                csharpNoOpHasInterveningWork = true;
        }

        var revalidatedMatches =
            new IndexedFileStatReuseResult?[fileTargets.Length];
        RevalidateNonCSharpFullScanTargets(
            context,
            statPreflightMatched,
            revalidatedMatches,
            ref csharpNoOpHasInterveningWork);
        var preservedCSharpNoOpInvalidated =
            RevalidateCSharpFullScanTargets(
                context,
                state,
                statPreflightMatched,
                revalidatedMatches,
                csharpNoOpHasInterveningWork);
        if (preservedCSharpNoOpInvalidated)
        {
            RebuildInvalidatedFullScanCSharpNoOp(
                context,
                state,
                revalidatedMatches);
        }

        state.ExtractionFileIndexes =
            new List<int>(fileTargets.Length);
        for (var fileIndex = 0; fileIndex < fileTargets.Length; fileIndex++)
        {
            context.ThrowIfFullScanCancelled();
            if (context.GetDeferCSharpMutationsForIncompleteScan()
                && fileTargets[fileIndex].Language == "csharp")
            {
                RecordDeferredFullScanCSharpTarget(context, fileIndex);
                continue;
            }

            var revalidated = revalidatedMatches[fileIndex];
            if (revalidated != null)
            {
                context.RecordFullScanTargetStatSkip(
                    fileIndex,
                    revalidated.Value);
            }
            else
            {
                state.ExtractionFileIndexes.Add(fileIndex);
            }
        }

        state.ExtractionWorkItemCount =
            state.ExtractionFileIndexes.Count;
    }

    private static void RevalidateNonCSharpFullScanTargets(
        FullScanTargetSelectionContext context,
        IReadOnlyList<bool> statPreflightMatched,
        IList<IndexedFileStatReuseResult?> revalidatedMatches,
        ref bool csharpNoOpHasInterveningWork)
    {
        for (var fileIndex = 0;
             fileIndex < context.FileTargets.Length;
             fileIndex++)
        {
            context.ThrowIfFullScanCancelled();
            if (context.FileTargets[fileIndex].Language == "csharp")
                continue;

            var revalidated = statPreflightMatched[fileIndex]
                ? context.GetFullScanTargetStatMatch(fileIndex, false)
                : null;
            revalidatedMatches[fileIndex] = revalidated;
            if (revalidated == null)
                csharpNoOpHasInterveningWork = true;
        }
    }

    private static bool RevalidateCSharpFullScanTargets(
        FullScanTargetSelectionContext context,
        FullScanTargetSelectionState state,
        IReadOnlyList<bool> statPreflightMatched,
        IList<IndexedFileStatReuseResult?> revalidatedMatches,
        bool csharpNoOpHasInterveningWork)
    {
        var preservedCSharpNoOpInvalidated = false;
        for (var fileIndex = 0;
             fileIndex < context.FileTargets.Length;
             fileIndex++)
        {
            context.ThrowIfFullScanCancelled();
            if (context.FileTargets[fileIndex].Language != "csharp"
                || context.GetDeferCSharpMutationsForIncompleteScan())
            {
                continue;
            }

            var revalidated = statPreflightMatched[fileIndex]
                ? context.GetFullScanTargetStatMatch(
                    fileIndex,
                    state.PreservePriorPositiveCSharpSourceNoOp
                    && !csharpNoOpHasInterveningWork)
                : null;
            revalidatedMatches[fileIndex] = revalidated;
            if (state.PreservePriorPositiveCSharpSourceNoOp
                && revalidated == null)
            {
                preservedCSharpNoOpInvalidated = true;
            }
        }

        return preservedCSharpNoOpInvalidated;
    }

    private static void RebuildInvalidatedFullScanCSharpNoOp(
        FullScanTargetSelectionContext context,
        FullScanTargetSelectionState state,
        IList<IndexedFileStatReuseResult?> revalidatedMatches)
    {
        state.CSharpWorkspace = BuildStableFullScanCSharpWorkspace(
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
        state.CSharpWorkspaceFileSnapshots = workspaceFileSnapshots;
        state.PreservePriorPositiveCSharpSourceNoOp = false;
        if (!state.CSharpWorkspace.SourceContractEvidenceComplete)
        {
            var incompleteSourcePaths =
                state.CSharpWorkspace.IncompleteSourcePaths;
            context.DeferCSharpMutationsForIncompleteWorkspace(
                state.CSharpWorkspace);
            state.CSharpSourceEvidenceForStamp = false;
            state.CSharpSourceEvidenceComplete = false;
            state.CSharpWorkspace =
                new CSharpStaticInterfaceWorkspaceSymbols(
                    [],
                    false,
                    SourceContractEvidenceComplete: false,
                    IncompleteSourcePaths: incompleteSourcePaths);
            return;
        }

        var requiresFullCSharpRefresh =
            context.PriorCSharpStaticInterfaceSourceEvidence == true
            || state.CSharpWorkspace.HasStaticInterfaceContracts
            || state.CSharpWorkspace
                .RequiresMemberReadReferenceRefresh;
        state.ForceFullCSharpRefreshFromInvalidatedNoOp =
            requiresFullCSharpRefresh;
        state.CSharpSourceEvidenceForStamp =
            state.CSharpWorkspace.HasSourceStaticInterfaceContracts;
        state.CSharpSourceEvidenceComplete = true;
        if (!requiresFullCSharpRefresh)
            return;

        state.CSharpWorkspace = state.CSharpWorkspace with
        {
            HasStaticInterfaceContracts = true,
        };
        for (var fileIndex = 0;
             fileIndex < context.FileTargets.Length;
             fileIndex++)
        {
            if (context.FileTargets[fileIndex].Language == "csharp")
                revalidatedMatches[fileIndex] = null;
        }
    }

    private static void SelectFullScanTargetsWithDeferredCSharp(
        FullScanTargetSelectionContext context,
        FullScanTargetSelectionState state)
    {
        state.ExtractionFileIndexes =
            new List<int>(context.FileTargets.Length);
        for (var fileIndex = 0;
             fileIndex < context.FileTargets.Length;
             fileIndex++)
        {
            if (context.FileTargets[fileIndex].Language != "csharp")
            {
                state.ExtractionFileIndexes.Add(fileIndex);
                continue;
            }

            RecordDeferredFullScanCSharpTarget(context, fileIndex);
        }

        state.ExtractionWorkItemCount =
            state.ExtractionFileIndexes.Count;
    }

    private static void RecordDeferredFullScanCSharpTarget(
        FullScanTargetSelectionContext context,
        int fileIndex)
    {
        long currentSize = 0;
        try
        {
            var info = new FileInfo(
                context.FileTargets[fileIndex].FilePath);
            if (info.Exists && info.Length >= 0)
                currentSize = info.Length;
        }
        catch (Exception ex) when (
            ex is IOException
                or UnauthorizedAccessException
                or NotSupportedException
                or ArgumentException)
        {
        }

        context.RecordFullScanTargetStatSkip(
            fileIndex,
            new IndexedFileStatReuseResult(0, currentSize));
    }
}

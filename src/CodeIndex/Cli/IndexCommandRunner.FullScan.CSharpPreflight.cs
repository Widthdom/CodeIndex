using CodeIndex.Database;
using CodeIndex.Indexer;

namespace CodeIndex.Cli;

public static partial class IndexCommandRunner
{
    private sealed class FullScanCSharpPreflightContext
    {
        internal required DbWriter Writer { get; init; }
        internal required FileIndexer Indexer { get; init; }
        internal required IndexCommandOptions Options { get; init; }
        internal required string ProjectRoot { get; init; }
        internal required FullScanFileTarget[] FileTargets { get; init; }
        internal required IReadOnlyList<CSharpStaticInterfacePrepass.FileTarget> CSharpPrepassTargets { get; init; }
        internal required int CSharpPrepassCapacity { get; init; }
        internal required FilePurgePlan StaleFilePurgePlan { get; init; }
        internal required bool StartedWithNoIndexedFiles { get; init; }
        internal required bool PriorIndexComplete { get; init; }
        internal required int PriorReadiness { get; init; }
        internal required bool ScanHadErrors { get; init; }
        internal required bool ForceExtractorRefresh { get; init; }
        internal required bool PriorSymbolsOnlyGraphOmitted { get; init; }
        internal required bool SymbolKindFilterMatchesPrior { get; init; }
        internal required bool CSharpSymbolNameContractMatchesCurrent { get; init; }
        internal required bool CSharpIndexedProjectRootCompatible { get; init; }
        internal required bool CSharpHotspotTrustMatchesCurrent { get; init; }
        internal required bool RequiresConservativeCSharpSourceRefresh { get; init; }
        internal required bool HadCSharpStaticInterfaceContractsBeforePurge { get; init; }
        internal required bool? PriorCSharpStaticInterfaceSourceEvidence { get; init; }
        internal required bool ProjectRootWritten { get; init; }
        internal required int ExtractionParallelism { get; init; }
        internal required int FilesCount { get; init; }
        internal required string ActualMode { get; init; }
        internal required CancellationToken CancellationToken { get; init; }
        internal required Func<string, bool> IsExistingCSharpSymbolPathNowNonCSharp { get; init; }
        internal required Func<bool> GetDeferCSharpMutationsForIncompleteScan { get; init; }
        internal required Func<int> GetPurged { get; init; }
        internal required Action<CSharpStaticInterfaceWorkspaceSymbols> DeferCSharpMutationsForIncompleteWorkspace { get; init; }
    }

    private sealed record FullScanCSharpPreflightResult(
        ReusableIndexedFileStatsSnapshot? ReusableIndexedFileStats,
        Dictionary<string, IndexedFileStatReuseResult?>? CSharpPrepassStatReuse,
        Dictionary<string, CSharpStaticInterfacePrepass.FileStatSnapshot>? CSharpWorkspaceFileSnapshots,
        CSharpStaticInterfaceWorkspaceSymbols CSharpWorkspace,
        bool ForceFullCSharpRefreshFromInvalidatedNoOp,
        bool PreservePriorPositiveCSharpSourceNoOp,
        bool CSharpSourceEvidenceForStamp,
        bool CSharpSourceEvidenceComplete);

    private static FullScanCSharpPreflightResult PrepareFullScanCSharpWorkspace(
        FullScanCSharpPreflightContext context)
    {
        var writer = context.Writer;
        var options = context.Options;
        var cancellationToken = context.CancellationToken;
        HashSet<string>? retainedPathsForReuse = null;
        if (!options.Rebuild
            && !context.StartedWithNoIndexedFiles
            && context.StaleFilePurgePlan.RemainingFileCount
                - context.FileTargets.LongLength
                > context.FileTargets.LongLength)
        {
            retainedPathsForReuse = new HashSet<string>(
                context.FileTargets.Length,
                StringComparer.Ordinal);
            foreach (var target in context.FileTargets)
                retainedPathsForReuse.Add(target.IndexPath);
        }

        var csharpPositiveNoOpPolicyCandidate = !options.SymbolsOnly
            && context.PriorCSharpStaticInterfaceSourceEvidence is not null
            && context.PriorIndexComplete
            && (context.PriorReadiness & DbContext.GraphReadyFlag) != 0
            && !context.ScanHadErrors
            && !context.HadCSharpStaticInterfaceContractsBeforePurge
            && !context.ForceExtractorRefresh
            && !context.PriorSymbolsOnlyGraphOmitted
            && context.SymbolKindFilterMatchesPrior
            && context.CSharpSymbolNameContractMatchesCurrent
            && context.CSharpIndexedProjectRootCompatible
            && context.CSharpHotspotTrustMatchesCurrent
            && context.CSharpPrepassTargets.Count > 0;
        var hasCSharpLanguageTransitions = false;
        void ObservePersistedCSharpPath(string indexPath)
        {
            if (!hasCSharpLanguageTransitions
                && context.IsExistingCSharpSymbolPathNowNonCSharp(indexPath))
            {
                hasCSharpLanguageTransitions = true;
            }
        }

        var reusableIndexedFileStats =
            !options.Rebuild && !context.StartedWithNoIndexedFiles
                ? writer.LoadReusableIndexedFileStats(
                    options.MaxSymbolsPerFile,
                    options.MaxReferencesPerFile,
                    cancellationToken,
                    context.FileTargets.Length,
                    retainedPathsForReuse,
                    context.StaleFilePurgePlan.FileIds,
                    csharpPositiveNoOpPolicyCandidate
                        ? ObservePersistedCSharpPath
                        : null,
                    maxFileSizeBytes:
                        options.MaxFileSizeBytes ?? FileIndexer.DefaultMaxFileSizeBytes)
                : null;
        Dictionary<string, IndexedFileStatReuseResult?>?
            csharpPrepassStatReuse = null;
        var priorPositiveCSharpSourceNoOpCandidate =
            csharpPositiveNoOpPolicyCandidate
            && !hasCSharpLanguageTransitions;
        var allCSharpPrepassTargetsReusable = false;
        if (priorPositiveCSharpSourceNoOpCandidate)
        {
            allCSharpPrepassTargetsReusable = true;
            csharpPrepassStatReuse =
                new Dictionary<string, IndexedFileStatReuseResult?>(
                    context.CSharpPrepassCapacity,
                    StringComparer.Ordinal);
            foreach (var target in context.CSharpPrepassTargets)
            {
                cancellationToken.ThrowIfCancellationRequested();
                var existingFile =
                    IndexedFileStatReuse.TryGetReusableUnchangedFile(
                        reusableIndexedFileStats!,
                        target.FilePath,
                        target.IndexPath,
                        target.Language,
                        target.GeneratedExtractionSuppressed);
                csharpPrepassStatReuse[target.IndexPath] = existingFile;
                allCSharpPrepassTargetsReusable &= existingFile != null;
            }
        }

        bool CanReuseCSharpPrepassTargetWithoutRead(
            CSharpStaticInterfacePrepass.FileTarget target)
        {
            if (context.ForceExtractorRefresh
                || options.Rebuild
                || context.StartedWithNoIndexedFiles
                || !context.ProjectRootWritten
                || (context.RequiresConservativeCSharpSourceRefresh
                    && !priorPositiveCSharpSourceNoOpCandidate)
                || !context.SymbolKindFilterMatchesPrior
                || !context.CSharpSymbolNameContractMatchesCurrent
                || target.Language != "csharp")
            {
                return false;
            }

            var existingFile =
                IndexedFileStatReuse.TryGetReusableUnchangedFile(
                    reusableIndexedFileStats!,
                    target.FilePath,
                    target.IndexPath,
                    target.Language,
                    target.GeneratedExtractionSuppressed);
            if (existingFile == null)
                allCSharpPrepassTargetsReusable = false;
            (csharpPrepassStatReuse ??=
                new Dictionary<string, IndexedFileStatReuseResult?>(
                    context.CSharpPrepassCapacity,
                    StringComparer.Ordinal))[target.IndexPath] = existingFile;
            return existingFile != null;
        }

        Dictionary<string, CSharpStaticInterfacePrepass.FileStatSnapshot>?
            csharpWorkspaceFileSnapshots = null;
        CSharpStaticInterfaceWorkspaceSymbols csharpWorkspace;
        var forceFullCSharpRefreshFromInvalidatedNoOp = false;
        var csharpWorkspaceMaterialized =
            !options.SymbolsOnly
            && !context.GetDeferCSharpMutationsForIncompleteScan()
            && context.CSharpPrepassTargets.Count > 0
            && !(priorPositiveCSharpSourceNoOpCandidate
                && allCSharpPrepassTargetsReusable);
        if (options.SymbolsOnly
            || context.GetDeferCSharpMutationsForIncompleteScan())
        {
            csharpWorkspace =
                new CSharpStaticInterfaceWorkspaceSymbols([], false);
        }
        else
        {
            csharpWorkspace = BuildFullScanCSharpWorkspaceWithHeartbeat(
                context,
                priorPositiveCSharpSourceNoOpCandidate,
                allCSharpPrepassTargetsReusable,
                CanReuseCSharpPrepassTargetWithoutRead,
                out csharpWorkspaceFileSnapshots);
            forceFullCSharpRefreshFromInvalidatedNoOp =
                csharpWorkspaceMaterialized
                && (context.PriorCSharpStaticInterfaceSourceEvidence == true
                    || csharpWorkspace.HasStaticInterfaceContracts
                    || csharpWorkspace
                        .RequiresMemberReadReferenceRefresh);
        }

        if (!options.SymbolsOnly
            && !csharpWorkspace.SourceContractEvidenceComplete)
        {
            var incompleteSourcePaths =
                csharpWorkspace.IncompleteSourcePaths;
            context.DeferCSharpMutationsForIncompleteWorkspace(
                csharpWorkspace);
            csharpWorkspace = new CSharpStaticInterfaceWorkspaceSymbols(
                [],
                false,
                SourceContractEvidenceComplete: false,
                IncompleteSourcePaths: incompleteSourcePaths);
        }

        var preservePriorPositiveCSharpSourceNoOp =
            priorPositiveCSharpSourceNoOpCandidate
            && allCSharpPrepassTargetsReusable
            && !context.GetDeferCSharpMutationsForIncompleteScan();
        var csharpSourceEvidenceForStamp =
            preservePriorPositiveCSharpSourceNoOp
                ? context.PriorCSharpStaticInterfaceSourceEvidence == true
                : csharpWorkspace.HasSourceStaticInterfaceContracts;
        var csharpSourceEvidenceComplete =
            preservePriorPositiveCSharpSourceNoOp
            || csharpWorkspace.SourceContractEvidenceComplete;
        if (preservePriorPositiveCSharpSourceNoOp)
        {
            csharpWorkspace =
                csharpWorkspace with { HasStaticInterfaceContracts = false };
        }
        if (!options.SymbolsOnly
            && !context.GetDeferCSharpMutationsForIncompleteScan()
            && !preservePriorPositiveCSharpSourceNoOp
            && (forceFullCSharpRefreshFromInvalidatedNoOp
                || context.RequiresConservativeCSharpSourceRefresh
                || !csharpSourceEvidenceComplete
                || (context.GetPurged() > 0
                    && context
                        .HadCSharpStaticInterfaceContractsBeforePurge)))
        {
            csharpWorkspace =
                csharpWorkspace with { HasStaticInterfaceContracts = true };
        }

        return new FullScanCSharpPreflightResult(
            reusableIndexedFileStats,
            csharpPrepassStatReuse,
            csharpWorkspaceFileSnapshots,
            csharpWorkspace,
            forceFullCSharpRefreshFromInvalidatedNoOp,
            preservePriorPositiveCSharpSourceNoOp,
            csharpSourceEvidenceForStamp,
            csharpSourceEvidenceComplete);
    }

    private static CSharpStaticInterfaceWorkspaceSymbols
        BuildFullScanCSharpWorkspaceWithHeartbeat(
            FullScanCSharpPreflightContext context,
            bool priorPositiveCSharpSourceNoOpCandidate,
            bool allCSharpPrepassTargetsReusable,
            Func<CSharpStaticInterfacePrepass.FileTarget, bool>
                canReuseCSharpPrepassTargetWithoutRead,
            out Dictionary<string,
                CSharpStaticInterfacePrepass.FileStatSnapshot>?
                csharpWorkspaceFileSnapshots)
    {
        WriteFullScanJsonLiveness(
            context.Options,
            "preparing C# workspace symbols...");
        var activeCSharpWorkspaceFiles =
            new string?[context.CSharpPrepassTargets.Count];
        var heartbeat = StartFullScanJsonPhaseHeartbeat(
            context.Options,
            "preparing C# workspace symbols",
            () => GetActiveCSharpPrepassPath(
                activeCSharpWorkspaceFiles));
        try
        {
            if (context.CSharpPrepassTargets.Count == 0
                || (priorPositiveCSharpSourceNoOpCandidate
                    && allCSharpPrepassTargetsReusable))
            {
                csharpWorkspaceFileSnapshots = null;
                return new CSharpStaticInterfaceWorkspaceSymbols([], false);
            }

            return BuildStableFullScanCSharpWorkspace(
                context.ProjectRoot,
                context.CSharpPrepassTargets,
                out csharpWorkspaceFileSnapshots,
                () => CSharpStaticInterfacePrepass.BuildWorkspaceSymbols(
                    context.Writer,
                    context.Indexer,
                    context.CSharpPrepassTargets,
                    includeExistingSymbols:
                        context.CSharpIndexedProjectRootCompatible
                        && !context.Options.Rebuild
                        && !context.StartedWithNoIndexedFiles,
                    canReuseExistingSymbolsWithoutRead:
                        priorPositiveCSharpSourceNoOpCandidate
                            ? null
                            : canReuseCSharpPrepassTargetWithoutRead,
                    reportCandidateFile: (candidateIndex, path) =>
                        SetActiveCSharpPrepassPath(
                            activeCSharpWorkspaceFiles,
                            candidateIndex,
                            path),
                    parallelism: context.ExtractionParallelism,
                    excludedExistingFileIds:
                        context.StaleFilePurgePlan.FileIds,
                    isExistingSymbolPathExcluded:
                        context
                            .IsExistingCSharpSymbolPathNowNonCSharp,
                    patternConfigsAlreadyLoaded: true,
                    cancellationToken: context.CancellationToken),
                context.CancellationToken);
        }
        catch (OperationCanceledException) when (
            context.CancellationToken.IsCancellationRequested)
        {
            throw new IndexInterruptedException(
                0,
                context.FilesCount,
                context.ActualMode);
        }
        finally
        {
            Array.Clear(activeCSharpWorkspaceFiles);
            StopFullScanJsonPhaseHeartbeat(heartbeat);
        }
    }
}

using CodeIndex.Database;
using CodeIndex.Indexer;

namespace CodeIndex.Cli;

public static partial class IndexCommandRunner
{
    private sealed partial class FullScanPreWriteSession
    {
        internal void PrepareCSharpWorkspace()
        {
            var request = Request;
            var core = request.Core;
            var baseline = request.Baseline;
            var contracts = request.Contracts;
            var runtime = request.Runtime;
            var scan = State.Scan;
            var csharp = State.CSharp;
            var writer = core.Writer;
            var options = core.Options;
            var cancellationToken = runtime.CancellationToken;
            HashSet<string>? retainedPathsForReuse = null;
            if (!options.Rebuild
                && !baseline.StartedWithNoIndexedFiles
                && scan.StaleFilePurgePlan.RemainingFileCount
                    - runtime.FileTargets.Count
                    > runtime.FileTargets.Count)
            {
                retainedPathsForReuse = new HashSet<string>(
                    runtime.FileTargets.Count,
                    StringComparer.Ordinal);
                foreach (var target in runtime.FileTargets)
                    retainedPathsForReuse.Add(target.IndexPath);
            }

            var csharpPositiveNoOpPolicyCandidate = !options.SymbolsOnly
                && baseline.PriorCSharpStaticInterfaceSourceEvidence is not null
                && baseline.PriorIndexComplete
                && (baseline.PriorReadiness & DbContext.GraphReadyFlag) != 0
                && !baseline.ScanHadErrors
                && !scan.HadCSharpStaticInterfaceContractsBeforePurge
                && !contracts.ForceExtractorRefresh
                && !baseline.PriorSymbolsOnlyGraphOmitted
                && contracts.SymbolKindFilterMatchesPrior
                && contracts.CSharpSymbolNameContractMatchesCurrent
                && contracts.CSharpIndexedProjectRootCompatible
                && contracts.CSharpHotspotTrustMatchesCurrent
                && runtime.CSharpPrepassTargets.Count > 0;
            var hasCSharpLanguageTransitions = false;
            void ObservePersistedCSharpPath(string indexPath)
            {
                if (!hasCSharpLanguageTransitions
                    && IsExistingCSharpSymbolPathNowNonCSharp(indexPath))
                {
                    hasCSharpLanguageTransitions = true;
                }
            }

            var reusableIndexedFileStats =
                !options.Rebuild && !baseline.StartedWithNoIndexedFiles
                    ? writer.LoadReusableIndexedFileStats(
                        options.MaxSymbolsPerFile,
                        options.MaxReferencesPerFile,
                        cancellationToken,
                        runtime.FileTargets.Length,
                        retainedPathsForReuse,
                        scan.StaleFilePurgePlan.FileIds,
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
                        runtime.CSharpPrepassCapacity,
                        StringComparer.Ordinal);
                foreach (var target in runtime.CSharpPrepassTargets)
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
                if (contracts.ForceExtractorRefresh
                    || options.Rebuild
                    || baseline.StartedWithNoIndexedFiles
                    || !baseline.ProjectRootWritten
                    || (contracts.RequiresConservativeCSharpSourceRefresh
                        && !priorPositiveCSharpSourceNoOpCandidate)
                    || !contracts.SymbolKindFilterMatchesPrior
                    || !contracts.CSharpSymbolNameContractMatchesCurrent
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
                        runtime.CSharpPrepassCapacity,
                        StringComparer.Ordinal))[target.IndexPath] = existingFile;
                return existingFile != null;
            }

            Dictionary<string, CSharpStaticInterfacePrepass.FileStatSnapshot>?
                csharpWorkspaceFileSnapshots = null;
            CSharpStaticInterfaceWorkspaceSymbols csharpWorkspace;
            var forceFullCSharpRefreshFromInvalidatedNoOp = false;
            var csharpWorkspaceMaterialized =
                !options.SymbolsOnly
                && !scan.DeferCSharpMutationsForIncompleteScan
                && runtime.CSharpPrepassTargets.Count > 0
                && !(priorPositiveCSharpSourceNoOpCandidate
                    && allCSharpPrepassTargetsReusable);
            var csharpPrepassSymbolArtifacts = CSharpPrepassSymbolArtifactCache
                .CreateForFreshBuiltInExtraction(
                    csharpWorkspaceMaterialized
                    && baseline.StartedWithNoIndexedFiles
                    && !options.Rebuild
                    && !options.SymbolsOnly
                    && IndexExtractionStallTimeoutForTesting == null);
            if (options.SymbolsOnly
                || scan.DeferCSharpMutationsForIncompleteScan)
            {
                csharpWorkspace =
                    new CSharpStaticInterfaceWorkspaceSymbols([], false);
            }
            else
            {
                csharpWorkspace = BuildFullScanCSharpWorkspaceWithHeartbeat(
                    priorPositiveCSharpSourceNoOpCandidate,
                    allCSharpPrepassTargetsReusable,
                    CanReuseCSharpPrepassTargetWithoutRead,
                    csharpPrepassSymbolArtifacts,
                    out csharpWorkspaceFileSnapshots);
                forceFullCSharpRefreshFromInvalidatedNoOp =
                    csharpWorkspaceMaterialized
                    && (baseline.PriorCSharpStaticInterfaceSourceEvidence == true
                        || csharpWorkspace.HasStaticInterfaceContracts
                        || csharpWorkspace
                            .RequiresMemberReadReferenceRefresh);
            }

            if (!options.SymbolsOnly
                && !csharpWorkspace.SourceContractEvidenceComplete)
            {
                csharpPrepassSymbolArtifacts?.Clear();
                csharpPrepassSymbolArtifacts = null;
                var incompleteSourcePaths =
                    csharpWorkspace.IncompleteSourcePaths;
                DeferCSharpMutationsForIncompleteWorkspace(
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
                && !scan.DeferCSharpMutationsForIncompleteScan;
            var csharpSourceEvidenceForStamp =
                preservePriorPositiveCSharpSourceNoOp
                    ? baseline.PriorCSharpStaticInterfaceSourceEvidence == true
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
                && !scan.DeferCSharpMutationsForIncompleteScan
                && !preservePriorPositiveCSharpSourceNoOp
                && (forceFullCSharpRefreshFromInvalidatedNoOp
                    || contracts.RequiresConservativeCSharpSourceRefresh
                    || !csharpSourceEvidenceComplete
                    || (scan.Purged > 0
                        && scan
                            .HadCSharpStaticInterfaceContractsBeforePurge)))
            {
                csharpWorkspace =
                    csharpWorkspace with { HasStaticInterfaceContracts = true };
            }

            csharp.ReusableIndexedFileStats = reusableIndexedFileStats;
            csharp.CSharpPrepassStatReuse = csharpPrepassStatReuse;
            csharp.WorkspaceFileSnapshots = csharpWorkspaceFileSnapshots;
            csharp.Workspace = csharpWorkspace;
            csharp.PrepassSymbolArtifacts = csharpPrepassSymbolArtifacts;
            csharp.ForceFullRefreshFromInvalidatedNoOp =
                forceFullCSharpRefreshFromInvalidatedNoOp;
            csharp.PreservePriorPositiveSourceNoOp =
                preservePriorPositiveCSharpSourceNoOp;
            csharp.Evidence.ForStamp = csharpSourceEvidenceForStamp;
            csharp.Evidence.Complete = csharpSourceEvidenceComplete;
        }

        private CSharpStaticInterfaceWorkspaceSymbols
            BuildFullScanCSharpWorkspaceWithHeartbeat(
                bool priorPositiveCSharpSourceNoOpCandidate,
                bool allCSharpPrepassTargetsReusable,
                Func<CSharpStaticInterfacePrepass.FileTarget, bool>
                    canReuseCSharpPrepassTargetWithoutRead,
                CSharpPrepassSymbolArtifactCache? symbolArtifactCache,
                out Dictionary<string,
                    CSharpStaticInterfacePrepass.FileStatSnapshot>?
                    csharpWorkspaceFileSnapshots)
        {
            var request = Request;
            var core = request.Core;
            var baseline = request.Baseline;
            var contracts = request.Contracts;
            var runtime = request.Runtime;
            var scan = State.Scan;
            WriteFullScanJsonLiveness(
                core.Options,
                "preparing C# workspace symbols...");
            var activeCSharpWorkspaceFiles =
                new string?[runtime.CSharpPrepassTargets.Count];
            var heartbeat = StartFullScanJsonPhaseHeartbeat(
                core.Options,
                "preparing C# workspace symbols",
                () => GetActiveCSharpPrepassPath(
                    activeCSharpWorkspaceFiles));
            try
            {
                if (runtime.CSharpPrepassTargets.Count == 0
                    || (priorPositiveCSharpSourceNoOpCandidate
                        && allCSharpPrepassTargetsReusable))
                {
                    csharpWorkspaceFileSnapshots = null;
                    return new CSharpStaticInterfaceWorkspaceSymbols([], false);
                }

                return BuildStableFullScanCSharpWorkspace(
                    core.ProjectRoot,
                    runtime.CSharpPrepassTargets,
                    out csharpWorkspaceFileSnapshots,
                    () => CSharpStaticInterfacePrepass.BuildWorkspaceSymbols(
                        core.Writer,
                        core.Indexer,
                        runtime.CSharpPrepassTargets,
                        includeExistingSymbols:
                            contracts.CSharpIndexedProjectRootCompatible
                            && !core.Options.Rebuild
                            && !baseline.StartedWithNoIndexedFiles,
                        canReuseExistingSymbolsWithoutRead:
                            priorPositiveCSharpSourceNoOpCandidate
                                ? null
                                : canReuseCSharpPrepassTargetWithoutRead,
                        reportCandidateFile: (candidateIndex, path) =>
                            SetActiveCSharpPrepassPath(
                                activeCSharpWorkspaceFiles,
                                candidateIndex,
                                path),
                        parallelism: runtime.ExtractionParallelism,
                        excludedExistingFileIds:
                            scan.StaleFilePurgePlan.FileIds,
                        isExistingSymbolPathExcluded:
                            IsExistingCSharpSymbolPathNowNonCSharp,
                        patternConfigsAlreadyLoaded: true,
                        cancellationToken: runtime.CancellationToken,
                        symbolArtifactCache: symbolArtifactCache),
                    runtime.CancellationToken);
            }
            catch (OperationCanceledException) when (
                runtime.CancellationToken.IsCancellationRequested)
            {
                throw new IndexInterruptedException(
                    0,
                    runtime.FilesCount,
                    runtime.ActualMode);
            }
            finally
            {
                Array.Clear(activeCSharpWorkspaceFiles);
                StopFullScanJsonPhaseHeartbeat(heartbeat);
            }
        }

        internal bool IsExistingCSharpSymbolPathNowNonCSharp(string indexPath)
        {
            var core = Request.Core;
            var currentPath = Path.Combine(
                core.ProjectRoot,
                FileIndexer.NormalizeRelativePathForCurrentPlatform(indexPath));
            return Request.Runtime.FileLanguages.TryGetValue(
                    currentPath,
                    out var currentLanguage)
                && currentLanguage != "csharp";
        }

        private void DeferCSharpMutationsForIncompleteWorkspace(
            CSharpStaticInterfaceWorkspaceSymbols workspace)
        {
            if (workspace.SourceContractEvidenceComplete)
                return;

            var scan = State.Scan;
            scan.DeferCSharpMutationsForIncompleteScan = true;
            scan.StaleFilePurgePlan = FilePurgePlan.Empty;
            scan.Purged = 0;
            scan.FtsMutated = false;
            scan.HadCSharpStaticInterfaceContractsBeforePurge = false;

            var incompletePaths = workspace.IncompleteSourcePaths;
            if (incompletePaths == null || incompletePaths.Count == 0)
            {
                RecordCSharpWorkspaceFailure(
                    "<csharp_workspace>",
                    "csharp_prepass",
                    new IOException(
                        "C# static-interface workspace preflight could not read a source file."));
                return;
            }

            foreach (var path in incompletePaths.Take(PartialIndexFileErrorLimit))
            {
                RecordCSharpWorkspaceFailure(
                    path,
                    "csharp_prepass",
                    new IOException(
                        "C# static-interface workspace preflight could not read this source file."));
            }
        }

        private void RecordCSharpWorkspaceFailure(
            string path,
            string phase,
            Exception exception)
        {
            var diagnostics = State.Diagnostics;
            path = string.IsNullOrWhiteSpace(path) ? "<csharp_workspace>" : path;
            if (!diagnostics.ReportedCSharpWorkspaceFailures.Add(
                    $"{phase}\n{path}"))
            {
                return;
            }

            diagnostics.Errors++;
            diagnostics.ErrorList.Add(
                new CliJsonMessage(path, FormatIndexFileException(exception)));
            if (diagnostics.FileErrorList.Count < PartialIndexFileErrorLimit)
            {
                diagnostics.FileErrorList.Add(
                    BuildIndexFileError(path, phase, exception));
            }
        }
    }
}

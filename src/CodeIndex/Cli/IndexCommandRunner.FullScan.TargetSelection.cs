using CodeIndex.Database;
using CodeIndex.Indexer;

namespace CodeIndex.Cli;

public static partial class IndexCommandRunner
{
    private sealed partial class FullScanPreWriteSession
    {
        internal void PrepareExtractionTargets()
        {
            ThrowIfFullScanCancelled();
            var selection = State.Selection;
            if (Request.Reuse.CanSkipTargetsBeforeContentLoad)
            {
                SelectReusableFullScanTargets();
            }
            else if (State.Scan.DeferCSharpMutationsForIncompleteScan)
            {
                SelectFullScanTargetsWithDeferredCSharp();
            }
            else
            {
                selection.ExtractionWorkItemCount =
                    Request.Runtime.FileTargets.Length;
            }
        }

        private void SelectReusableFullScanTargets()
        {
            var fileTargets = Request.Runtime.FileTargets;
            var statPreflightMatched = new bool[fileTargets.Length];
            var csharpNoOpHasInterveningWork =
                State.Scan.StaleFilePurgePlan.Count > 0;
            for (var fileIndex = 0;
                 fileIndex < fileTargets.Length;
                 fileIndex++)
            {
                ThrowIfFullScanCancelled();
                if (State.Scan.DeferCSharpMutationsForIncompleteScan
                    && fileTargets[fileIndex].Language == "csharp")
                {
                    continue;
                }

                statPreflightMatched[fileIndex] =
                    GetFullScanTargetStatMatch(fileIndex, true) != null;
                if (!statPreflightMatched[fileIndex])
                    csharpNoOpHasInterveningWork = true;
            }

            var revalidatedMatches =
                new IndexedFileStatReuseResult?[fileTargets.Length];
            RevalidateNonCSharpFullScanTargets(
                statPreflightMatched,
                revalidatedMatches,
                ref csharpNoOpHasInterveningWork);
            var preservedCSharpNoOpInvalidated =
                RevalidateCSharpFullScanTargets(
                    statPreflightMatched,
                    revalidatedMatches,
                    csharpNoOpHasInterveningWork);
            if (preservedCSharpNoOpInvalidated)
                RebuildInvalidatedFullScanCSharpNoOp(revalidatedMatches);

            var selection = State.Selection;
            selection.ExtractionFileIndexes =
                new List<int>(fileTargets.Length);
            for (var fileIndex = 0;
                 fileIndex < fileTargets.Length;
                 fileIndex++)
            {
                ThrowIfFullScanCancelled();
                if (State.Scan.DeferCSharpMutationsForIncompleteScan
                    && fileTargets[fileIndex].Language == "csharp")
                {
                    RecordDeferredFullScanCSharpTarget(fileIndex);
                    continue;
                }

                var revalidated = revalidatedMatches[fileIndex];
                if (revalidated != null)
                {
                    RecordFullScanTargetStatSkip(
                        fileIndex,
                        revalidated.Value);
                }
                else
                {
                    selection.ExtractionFileIndexes.Add(fileIndex);
                }
            }

            selection.ExtractionWorkItemCount =
                selection.ExtractionFileIndexes.Count;
        }

        private void RevalidateNonCSharpFullScanTargets(
            IReadOnlyList<bool> statPreflightMatched,
            IList<IndexedFileStatReuseResult?> revalidatedMatches,
            ref bool csharpNoOpHasInterveningWork)
        {
            var fileTargets = Request.Runtime.FileTargets;
            for (var fileIndex = 0;
                 fileIndex < fileTargets.Length;
                 fileIndex++)
            {
                ThrowIfFullScanCancelled();
                if (fileTargets[fileIndex].Language == "csharp")
                    continue;

                var revalidated = statPreflightMatched[fileIndex]
                    ? GetFullScanTargetStatMatch(fileIndex, false)
                    : null;
                revalidatedMatches[fileIndex] = revalidated;
                if (revalidated == null)
                    csharpNoOpHasInterveningWork = true;
            }
        }

        private bool RevalidateCSharpFullScanTargets(
            IReadOnlyList<bool> statPreflightMatched,
            IList<IndexedFileStatReuseResult?> revalidatedMatches,
            bool csharpNoOpHasInterveningWork)
        {
            var fileTargets = Request.Runtime.FileTargets;
            var csharp = State.CSharp;
            var preservedCSharpNoOpInvalidated = false;
            for (var fileIndex = 0;
                 fileIndex < fileTargets.Length;
                 fileIndex++)
            {
                ThrowIfFullScanCancelled();
                if (fileTargets[fileIndex].Language != "csharp"
                    || State.Scan.DeferCSharpMutationsForIncompleteScan)
                {
                    continue;
                }

                var revalidated = statPreflightMatched[fileIndex]
                    ? GetFullScanTargetStatMatch(
                        fileIndex,
                        csharp.PreservePriorPositiveSourceNoOp
                        && !csharpNoOpHasInterveningWork)
                    : null;
                revalidatedMatches[fileIndex] = revalidated;
                if (csharp.PreservePriorPositiveSourceNoOp
                    && revalidated == null)
                {
                    preservedCSharpNoOpInvalidated = true;
                }
            }

            return preservedCSharpNoOpInvalidated;
        }

        private void RebuildInvalidatedFullScanCSharpNoOp(
            IList<IndexedFileStatReuseResult?> revalidatedMatches)
        {
            var request = Request;
            var core = request.Core;
            var baseline = request.Baseline;
            var contracts = request.Contracts;
            var runtime = request.Runtime;
            var scan = State.Scan;
            var csharp = State.CSharp;
            csharp.Workspace = BuildStableFullScanCSharpWorkspace(
                core.ProjectRoot,
                runtime.CSharpPrepassTargets,
                out var workspaceFileSnapshots,
                () => CSharpStaticInterfacePrepass.BuildWorkspaceSymbols(
                    core.Writer,
                    core.Indexer,
                    runtime.CSharpPrepassTargets,
                    includeExistingSymbols:
                        contracts.CSharpIndexedProjectRootCompatible
                        && !core.Options.Rebuild
                        && !baseline.StartedWithNoIndexedFiles,
                    canReuseExistingSymbolsWithoutRead: null,
                    parallelism: runtime.ExtractionParallelism,
                    excludedExistingFileIds:
                        scan.StaleFilePurgePlan.FileIds,
                    isExistingSymbolPathExcluded:
                        IsExistingCSharpSymbolPathNowNonCSharp,
                    patternConfigsAlreadyLoaded: true,
                    cancellationToken: runtime.CancellationToken),
                runtime.CancellationToken);
            csharp.WorkspaceFileSnapshots = workspaceFileSnapshots;
            csharp.PreservePriorPositiveSourceNoOp = false;
            if (!csharp.Workspace.SourceContractEvidenceComplete)
            {
                var incompleteSourcePaths =
                    csharp.Workspace.IncompleteSourcePaths;
                DeferCSharpMutationsForIncompleteWorkspace(csharp.Workspace);
                csharp.Evidence.ForStamp = false;
                csharp.Evidence.Complete = false;
                csharp.Workspace =
                    new CSharpStaticInterfaceWorkspaceSymbols(
                        [],
                        false,
                        SourceContractEvidenceComplete: false,
                        IncompleteSourcePaths: incompleteSourcePaths);
                return;
            }

            var requiresFullCSharpRefresh =
                baseline.PriorCSharpStaticInterfaceSourceEvidence == true
                || csharp.Workspace.HasStaticInterfaceContracts
                || csharp.Workspace.RequiresMemberReadReferenceRefresh;
            csharp.ForceFullRefreshFromInvalidatedNoOp =
                requiresFullCSharpRefresh;
            csharp.Evidence.ForStamp =
                csharp.Workspace.HasSourceStaticInterfaceContracts;
            csharp.Evidence.Complete = true;
            if (!requiresFullCSharpRefresh)
                return;

            csharp.Workspace = csharp.Workspace with
            {
                HasStaticInterfaceContracts = true,
            };
            for (var fileIndex = 0;
                 fileIndex < runtime.FileTargets.Length;
                 fileIndex++)
            {
                if (runtime.FileTargets[fileIndex].Language == "csharp")
                    revalidatedMatches[fileIndex] = null;
            }
        }

        private void SelectFullScanTargetsWithDeferredCSharp()
        {
            var fileTargets = Request.Runtime.FileTargets;
            var selection = State.Selection;
            selection.ExtractionFileIndexes =
                new List<int>(fileTargets.Length);
            for (var fileIndex = 0;
                 fileIndex < fileTargets.Length;
                 fileIndex++)
            {
                if (fileTargets[fileIndex].Language != "csharp")
                {
                    selection.ExtractionFileIndexes.Add(fileIndex);
                    continue;
                }

                RecordDeferredFullScanCSharpTarget(fileIndex);
            }

            selection.ExtractionWorkItemCount =
                selection.ExtractionFileIndexes.Count;
        }

        private void RecordDeferredFullScanCSharpTarget(int fileIndex)
        {
            long currentSize = 0;
            try
            {
                var info = new FileInfo(
                    Request.Runtime.FileTargets[fileIndex].FilePath);
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

            RecordFullScanTargetStatSkip(
                fileIndex,
                new IndexedFileStatReuseResult(0, currentSize));
        }

        private IndexedFileStatReuseResult? GetFullScanTargetStatMatch(
            int fileIndex,
            bool allowCSharpPrepassCache)
        {
            var request = Request;
            var core = request.Core;
            var baseline = request.Baseline;
            var contracts = request.Contracts;
            var reuse = request.Reuse;
            if (!reuse.CanSkipTargetsBeforeContentLoad)
                return null;

            var csharp = State.CSharp;
            var target = request.Runtime.FileTargets[fileIndex];
            var language = target.Language;
            var targetRequiresRefresh =
                reuse.JavaScriptTypeScriptRefreshRequired
                && (IsJavaScriptTypeScriptLanguage(language)
                    || IsJavaScriptTypeScriptConfigPath(target.IndexPath));
            var allowReuse = contracts.SymbolKindFilterMatchesPrior
                && !targetRequiresRefresh
                && !baseline.PriorSymbolsOnlyGraphOmitted
                && (language != "csharp"
                    || contracts.CSharpIndexedProjectRootCompatible)
                && (language != "csharp"
                    || contracts.CSharpSymbolNameContractMatchesCurrent)
                && (language != "csharp"
                    || !csharp.Workspace.HasStaticInterfaceContracts)
                && (language != "sql"
                    || reuse.SqlGraphContractMatchesCurrent)
                && (language is not ("verilog" or "systemverilog" or "vhdl")
                    || reuse.HdlGraphContractMatchesCurrent)
                && AllowReuseWithCurrentHotspotFamilyTrust(
                    language,
                    reuse.HotspotFamilyTrustMatchesCurrent);
            if (!allowReuse)
                return null;

            if (allowCSharpPrepassCache
                && language == "csharp"
                && csharp.CSharpPrepassStatReuse != null
                && csharp.CSharpPrepassStatReuse.TryGetValue(
                    target.IndexPath,
                    out var cachedCSharpPrepassReuse))
            {
                return cachedCSharpPrepassReuse;
            }

            return IndexedFileStatReuse.TryGetReusableUnchangedFile(
                csharp.ReusableIndexedFileStats!,
                target.FilePath,
                target.IndexPath,
                language,
                target.GeneratedExtractionSuppressed);
        }

        private void RecordFullScanTargetStatSkip(
            int fileIndex,
            IndexedFileStatReuseResult existingFile)
        {
            var runtime = Request.Runtime;
            var options = Request.Core.Options;
            var selection = State.Selection;
            var target = runtime.FileTargets[fileIndex];
            var language = target.Language;
            selection.Skipped++;
            selection.Processed++;
            selection.ReadableFileBytes.Remember(fileIndex, existingFile.Size);
            if (!string.IsNullOrWhiteSpace(language))
            {
                selection.SkippedSymbolExtractorLanguages ??=
                    new HashSet<string>(StringComparer.Ordinal);
                selection.SkippedSymbolExtractorLanguages.Add(language);
            }

            if (FileIndexer.SupportsHotspotFamilyMarkerLanguage(language)
                && language != null)
            {
                selection.ReusedHotspotFamilyLanguages ??=
                    new HashSet<string>(StringComparer.Ordinal);
                selection.ReusedHotspotFamilyLanguages.Add(language);
            }

            if (options.Verbose && !options.Json && !options.Quiet)
            {
                CommandOutputWriter.WriteLine(
                    $"  [SKIP] {target.IndexPath} (unchanged)");
            }
        }

        private void ThrowIfFullScanCancelled()
        {
            var runtime = Request.Runtime;
            if (!runtime.CancellationToken.IsCancellationRequested)
                return;

            throw new IndexInterruptedException(
                State.Selection.Processed,
                runtime.FilesCount,
                runtime.ActualMode);
        }
    }
}

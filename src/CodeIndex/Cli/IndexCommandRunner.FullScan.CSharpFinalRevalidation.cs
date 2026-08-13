using CodeIndex.Database;
using CodeIndex.Indexer;

namespace CodeIndex.Cli;

public static partial class IndexCommandRunner
{
    private sealed partial class FullScanPreWriteSession
    {
        internal void RevalidateFinalCSharpNoOp()
        {
            var csharp = State.CSharp;
            var selection = State.Selection;
            if (!csharp.PreservePriorPositiveSourceNoOp
                || (selection.ExtractionWorkItemCount == 0
                    && State.Scan.StaleFilePurgePlan.Count == 0))
            {
                return;
            }

            // The dirty-byte pass can be long on a mixed-language monorepo. Revalidate C#
            // once more at the final read-only boundary, then undo tentative stat skips
            // and promote every affected C# target if any source changed.
            // mixed-language dirty-byte pass後の最終read-only境界でC#を再statする。
            FullScanCSharpFinalStatRevalidationForTesting?.Invoke();
            var invalidatedCSharpFileIndexes =
                FindInvalidatedFinalCSharpTargets();
            if (invalidatedCSharpFileIndexes.Count == 0)
                return;

            RebuildFinalFullScanCSharpWorkspace(
                invalidatedCSharpFileIndexes);
        }

        private List<int> FindInvalidatedFinalCSharpTargets()
        {
            var runtime = Request.Runtime;
            var invalidatedCSharpFileIndexes = new List<int>();
            for (var fileIndex = 0;
                 fileIndex < runtime.FileTargets.Length;
                 fileIndex++)
            {
                var target = runtime.FileTargets[fileIndex];
                if (target.Language != "csharp")
                    continue;

                runtime.CancellationToken.ThrowIfCancellationRequested();
                if (IndexedFileStatReuse.TryGetReusableUnchangedFile(
                        State.CSharp.ReusableIndexedFileStats!,
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

        private void RebuildFinalFullScanCSharpWorkspace(
            IReadOnlyList<int> invalidatedCSharpFileIndexes)
        {
            var request = Request;
            var core = request.Core;
            var baseline = request.Baseline;
            var contracts = request.Contracts;
            var runtime = request.Runtime;
            var scan = State.Scan;
            var csharp = State.CSharp;
            var selection = State.Selection;
            var workspace = BuildStableFullScanCSharpWorkspace(
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
            if (!workspace.SourceContractEvidenceComplete)
            {
                var incompleteSourcePaths = workspace.IncompleteSourcePaths;
                DeferCSharpMutationsForIncompleteWorkspace(workspace);
                selection.UseFtsBulkLoad = false;
                csharp.Workspace =
                    new CSharpStaticInterfaceWorkspaceSymbols(
                        [],
                        false,
                        SourceContractEvidenceComplete: false,
                        IncompleteSourcePaths: incompleteSourcePaths);
                csharp.WorkspaceFileSnapshots = workspaceFileSnapshots;
                csharp.PreservePriorPositiveSourceNoOp = false;
                csharp.Evidence.ForStamp = false;
                csharp.Evidence.Complete = false;
                return;
            }

            var requiresFullCSharpRefresh =
                baseline.PriorCSharpStaticInterfaceSourceEvidence == true
                || workspace.HasStaticInterfaceContracts;
            IReadOnlyList<int> csharpFileIndexesToRefresh;
            if (requiresFullCSharpRefresh)
            {
                workspace = workspace with
                {
                    HasStaticInterfaceContracts = true,
                };
                var allCSharpFileIndexes =
                    new List<int>(runtime.CSharpPrepassTargets.Count);
                for (var fileIndex = 0;
                     fileIndex < runtime.FileTargets.Length;
                     fileIndex++)
                {
                    if (runtime.FileTargets[fileIndex].Language == "csharp")
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

            var extractionFileIndexes = selection.ExtractionFileIndexes
                ?? new List<int>(csharpFileIndexesToRefresh.Count);
            foreach (var fileIndex in csharpFileIndexesToRefresh)
                extractionFileIndexes.Add(fileIndex);
            extractionFileIndexes.Sort();
            selection.ExtractionFileIndexes = extractionFileIndexes;
            selection.ExtractionWorkItemCount = extractionFileIndexes.Count;
            selection.UseFtsBulkLoad = false;
            selection.Skipped -= csharpFileIndexesToRefresh.Count;
            selection.Processed -= csharpFileIndexesToRefresh.Count;
            if (csharpFileIndexesToRefresh.Count
                == runtime.CSharpPrepassTargets.Count)
            {
                selection.SkippedSymbolExtractorLanguages?.Remove("csharp");
                selection.ReusedHotspotFamilyLanguages?.Remove("csharp");
            }

            csharp.Workspace = workspace;
            csharp.WorkspaceFileSnapshots = workspaceFileSnapshots;
            csharp.ForceFullRefreshFromInvalidatedNoOp =
                requiresFullCSharpRefresh;
            csharp.PreservePriorPositiveSourceNoOp = false;
            csharp.Evidence.ForStamp =
                workspace.HasSourceStaticInterfaceContracts;
            csharp.Evidence.Complete = true;
        }
    }
}

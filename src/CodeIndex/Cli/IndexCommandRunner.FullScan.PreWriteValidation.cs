using CodeIndex.Indexer;

namespace CodeIndex.Cli;

public static partial class IndexCommandRunner
{
    private readonly record struct FullScanPreWriteInputValidationResult(
        bool IsValid,
        string ChangedPath);

    private sealed partial class FullScanPreWriteSession
    {
        internal FullScanPreWriteInputValidationResult PrepareWriteBoundary(
            FileIndexer.ScanInputSnapshot? inputSnapshot)
        {
            PrepareExtractionTargets();
            DecideFtsBulkLoad();
            RevalidateFinalCSharpNoOp();
            var inputValidation =
                ValidateBeforeWriteScanInput(inputSnapshot);
            if (inputValidation.IsValid)
                ValidateBeforeWriteCSharpFileSnapshots();
            return inputValidation;
        }

        internal FullScanPreWriteInputValidationResult
            ValidateBeforeWriteScanInput(
                FileIndexer.ScanInputSnapshot? inputSnapshot)
        {
            if (inputSnapshot == null)
                return new(true, string.Empty);

            FullScanInputSnapshotBarrierForTesting?.Invoke("before_write");
            var isValid = Request.Core.Indexer.TryValidateScanInputSnapshot(
                inputSnapshot,
                out var changedPath,
                Request.Runtime.CancellationToken);
            return new(isValid, changedPath);
        }

        internal void ValidateBeforeWriteCSharpFileSnapshots()
        {
            var options = Request.Core.Options;
            var scan = State.Scan;
            if (options.SymbolsOnly
                || scan.DeferCSharpMutationsForIncompleteScan)
            {
                return;
            }

            var csharp = State.CSharp;
            var changedFilePath = string.Empty;
            var stableFiles = csharp.WorkspaceFileSnapshots == null
                || CSharpStaticInterfacePrepass.TryValidateFileStatSnapshots(
                    Request.Runtime.CSharpPrepassTargets,
                    csharp.WorkspaceFileSnapshots,
                    out changedFilePath,
                    Request.Runtime.CancellationToken);
            if (stableFiles)
                return;

            csharp.PrepassSymbolArtifacts?.Clear();
            csharp.PrepassSymbolArtifacts = null;
            var driftPath = FormatCSharpWorkspaceSnapshotPath(
                Request.Core.ProjectRoot,
                changedFilePath);
            var incompleteWorkspace =
                new CSharpStaticInterfaceWorkspaceSymbols(
                    [],
                    HasStaticInterfaceContracts: true,
                    SourceContractEvidenceComplete: false,
                    IncompleteSourcePaths: [driftPath]);
            DeferCSharpMutationsForIncompleteWorkspace(incompleteWorkspace);
            csharp.PreservePriorPositiveSourceNoOp = false;
            csharp.Evidence.ForStamp = false;
            csharp.Evidence.Complete = false;
            csharp.WorkspaceFileSnapshots = null;
            csharp.Workspace = incompleteWorkspace;
            State.Selection.UseFtsBulkLoad = false;

            DeferCSharpExtractionTargets();
        }

        private void DeferCSharpExtractionTargets()
        {
            var fileTargets = Request.Runtime.FileTargets;
            var selection = State.Selection;
            var deferredCSharpIndexes =
                new List<int>(Request.Runtime.CSharpPrepassTargets.Count);
            if (selection.ExtractionFileIndexes == null)
            {
                selection.ExtractionFileIndexes =
                    new List<int>(fileTargets.Length);
                for (var fileIndex = 0;
                     fileIndex < fileTargets.Length;
                     fileIndex++)
                {
                    if (fileTargets[fileIndex].Language == "csharp")
                        deferredCSharpIndexes.Add(fileIndex);
                    else
                        selection.ExtractionFileIndexes.Add(fileIndex);
                }
            }
            else
            {
                for (var extractionIndex =
                         selection.ExtractionFileIndexes.Count - 1;
                     extractionIndex >= 0;
                     extractionIndex--)
                {
                    var fileIndex =
                        selection.ExtractionFileIndexes[extractionIndex];
                    if (fileTargets[fileIndex].Language != "csharp")
                        continue;

                    deferredCSharpIndexes.Add(fileIndex);
                    selection.ExtractionFileIndexes.RemoveAt(
                        extractionIndex);
                }
            }

            foreach (var fileIndex in deferredCSharpIndexes)
                RecordDeferredFullScanCSharpTarget(fileIndex);

            selection.ExtractionFileIndexes.Sort();
            selection.ExtractionWorkItemCount =
                selection.ExtractionFileIndexes.Count;
        }
    }
}

using CodeIndex.Indexer;

namespace CodeIndex.Cli;

public static partial class IndexCommandRunner
{
    private static CSharpStaticInterfaceWorkspaceSymbols BuildStableFullScanCSharpWorkspace(
        string projectRoot,
        IReadOnlyList<CSharpStaticInterfacePrepass.FileTarget> csharpPrepassTargets,
        out Dictionary<string, CSharpStaticInterfacePrepass.FileStatSnapshot>?
            csharpWorkspaceFileSnapshots,
        Func<CSharpStaticInterfaceWorkspaceSymbols> buildWorkspace,
        CancellationToken cancellationToken)
    {
        csharpWorkspaceFileSnapshots = null;
        Dictionary<string, CSharpStaticInterfacePrepass.FileStatSnapshot> fileSnapshots = [];
        var capturedFiles = CSharpStaticInterfacePrepass.TryCaptureFileStatSnapshots(
            csharpPrepassTargets,
            out fileSnapshots,
            out var failedFilePath,
            cancellationToken);
        if (!capturedFiles)
        {
            return new CSharpStaticInterfaceWorkspaceSymbols(
                [],
                HasStaticInterfaceContracts: true,
                SourceContractEvidenceComplete: false,
                IncompleteSourcePaths:
                [
                    FormatCSharpWorkspaceSnapshotPath(projectRoot, failedFilePath)
                ]);
        }

        FullScanCSharpPrepassForTesting?.Invoke();
        var workspace = buildWorkspace();
        var stableFiles = CSharpStaticInterfacePrepass.TryValidateFileStatSnapshots(
            csharpPrepassTargets,
            fileSnapshots,
            out var changedFilePath,
            cancellationToken);
        if (!stableFiles || !workspace.SourceContractEvidenceComplete)
        {
            var incompletePath = workspace.IncompleteSourcePaths?.FirstOrDefault()
                ?? changedFilePath
                ?? "<csharp_workspace>";
            return workspace with
            {
                HasStaticInterfaceContracts = true,
                SourceContractEvidenceComplete = false,
                IncompleteSourcePaths =
                [
                    FormatCSharpWorkspaceSnapshotPath(projectRoot, incompletePath)
                ],
            };
        }

        csharpWorkspaceFileSnapshots = fileSnapshots;
        return workspace;
    }
}

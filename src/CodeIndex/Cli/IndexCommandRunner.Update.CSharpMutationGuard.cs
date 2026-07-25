using CodeIndex.Database;
using CodeIndex.Indexer;

namespace CodeIndex.Cli;

public static partial class IndexCommandRunner
{
    private sealed class UpdateCSharpMutationGuardContext
    {
        internal required DbWriter Writer { get; init; }
        internal required FileIndexer Indexer { get; init; }
        internal required string ProjectRoot { get; init; }
        internal required HashSet<string> TargetPaths { get; init; }
        internal IReadOnlyDictionary<string, string>? ScannedUpdateLanguages
        {
            get;
            init;
        }

        internal required FilePurgePlan ScopedCleanupPlan { get; init; }
        internal FileIndexer.ScanInputSnapshot? CSharpWorkspaceInputSnapshot
        {
            get;
            init;
        }

        internal Dictionary<string, CSharpStaticInterfacePrepass.FileStatSnapshot>?
            CSharpWorkspaceSnapshots
        { get; init; }
        internal required CSharpStaticInterfaceWorkspaceSymbols CSharpWorkspace
        {
            get;
            init;
        }

        internal required bool DeferCSharpMutationsForIncompleteWorkspace
        {
            get;
            init;
        }

        internal required bool? CSharpSourceEvidenceForStamp { get; init; }
        internal required bool CSharpSourceEvidenceCompleteForStamp
        {
            get;
            init;
        }

        internal required CancellationToken CancellationToken { get; init; }
        internal required Action<string, string> RecordCSharpWorkspaceDrift
        {
            get;
            init;
        }
    }

    private sealed record UpdateCSharpMutationGuardResult(
        string? InputSnapshotFailurePath,
        bool DeferCSharpMutationsForIncompleteWorkspace,
        bool? CSharpSourceEvidenceForStamp,
        bool CSharpSourceEvidenceCompleteForStamp,
        Dictionary<string, CSharpStaticInterfacePrepass.FileStatSnapshot>?
            CSharpWorkspaceSnapshots,
        CSharpStaticInterfaceWorkspaceSymbols CSharpWorkspace);

    private static UpdateCSharpMutationGuardResult
        GuardUpdateCSharpMutationInputs(
            UpdateCSharpMutationGuardContext context)
    {
        var inputSnapshotFailurePath =
            ValidateUpdateCSharpInputSnapshot(context);
        if (inputSnapshotFailurePath != null)
            return BuildUpdateCSharpMutationGuardResult(
                context,
                inputSnapshotFailurePath);

        var deferCSharpMutations =
            context.DeferCSharpMutationsForIncompleteWorkspace;
        var sourceEvidence = context.CSharpSourceEvidenceForStamp;
        var sourceEvidenceComplete =
            context.CSharpSourceEvidenceCompleteForStamp;
        var workspaceSnapshots = context.CSharpWorkspaceSnapshots;
        var workspace = context.CSharpWorkspace;

        string? changedTargetPath = null;
        var stableTargetSet = workspaceSnapshots == null
            || TryValidateCurrentCSharpTargetSet(
                context.ProjectRoot,
                context.TargetPaths,
                context.ScannedUpdateLanguages,
                workspaceSnapshots,
                out changedTargetPath,
                context.CancellationToken);
        if (!deferCSharpMutations && !stableTargetSet)
        {
            DeferUpdateCSharpWorkspaceForMutationDrift(
                context,
                changedTargetPath ?? "<csharp_workspace>",
                "The C# workspace target set changed after contract preflight.",
                ref deferCSharpMutations,
                ref sourceEvidence,
                ref sourceEvidenceComplete,
                ref workspaceSnapshots,
                ref workspace);
        }

        if (!deferCSharpMutations && context.ScopedCleanupPlan.Count > 0)
        {
            var reappearedCleanupPath =
                FindReappearedUpdateCleanupPath(context);
            if (reappearedCleanupPath != null)
            {
                DeferUpdateCSharpWorkspaceForMutationDrift(
                    context,
                    reappearedCleanupPath,
                    "A cleanup-planned path reappeared after C# workspace discovery.",
                    ref deferCSharpMutations,
                    ref sourceEvidence,
                    ref sourceEvidenceComplete,
                    ref workspaceSnapshots,
                    ref workspace);
            }
        }

        return new UpdateCSharpMutationGuardResult(
            InputSnapshotFailurePath: null,
            deferCSharpMutations,
            sourceEvidence,
            sourceEvidenceComplete,
            workspaceSnapshots,
            workspace);
    }

    private static string? ValidateUpdateCSharpInputSnapshot(
        UpdateCSharpMutationGuardContext context)
    {
        if (context.CSharpWorkspaceInputSnapshot == null)
            return null;

        UpdateScanInputSnapshotBarrierForTesting?.Invoke("before_write");
        return context.Indexer.TryValidateScanInputSnapshot(
            context.CSharpWorkspaceInputSnapshot,
            out var changedInputPath,
            context.CancellationToken)
                ? null
                : changedInputPath ?? context.ProjectRoot;
    }

    private static string? FindReappearedUpdateCleanupPath(
        UpdateCSharpMutationGuardContext context)
    {
        UpdateScanInputSnapshotBarrierForTesting?.Invoke(
            "before_cleanup_apply");
        Dictionary<string, HashSet<FileIndexer.FileIdentity>>?
            retainedFileIdentitiesByCaseFold = null;
        var retainedPathsExact =
            new HashSet<string>(StringComparer.Ordinal);
        foreach (var retainedTargetPath in context.TargetPaths)
        {
            context.CancellationToken.ThrowIfCancellationRequested();
            var retainedTarget = UpdateFileTarget.Create(
                context.ProjectRoot,
                retainedTargetPath);
            retainedPathsExact.Add(retainedTarget.IndexPath);
            var ioPath =
                LongPath.EnsureWindowsPrefix(retainedTarget.FilePath);
            if (!File.Exists(ioPath))
                continue;

            if (!FileIndexer.TryGetFileIdentity(
                    ioPath,
                    out var retainedIdentity))
            {
                continue;
            }

            retainedFileIdentitiesByCaseFold ??=
                new Dictionary<string, HashSet<FileIndexer.FileIdentity>>(
                    StringComparer.OrdinalIgnoreCase);
            if (!retainedFileIdentitiesByCaseFold.TryGetValue(
                    retainedTarget.IndexPath,
                    out var retainedIdentities))
            {
                retainedIdentities = [];
                retainedFileIdentitiesByCaseFold.Add(
                    retainedTarget.IndexPath,
                    retainedIdentities);
            }

            retainedIdentities.Add(retainedIdentity);
        }

        return context.Writer.FindReappearedFileInScopedCleanupPlan(
            context.ProjectRoot,
            context.ScopedCleanupPlan.FileIds,
            retainedPathsExact,
            retainedFileIdentitiesByCaseFold,
            context.CancellationToken);
    }

    private static void DeferUpdateCSharpWorkspaceForMutationDrift(
        UpdateCSharpMutationGuardContext context,
        string path,
        string detail,
        ref bool deferCSharpMutations,
        ref bool? sourceEvidence,
        ref bool sourceEvidenceComplete,
        ref Dictionary<string,
            CSharpStaticInterfacePrepass.FileStatSnapshot>?
            workspaceSnapshots,
        ref CSharpStaticInterfaceWorkspaceSymbols workspace)
    {
        deferCSharpMutations = true;
        context.RecordCSharpWorkspaceDrift(path, detail);
        sourceEvidence = null;
        sourceEvidenceComplete = false;
        workspaceSnapshots = null;
        workspace = workspace with
        {
            HasStaticInterfaceContracts = true,
            SourceContractEvidenceComplete = false,
        };
        DeferCSharpTargetsAfterIncompleteWorkspace(
            context.Writer,
            context.ProjectRoot,
            context.TargetPaths,
            context.CancellationToken);
    }

    private static UpdateCSharpMutationGuardResult
        BuildUpdateCSharpMutationGuardResult(
            UpdateCSharpMutationGuardContext context,
            string inputSnapshotFailurePath)
        => new(
            inputSnapshotFailurePath,
            context.DeferCSharpMutationsForIncompleteWorkspace,
            context.CSharpSourceEvidenceForStamp,
            context.CSharpSourceEvidenceCompleteForStamp,
            context.CSharpWorkspaceSnapshots,
            context.CSharpWorkspace);
}

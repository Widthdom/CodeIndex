using CodeIndex.Database;
using CodeIndex.Indexer;
using CodeIndex.Models;

namespace CodeIndex.Mcp;

public partial class McpServer
{
    private sealed class McpIndexCSharpFailureCollector(
        string projectPath,
        List<IndexFileFailure> failures)
    {
        private readonly HashSet<string> _reported = new(StringComparer.Ordinal);

        internal void Record(string path, string stage, Exception exception)
        {
            path = string.IsNullOrWhiteSpace(path) ? "<csharp_workspace>" : path;
            if (!_reported.Add($"{stage}\n{path}"))
                return;

            var platformRelativePath = FileIndexer.NormalizeRelativePathForCurrentPlatform(path);
            failures.Add(BuildIndexFileFailure(
                projectPath,
                Path.Combine(projectPath, platformRelativePath),
                exception,
                stage));
        }

        internal void RecordIncompletePrepass(IReadOnlyList<string> paths)
        {
            if (paths.Count == 0)
            {
                Record(
                    "<csharp_workspace>",
                    "csharp_prepass",
                    new IOException(
                        "C# static-interface workspace preflight could not read a source file."));
                return;
            }

            foreach (var path in paths.Take(50))
            {
                Record(
                    path,
                    "csharp_prepass",
                    new IOException(
                        "C# static-interface workspace preflight could not read this source file."));
            }
        }
    }

    private sealed record McpIndexCSharpWorkspaceContext(
        string ProjectPath,
        McpPathBoundary.IndexRootAuthorization AuthorizedRoot,
        List<CSharpStaticInterfacePrepass.FileTarget> Targets,
        ReusableIndexedFileStatsSnapshot? ReusableIndexedFileStats,
        McpIndexReadableByteTracker ReadableBytes,
        McpIndexCSharpFailureCollector Failures,
        CancellationToken CancellationToken);

    private sealed class McpIndexCSharpPreflightCache
    {
        internal Dictionary<string, CSharpStaticInterfacePrepass.FileStatSnapshot>? FileSnapshots
        { get; set; }
        internal Dictionary<string, IndexedFileStatReuseResult?>? StatReuse { get; set; }
    }

    private sealed class McpIndexCSharpTransitions
    {
        internal bool DeferMutations { get; set; }
        internal bool PreservePriorPositiveSourceNoOp { get; set; }
        internal bool SourceEvidenceForStamp { get; set; }
        internal bool SourceEvidenceComplete { get; set; }
        internal bool ForceFullRefreshFromInvalidatedNoOp { get; set; }
        internal bool AllPrepassTargetsReusable { get; set; }
    }

    private sealed class McpIndexCSharpWorkspaceState(
        McpIndexCSharpWorkspaceContext context)
    {
        private readonly McpIndexCSharpPreflightCache _preflight = new();
        private readonly McpIndexCSharpTransitions _transitions = new();

        internal CSharpStaticInterfaceWorkspaceSymbols Workspace { get; set; } =
            new([], false);
        internal CSharpPrepassSymbolArtifactCache? PrepassArtifacts { get; set; }
        internal IReadOnlyList<string> IncompletePrepassPaths { get; set; } = [];
        internal bool DeferMutations
        {
            get => _transitions.DeferMutations;
            set => _transitions.DeferMutations = value;
        }
        internal bool PreservePriorPositiveSourceNoOp
        {
            get => _transitions.PreservePriorPositiveSourceNoOp;
            set => _transitions.PreservePriorPositiveSourceNoOp = value;
        }
        internal bool SourceEvidenceForStamp
        {
            get => _transitions.SourceEvidenceForStamp;
            set => _transitions.SourceEvidenceForStamp = value;
        }
        internal bool SourceEvidenceComplete
        {
            get => _transitions.SourceEvidenceComplete;
            set => _transitions.SourceEvidenceComplete = value;
        }
        internal bool ForceFullRefreshFromInvalidatedNoOp
        {
            get => _transitions.ForceFullRefreshFromInvalidatedNoOp;
            set => _transitions.ForceFullRefreshFromInvalidatedNoOp = value;
        }
        internal bool AllPrepassTargetsReusable
        {
            get => _transitions.AllPrepassTargetsReusable;
            set => _transitions.AllPrepassTargetsReusable = value;
        }
        internal bool HasFileSnapshots => _preflight.FileSnapshots != null;

        internal Dictionary<string, IndexedFileStatReuseResult?> EnsurePrepassStatReuse() =>
            _preflight.StatReuse ??= new Dictionary<string, IndexedFileStatReuseResult?>(
                context.Targets.Count,
                StringComparer.Ordinal);

        internal CSharpStaticInterfaceWorkspaceSymbols BuildStableWorkspace(
            Func<CSharpStaticInterfaceWorkspaceSymbols> buildWorkspace)
        {
            _preflight.FileSnapshots = null;
            Dictionary<string, CSharpStaticInterfacePrepass.FileStatSnapshot> snapshots = [];
            var captured = CSharpStaticInterfacePrepass.TryCaptureFileStatSnapshots(
                context.Targets,
                out snapshots,
                out var failedFilePath,
                context.CancellationToken,
                target => context.AuthorizedRoot.EnsureAuthorizedEntry(target.FilePath));
            if (!captured)
            {
                return new CSharpStaticInterfaceWorkspaceSymbols(
                    [],
                    HasStaticInterfaceContracts: true,
                    SourceContractEvidenceComplete: false,
                    IncompleteSourcePaths: [FormatSnapshotPath(failedFilePath)]);
            }

            McpIndexCSharpPrepassForTesting?.Invoke();
            var workspace = buildWorkspace();
            var stable = CSharpStaticInterfacePrepass.TryValidateFileStatSnapshots(
                context.Targets,
                snapshots,
                out var changedFilePath,
                context.CancellationToken,
                target => context.AuthorizedRoot.EnsureAuthorizedEntry(target.FilePath));
            if (!stable || !workspace.SourceContractEvidenceComplete)
            {
                var incompletePath = workspace.IncompleteSourcePaths?.FirstOrDefault()
                    ?? changedFilePath
                    ?? "<csharp_workspace>";
                return workspace with
                {
                    HasStaticInterfaceContracts = true,
                    SourceContractEvidenceComplete = false,
                    IncompleteSourcePaths = [FormatSnapshotPath(incompletePath)],
                };
            }

            _preflight.FileSnapshots = snapshots;
            return workspace;
        }

        internal string FormatSnapshotPath(string? path)
        {
            if (string.IsNullOrWhiteSpace(path) || path == "<csharp_workspace>")
                return "<csharp_workspace>";
            if (!Path.IsPathRooted(path))
                return FileIndexer.NormalizePathSeparators(path);
            try
            {
                var relative = FileIndexer.NormalizePathSeparators(
                    FileIndexer.GetRelativePathFromDirectory(context.ProjectPath, path));
                return relative == "."
                    || relative.StartsWith("../", StringComparison.Ordinal)
                    || Path.IsPathRooted(relative)
                        ? path
                        : relative;
            }
            catch (Exception ex) when (ex is IOException or UnauthorizedAccessException
                or NotSupportedException or ArgumentException)
            {
                return path;
            }
        }

        internal IndexedFileStatReuseResult? GetStatMatchedFile(
            in FileIndexer.IndexingFileTarget target)
        {
            return target.Language == "csharp"
                && _preflight.StatReuse != null
                && _preflight.StatReuse.TryGetValue(target.IndexPath, out var cached)
                    ? cached
                    : IndexedFileStatReuse.TryGetReusableUnchangedFile(
                        context.ReusableIndexedFileStats!,
                        target.FilePath,
                        target.IndexPath,
                        target.Language,
                        target.GeneratedExtractionSuppressed == true);
        }

        internal bool LoadedSnapshotMatches(
            in FileIndexer.IndexingFileTarget target,
            FileRecord record,
            DbWriter writer)
        {
            if (target.Language != "csharp" || _preflight.FileSnapshots == null)
                return true;

            if (CSharpStaticInterfacePrepass.TryValidateLoadedFileStatSnapshot(
                    target.FilePath,
                    target.IndexPath,
                    target.DisplayRelativePath,
                    record.Size,
                    record.Modified,
                    _preflight.FileSnapshots,
                    out var changedPath,
                    context.CancellationToken,
                    context.AuthorizedRoot.EnsureAuthorizedEntry))
            {
                return true;
            }

            DeferForLoadedSnapshotDrift(
                FormatSnapshotPath(changedPath ?? target.DisplayRelativePath),
                writer);
            return false;
        }

        internal void DeferForLoadedSnapshotDrift(string path, DbWriter writer)
        {
            ClearArtifacts();
            DeferMutations = true;
            PreservePriorPositiveSourceNoOp = false;
            SourceEvidenceForStamp = false;
            SourceEvidenceComplete = false;
            IncompletePrepassPaths = [path];
            _preflight.FileSnapshots = null;
            Workspace = new CSharpStaticInterfaceWorkspaceSymbols(
                [],
                HasStaticInterfaceContracts: true,
                SourceContractEvidenceComplete: false,
                IncompleteSourcePaths: IncompletePrepassPaths);
            writer.SetCSharpStaticInterfaceSourceEvidence(null);
            context.Failures.Record(
                path,
                "csharp_workspace_validation",
                new IOException(
                    "A C# source changed after workspace preflight; rerun indexing to refresh the complete C# graph."));
        }

        internal void DeferForStatRevalidation(
            in FileIndexer.IndexingFileTarget target,
            DbWriter writer)
        {
            PreservePriorPositiveSourceNoOp = false;
            DeferMutations = true;
            SourceEvidenceForStamp = false;
            SourceEvidenceComplete = false;
            writer.SetCSharpStaticInterfaceSourceEvidence(null);
            context.Failures.Record(
                target.IndexPath,
                "csharp_stat_revalidation",
                new IOException(
                    "A C# source changed after final workspace preflight; rerun indexing to refresh the complete C# graph."));
            try
            {
                var info = new FileInfo(target.FilePath);
                if (info.Exists && info.Length >= 0)
                    context.ReadableBytes.Remember(target.FilePath, info.Length);
            }
            catch (Exception ex) when (ex is IOException or UnauthorizedAccessException
                or NotSupportedException or ArgumentException)
            {
            }
        }

        internal bool TryValidateFileSnapshots(out string? changedFilePath)
        {
            changedFilePath = null;
            return _preflight.FileSnapshots == null
                || CSharpStaticInterfacePrepass.TryValidateFileStatSnapshots(
                    context.Targets,
                    _preflight.FileSnapshots,
                    out changedFilePath,
                    context.CancellationToken,
                    target => context.AuthorizedRoot.EnsureAuthorizedEntry(target.FilePath));
        }

        internal void RecordIncompletePrepass() =>
            context.Failures.RecordIncompletePrepass(IncompletePrepassPaths);

        internal void ClearArtifacts()
        {
            PrepassArtifacts?.Clear();
            PrepassArtifacts = null;
        }

        internal void DeferForIncompletePrepass()
        {
            ClearArtifacts();
            IncompletePrepassPaths = Workspace.IncompleteSourcePaths ?? [];
            DeferMutations = true;
            Workspace = new CSharpStaticInterfaceWorkspaceSymbols(
                [],
                false,
                SourceContractEvidenceComplete: false,
                IncompleteSourcePaths: IncompletePrepassPaths);
        }

        internal void DeferForBeforeWriteStatDrift(string? changedFilePath)
        {
            ClearArtifacts();
            var driftPath = FormatSnapshotPath(changedFilePath);
            IncompletePrepassPaths = [driftPath];
            DeferMutations = true;
            PreservePriorPositiveSourceNoOp = false;
            SourceEvidenceForStamp = false;
            SourceEvidenceComplete = false;
            _preflight.FileSnapshots = null;
            Workspace = new CSharpStaticInterfaceWorkspaceSymbols(
                [],
                HasStaticInterfaceContracts: true,
                SourceContractEvidenceComplete: false,
                IncompleteSourcePaths: IncompletePrepassPaths);
            context.Failures.RecordIncompletePrepass(IncompletePrepassPaths);
        }

        internal void RecordScanInputDrift(string changedPath)
        {
            context.Failures.Record(
                FormatSnapshotPath(changedPath),
                "csharp_workspace_validation",
                new IOException(
                    "Directory entries or scan configuration changed after source discovery; rerun indexing from a stable workspace snapshot."));
        }
    }
}

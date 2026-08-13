using System.Diagnostics;
using CodeIndex.Database;
using CodeIndex.Diagnostics;
using CodeIndex.Indexer;
using CodeIndex.Indexer.Extensibility;
using CodeIndex.Indexer.Hooks;
using CodeIndex.Models;

namespace CodeIndex.Cli;

public static partial class IndexCommandRunner
{
    private readonly record struct UpdateFileLoopRuntime(
        DbWriter Writer,
        FileIndexer Indexer,
        IndexCommandOptions Options,
        string ProjectRoot,
        IndexProgressReporter Progress,
        CancellationToken CancellationToken,
        IReadOnlyCollection<string> TargetPaths,
        List<IndexMemorySampleJsonResult> MemorySamples);

    private record struct UpdateFileLoopCounters(
        int Updated,
        int Removed,
        int Skipped,
        int Warnings,
        int Errors,
        int SymbolsDroppedByKindFilter);

    private record struct UpdateFileLoopRefreshState(
        bool FtsMutated,
        bool MutualRecursionRefreshNeeded,
        bool CSharpMetadataTargetsNeedRefresh,
        string? CurrentPath,
        string CurrentPhase,
        bool ParallelSourceWorkspaceDriftDetected,
        ReadableFileByteTracker? ReadableFileBytes);

    private readonly record struct UpdateFileLoopWorkspace(
        CSharpStaticInterfaceWorkspaceSymbols CSharpWorkspace,
        Dictionary<string, CSharpStaticInterfacePrepass.FileStatSnapshot>? CSharpWorkspaceSnapshots,
        IReadOnlyDictionary<string, string>? ScannedUpdateLanguages,
        bool SymbolKindFilterMatchesPrior,
        bool CSharpSymbolNameContractMatchesCurrent,
        bool SqlGraphContractMatchesCurrent,
        bool HdlGraphContractMatchesCurrent);

    private readonly record struct UpdateFileLoopOutput(
        Stopwatch Stopwatch,
        List<string>? IndexRunDiagnostics,
        LazyDisposable<PostExtractionHookRunner> PostExtractionHooks,
        HashSet<FileIndexer.FileIdentity> VisitedFileIdentities,
        List<CliJsonMessage> ErrorList,
        List<StatusIndexFileError> FileErrorList,
        List<CliJsonMessage> WarningList);

    private readonly record struct UpdateFileLoopReadinessOperations(
        Action<IEnumerable<FileIndexer.ScanError>, string> RecordScanErrors,
        Action<string, string, string> RecordCSharpWorkspaceDrift,
        Action DemoteReadinessOnce,
        Action RequireTypeScriptAugmentationRefresh,
        Action<string?> RecordDynamicGraphFileRefresh);

    private readonly record struct UpdateFileLoopPersistenceOperations(
        Action WriteProjectRootOnce,
        Func<string, string?, bool, int> PurgeStaleUpdateCleanupPaths,
        Func<bool> IsProjectRootWritten);

    private readonly record struct UpdateFileLoopParallelTesting(
        Action<UpdateParallelExtractionTestEvent>? ExtractionEvent,
        Func<string, string, Exception?>? ExtractionFailure,
        Func<TimeSpan>? ExtractionStallTimeout,
        Action? ExtractionWorkersStopped);

    private readonly record struct UpdateFileLoopRefreshResult(
        bool FtsMutated,
        bool MutualRecursionRefreshNeeded,
        bool CSharpMetadataTargetsNeedRefresh);

    private readonly record struct UpdateFileLoopOutcome(
        UpdateFileLoopCounters Counters,
        UpdateFileLoopRefreshResult Refresh,
        ReadableFileByteTracker ReadableFileBytes);

    private sealed partial class UpdateFileLoopSession
    {
        private readonly UpdateFileLoopRuntime runtime;
        private UpdateFileLoopCounters counters;
        private UpdateFileLoopRefreshState refresh;
        private readonly UpdateFileLoopWorkspace workspace;
        private readonly UpdateFileLoopOutput output;
        private readonly UpdateFileLoopReadinessOperations readiness;
        private readonly UpdateFileLoopPersistenceOperations persistenceOperations;
        private readonly UpdateFileLoopParallelTesting testing;

        internal UpdateFileLoopSession(
            UpdateFileLoopRuntime runtime,
            UpdateFileLoopCounters counters,
            UpdateFileLoopRefreshState refresh,
            UpdateFileLoopWorkspace workspace,
            UpdateFileLoopOutput output,
            UpdateFileLoopReadinessOperations readiness,
            UpdateFileLoopPersistenceOperations persistence,
            UpdateFileLoopParallelTesting testing)
        {
            this.runtime = runtime;
            this.counters = counters;
            this.refresh = refresh;
            this.workspace = workspace;
            this.output = output;
            this.readiness = readiness;
            persistenceOperations = persistence;
            this.testing = testing;
        }

        private DbWriter writer => runtime.Writer;
        private FileIndexer indexer => runtime.Indexer;
        private IndexCommandOptions options => runtime.Options;
        private string projectRoot => runtime.ProjectRoot;
        private IndexProgressReporter updateProgress => runtime.Progress;
        private CancellationToken cancellationToken => runtime.CancellationToken;
        private IReadOnlyCollection<string> targetPaths => runtime.TargetPaths;
        private List<IndexMemorySampleJsonResult> memorySamples => runtime.MemorySamples;
        private int updated { get => counters.Updated; set => counters.Updated = value; }
        private int removed { get => counters.Removed; set => counters.Removed = value; }
        private int skipped { get => counters.Skipped; set => counters.Skipped = value; }
        private int warnings { get => counters.Warnings; set => counters.Warnings = value; }
        private int errors { get => counters.Errors; set => counters.Errors = value; }
        private int symbolsDroppedByKindFilter
        {
            get => counters.SymbolsDroppedByKindFilter;
            set => counters.SymbolsDroppedByKindFilter = value;
        }
        private bool ftsMutated { get => refresh.FtsMutated; set => refresh.FtsMutated = value; }
        private bool mutualRecursionRefreshNeeded
        {
            get => refresh.MutualRecursionRefreshNeeded;
            set => refresh.MutualRecursionRefreshNeeded = value;
        }
        private bool csharpMetadataTargetsNeedRefresh
        {
            get => refresh.CSharpMetadataTargetsNeedRefresh;
            set => refresh.CSharpMetadataTargetsNeedRefresh = value;
        }
        private string? currentUpdatePath
        {
            get => refresh.CurrentPath;
            set => refresh.CurrentPath = value;
        }
        private string currentUpdatePhase
        {
            get => refresh.CurrentPhase;
            set => refresh.CurrentPhase = value;
        }
        private bool parallelSourceWorkspaceDriftDetected
        {
            get => refresh.ParallelSourceWorkspaceDriftDetected;
            set => refresh.ParallelSourceWorkspaceDriftDetected = value;
        }
        private ReadableFileByteTracker readableFileBytes
        {
            get => refresh.ReadableFileBytes!;
            set => refresh.ReadableFileBytes = value;
        }
        private CSharpStaticInterfaceWorkspaceSymbols csharpWorkspace => workspace.CSharpWorkspace;
        private Dictionary<string, CSharpStaticInterfacePrepass.FileStatSnapshot>?
            csharpWorkspaceSnapshots => workspace.CSharpWorkspaceSnapshots;
        private IReadOnlyDictionary<string, string>? scannedUpdateLanguages
            => workspace.ScannedUpdateLanguages;
        private bool symbolKindFilterMatchesPrior => workspace.SymbolKindFilterMatchesPrior;
        private bool csharpSymbolNameContractMatchesCurrent
            => workspace.CSharpSymbolNameContractMatchesCurrent;
        private bool sqlGraphContractMatchesCurrent => workspace.SqlGraphContractMatchesCurrent;
        private bool hdlGraphContractMatchesCurrent => workspace.HdlGraphContractMatchesCurrent;
        private Stopwatch stopwatch => output.Stopwatch;
        private List<string>? indexRunDiagnostics => output.IndexRunDiagnostics;
        private LazyDisposable<PostExtractionHookRunner> postExtractionHooks
            => output.PostExtractionHooks;
        private HashSet<FileIndexer.FileIdentity> visitedFileIdentities
            => output.VisitedFileIdentities;
        private List<CliJsonMessage> errorList => output.ErrorList;
        private List<StatusIndexFileError> fileErrorList => output.FileErrorList;
        private List<CliJsonMessage> warningList => output.WarningList;
        private Action<UpdateParallelExtractionTestEvent>? parallelExtractionEventForTesting
            => testing.ExtractionEvent;
        private Func<string, string, Exception?>? parallelExtractionFailureForTesting
            => testing.ExtractionFailure;
        private Func<TimeSpan>? extractionStallTimeoutForTesting => testing.ExtractionStallTimeout;
        private Action? parallelExtractionWorkersStoppedForTesting => testing.ExtractionWorkersStopped;

        private void RecordScanErrors(
            IEnumerable<FileIndexer.ScanError> scanErrors,
            string fatalPhase = "discovery")
            => readiness.RecordScanErrors(scanErrors, fatalPhase);

        private void RecordCSharpWorkspaceDrift(
            string relativePath,
            string detail,
            string fatalPhase = "reading")
            => readiness.RecordCSharpWorkspaceDrift(relativePath, detail, fatalPhase);

        private void DemoteReadinessOnce() => readiness.DemoteReadinessOnce();
        private void WriteProjectRootOnce() => persistenceOperations.WriteProjectRootOnce();
        private void RequireTypeScriptAugmentationRefresh()
            => readiness.RequireTypeScriptAugmentationRefresh();

        private int PurgeStaleUpdateCleanupPaths(
            string retainedRelativePath,
            string? checksum,
            bool includeDirectoryAndStem)
            => persistenceOperations.PurgeStaleUpdateCleanupPaths(
                retainedRelativePath,
                checksum,
                includeDirectoryAndStem);

        private void RecordDynamicGraphFileRefresh(string? language)
            => readiness.RecordDynamicGraphFileRefresh(language);

        private void RecordUpdateFileFailure(
            string relativePath,
            string phase,
            Exception exception)
        {
            DemoteReadinessOnce();
            LogIndexFileFailure("index_update_file_failed", relativePath, phase, exception);

            errors++;
            var errorMessage = FormatIndexFileException(exception);
            errorList.Add(new CliJsonMessage(relativePath, errorMessage));
            if (fileErrorList.Count < PartialIndexFileErrorLimit)
                fileErrorList.Add(BuildIndexFileError(relativePath, phase, exception));
            if (!options.Json)
            {
                updateProgress.Pause();
                CommandErrorWriter.WriteStderr(
                    FormatPerFileErrorLine("ERR ", relativePath, exception, errorMessage));
                updateProgress.Resume();
            }
        }

        private void ThrowIfUpdateCancelled()
        {
            if (!cancellationToken.IsCancellationRequested)
                return;

            updateProgress.Pause();
            throw new IndexInterruptedException(updated + removed, targetPaths.Count);
        }
    }
}

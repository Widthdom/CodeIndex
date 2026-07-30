using System.Collections.Concurrent;
using System.Diagnostics;
using System.Runtime.InteropServices;
using System.Text.RegularExpressions;
using System.Text.Json;
using CodeIndex.Database;
using CodeIndex.Diagnostics;
using CodeIndex.Indexer;
using CodeIndex.Indexer.Hooks;
using CodeIndex.Models;
using Microsoft.Data.Sqlite;

namespace CodeIndex.Cli;

/// <summary>
/// Runs indexing CLI commands.
/// インデックス系CLIコマンドを実行する。
/// </summary>
public static partial class IndexCommandRunner
{
    internal const string IncludeSymbolKindsEnvironmentVariable = "CDIDX_INDEX_INCLUDE_SYMBOL_KINDS";
    internal const string ExcludeSymbolKindsEnvironmentVariable = "CDIDX_INDEX_EXCLUDE_SYMBOL_KINDS";
    internal const string GeneratedCodePatternsEnvironmentVariable = "CDIDX_INDEX_GENERATED_CODE_PATTERNS";
    internal const int DefaultMaxSymbolsPerFile = 5000;
    internal const int MaxSymbolsPerFileLimit = 50_000;
    internal const int DefaultMaxReferencesPerFile = 100_000;
    internal const int MaxReferencesPerFileLimit = 1_000_000;
    internal const int MaxCommitRefCount = 64;
    internal const int MaxCommitRefLength = 256;
    internal const int MaxGeneratedCodePatternCsvLength = 32_768;
    internal const int MaxGeneratedCodePatternCount = 128;
    internal const int MaxGitExcludeBytes = 256 * 1024;
    internal const string SymbolKindFilterMetaKey = "index_symbol_kind_filter";
    private const int MaxIndexRunDiagnosticLength = 512;
    private const int ScanCheckpointVersion = 1;
    private const string ScanCheckpointFileName = "scan-checkpoint.json";
    private static readonly System.Threading.AsyncLocal<Action<DbWriter, IReadOnlyList<string>>?> ScopedPlannerStatisticsMaintenanceDiagnosticStampingForTesting = new();
    private static readonly TimeSpan IndexExtractionStallTimeout = TimeSpan.FromMinutes(5);

    internal readonly record struct FileByteReadSummary(long BytesRead, long SkippedFileCount);

    internal static void StampWriterVersionAndSymbolKindFilter(
        DbWriter writer,
        string? writerVersion,
        string symbolKindFilterSignature)
    {
        if (string.IsNullOrWhiteSpace(writerVersion))
        {
            writer.SetMeta(SymbolKindFilterMetaKey, symbolKindFilterSignature);
            return;
        }

        writer.SetMetaValues(
            (DbContext.CdidxWriterVersionMetaKey, writerVersion),
            (SymbolKindFilterMetaKey, symbolKindFilterSignature));
    }

    private sealed record ScanCheckpoint(
        int Version,
        string? GitHead,
        IReadOnlyList<string> Directories);

    internal sealed record ScanCheckpointLoadResult(
        IReadOnlySet<string> Directories,
        string? WarningMessage);

    internal sealed class LazyDisposable<T>(Func<T> factory) : IDisposable
        where T : class, IDisposable
    {
        private T? value;

        internal T Value => value ??= factory();
        internal T? ValueIfCreated => value;

        public void Dispose()
        {
            value?.Dispose();
            value = null;
        }
    }

    internal static Action? FullScanWritePhaseStartedForTesting { get; set; }
    internal static Action<bool, string?>? FullScanExtractionSchedulingForTesting { get; set; }
    internal static Action? FullScanExtractionWorkStartedForTesting { get; set; }
    internal static Action<string>? FullScanFileContentLoadForTesting { get; set; }
    internal static Action<string, string>? FullScanFilePhaseForTesting { get; set; }
    internal static Action<int>? FullScanExtractionQueueCapacityForTesting { get; set; }
    internal static Action? FullScanFtsOptimizeForTesting { get; set; }
    internal static Action? FullScanFtsMergeForTesting { get; set; }
    internal static Action<bool>? FullScanStaleFilePurgeForTesting { get; set; }
    internal static Action? FullScanReferencePurgeForTesting { get; set; }
    internal static Action? FullScanCSharpPrepassForTesting { get; set; }
    internal static Action? FullScanCSharpFinalStatRevalidationForTesting { get; set; }
    internal static Action? FullScanCSharpReadinessValidationForTesting { get; set; }
    internal static Action? FullScanCSharpMetadataResolveForTesting { get; set; }
    internal static Action? FullScanTypeScriptAugmentationRebuildForTesting { get; set; }
    internal static Action? UpdateCSharpPrepassForTesting { get; set; }
    internal static Action? UpdateCSharpExpansionScanStartingForTesting { get; set; }
    internal static Action<string>? UpdateCleanupChecksumReadForTesting { get; set; }
    internal static Action? UpdateCSharpMetadataResolveForTesting { get; set; }
    internal static Action? UpdateTypeScriptAugmentationRebuildForTesting { get; set; }
    internal static Action? UpdateExtractionWorkStartedForTesting { get; set; }
    internal static Action<string>? UpdateFileContentLoadForTesting { get; set; }
    internal static Action<string>? UpdateSkippedFileRecordBuiltForTesting { get; set; }
    internal static Action<int, int>? UpdateFileCommittedForTesting { get; set; }
    internal static Func<TimeSpan>? IndexExtractionStallTimeoutForTesting { get; set; }
    internal static Action? HotspotFamilyUpdateRestampReadyForCommitForTesting { get; set; }
    internal static Action<string>? WriteScanCheckpointForTesting { get; set; }
    internal static Action<string>? DeleteScanCheckpointForTesting { get; set; }
    internal static Func<bool> IsInputRedirectedForTesting { get; set; } = () => Console.IsInputRedirected;
    internal static Func<string?> ReadLineForTesting { get; set; } = Console.ReadLine;
    internal static TimeProvider TimeProvider { get; set; } = TimeProvider.System;

    private static DateTime GetUtcNow() => TimeProvider.GetUtcNow().UtcDateTime;

    public static int Run(string[] indexArgs, JsonSerializerOptions jsonOptions) =>
        Run(indexArgs, jsonOptions, cancellationForTesting: null);

    internal static int Run(string[] indexArgs, JsonSerializerOptions jsonOptions, CancellationTokenSource? cancellationForTesting)
        => Run(indexArgs, jsonOptions, cancellationForTesting, output: null);

    internal static int Run(
        string[] indexArgs,
        JsonSerializerOptions jsonOptions,
        CancellationTokenSource? cancellationForTesting,
        TextWriter? output)
    {
        using var outputScope = output == null ? null : CommandOutputWriter.Push(output);
        RuntimeSafety.Configure();
        var options = ParseArgs(indexArgs);
        ConsoleUi.SetWidthDetectionTracing(options.Verbose && !options.Json && !options.Quiet);
        var jsonContext = CliJsonSerializerContextFactory.Create(jsonOptions);
        using var ownedCancellation = cancellationForTesting == null ? new CancellationTokenSource() : null;
        var indexCancellation = cancellationForTesting ?? ownedCancellation!;
        using var cancelKeyPressRegistration = cancellationForTesting == null
            ? RegisterIndexCancelKeyPress(indexCancellation)
            : NullDisposable.Instance;
        using var terminateSignalRegistration = cancellationForTesting == null
            ? RegisterIndexTerminateSignal(indexCancellation)
            : NullDisposable.Instance;

        if (options.ShowHelp)
        {
            ConsoleUi.PrintUsageFull();
            return CommandExitCodes.Success;
        }

        var spinnerFrames = ConsoleUi.GetSpinnerFrames(options.EasterEgg);
        ConsoleUi.SetProgressTheme(options.EasterEgg);

        if (options.ProjectPath == null)
        {
            ConsoleUi.PrintUsage(showBanner: false);
            return CommandExitCodes.UsageError;
        }

        if (options.ProjectFilterError != null)
        {
            return WriteCommandError(
                options.Json,
                jsonOptions,
                options.ProjectFilterError,
                CommandExitCodes.UsageError,
                "Check --project / --solution and rerun the command.",
                CommandErrorCodes.UsageError);
        }

        // Snapshot cwd alongside the already-absolutized options so the finalize step can
        // detect mid-run drift (embedded host, signal handler, future plugin) and warn the
        // operator. Failure to read cwd (e.g. it was deleted out from under us) is best-effort
        // -- we just skip the drift warning rather than block the run. Issue #1577.
        var initialCwd = TryCaptureCurrentDirectory();
        var dbResolution = DbPathResolver.ResolveForIndex(options.ProjectPath, options.DbPath, options.DataDir);
        var dbPath = dbResolution.DbPath;
        var stopwatch = Stopwatch.StartNew();
        var runStartedAtUtc = GetUtcNow();
        var isUpdateMode = IsUpdateMode(options);
        var mode = options.Rebuild ? "rebuild" : isUpdateMode ? "update" : "incremental";

        if (!Directory.Exists(options.ProjectPath))
        {
            return CommandErrorWriter.WriteJsonOrHuman(
                options.Json,
                jsonOptions,
                $"directory not found: {options.ProjectPath}",
                CommandExitCodes.NotFound,
                "check the project path and rerun `cdidx index <projectPath>` with an existing directory.",
                errorCode: CommandErrorCodes.DirectoryNotFound);
        }

        var validationExitCode = ValidateIndexRunOptions(options, isUpdateMode, dbPath, jsonOptions);
        if (validationExitCode != null)
            return validationExitCode.Value;

        var requestedDbPath = dbPath;
        dbPath = DbPathResolver.NormalizeDbPath(dbPath);
        var resolvedDbPath = Path.GetFullPath(dbPath);
        var databaseExistedBeforeIndex = File.Exists(LongPath.EnsureWindowsPrefix(resolvedDbPath));

        if (!options.Json && !options.Quiet)
        {
            var projectDisplayPath = options.OptimizeOnly
                ? MaintenanceDatabaseErrorClassifier.FormatPathForOutput(
                    Path.GetFullPath(options.ProjectPath!),
                    options.ShowPaths)
                : Path.GetFullPath(options.ProjectPath!);
            var databaseDisplayPath = options.OptimizeOnly
                ? MaintenanceDatabaseErrorClassifier.FormatPathForOutput(
                    resolvedDbPath,
                    options.ShowPaths)
                : resolvedDbPath;
            ConsoleUi.PrintBanner();
            CommandOutputWriter.WriteLine();
            CommandOutputWriter.WriteLine($"  Project : {projectDisplayPath}");
            CommandOutputWriter.WriteLine($"  Output  : {databaseDisplayPath}");
            CommandOutputWriter.WriteLine($"  Mode    : {(options.OptimizeOnly ? "optimize" : mode)}");
            CommandOutputWriter.WriteLine();
        }

        if (options.OptimizeOnly)
            return RunOptimizeFtsForDb(
                resolvedDbPath,
                options.Json,
                jsonOptions,
                options.ProjectPath,
                options.DryRun,
                showPaths: options.ShowPaths,
                queryOnlyDbPath: options.DryRun ? requestedDbPath : null);

        bool ignoreCase;
        string ignoreRuleRoot;
        try
        {
            ignoreCase = GitHelper.ResolveIgnoreCase(options.ProjectPath, indexCancellation.Token);
            ignoreRuleRoot = GitHelper.TryGetRepositoryRoot(options.ProjectPath, indexCancellation.Token) ?? Path.GetFullPath(options.ProjectPath!);
        }
        catch (OperationCanceledException) when (indexCancellation.IsCancellationRequested)
        {
            const bool progressPersisted = false;
            TryStampLastFailedIndexRun(
                resolvedDbPath,
                status: "partial",
                mode,
                runStartedAtUtc,
                stopwatch.ElapsedMilliseconds,
                filesProcessed: 0,
                filesTotal: null,
                CommandErrorCodes.Interrupted,
                reason: "cancelled",
                progressPersisted: progressPersisted,
                recoveryHint: BuildInterruptedRecoveryHint(mode, progressPersisted));
            return WriteInterruptedResult(options.Json, jsonOptions, filesProcessed: 0, filesTotal: null, mode, progressPersisted);
        }

        var executionContext = new IndexRunExecutionContext(
            options,
            jsonOptions,
            jsonContext,
            indexCancellation,
            initialCwd,
            dbPath,
            resolvedDbPath,
            stopwatch,
            runStartedAtUtc,
            isUpdateMode,
            mode,
            spinnerFrames,
            databaseExistedBeforeIndex,
            ignoreCase,
            ignoreRuleRoot);

        if (!options.Watch)
            return RunInitialIndex(executionContext);

        // Subscribe the watch backend before the one required baseline scan. This closes the
        // old initial-scan/subscription gap without paying for an unconditional second scan.
        // Recovery scans are reserved for event loss after the backend becomes active.
        // 必要な baseline scan 1 回より先に watch backend を subscribe する。これにより従来の
        // initial-scan / subscribe 間の gap を閉じ、無条件の 2 回目 scan を避ける。recovery scan は
        // backend 有効化後に event loss が発生した場合だけ実行する。
        return IndexWatchRunner.Run(
            options,
            jsonOptions,
            Path.GetFullPath(options.ProjectPath!),
            Path.GetFullPath(dbPath),
            indexCancellation.Token,
            () => RunInitialIndex(executionContext));
    }

    private sealed record IndexRunExecutionContext(
        IndexCommandOptions Options,
        JsonSerializerOptions JsonOptions,
        CliJsonSerializerContext JsonContext,
        CancellationTokenSource IndexCancellation,
        string? InitialCwd,
        string DbPath,
        string ResolvedDbPath,
        Stopwatch Stopwatch,
        DateTime RunStartedAtUtc,
        bool IsUpdateMode,
        string Mode,
        string[] SpinnerFrames,
        bool DatabaseExistedBeforeIndex,
        bool IgnoreCase,
        string IgnoreRuleRoot);

    private static int RunInitialIndex(IndexRunExecutionContext context)
    {
        var options = context.Options;
        var jsonOptions = context.JsonOptions;
        var jsonContext = context.JsonContext;
        var indexCancellation = context.IndexCancellation;
        var initialCwd = context.InitialCwd;
        var dbPath = context.DbPath;
        var resolvedDbPath = context.ResolvedDbPath;
        var stopwatch = context.Stopwatch;
        var runStartedAtUtc = context.RunStartedAtUtc;
        var isUpdateMode = context.IsUpdateMode;
        var mode = context.Mode;
        var spinnerFrames = context.SpinnerFrames;
        var databaseExistedBeforeIndex = context.DatabaseExistedBeforeIndex;
        var ignoreCase = context.IgnoreCase;
        var ignoreRuleRoot = context.IgnoreRuleRoot;

        // --dry-run: scan files but do not write to database / --dry-run: ファイルスキャンのみでDBに書き込まない
        if (options.DryRun)
            return RunDryRun(
                options,
                ignoreCase,
                ignoreRuleRoot,
                jsonOptions,
                jsonContext,
                indexCancellation.Token);

        int initialExitCode;
        try
        {
            var dbDir = Path.GetDirectoryName(dbPath);
            if (!string.IsNullOrEmpty(dbDir))
                DataDirectorySecurity.CreatePrivateDirectory(dbDir);

            // Acquire a process-exclusive lock so concurrent `cdidx index` runs against the
            // same DB cannot interleave schema/data writes and corrupt the database.
            // `--force` bypasses the check for users who knowingly accept the risk.
            // 同一 DB に対する `cdidx index` の同時実行が schema / data 書き込みを交錯させ
            // DB を壊さないよう排他ロックを取る。`--force` はリスクを承知の場合に bypass。
            var lockPath = IndexLock.GetLockPath(resolvedDbPath);
            IndexLock? indexLock = null;
            if (!options.Force)
            {
                try
                {
                    indexLock = IndexLock.Acquire(lockPath, options.ProjectPath!);
                }
                catch (IndexLockConflictException ex)
                {
                    var holderDescription = DescribeLockHolder(ex.Holder);
                    var message = string.IsNullOrEmpty(holderDescription)
                        ? "another cdidx index is already running on this database"
                        : $"another cdidx index is already running on this database ({holderDescription})";
                    return WriteCommandError(
                        options.Json,
                        jsonOptions,
                        message,
                        CommandExitCodes.DatabaseError,
                        "Wait for the running index to finish, or pass --force to bypass the lock if you are sure no other cdidx index is active.",
                        CommandErrorCodes.DbLocked);
                }
            }
            else if (!options.Json)
            {
                ConsoleUi.PrintWarning("--force bypasses the index lock; concurrent cdidx index runs may corrupt the database.");
            }

            using (indexLock)
            {
                using var db = new DbContext(
                    options.Rebuild ? DbOpenIntent.Repair : DbOpenIntent.WriteIndex,
                    dbPath,
                    indexCancellation.Token);
                if (db.ReadOnlyFallback)
                {
                    return WriteCommandError(
                        options.Json,
                        jsonOptions,
                        $"database opened through stale read-only fallback after WAL checkpoint failed: {resolvedDbPath}; index requires a writable database",
                        CommandExitCodes.DatabaseError,
                        "Move the database to writable storage, stop the writer holding the WAL lock, or rerun the query command with --read-only if you only need read access.",
                        CommandErrorCodes.DbNotWritable);
                }
                // Capture prior readiness BEFORE we clear it. Update mode (--commits / --files) only
                // touches a subset of files, so trust bits the DB did NOT previously carry must not
                // be fabricated after a partial pass. But bits the DB DID carry should survive —
                // independently, not as a single all-or-nothing gate. Codex #86 review flagged that
                // gating all three bits on `user_version == CurrentSchemaVersion` regressed pre-#86
                // DBs (user_version=3): a `--files` refresh on such a DB would silently drop Graph/
                // Issues trust too, breaking references/callers/callees/impact for the whole repo.
                // update モードは元々立っていた readiness bit のみを個別に復元する。pre-#86 DB
                // (user_version=3) でも Graph/Issues を巻き込んで落とさないように、単一フラグではなく
                // 事前 bit をそのまま保持する。Codex #86 第 2 pass レビュー対応。
                var priorReadiness = db.GetUserVersion();
                // Also snapshot the stored fold-key version BEFORE ClearReadyFlags wipes trust. When
                // a future `NameFold.Version` bump lands, a partial update must NOT restamp
                // FoldReady on a DB whose untouched rows still carry the old-version fold keys — we
                // can't re-fold those rows without re-reading them, so the only safe state is to leave
                // fold degraded until `--rebuild`. Snapshot both version and runtime fingerprint so
                // partial update does not restamp FoldReady across either algorithm drift or runtime
                // casing-table drift. Issue #97.
                // fold metadata を事前 snapshot する。version だけでなく fingerprint のズレでも
                // partial update で FoldReady を restamp しない。
                var priorMeta = db.GetMetaStrings(
                [
                    "fold_key_version",
                    "fold_key_fingerprint",
                    DbContext.CSharpSymbolNameContractVersionMetaKey,
                    DbContext.GetMetadataTargetVersionMetaKey("csharp"),
                    DbContext.SqlGraphContractVersionMetaKey,
                    DbContext.HdlGraphContractVersionMetaKey,
                    DbContext.SymbolsOnlyGraphOmittedMetaKey,
                    DbContext.IndexedProjectRootMetaKey,
                    SymbolKindFilterMetaKey,
                    DbContext.IndexedHeadCommitMetaKey,
                    DbContext.IndexCompletenessMetaKey,
                    DbContext.IndexIncompleteReasonsMetaKey,
                ]);
                string? PriorMeta(string key) => priorMeta.TryGetValue(key, out var value) ? value : null;
                var priorFoldVersion = PriorMeta("fold_key_version");
                var priorFoldFingerprint = PriorMeta("fold_key_fingerprint");
                var priorSymbolExtractorVersionsMatchCurrent = new DbWriter(db).SymbolExtractorVersionsMatchCurrent();
                var priorCSharpSymbolNameContractVersion = PriorMeta(DbContext.CSharpSymbolNameContractVersionMetaKey);
                var priorMetadataTargetCsharp = PriorMeta(DbContext.GetMetadataTargetVersionMetaKey("csharp"));
                var priorSqlGraphContractVersion = PriorMeta(DbContext.SqlGraphContractVersionMetaKey);
                var priorHdlGraphContractVersion = PriorMeta(DbContext.HdlGraphContractVersionMetaKey);
                var priorSymbolsOnlyGraphOmitted = string.Equals(
                    PriorMeta(DbContext.SymbolsOnlyGraphOmittedMetaKey),
                    "true",
                    StringComparison.OrdinalIgnoreCase);
                var priorIndexIncompleteReasons = JsonStringListCodec.Deserialize(PriorMeta(DbContext.IndexIncompleteReasonsMetaKey));
                var priorIndexComplete = string.Equals(
                    PriorMeta(DbContext.IndexCompletenessMetaKey),
                    "complete",
                    StringComparison.OrdinalIgnoreCase);
                var priorFileIndexIncomplete = string.Equals(
                        PriorMeta(DbContext.IndexCompletenessMetaKey),
                        "incomplete",
                        StringComparison.OrdinalIgnoreCase)
                    && priorIndexIncompleteReasons?.Contains("file_index_error", StringComparer.Ordinal) == true;
                var priorHotspotFamilyVersions = GetHotspotFamilyMetaSnapshot(db, DbContext.GetHotspotFamilyVersionMetaKey);
                var priorHotspotFamilyMarkerFingerprints = GetHotspotFamilyMetaSnapshot(db, DbContext.GetHotspotFamilyMarkerFingerprintMetaKey);
                var priorIndexedProjectRoot = PriorMeta(DbContext.IndexedProjectRootMetaKey);
                var priorSymbolKindFilterSignature = PriorMeta(SymbolKindFilterMetaKey);
                // Captured BEFORE `--rebuild` drops the DB so an incremental run can warn the user when
                // the worktree's HEAD has moved since the previously indexed snapshot. The same value
                // is read at `status` time (without `--check`) to surface a worktree branch / HEAD
                // switch via `worktree_head_changed`. Issues #1508 and #1512.
                // `--rebuild` が DB を消す前に取り出す。incremental 経路で HEAD 差分を検知し、`status`
                // (no `--check`) でも worktree の HEAD 切替検出に利用する。
                var priorIndexedHeadCommit = PriorMeta(DbContext.IndexedHeadCommitMetaKey);
                var currentHeadCommit = GitHelper.TryGetHeadCommit(options.ProjectPath!, indexCancellation.Token);

                // Don't demote readiness yet. A transient usage error in update-mode preflight
                // (bad --commits hash, git unavailable, etc.) would permanently downgrade a healthy
                // DB even though no data was touched. Clearing happens just before the first
                // destructive / schema-changing operation, inside the mode-specific runner.
                // まだ clear しない。update モードの preflight が失敗しただけで healthy な DB を
                // 縮退状態に落とさないよう、clear は実際に書き込み直前で行う。

                db.InitializeSchema();
                var indexRunDiagnostics = new List<string>();
                AddToGitExclude(options.ProjectPath!, dbPath, indexRunDiagnostics, indexCancellation.Token);

                var writer = new DbWriter(db);
                var indexer = new FileIndexer(
                    options.ProjectPath!,
                    ignoreCase,
                    ignoreRuleRoot,
                    options.MaxFileSizeBytes,
                    directoryIgnoreCaseProbe: null,
                    symlinkPolicy: options.SymlinkPolicy,
                    generatedCodePatterns: options.GeneratedCodePatterns,
                    internalIndexDatabasePath: resolvedDbPath);
                var currentHotspotFamilyMarkerFingerprints = GetHotspotFamilyMarkerFingerprints(indexer, indexCancellation.Token);
                var projectRoot = Path.GetFullPath(options.ProjectPath!);

                initialExitCode = isUpdateMode
                    ? RunUpdateMode(db, writer, indexer, projectRoot, resolvedDbPath, options, stopwatch, runStartedAtUtc, spinnerFrames, jsonOptions, priorReadiness, priorIndexComplete, priorFileIndexIncomplete, priorSymbolsOnlyGraphOmitted, priorFoldVersion, priorFoldFingerprint, priorSymbolExtractorVersionsMatchCurrent, priorCSharpSymbolNameContractVersion, priorMetadataTargetCsharp, priorSqlGraphContractVersion, priorHdlGraphContractVersion, priorHotspotFamilyVersions, priorHotspotFamilyMarkerFingerprints, currentHotspotFamilyMarkerFingerprints, priorIndexedProjectRoot, priorIndexedHeadCommit, currentHeadCommit, priorSymbolKindFilterSignature, initialCwd, indexRunDiagnostics, indexCancellation.Token)
                    : RunFullScan(db, writer, indexer, projectRoot, resolvedDbPath, options, stopwatch, runStartedAtUtc, spinnerFrames, jsonOptions, priorReadiness, priorIndexComplete, priorSymbolsOnlyGraphOmitted, priorFoldVersion, priorFoldFingerprint, priorSymbolExtractorVersionsMatchCurrent, priorCSharpSymbolNameContractVersion, priorMetadataTargetCsharp, priorSqlGraphContractVersion, priorHdlGraphContractVersion, priorHotspotFamilyVersions, priorHotspotFamilyMarkerFingerprints, currentHotspotFamilyMarkerFingerprints, priorIndexedProjectRoot, priorIndexedHeadCommit, currentHeadCommit, priorSymbolKindFilterSignature, initialCwd, indexRunDiagnostics, showNextSteps: !databaseExistedBeforeIndex, indexCancellation.Token);
                if (initialExitCode == CommandExitCodes.Success)
                {
                    try
                    {
                        var plannerMaintenanceFailure = db.RunPlannerStatisticsMaintenance(
                            forceAnalyze: !databaseExistedBeforeIndex,
                            indexCancellation.Token);
                        if (plannerMaintenanceFailure != null)
                            TryStampPlannerStatisticsMaintenanceDiagnostic(writer, indexRunDiagnostics, plannerMaintenanceFailure);
                    }
                    catch (OperationCanceledException) when (indexCancellation.IsCancellationRequested)
                    {
                        // The authoritative result has already been emitted by the mode runner.
                        // Stop optional planner maintenance without appending a second JSON result.
                        // authoritative result は mode runner が出力済み。optional planner
                        // maintenance を止め、2 個目の JSON result は追加しない。
                    }
                }
            }
        }
        catch (IndexInterruptedException ex)
        {
            var failureMode = ex.ActualMode ?? mode;
            var progressPersisted = InterruptedProgressIsPersisted(failureMode, ex.FilesProcessed);
            TryStampLastFailedIndexRun(
                resolvedDbPath,
                status: "partial",
                failureMode,
                runStartedAtUtc,
                stopwatch.ElapsedMilliseconds,
                ex.FilesProcessed,
                ex.FilesTotal,
                CommandErrorCodes.Interrupted,
                reason: "interrupted",
                progressPersisted: progressPersisted,
                recoveryHint: BuildInterruptedRecoveryHint(failureMode, progressPersisted));
            return WriteInterruptedResult(options.Json, jsonOptions, ex.FilesProcessed, ex.FilesTotal, failureMode, progressPersisted);
        }
        catch (OperationCanceledException) when (indexCancellation.IsCancellationRequested)
        {
            const bool progressPersisted = false;
            TryStampLastFailedIndexRun(
                resolvedDbPath,
                status: "partial",
                mode,
                runStartedAtUtc,
                stopwatch.ElapsedMilliseconds,
                filesProcessed: 0,
                filesTotal: null,
                CommandErrorCodes.Interrupted,
                reason: "cancelled",
                progressPersisted: progressPersisted,
                recoveryHint: BuildInterruptedRecoveryHint(mode, progressPersisted));
            return WriteInterruptedResult(options.Json, jsonOptions, filesProcessed: 0, filesTotal: null, mode, progressPersisted);
        }
        catch (IndexExtractionStalledException ex)
        {
            TryStampLastFailedIndexRun(
                resolvedDbPath,
                status: "failed",
                mode,
                runStartedAtUtc,
                stopwatch.ElapsedMilliseconds,
                ex.FilesProcessed,
                ex.FilesTotal,
                CommandErrorCodes.IndexExtractionStalled,
                reason: "extraction_stalled");
            return WriteExtractionStalledResult(options.Json, jsonOptions, ex);
        }
        catch (Exception ex) when (IsDatabaseFilesystemError(ex))
        {
            TryStampLastFailedIndexRun(
                resolvedDbPath,
                status: "failed",
                mode,
                runStartedAtUtc,
                stopwatch.ElapsedMilliseconds,
                filesProcessed: null,
                filesTotal: null,
                CommandErrorCodes.DbError,
                reason: "database_filesystem_error");
            return WriteDatabaseFilesystemError(options.Json, jsonOptions, resolvedDbPath, ex);
        }

        return initialExitCode;
    }



    // Issue #1509: stamp the Git HEAD commit, branch, and UTC timestamp into
    // codeindex_meta so cross-session staleness ("the DB was indexed at commit X but
    // you're now at Y, N commits ahead") is detectable by `status` / consumers. Only
    // called when the index run completed without per-file errors so the stamp always
    // reflects an authoritative DB state. When git is unavailable (no repo, no `git`
    // binary, etc.) the keys are written as NULL so a stale stamp from a prior repo
    // state can't masquerade as current. Failures here must not block index success —
    // the index data itself is valid; the metadata stamp is best-effort. Issue #1509.
    // #1509: 成功 index 末尾で HEAD / branch / timestamp を codeindex_meta に保存する。
    // git 不在時は NULL stamp、stamp 自体の例外は warn せず無視（index 本体は成功）。



}

public sealed class IndexCommandOptions
{
    public bool ShowHelp { get; init; }
    public string? ProjectPath { get; init; }
    public string? DbPath { get; init; }
    public string? DataDir { get; init; }
    public bool Rebuild { get; init; }
    public bool Verbose { get; init; }
    public bool Json { get; init; }
    public bool Quiet { get; init; }
    public bool AllowPartial { get; init; }
    public List<string> Commits { get; init; } = [];
    public bool ChangedBetweenSpecified { get; init; }
    public List<string> ChangedBetweenRefs { get; init; } = [];
    public List<string> UpdateFiles { get; init; } = [];
    public List<string> ProjectFilters { get; init; } = [];
    public string? SolutionPath { get; init; }
    public string? ProjectFilterError { get; init; }
    public string? ParseError { get; init; }
    public string? EasterEgg { get; init; }
    public bool DryRun { get; init; }
    public int DryRunPathLimit { get; init; } = IndexCommandRunner.DefaultDryRunPathLimit;
    public bool Force { get; init; }
    public bool ReadOnly { get; init; }
    public bool Yes { get; init; }
    public bool Watch { get; init; }
    public bool OptimizeOnly { get; init; }
    public bool ShowPaths { get; init; }
    public bool SymbolsOnly { get; init; }
    public int? WatchDebounceMs { get; init; }
    public int WatchPendingPathLimit { get; init; } = IndexWatchRunner.DefaultWatchPendingPathLimit;
    public DurationOutputFormat DurationFormat { get; init; } = DurationOutputFormat.Auto;
    public CompletionNotificationMode NotifyMode { get; init; } = CompletionNotificationMode.Auto;
    public long? MaxFileSizeBytes { get; init; }
    public int MaxSymbolsPerFile { get; init; } = IndexCommandRunner.DefaultMaxSymbolsPerFile;
    public int MaxReferencesPerFile { get; init; } = IndexCommandRunner.DefaultMaxReferencesPerFile;
    public int Parallelism { get; init; } = IndexCommandRunner.DefaultIndexParallelism();
    public bool MemoryTrace { get; init; }
    public FileIndexer.SymlinkPolicy SymlinkPolicy { get; init; } = FileIndexer.SymlinkPolicy.None;
    public SymbolKindFilter SymbolKindFilter { get; init; } = SymbolKindFilter.Empty;
    public IReadOnlyList<string> GeneratedCodePatterns { get; init; } = [];
}

public sealed class SymbolKindFilter
{
    private static readonly string[] CSharpStaticInterfaceContractMemberKinds =
        ["function", "operator", "property"];

    public static readonly SymbolKindFilter Empty = new([], [], null);

    private readonly HashSet<string> _include;
    private readonly HashSet<string> _exclude;

    private SymbolKindFilter(IReadOnlyList<string> include, IReadOnlyList<string> exclude, string? parseError)
    {
        Include = include;
        Exclude = exclude;
        ParseError = parseError;
        _include = new HashSet<string>(include, StringComparer.OrdinalIgnoreCase);
        _exclude = new HashSet<string>(exclude, StringComparer.OrdinalIgnoreCase);
        Signature = $"include={string.Join(",", include)};exclude={string.Join(",", exclude)}";
    }

    public IReadOnlyList<string> Include { get; }
    public IReadOnlyList<string> Exclude { get; }
    public string? ParseError { get; }
    public string Signature { get; }
    public bool IsActive => Include.Count > 0 || Exclude.Count > 0;

    public static SymbolKindFilter Create(IEnumerable<string> include, IEnumerable<string> exclude, string? parseError)
    {
        static IReadOnlyList<string> Normalize(IEnumerable<string> values)
            => values
                .Select(value => value.Trim())
                .Where(value => value.Length > 0)
                .Distinct(StringComparer.OrdinalIgnoreCase)
                .OrderBy(value => value, StringComparer.OrdinalIgnoreCase)
                .ToArray();

        return new SymbolKindFilter(Normalize(include), Normalize(exclude), parseError);
    }

    internal static bool SignatureRetainsCSharpStaticInterfaceContractMembers(string? signature)
    {
        const string includePrefix = "include=";
        const string excludeSeparator = ";exclude=";
        if (signature == null || !signature.StartsWith(includePrefix, StringComparison.Ordinal))
            return false;

        var excludeSeparatorIndex = signature.IndexOf(excludeSeparator, StringComparison.Ordinal);
        if (excludeSeparatorIndex < includePrefix.Length
            || signature.IndexOf(excludeSeparator, excludeSeparatorIndex + excludeSeparator.Length, StringComparison.Ordinal) >= 0)
        {
            return false;
        }

        var includeText = signature[includePrefix.Length..excludeSeparatorIndex];
        var excludeText = signature[(excludeSeparatorIndex + excludeSeparator.Length)..];
        if (includeText.Contains(';', StringComparison.Ordinal)
            || excludeText.Contains(';', StringComparison.Ordinal))
        {
            return false;
        }

        static HashSet<string> ParseKinds(string value)
            => value
                .Split(',', StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries)
                .ToHashSet(StringComparer.OrdinalIgnoreCase);

        var includeKinds = ParseKinds(includeText);
        var excludeKinds = ParseKinds(excludeText);
        return CSharpStaticInterfaceContractMemberKinds.All(kind =>
            (includeKinds.Count == 0 || includeKinds.Contains(kind))
            && !excludeKinds.Contains(kind));
    }

    public int Apply(IList<SymbolRecord> symbols)
    {
        if (!IsActive || symbols.Count == 0)
            return 0;

        var before = symbols.Count;
        for (var i = symbols.Count - 1; i >= 0; i--)
        {
            var kind = symbols[i].Kind;
            if (ShouldDrop(kind))
                symbols.RemoveAt(i);
        }

        return before - symbols.Count;
    }

    public IndexSymbolKindFilterJsonResult ToJsonResult()
        => new()
        {
            Include = Include,
            Exclude = Exclude,
        };

    private bool ShouldDrop(string? kind)
    {
        if (string.IsNullOrWhiteSpace(kind))
            return _include.Count > 0;

        if (_include.Count > 0 && !_include.Contains(kind))
            return true;

        return _exclude.Contains(kind);
    }
}

public sealed class BackfillFoldCommandOptions
{
    public bool ShowHelp { get; init; }
    public string DbPath { get; init; } = Path.Combine(".cdidx", "codeindex.db");
    public bool Json { get; init; }
    public bool DryRun { get; init; }
    public bool ShowPaths { get; init; }
    public bool NoCheckpoint { get; init; }
    public string? ParseError { get; init; }
}

public sealed class OptimizeFtsCommandOptions
{
    public bool ShowHelp { get; init; }
    public string DbPath { get; init; } = Path.Combine(".cdidx", "codeindex.db");
    public bool Json { get; init; }
    public bool DryRun { get; init; }
    public bool ShowPaths { get; init; }
    public string? ParseError { get; init; }
}

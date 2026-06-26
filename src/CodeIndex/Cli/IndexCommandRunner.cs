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

    private sealed record ScanCheckpoint(
        int Version,
        string? GitHead,
        IReadOnlyList<string> Directories);

    internal sealed record ScanCheckpointLoadResult(
        IReadOnlySet<string> Directories,
        string? WarningMessage);

    internal static Action? FullScanWritePhaseStartedForTesting { get; set; }
    internal static Action<bool, string?>? FullScanExtractionSchedulingForTesting { get; set; }
    internal static Action<string>? FullScanFileContentLoadForTesting { get; set; }
    internal static Action? FullScanFtsOptimizeForTesting { get; set; }
    internal static Action? FullScanCSharpPrepassForTesting { get; set; }
    internal static Action? FullScanCSharpMetadataResolveForTesting { get; set; }
    internal static Action? FullScanTypeScriptAugmentationRebuildForTesting { get; set; }
    internal static Action? UpdateCSharpPrepassForTesting { get; set; }
    internal static Action? UpdateCSharpMetadataResolveForTesting { get; set; }
    internal static Action? UpdateTypeScriptAugmentationRebuildForTesting { get; set; }
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
    {
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

        dbPath = DbPathResolver.NormalizeDbPath(dbPath);
        var resolvedDbPath = Path.GetFullPath(dbPath);
        var databaseExistedBeforeIndex = File.Exists(LongPath.EnsureWindowsPrefix(resolvedDbPath));

        if (!options.Json && !options.Quiet)
        {
            ConsoleUi.PrintBanner();
            Console.WriteLine();
            Console.WriteLine($"  Project : {Path.GetFullPath(options.ProjectPath!)}");
            Console.WriteLine($"  Output  : {resolvedDbPath}");
            Console.WriteLine($"  Mode    : {(options.OptimizeOnly ? "optimize" : mode)}");
            Console.WriteLine();
        }

        if (options.OptimizeOnly)
            return RunOptimizeFtsForDb(resolvedDbPath, options.Json, jsonOptions, options.ProjectPath);

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
                    indexLock = IndexLock.Acquire(lockPath, options.ProjectPath);
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
                using var db = new DbContext(dbPath, indexCancellation.Token);
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
                var priorFoldVersion = db.GetMetaString("fold_key_version");
                var priorFoldFingerprint = db.GetMetaString("fold_key_fingerprint");
                var priorSymbolExtractorVersionsMatchCurrent = new DbWriter(db).SymbolExtractorVersionsMatchCurrent();
                var priorCSharpSymbolNameContractVersion = db.GetMetaString(DbContext.CSharpSymbolNameContractVersionMetaKey);
                var priorMetadataTargetCsharp = db.GetMetaString(DbContext.GetMetadataTargetVersionMetaKey("csharp"));
                var priorSqlGraphContractVersion = db.GetMetaString(DbContext.SqlGraphContractVersionMetaKey);
                var priorSymbolsOnlyGraphOmitted = string.Equals(
                    db.GetMetaString(DbContext.SymbolsOnlyGraphOmittedMetaKey),
                    "true",
                    StringComparison.OrdinalIgnoreCase);
                var priorHotspotFamilyVersions = GetHotspotFamilyMetaSnapshot(db, DbContext.GetHotspotFamilyVersionMetaKey);
                var priorHotspotFamilyMarkerFingerprints = GetHotspotFamilyMetaSnapshot(db, DbContext.GetHotspotFamilyMarkerFingerprintMetaKey);
                var priorIndexedProjectRoot = db.GetMetaString(DbContext.IndexedProjectRootMetaKey);
                var priorSymbolKindFilterSignature = db.GetMetaString(SymbolKindFilterMetaKey);
                // Captured BEFORE `--rebuild` drops the DB so an incremental run can warn the user when
                // the worktree's HEAD has moved since the previously indexed snapshot. The same value
                // is read at `status` time (without `--check`) to surface a worktree branch / HEAD
                // switch via `worktree_head_changed`. Issues #1508 and #1512.
                // `--rebuild` が DB を消す前に取り出す。incremental 経路で HEAD 差分を検知し、`status`
                // (no `--check`) でも worktree の HEAD 切替検出に利用する。
                var priorIndexedHeadCommit = db.GetMetaString(DbContext.IndexedHeadCommitMetaKey);
                var currentHeadCommit = GitHelper.TryGetHeadCommit(options.ProjectPath, indexCancellation.Token);

                // Don't demote readiness yet. A transient usage error in update-mode preflight
                // (bad --commits hash, git unavailable, etc.) would permanently downgrade a healthy
                // DB even though no data was touched. Clearing happens just before the first
                // destructive / schema-changing operation, inside the mode-specific runner.
                // まだ clear しない。update モードの preflight が失敗しただけで healthy な DB を
                // 縮退状態に落とさないよう、clear は実際に書き込み直前で行う。

                db.InitializeSchema();
                var indexRunDiagnostics = new List<string>();
                AddToGitExclude(options.ProjectPath, dbPath, indexRunDiagnostics, indexCancellation.Token);

                var writer = new DbWriter(db);
                var indexer = new FileIndexer(
                    options.ProjectPath,
                    ignoreCase,
                    ignoreRuleRoot,
                    options.MaxFileSizeBytes,
                    directoryIgnoreCaseProbe: null,
                    symlinkPolicy: options.SymlinkPolicy,
                    generatedCodePatterns: options.GeneratedCodePatterns);
                var currentHotspotFamilyMarkerFingerprints = GetHotspotFamilyMarkerFingerprints(indexer, indexCancellation.Token);
                var projectRoot = Path.GetFullPath(options.ProjectPath!);

                initialExitCode = isUpdateMode
                    ? RunUpdateMode(writer, indexer, projectRoot, resolvedDbPath, options, stopwatch, runStartedAtUtc, spinnerFrames, jsonOptions, priorReadiness, priorSymbolsOnlyGraphOmitted, priorFoldVersion, priorFoldFingerprint, priorSymbolExtractorVersionsMatchCurrent, priorCSharpSymbolNameContractVersion, priorMetadataTargetCsharp, priorSqlGraphContractVersion, priorHotspotFamilyVersions, priorHotspotFamilyMarkerFingerprints, currentHotspotFamilyMarkerFingerprints, priorIndexedProjectRoot, priorIndexedHeadCommit, currentHeadCommit, priorSymbolKindFilterSignature, initialCwd, indexRunDiagnostics, indexCancellation.Token)
                    : RunFullScan(writer, indexer, projectRoot, resolvedDbPath, options, stopwatch, runStartedAtUtc, spinnerFrames, jsonOptions, priorReadiness, priorSymbolsOnlyGraphOmitted, priorFoldVersion, priorFoldFingerprint, priorSymbolExtractorVersionsMatchCurrent, priorCSharpSymbolNameContractVersion, priorMetadataTargetCsharp, priorSqlGraphContractVersion, priorHotspotFamilyVersions, priorHotspotFamilyMarkerFingerprints, currentHotspotFamilyMarkerFingerprints, priorIndexedProjectRoot, priorIndexedHeadCommit, currentHeadCommit, priorSymbolKindFilterSignature, initialCwd, indexRunDiagnostics, showNextSteps: !databaseExistedBeforeIndex, indexCancellation.Token);
                if (initialExitCode == CommandExitCodes.Success)
                {
                    var plannerMaintenanceFailure = db.RunPlannerStatisticsMaintenance(forceAnalyze: !databaseExistedBeforeIndex);
                    if (plannerMaintenanceFailure != null)
                        TryStampPlannerStatisticsMaintenanceDiagnostic(writer, indexRunDiagnostics, plannerMaintenanceFailure);
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

        if (!options.Watch || initialExitCode != CommandExitCodes.Success)
            return initialExitCode;

        // Release the index lock before entering the watch loop so concurrent
        // `cdidx index` invocations between batches can still acquire it. Each
        // partial-update batch re-acquires the lock through IndexCommandRunner.Run.
        // watch ループ突入前にロックを解放し、バッチ間に別プロセスの `cdidx index` が
        // 取得できる状態にする。各バッチ更新はサブ実行で再取得する。
        return IndexWatchRunner.Run(options, jsonOptions, Path.GetFullPath(options.ProjectPath!), Path.GetFullPath(dbPath));
    }

    private static string DescribeLockHolder(IndexLockInfo? holder)
    {
        if (holder == null)
            return string.Empty;
        var startedLocal = holder.StartedAt.ToLocalTime();
        var verification = holder.Verification switch
        {
            IndexLockHolderVerification.Verified => "verified",
            IndexLockHolderVerification.Stale => "stale",
            _ => "unverified",
        };
        return $"PID {holder.Pid.ToString(System.Globalization.CultureInfo.InvariantCulture)} ({verification}), started {startedLocal.ToString("yyyy-MM-dd HH:mm:ss zzz", System.Globalization.CultureInfo.InvariantCulture)}";
    }
    private static Dictionary<string, string?> GetHotspotFamilyMetaSnapshot(DbContext db, Func<string, string> keyFactory)
    {
        var values = new Dictionary<string, string?>(StringComparer.Ordinal);
        foreach (var lang in FileIndexer.GetHotspotFamilyMarkerLanguages())
            values[lang] = db.GetMetaString(keyFactory(lang));
        return values;
    }

    private static IndexMemorySampleJsonResult CaptureMemorySample(string phase, Stopwatch stopwatch)
    {
        var snapshot = ProcessMemorySnapshot.Capture();
        return new IndexMemorySampleJsonResult
        {
            Phase = phase,
            ElapsedMs = stopwatch.ElapsedMilliseconds,
            HeapBytes = snapshot.HeapBytes,
            TotalAllocatedBytes = snapshot.TotalAllocatedBytes,
            GcHeapSizeBytes = snapshot.GcHeapSizeBytes,
            FragmentedBytes = snapshot.FragmentedBytes,
            WorkingSetBytes = snapshot.WorkingSetBytes,
            Gen0Collections = snapshot.Gen0Collections,
            Gen1Collections = snapshot.Gen1Collections,
            Gen2Collections = snapshot.Gen2Collections,
        };
    }

    private static IndexMemoryTimelineJsonResult? BuildMemoryTimeline(List<IndexMemorySampleJsonResult> samples)
    {
        if (samples.Count == 0)
            return null;

        return new IndexMemoryTimelineJsonResult
        {
            Samples = samples,
            PeakWorkingSetBytes = samples.Max(static sample => sample.WorkingSetBytes),
            PeakHeapBytes = samples.Max(static sample => sample.HeapBytes),
        };
    }

    private static void WarnIfMemoryThresholdExceeded(IndexMemoryTimelineJsonResult? timeline)
    {
        var rawThreshold = Environment.GetEnvironmentVariable("CDIDX_MEM_WARN_MB");
        if (timeline == null || !long.TryParse(rawThreshold, System.Globalization.NumberStyles.Integer, System.Globalization.CultureInfo.InvariantCulture, out var thresholdMb) || thresholdMb <= 0)
            return;

        var peakMb = timeline.PeakWorkingSetBytes / (1024 * 1024);
        if (peakMb >= thresholdMb)
            CommandErrorWriter.WriteStderr($"Warning: cdidx working set reached {peakMb:N0} MB (CDIDX_MEM_WARN_MB={thresholdMb:N0}).");
    }

    private static void StampLastIndexRunMetadata(
        DbWriter writer,
        string mode,
        DateTime startedAtUtc,
        long durationMs,
        long filesScanned,
        long filesSkipped,
        long parseErrors,
        long bytesRead,
        long bytesReadSkippedFileCount,
        long rowsUpserted,
        long rowsDeleted,
        IndexMemoryTimelineJsonResult? memoryTimeline)
        => StampLastIndexRunMetadata(
            writer,
            mode,
            startedAtUtc,
            durationMs,
            filesScanned,
            filesSkipped,
            parseErrors,
            bytesRead,
            bytesReadSkippedFileCount,
            rowsUpserted,
            rowsDeleted,
            memoryTimeline,
            diagnostics: null);

    private static void StampLastIndexRunMetadata(
        DbWriter writer,
        string mode,
        DateTime startedAtUtc,
        long durationMs,
        long filesScanned,
        long filesSkipped,
        long parseErrors,
        long bytesRead,
        long bytesReadSkippedFileCount,
        long rowsUpserted,
        long rowsDeleted,
        IndexMemoryTimelineJsonResult? memoryTimeline,
        IReadOnlyList<string>? diagnostics)
    {
        writer.SetMeta(DbContext.LastIndexRunModeMetaKey, mode);
        writer.SetMeta(DbContext.LastIndexRunStartedAtMetaKey, startedAtUtc.ToString("o", System.Globalization.CultureInfo.InvariantCulture));
        writer.SetMeta(DbContext.LastIndexRunDurationMsMetaKey, durationMs.ToString(System.Globalization.CultureInfo.InvariantCulture));
        writer.SetMeta(DbContext.LastIndexRunFilesScannedMetaKey, filesScanned.ToString(System.Globalization.CultureInfo.InvariantCulture));
        writer.SetMeta(DbContext.LastIndexRunFilesSkippedMetaKey, filesSkipped.ToString(System.Globalization.CultureInfo.InvariantCulture));
        writer.SetMeta(DbContext.LastIndexRunParseErrorsMetaKey, parseErrors.ToString(System.Globalization.CultureInfo.InvariantCulture));
        writer.SetMeta(DbContext.LastIndexRunBytesReadMetaKey, bytesRead.ToString(System.Globalization.CultureInfo.InvariantCulture));
        writer.SetMeta(DbContext.LastIndexRunBytesReadSkippedFileCountMetaKey, bytesReadSkippedFileCount.ToString(System.Globalization.CultureInfo.InvariantCulture));
        writer.SetMeta(DbContext.LastIndexRunBytesReadIncompleteMetaKey, (bytesReadSkippedFileCount > 0).ToString(System.Globalization.CultureInfo.InvariantCulture));
        writer.SetMeta(DbContext.LastIndexRunRowsUpsertedMetaKey, rowsUpserted.ToString(System.Globalization.CultureInfo.InvariantCulture));
        writer.SetMeta(DbContext.LastIndexRunRowsDeletedMetaKey, rowsDeleted.ToString(System.Globalization.CultureInfo.InvariantCulture));
        writer.SetMeta(DbContext.LastIndexRunPeakMemoryMbMetaKey, memoryTimeline == null
            ? null
            : (memoryTimeline.PeakWorkingSetBytes / (1024 * 1024)).ToString(System.Globalization.CultureInfo.InvariantCulture));
        StampLastIndexRunDiagnostics(writer, diagnostics);
        writer.ClearLastFailedIndexRunMetadata();
    }

    internal static void StampLastIndexRunDiagnostics(DbWriter writer, IReadOnlyList<string>? diagnostics)
    {
        var total = diagnostics?.Count ?? 0;
        if (total == 0)
        {
            writer.SetMeta(DbContext.LastIndexRunDiagnosticsMetaKey, null);
            writer.SetMeta(DbContext.LastIndexRunDiagnosticCountMetaKey, null);
            writer.SetMeta(DbContext.LastIndexRunDiagnosticsTruncatedMetaKey, null);
            return;
        }

        var sample = JsonStringListCodec.TakeSerializableSample(
            diagnostics!,
            DbContext.LastIndexRunDiagnosticSampleLimit);
        writer.SetMeta(
            DbContext.LastIndexRunDiagnosticsMetaKey,
            JsonStringListCodec.Serialize(sample));
        writer.SetMeta(
            DbContext.LastIndexRunDiagnosticCountMetaKey,
            total.ToString(System.Globalization.CultureInfo.InvariantCulture));
        writer.SetMeta(
            DbContext.LastIndexRunDiagnosticsTruncatedMetaKey,
            (total > sample.Count).ToString(System.Globalization.CultureInfo.InvariantCulture));
    }

    internal static Action<DbWriter, IReadOnlyList<string>>? PlannerStatisticsMaintenanceDiagnosticStampingForTesting
    {
        get => ScopedPlannerStatisticsMaintenanceDiagnosticStampingForTesting.Value;
        set => ScopedPlannerStatisticsMaintenanceDiagnosticStampingForTesting.Value = value;
    }

    internal static bool TryStampPlannerStatisticsMaintenanceDiagnostic(
        DbWriter writer,
        List<string> indexRunDiagnostics,
        DbContext.PlannerStatisticsMaintenanceFailure plannerMaintenanceFailure)
    {
        indexRunDiagnostics.Add(FormatPlannerStatisticsMaintenanceDiagnostic(plannerMaintenanceFailure));
        try
        {
            PlannerStatisticsMaintenanceDiagnosticStampingForTesting?.Invoke(writer, indexRunDiagnostics);
            StampLastIndexRunDiagnostics(writer, indexRunDiagnostics);
            return true;
        }
        catch (Exception ex)
        {
            GlobalToolLog.Error("planner_statistics_maintenance_diagnostic_persist_failed", ex, includeStacks: false);
            return false;
        }
    }

    internal static string FormatIndexRunDiagnostic(string code, Exception ex)
    {
        var raw = $"{code}: {ex.GetType().Name}: {CollapseLineBreaks(ex.Message)}";
        return raw.Length <= MaxIndexRunDiagnosticLength
            ? raw
            : raw[..MaxIndexRunDiagnosticLength] + "...<truncated>";
    }

    internal static string FormatIndexRunDiagnostic(string code, string? target, Exception ex)
    {
        if (string.IsNullOrWhiteSpace(target))
            return FormatIndexRunDiagnostic(code, ex);

        var raw = $"{code}: {CollapseLineBreaks(target)}: {ex.GetType().Name}: {CollapseLineBreaks(ex.Message)}";
        return raw.Length <= MaxIndexRunDiagnosticLength
            ? raw
            : raw[..MaxIndexRunDiagnosticLength] + "...<truncated>";
    }

    internal static string FormatPlannerStatisticsMaintenanceDiagnostic(DbContext.PlannerStatisticsMaintenanceFailure failure)
        => FormatIndexRunDiagnostic(
            "planner_statistics_maintenance_failed",
            failure.CommandText,
            failure.Exception);

    private static void RecordIndexRunDiagnostic(List<string>? diagnostics, string code, Exception ex)
    {
        if (diagnostics == null)
            return;

        diagnostics.Add(FormatIndexRunDiagnostic(code, ex));
    }

    private static void RecordIndexRunDiagnostic(List<string>? diagnostics, string code, string? target, Exception ex)
    {
        if (diagnostics == null)
            return;

        diagnostics.Add(FormatIndexRunDiagnostic(code, target, ex));
    }

    private static void TryStampLastFailedIndexRun(
        string dbPath,
        string status,
        string mode,
        DateTime startedAtUtc,
        long durationMs,
        long? filesProcessed,
        long? filesTotal,
        string errorCode,
        string reason,
        bool? progressPersisted = null,
        string? recoveryHint = null)
    {
        if (string.IsNullOrWhiteSpace(dbPath)
            || dbPath.StartsWith("file:", StringComparison.OrdinalIgnoreCase)
            || !File.Exists(dbPath))
        {
            return;
        }

        try
        {
            using var db = new DbContext(dbPath);
            db.InitializeSchema();
            var writer = new DbWriter(db);
            writer.SetMeta(DbContext.LastFailedIndexRunStatusMetaKey, status);
            writer.SetMeta(DbContext.LastFailedIndexRunModeMetaKey, mode);
            writer.SetMeta(DbContext.LastFailedIndexRunStartedAtMetaKey, startedAtUtc.ToString("o", System.Globalization.CultureInfo.InvariantCulture));
            writer.SetMeta(DbContext.LastFailedIndexRunDurationMsMetaKey, durationMs.ToString(System.Globalization.CultureInfo.InvariantCulture));
            writer.SetMeta(DbContext.LastFailedIndexRunFilesProcessedMetaKey, filesProcessed?.ToString(System.Globalization.CultureInfo.InvariantCulture));
            writer.SetMeta(DbContext.LastFailedIndexRunFilesTotalMetaKey, filesTotal?.ToString(System.Globalization.CultureInfo.InvariantCulture));
            writer.SetMeta(DbContext.LastFailedIndexRunErrorCodeMetaKey, errorCode);
            writer.SetMeta(DbContext.LastFailedIndexRunReasonMetaKey, reason);
            writer.SetMeta(DbContext.LastFailedIndexRunProgressPersistedMetaKey, progressPersisted?.ToString(System.Globalization.CultureInfo.InvariantCulture));
            writer.SetMeta(DbContext.LastFailedIndexRunRecoveryHintMetaKey, recoveryHint);
        }
        catch (Exception ex) when (ex is CodeIndexException or IOException or UnauthorizedAccessException or NotSupportedException or SqliteException)
        {
        }
    }

    internal static FileByteReadSummary MeasureReadableFileBytes(
        IEnumerable<string> paths,
        string? projectRoot = null,
        List<string>? diagnostics = null,
        IReadOnlyDictionary<string, long>? knownFileSizes = null)
    {
        long total = 0;
        long skipped = 0;
        foreach (var path in paths)
        {
            if (knownFileSizes != null && knownFileSizes.TryGetValue(path, out var knownSize))
            {
                total += knownSize;
                continue;
            }

            try
            {
                var info = new FileInfo(path);
                if (info.Exists)
                    total += info.Length;
            }
            catch (Exception ex) when (ex is IOException or UnauthorizedAccessException or NotSupportedException or ArgumentException)
            {
                skipped++;
                RecordIndexRunDiagnostic(diagnostics, "file_size_bytes_skipped", FormatDiagnosticPath(projectRoot, path), ex);
            }
        }

        return new FileByteReadSummary(total, skipped);
    }

    private static string FormatDiagnosticPath(string? projectRoot, string path)
    {
        if (string.IsNullOrWhiteSpace(projectRoot))
            return path;

        try
        {
            var relative = FileIndexer.NormalizePathSeparators(Path.GetRelativePath(projectRoot, path));
            return IsOutsideProjectRoot(relative) ? path : relative;
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException or NotSupportedException or ArgumentException)
        {
            return path;
        }
    }

    private static Dictionary<string, FileIndexer.ProjectMarkerFingerprintResult> GetHotspotFamilyMarkerFingerprints(
        FileIndexer indexer,
        CancellationToken cancellationToken = default)
    {
        var values = new Dictionary<string, FileIndexer.ProjectMarkerFingerprintResult>(StringComparer.Ordinal);
        foreach (var lang in FileIndexer.GetHotspotFamilyMarkerLanguages())
            values[lang] = indexer.GetProjectMarkerFingerprintResult(lang, cancellationToken);
        return values;
    }

    private static int AddProjectMarkerFingerprintWarnings(
        IReadOnlyDictionary<string, FileIndexer.ProjectMarkerFingerprintResult> currentFingerprints,
        List<CliJsonMessage> warningList,
        IndexCommandOptions options)
    {
        var added = 0;
        var seen = new HashSet<string>(StringComparer.Ordinal);
        foreach (var fingerprint in currentFingerprints.Values)
        {
            foreach (var warning in fingerprint.Warnings)
            {
                if (!IsProjectMarkerFingerprintWarning(warning))
                    continue;

                var path = string.IsNullOrWhiteSpace(warning.Path)
                    ? "<project_marker_fingerprint>"
                    : warning.Path;
                var key = $"{path}\0{warning.Message}";
                if (!seen.Add(key))
                    continue;

                warningList.Add(new CliJsonMessage(path, warning.Message));
                added++;
                if (!options.Json && !options.Quiet)
                    ConsoleUi.PrintWarning($"{path}: {warning.Message}");
            }
        }

        return added;
    }

    private static bool IsProjectMarkerFingerprintWarning(FileIndexer.ScanError warning) =>
        warning.Message.StartsWith("Project marker discovery skipped", StringComparison.Ordinal)
        || warning.Message.StartsWith("Project marker discovery truncated", StringComparison.Ordinal)
        || warning.Message.StartsWith("Skipped .gitmodules", StringComparison.Ordinal);

    private static void RestampHotspotFamilyTrustForUpdate(
        DbWriter writer,
        IReadOnlyDictionary<string, string?> priorVersions,
        IReadOnlyDictionary<string, string?> priorFingerprints,
        IReadOnlyDictionary<string, FileIndexer.ProjectMarkerFingerprintResult> currentFingerprints)
    {
        var currentVersion = DbContext.HotspotFamilyVersion.ToString(System.Globalization.CultureInfo.InvariantCulture);
        foreach (var lang in FileIndexer.GetHotspotFamilyMarkerLanguages())
        {
            if (!currentFingerprints.TryGetValue(lang, out var currentFingerprint))
                continue;

            if (!currentFingerprint.IsComplete)
            {
                writer.MarkHotspotFamilyMarkerFingerprintIncomplete(lang, currentFingerprint.Fingerprint);
                continue;
            }

            if (priorVersions.TryGetValue(lang, out var priorVersion)
                && priorFingerprints.TryGetValue(lang, out var priorFingerprint)
                && priorVersion == currentVersion
                && priorFingerprint == currentFingerprint.Fingerprint)
            {
                writer.MarkHotspotFamilyReady(lang, currentFingerprint.Fingerprint);
            }
        }
    }

    private static void RestampHotspotFamilyTrustForFullScan(
        DbWriter writer,
        IReadOnlySet<string> reusedLanguages,
        IReadOnlyDictionary<string, string?> priorVersions,
        IReadOnlyDictionary<string, string?> priorFingerprints,
        IReadOnlyDictionary<string, FileIndexer.ProjectMarkerFingerprintResult> currentFingerprints)
    {
        var currentVersion = DbContext.HotspotFamilyVersion.ToString(System.Globalization.CultureInfo.InvariantCulture);
        foreach (var lang in FileIndexer.GetHotspotFamilyMarkerLanguages())
        {
            if (!currentFingerprints.TryGetValue(lang, out var currentFingerprint))
                continue;

            if (!currentFingerprint.IsComplete)
            {
                writer.MarkHotspotFamilyMarkerFingerprintIncomplete(lang, currentFingerprint.Fingerprint);
                continue;
            }

            priorVersions.TryGetValue(lang, out var priorVersion);
            priorFingerprints.TryGetValue(lang, out var priorFingerprint);
            if (!reusedLanguages.Contains(lang) || (priorVersion == currentVersion && priorFingerprint == currentFingerprint.Fingerprint))
                writer.MarkHotspotFamilyReady(lang, currentFingerprint.Fingerprint);
        }
    }

    private static Dictionary<string, bool> GetHotspotFamilyTrustMatchesCurrent(
        IReadOnlyDictionary<string, string?> priorVersions,
        IReadOnlyDictionary<string, string?> priorFingerprints,
        IReadOnlyDictionary<string, FileIndexer.ProjectMarkerFingerprintResult> currentFingerprints)
    {
        var currentVersion = DbContext.HotspotFamilyVersion.ToString(System.Globalization.CultureInfo.InvariantCulture);
        var values = new Dictionary<string, bool>(StringComparer.Ordinal);
        foreach (var lang in FileIndexer.GetHotspotFamilyMarkerLanguages())
        {
            currentFingerprints.TryGetValue(lang, out var currentFingerprint);
            priorVersions.TryGetValue(lang, out var priorVersion);
            priorFingerprints.TryGetValue(lang, out var priorFingerprint);
            values[lang] = currentFingerprint.IsComplete
                && priorVersion == currentVersion
                && priorFingerprint == currentFingerprint.Fingerprint;
        }

        return values;
    }

    private static bool AllowReuseWithCurrentHotspotFamilyTrust(
        string? lang,
        IReadOnlyDictionary<string, bool> hotspotFamilyTrustMatchesCurrent)
    {
        if (!FileIndexer.SupportsHotspotFamilyMarkerLanguage(lang))
            return true;

        return lang != null
            && hotspotFamilyTrustMatchesCurrent.TryGetValue(lang, out var matchesCurrent)
            && matchesCurrent;
    }

    internal static bool IsOutsideProjectRoot(string relativePath)
    {
        if (Path.IsPathRooted(relativePath))
            return true;

        var normalized = OperatingSystem.IsWindows()
            ? relativePath.Replace('\\', '/')
            : relativePath;
        return normalized == ".." || normalized.StartsWith("../", StringComparison.Ordinal);
    }

    private static bool ContainsIgnoreFilePath(IEnumerable<string> paths)
        => paths.Any(FileIndexer.IsIgnoreFilePath);

    private static bool ContainsJavaScriptTypeScriptConfigPath(IEnumerable<string> paths)
        => paths.Any(IsJavaScriptTypeScriptConfigPath);

    private static bool IsJavaScriptTypeScriptConfigPath(string path)
    {
        var fileName = Path.GetFileName(path);
        return string.Equals(fileName, "jsconfig.json", StringComparison.OrdinalIgnoreCase)
            || string.Equals(fileName, "tsconfig.json", StringComparison.OrdinalIgnoreCase)
            || (fileName.StartsWith("jsconfig.", StringComparison.OrdinalIgnoreCase)
                && fileName.EndsWith(".json", StringComparison.OrdinalIgnoreCase))
            || (fileName.StartsWith("tsconfig.", StringComparison.OrdinalIgnoreCase)
                && fileName.EndsWith(".json", StringComparison.OrdinalIgnoreCase));
    }

    private static bool ContainsRelevantIgnoreFileUpdate(string projectRoot, IEnumerable<string> updateFiles)
    {
        foreach (var file in updateFiles)
        {
            var absolutePath = Path.IsPathRooted(file)
                ? Path.GetFullPath(file)
                : Path.GetFullPath(Path.Combine(projectRoot, file));
            if (FileIndexer.IsIgnoreFilePath(absolutePath) && IsRelevantIgnoreFileForProjectRoot(projectRoot, absolutePath))
                return true;
        }

        return false;
    }

    private static IReadOnlyList<string> NormalizeCommitFileTargets(
        string projectRoot,
        string repoRoot,
        IEnumerable<string> changedFiles,
        out bool relevantIgnoreFileChanged)
    {
        relevantIgnoreFileChanged = false;
        var normalized = new List<string>();
        foreach (var changedFile in changedFiles)
        {
            var absolutePath = Path.GetFullPath(Path.Combine(repoRoot, changedFile.Replace('/', Path.DirectorySeparatorChar)));
            if (FileIndexer.IsIgnoreFilePath(absolutePath) && IsRelevantIgnoreFileForProjectRoot(projectRoot, absolutePath))
                relevantIgnoreFileChanged = true;

            var relativePath = FileIndexer.NormalizePathSeparators(Path.GetRelativePath(projectRoot, absolutePath));
            if (IsOutsideProjectRoot(relativePath))
                continue;

            normalized.Add(relativePath);
        }

        return normalized;
    }

    private static bool IsRelevantIgnoreFileForProjectRoot(string projectRoot, string ignoreFileAbsolutePath)
    {
        var ignoreDirectory = Path.GetDirectoryName(ignoreFileAbsolutePath);
        if (string.IsNullOrEmpty(ignoreDirectory))
            return false;

        return IsPathEqualOrParent(ignoreDirectory, projectRoot)
            || IsPathEqualOrParent(projectRoot, ignoreDirectory);
    }

    private static string DescribePathFilter(FileIndexer.PathFilterKind filterKind)
        => filterKind switch
        {
            FileIndexer.PathFilterKind.IgnoredByRules => "ignored by .gitignore/.cdidxignore",
            FileIndexer.PathFilterKind.ExcludedByDefaultDirectory => "excluded by default directory rules",
            FileIndexer.PathFilterKind.ExcludedByDefaultFile => "excluded by default file rules",
            FileIndexer.PathFilterKind.OutsideProjectRoot => "outside the project root",
            FileIndexer.PathFilterKind.IgnoreRulesUnavailable => "ignore rules unavailable",
            _ => "filtered",
        };

    private static IReadOnlyList<string> NormalizeUpdateFileTargets(string projectRoot, IEnumerable<string> updateFiles, bool json)
    {
        var normalized = new List<string>();
        foreach (var file in updateFiles)
        {
            var absPath = Path.IsPathRooted(file) ? file : Path.GetFullPath(Path.Combine(projectRoot, file));
            var relPath = FileIndexer.NormalizePathSeparators(Path.GetRelativePath(projectRoot, absPath));
            if (IsOutsideProjectRoot(relPath))
            {
                if (!json)
                    CommandErrorWriter.WriteStderr($"  [WARN] Skipping file outside project root: {file}. Use a path under the indexed project root or run `cdidx index` from the correct workspace.");
                continue;
            }

            normalized.Add(relPath);
        }

        return normalized;
    }

    private static bool IsPathEqualOrParent(string candidateParent, string candidateChild)
    {
        var normalizedParent = Path.GetFullPath(candidateParent)
            .TrimEnd(Path.DirectorySeparatorChar, Path.AltDirectorySeparatorChar);
        var normalizedChild = Path.GetFullPath(candidateChild)
            .TrimEnd(Path.DirectorySeparatorChar, Path.AltDirectorySeparatorChar);
        return PathCasing.IsPathEqualOrParent(normalizedParent, normalizedChild);
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
    private static void StampIndexedHeadMetadata(DbWriter writer, string projectRoot, List<string>? diagnostics, CancellationToken cancellationToken)
    {
        try
        {
            var headSha = GitHelper.TryGetHeadCommit(projectRoot, cancellationToken);
            var headBranch = GitHelper.TryGetHeadBranch(projectRoot, cancellationToken);
            var timestamp = headSha != null
                ? GetUtcNow().ToString("o", System.Globalization.CultureInfo.InvariantCulture)
                : null;
            writer.SetMeta(DbContext.IndexedHeadShaMetaKey, headSha);
            writer.SetMeta(DbContext.IndexedHeadBranchMetaKey, headBranch);
            writer.SetMeta(DbContext.IndexedHeadTimestampMetaKey, timestamp);
        }
        catch (OperationCanceledException) when (cancellationToken.IsCancellationRequested)
        {
            throw;
        }
        catch (Exception ex)
        {
            // Best-effort metadata only; never fail an otherwise-successful index run.
            // best-effort であり、stamp の失敗で index 全体を失敗扱いにしない。
            RecordIndexRunDiagnostic(diagnostics, "indexed_head_metadata_write_failed", ex);
        }
        StampWorkspacePathCaseSensitivity(writer, projectRoot, diagnostics, cancellationToken);
    }

    private static void StampCommitScopedFreshHeadMetadata(
        DbWriter writer,
        IndexCommandOptions options,
        string projectRoot,
        string? currentHeadCommit,
        List<string>? diagnostics,
        CancellationToken cancellationToken = default)
    {
        try
        {
            var coveredHead = !string.IsNullOrWhiteSpace(currentHeadCommit)
                && (options.Commits.Any(commit => GitRefCoversCurrentHead(projectRoot, commit, currentHeadCommit, cancellationToken))
                    || TryChangedBetweenCoversCurrentHead(options, projectRoot, currentHeadCommit, cancellationToken))
                ? currentHeadCommit
                : null;
            writer.SetMeta(DbContext.CommitScopedFreshHeadShaMetaKey, coveredHead);
        }
        catch (OperationCanceledException) when (cancellationToken.IsCancellationRequested)
        {
            throw;
        }
        catch (Exception ex)
        {
            // Best-effort metadata only; never fail an otherwise-successful index run.
            // best-effort のみ。stamp 失敗で index 全体を落とさない。
            RecordIndexRunDiagnostic(diagnostics, "commit_scoped_head_metadata_write_failed", ex);
        }
    }

    private static bool GitRefCoversCurrentHead(
        string projectRoot,
        string refName,
        string currentHeadCommit,
        CancellationToken cancellationToken)
    {
        if (currentHeadCommit.StartsWith(refName, StringComparison.OrdinalIgnoreCase))
            return true;

        var resolvedRef = GitHelper.TryResolveCommit(projectRoot, refName, cancellationToken);
        return string.Equals(resolvedRef, currentHeadCommit, StringComparison.OrdinalIgnoreCase);
    }

    private static bool TryChangedBetweenCoversCurrentHead(
        IndexCommandOptions options,
        string projectRoot,
        string currentHeadCommit,
        CancellationToken cancellationToken)
    {
        if (options.ChangedBetweenRefs.Count != 2)
            return false;

        return GitRefCoversCurrentHead(projectRoot, options.ChangedBetweenRefs[1], currentHeadCommit, cancellationToken);
    }

    // Issue #1546: capture the actual case-sensitivity of the workspace filesystem so
    // `cdidx status` can diagnose phantom path collapses on case-sensitive APFS / WSL
    // NTFS / ReFS volumes (where the OS-keyed heuristic would mismatch reality). Probed
    // via the same `core.ignorecase` + filesystem probe used by FileIndexer, then
    // persisted as "true" / "false" alongside the HEAD stamp. Failures are swallowed so
    // an unwritable git config / temp probe never blocks an otherwise-successful index.
    // #1546: workspace FS の大小区別を実プローブして codeindex_meta に保存する。
    // probe 失敗時は黙って null stamp にして index 本体は成功扱いのままとする。
    private static void StampWorkspacePathCaseSensitivity(DbWriter writer, string projectRoot, List<string>? diagnostics, CancellationToken cancellationToken)
    {
        try
        {
            var ignoreCase = GitHelper.ResolveIgnoreCase(projectRoot, cancellationToken);
            PathCasing.SeedFromWorkspace(projectRoot, ignoreCase);
            var caseSensitive = (!ignoreCase).ToString(System.Globalization.CultureInfo.InvariantCulture);
            writer.SetMeta(DbContext.WorkspacePathCaseSensitiveMetaKey, caseSensitive);
        }
        catch (OperationCanceledException) when (cancellationToken.IsCancellationRequested)
        {
            throw;
        }
        catch (Exception ex)
        {
            // Best-effort metadata only; never fail an otherwise-successful index run.
            // best-effort のみ。stamp 失敗で index 全体を落とさない。
            RecordIndexRunDiagnostic(diagnostics, "path_case_sensitivity_metadata_write_failed", ex);
        }
    }

    private static void AddToGitExclude(
        string projectPath,
        string dbPath,
        List<string>? diagnostics,
        CancellationToken cancellationToken)
    {
        try
        {
            var projectRoot = Path.GetFullPath(projectPath);
            var gitDir = GitHelper.ResolveGitCommonDir(projectRoot, cancellationToken);
            if (gitDir == null) return;

            var excludeFile = Path.Combine(gitDir, "info", "exclude");
            var dbAbsolutePath = Path.IsPathRooted(dbPath)
                ? Path.GetFullPath(dbPath)
                : Path.GetFullPath(Path.Combine(projectRoot, dbPath));
            var dbDirAbsolute = Path.GetDirectoryName(dbAbsolutePath);
            if (string.IsNullOrEmpty(dbDirAbsolute)) return;

            var dbDirRelative = FileIndexer.NormalizePathSeparators(Path.GetRelativePath(projectRoot, dbDirAbsolute));
            if (IsOutsideProjectRoot(dbDirRelative)) return;

            string[] patterns;
            if (dbDirRelative == ".")
            {
                var dbFileName = Path.GetFileName(dbAbsolutePath);
                patterns = [dbFileName, $"{dbFileName}-*"];
            }
            else
            {
                patterns = [$"{dbDirRelative.TrimEnd('/')}/"];
            }

            var ioExcludeFile = LongPath.EnsureWindowsPrefix(excludeFile);
            var existingContent = File.Exists(ioExcludeFile)
                ? DataDirectorySecurity.ReadTextWithinLimit(ioExcludeFile, MaxGitExcludeBytes, FileShare.ReadWrite)
                : "";
            if (existingContent is null)
                return;

            var existingLines = existingContent.Split('\n').Select(l => l.TrimEnd('\r')).ToHashSet();

            var missing = patterns.Where(p => !existingLines.Contains(p)).ToList();
            if (missing.Count == 0) return;

            Directory.CreateDirectory(LongPath.EnsureWindowsPrefix(Path.GetDirectoryName(excludeFile)!));

            using var stream = new FileStream(
                ioExcludeFile,
                FileMode.Append,
                FileAccess.Write,
                FileShare.Read);
            using var sw = new StreamWriter(stream);
            if (existingContent.Length > 0 && !existingContent.EndsWith('\n'))
                sw.WriteLine();
            sw.WriteLine("# cdidx (CodeIndex) — auto-generated");
            foreach (var pattern in missing)
                sw.WriteLine(pattern);
        }
        catch (OperationCanceledException) when (cancellationToken.IsCancellationRequested)
        {
            throw;
        }
        catch (Exception ex)
        {
            RecordIndexRunDiagnostic(diagnostics, "git_exclude_metadata_write_failed", ex);
        }
    }

    private static string? GetStatReusableLanguage(
        string absolutePath,
        FileIndexer.LanguageDetectionResult detection)
    {
        if (string.Equals(Path.GetExtension(absolutePath), ".h", StringComparison.OrdinalIgnoreCase))
            return null;

        return detection.Status == FileIndexer.FileProbeStatus.Supported
            ? detection.Language
            : null;
    }

    private static long? TryGetUnchangedFileIdFromStat(
        DbWriter writer,
        string absolutePath,
        string relativePath,
        string? language,
        bool allowReuse,
        out long? size)
    {
        size = null;
        if (!allowReuse || language == null)
            return null;

        try
        {
            var info = new FileInfo(absolutePath);
            if (!info.Exists)
                return null;

            size = info.Length;
            return writer.GetUnchangedFileId(
                relativePath,
                info.LastWriteTimeUtc,
                checksum: null,
                size: info.Length,
                language: language);
        }
        catch (IOException)
        {
            return null;
        }
        catch (UnauthorizedAccessException)
        {
            return null;
        }
    }

    private readonly record struct FullScanFileTarget(
        string FilePath,
        string RelativePath,
        string DisplayRelativePath,
        string IndexPath,
        string? Language)
    {
        public static FullScanFileTarget CreateFromPath(string projectRoot, string path)
        {
            var filePath = Path.IsPathRooted(path)
                ? path
                : Path.Combine(projectRoot, path.Replace('/', Path.DirectorySeparatorChar));
            return Create(projectRoot, filePath);
        }

        public static FullScanFileTarget Create(string projectRoot, string filePath, string? language = null)
        {
            var relativePath = Path.GetRelativePath(projectRoot, filePath);
            return new FullScanFileTarget(
                filePath,
                relativePath,
                FileIndexer.NormalizePathSeparators(relativePath),
                FileIndexer.NormalizeIndexPath(relativePath),
                language);
        }
    }

    private sealed record FullScanFileWorkItem(
        string FilePath,
        string RelativePath,
        FileRecord? Record,
        string? Content,
        byte[]? RawBytes,
        FileContentInspection? Inspection,
        bool? HasOversizeLine,
        string? Warning,
        IReadOnlyList<ChunkRecord>? Chunks,
        IReadOnlyList<SymbolRecord>? Symbols,
        IReadOnlyList<ReferenceRecord>? References,
        IReadOnlyList<FileIssue>? Issues,
        FileIssue? GeneratedSuppressionIssue,
        bool GeneratedSuppressionChecked,
        Exception? Exception)
    {
        public static FullScanFileWorkItem Success(
            string filePath,
            string relativePath,
            FileRecord record,
            string? content,
            byte[]? rawBytes,
            FileContentInspection? inspection,
            bool hasOversizeLine,
            string? warning,
            IReadOnlyList<ChunkRecord>? chunks,
            IReadOnlyList<SymbolRecord>? symbols,
            IReadOnlyList<ReferenceRecord>? references,
            IReadOnlyList<FileIssue>? issues,
            FileIssue? generatedSuppressionIssue,
            bool generatedSuppressionChecked)
        {
            return new FullScanFileWorkItem(
                filePath,
                relativePath,
                record,
                content,
                rawBytes,
                inspection,
                hasOversizeLine,
                warning,
                chunks,
                symbols,
                references,
                issues,
                generatedSuppressionIssue,
                generatedSuppressionChecked,
                null);
        }

        public static FullScanFileWorkItem Precomputed(
            string filePath,
            string relativePath,
            FileRecord record,
            string? warning,
            IReadOnlyList<ChunkRecord> chunks,
            IReadOnlyList<SymbolRecord> symbols,
            IReadOnlyList<ReferenceRecord> references,
            IReadOnlyList<FileIssue> issues,
            FileIssue? generatedSuppressionIssue = null,
            bool generatedSuppressionChecked = false)
        {
            return new FullScanFileWorkItem(
                filePath,
                relativePath,
                record,
                null,
                null,
                null,
                null,
                warning,
                chunks,
                symbols,
                references,
                issues,
                generatedSuppressionIssue,
                generatedSuppressionChecked,
                null);
        }

        public static FullScanFileWorkItem Failure(string filePath, string relativePath, Exception exception)
            => new(filePath, relativePath, null, null, null, null, null, null, null, null, null, null, null, false, exception);

        public static FullScanFileWorkItem Skipped(string filePath, string relativePath, string warning)
            => new(filePath, relativePath, null, null, null, null, null, warning, null, null, null, null, null, false, null);
    }

    private sealed record FoldOnlyRemediation(
        string DegradedReason,
        string RecommendedAction,
        string AlternativeAction);

    private sealed class IndexInterruptedException : OperationCanceledException
    {
        public IndexInterruptedException(int filesProcessed, int? filesTotal, string? actualMode = null)
            : base("Indexing was interrupted.")
        {
            FilesProcessed = filesProcessed;
            FilesTotal = filesTotal;
            ActualMode = actualMode;
        }

        public int FilesProcessed { get; }
        public int? FilesTotal { get; }
        public string? ActualMode { get; }
    }

    private sealed class IndexExtractionStalledException : Exception
    {
        public IndexExtractionStalledException(int filesProcessed, int? filesTotal, TimeSpan timeout, string? activePath, string? workerError = null)
            : base("Index extraction stalled.")
        {
            FilesProcessed = filesProcessed;
            FilesTotal = filesTotal;
            Timeout = timeout;
            ActivePath = activePath;
            WorkerError = workerError;
        }

        public int FilesProcessed { get; }
        public int? FilesTotal { get; }
        public TimeSpan Timeout { get; }
        public string? ActivePath { get; }
        public string? WorkerError { get; }
    }

    private sealed class CancelKeyPressRegistration(ConsoleCancelEventHandler handler) : IDisposable
    {
        public void Dispose()
        {
            Console.CancelKeyPress -= handler;
        }
    }

    private sealed class NullDisposable : IDisposable
    {
        public static readonly NullDisposable Instance = new();

        public void Dispose()
        {
        }
    }
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
    public bool NoCheckpoint { get; init; }
    public string? ParseError { get; init; }
}

public sealed class OptimizeFtsCommandOptions
{
    public bool ShowHelp { get; init; }
    public string DbPath { get; init; } = Path.Combine(".cdidx", "codeindex.db");
    public bool Json { get; init; }
    public string? ParseError { get; init; }
}

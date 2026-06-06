using System.Collections.Concurrent;
using System.Diagnostics;
using System.Runtime.InteropServices;
using System.Text.RegularExpressions;
using System.Text.Json;
using CodeIndex.Database;
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
    internal const int DefaultMaxSymbolsPerFile = 5000;
    internal const int MaxSymbolsPerFileLimit = 50_000;
    internal const int MaxCommitRefCount = 64;
    internal const int MaxCommitRefLength = 256;
    internal const int MaxGitExcludeBytes = 256 * 1024;
    private const string SymbolKindFilterMetaKey = "index_symbol_kind_filter";
    private const int ScanCheckpointVersion = 1;
    private const string ScanCheckpointFileName = "scan-checkpoint.json";
    private static readonly TimeSpan IndexExtractionStallTimeout = TimeSpan.FromMinutes(5);

    private sealed record ScanCheckpoint(
        int Version,
        string? GitHead,
        IReadOnlyList<string> Directories);

    internal static Action? FullScanWritePhaseStartedForTesting { get; set; }
    internal static Action<bool, string?>? FullScanExtractionSchedulingForTesting { get; set; }
    internal static Func<TimeSpan>? IndexExtractionStallTimeoutForTesting { get; set; }
    internal static Action? HotspotFamilyUpdateRestampReadyForCommitForTesting { get; set; }
    internal static Func<bool> IsInputRedirectedForTesting { get; set; } = () => Console.IsInputRedirected;
    internal static Func<string?> ReadLineForTesting { get; set; } = Console.ReadLine;

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
        var runStartedAtUtc = DateTime.UtcNow;
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
            return WriteInterruptedResult(options.Json, jsonOptions, filesProcessed: 0, filesTotal: null);
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
                AddToGitExclude(options.ProjectPath, dbPath);

                var writer = new DbWriter(db);
                var indexer = new FileIndexer(options.ProjectPath, ignoreCase, ignoreRuleRoot, options.MaxFileSizeBytes, directoryIgnoreCaseProbe: null, symlinkPolicy: options.SymlinkPolicy);
                var currentHotspotFamilyMarkerFingerprints = GetHotspotFamilyMarkerFingerprints(indexer, indexCancellation.Token);
                var projectRoot = Path.GetFullPath(options.ProjectPath!);

                initialExitCode = isUpdateMode
                    ? RunUpdateMode(writer, indexer, projectRoot, resolvedDbPath, options, stopwatch, runStartedAtUtc, spinnerFrames, jsonOptions, priorReadiness, priorFoldVersion, priorFoldFingerprint, priorSymbolExtractorVersionsMatchCurrent, priorCSharpSymbolNameContractVersion, priorMetadataTargetCsharp, priorSqlGraphContractVersion, priorHotspotFamilyVersions, priorHotspotFamilyMarkerFingerprints, currentHotspotFamilyMarkerFingerprints, priorIndexedProjectRoot, priorIndexedHeadCommit, currentHeadCommit, priorSymbolKindFilterSignature, initialCwd, indexCancellation.Token)
                    : RunFullScan(writer, indexer, projectRoot, resolvedDbPath, options, stopwatch, runStartedAtUtc, spinnerFrames, jsonOptions, priorFoldVersion, priorFoldFingerprint, priorSymbolExtractorVersionsMatchCurrent, priorCSharpSymbolNameContractVersion, priorMetadataTargetCsharp, priorSqlGraphContractVersion, priorHotspotFamilyVersions, priorHotspotFamilyMarkerFingerprints, currentHotspotFamilyMarkerFingerprints, priorIndexedProjectRoot, priorIndexedHeadCommit, currentHeadCommit, priorSymbolKindFilterSignature, initialCwd, showNextSteps: !databaseExistedBeforeIndex, indexCancellation.Token);
                if (initialExitCode == CommandExitCodes.Success)
                    db.RunPlannerStatisticsMaintenance(forceAnalyze: !databaseExistedBeforeIndex);
            }
        }
        catch (IndexInterruptedException ex)
        {
            return WriteInterruptedResult(options.Json, jsonOptions, ex.FilesProcessed, ex.FilesTotal);
        }
        catch (OperationCanceledException) when (indexCancellation.IsCancellationRequested)
        {
            return WriteInterruptedResult(options.Json, jsonOptions, filesProcessed: 0, filesTotal: null);
        }
        catch (IndexExtractionStalledException ex)
        {
            return WriteExtractionStalledResult(options.Json, jsonOptions, ex);
        }
        catch (Exception ex) when (IsDatabaseFilesystemError(ex))
        {
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
        return $"PID {holder.Pid.ToString(System.Globalization.CultureInfo.InvariantCulture)}, started {startedLocal.ToString("yyyy-MM-dd HH:mm:ss zzz", System.Globalization.CultureInfo.InvariantCulture)}";
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
        var gcInfo = GC.GetGCMemoryInfo();
        return new IndexMemorySampleJsonResult
        {
            Phase = phase,
            ElapsedMs = stopwatch.ElapsedMilliseconds,
            HeapBytes = GC.GetTotalMemory(forceFullCollection: false),
            TotalAllocatedBytes = GC.GetTotalAllocatedBytes(),
            GcHeapSizeBytes = gcInfo.HeapSizeBytes,
            FragmentedBytes = gcInfo.FragmentedBytes,
            WorkingSetBytes = Process.GetCurrentProcess().WorkingSet64,
            Gen0Collections = GC.CollectionCount(0),
            Gen1Collections = GC.CollectionCount(1),
            Gen2Collections = GC.CollectionCount(2),
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
            Console.Error.WriteLine($"Warning: cdidx working set reached {peakMb:N0} MB (CDIDX_MEM_WARN_MB={thresholdMb:N0}).");
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
        long rowsUpserted,
        long rowsDeleted,
        IndexMemoryTimelineJsonResult? memoryTimeline)
    {
        writer.SetMeta(DbContext.LastIndexRunModeMetaKey, mode);
        writer.SetMeta(DbContext.LastIndexRunStartedAtMetaKey, startedAtUtc.ToString("o", System.Globalization.CultureInfo.InvariantCulture));
        writer.SetMeta(DbContext.LastIndexRunDurationMsMetaKey, durationMs.ToString(System.Globalization.CultureInfo.InvariantCulture));
        writer.SetMeta(DbContext.LastIndexRunFilesScannedMetaKey, filesScanned.ToString(System.Globalization.CultureInfo.InvariantCulture));
        writer.SetMeta(DbContext.LastIndexRunFilesSkippedMetaKey, filesSkipped.ToString(System.Globalization.CultureInfo.InvariantCulture));
        writer.SetMeta(DbContext.LastIndexRunParseErrorsMetaKey, parseErrors.ToString(System.Globalization.CultureInfo.InvariantCulture));
        writer.SetMeta(DbContext.LastIndexRunBytesReadMetaKey, bytesRead.ToString(System.Globalization.CultureInfo.InvariantCulture));
        writer.SetMeta(DbContext.LastIndexRunRowsUpsertedMetaKey, rowsUpserted.ToString(System.Globalization.CultureInfo.InvariantCulture));
        writer.SetMeta(DbContext.LastIndexRunRowsDeletedMetaKey, rowsDeleted.ToString(System.Globalization.CultureInfo.InvariantCulture));
        writer.SetMeta(DbContext.LastIndexRunPeakMemoryMbMetaKey, memoryTimeline == null
            ? null
            : (memoryTimeline.PeakWorkingSetBytes / (1024 * 1024)).ToString(System.Globalization.CultureInfo.InvariantCulture));
    }

    private static long SumReadableFileBytes(IEnumerable<string> paths)
    {
        long total = 0;
        foreach (var path in paths)
        {
            try
            {
                var info = new FileInfo(path);
                if (info.Exists)
                    total += info.Length;
            }
            catch (Exception ex) when (ex is IOException or UnauthorizedAccessException or NotSupportedException or ArgumentException)
            {
            }
        }

        return total;
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
                    Console.Error.WriteLine($"  [WARN] Skipping file outside project root: {file}. Use a path under the indexed project root or run `cdidx index` from the correct workspace.");
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

    private static bool TryProbeDryRunFile(FileIndexer indexer, string absolutePath, out string lang, out string? error)
    {
        lang = string.Empty;
        error = null;

        var indexability = FileIndexer.GetFileIndexability(absolutePath);
        if (indexability == FileIndexer.FileProbeStatus.ProbeFailed)
        {
            error = "Could not probe file for indexability/language.";
            return false;
        }

        if (indexability != FileIndexer.FileProbeStatus.Supported)
            return false;

        var detection = FileIndexer.TryDetectLanguage(absolutePath);
        if (detection.Status == FileIndexer.FileProbeStatus.ProbeFailed)
        {
            error = "Could not probe file for indexability/language.";
            return false;
        }

        if (detection.Status != FileIndexer.FileProbeStatus.Supported)
            return false;

        try
        {
            var (record, _, _, warning) = indexer.BuildRecordWithRawBytes(absolutePath);
            lang = record.Lang ?? "unknown";
            error = warning;
            return true;
        }
        catch (Exception ex)
        {
            error = ex.Message;
            return false;
        }
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
    private static void StampIndexedHeadMetadata(DbWriter writer, string projectRoot, CancellationToken cancellationToken)
    {
        try
        {
            var headSha = GitHelper.TryGetHeadCommit(projectRoot, cancellationToken);
            var headBranch = GitHelper.TryGetHeadBranch(projectRoot, cancellationToken);
            var timestamp = headSha != null
                ? DateTime.UtcNow.ToString("o", System.Globalization.CultureInfo.InvariantCulture)
                : null;
            writer.SetMeta(DbContext.IndexedHeadShaMetaKey, headSha);
            writer.SetMeta(DbContext.IndexedHeadBranchMetaKey, headBranch);
            writer.SetMeta(DbContext.IndexedHeadTimestampMetaKey, timestamp);
        }
        catch (OperationCanceledException) when (cancellationToken.IsCancellationRequested)
        {
            throw;
        }
        catch
        {
            // Best-effort metadata only; never fail an otherwise-successful index run.
            // best-effort であり、stamp の失敗で index 全体を失敗扱いにしない。
        }
        StampWorkspacePathCaseSensitivity(writer, projectRoot, cancellationToken);
    }

    private static void StampCommitScopedFreshHeadMetadata(DbWriter writer, IndexCommandOptions options, string projectRoot, string? currentHeadCommit)
    {
        try
        {
            var coveredHead = !string.IsNullOrWhiteSpace(currentHeadCommit)
                && (options.Commits.Any(commit => GitRefCoversCurrentHead(projectRoot, commit, currentHeadCommit))
                    || TryChangedBetweenCoversCurrentHead(options, projectRoot, currentHeadCommit))
                ? currentHeadCommit
                : null;
            writer.SetMeta(DbContext.CommitScopedFreshHeadShaMetaKey, coveredHead);
        }
        catch
        {
            // Best-effort metadata only; never fail an otherwise-successful index run.
            // best-effort のみ。stamp 失敗で index 全体を落とさない。
        }
    }

    private static bool GitRefCoversCurrentHead(string projectRoot, string refName, string currentHeadCommit)
    {
        if (currentHeadCommit.StartsWith(refName, StringComparison.OrdinalIgnoreCase))
            return true;

        var resolvedRef = GitHelper.TryResolveCommit(projectRoot, refName);
        return string.Equals(resolvedRef, currentHeadCommit, StringComparison.OrdinalIgnoreCase);
    }

    private static bool TryChangedBetweenCoversCurrentHead(IndexCommandOptions options, string projectRoot, string currentHeadCommit)
    {
        if (options.ChangedBetweenRefs.Count != 2)
            return false;

        return GitRefCoversCurrentHead(projectRoot, options.ChangedBetweenRefs[1], currentHeadCommit);
    }

    // Issue #1546: capture the actual case-sensitivity of the workspace filesystem so
    // `cdidx status` can diagnose phantom path collapses on case-sensitive APFS / WSL
    // NTFS / ReFS volumes (where the OS-keyed heuristic would mismatch reality). Probed
    // via the same `core.ignorecase` + filesystem probe used by FileIndexer, then
    // persisted as "true" / "false" alongside the HEAD stamp. Failures are swallowed so
    // an unwritable git config / temp probe never blocks an otherwise-successful index.
    // #1546: workspace FS の大小区別を実プローブして codeindex_meta に保存する。
    // probe 失敗時は黙って null stamp にして index 本体は成功扱いのままとする。
    private static void StampWorkspacePathCaseSensitivity(DbWriter writer, string projectRoot, CancellationToken cancellationToken)
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
        catch
        {
            // Best-effort metadata only; never fail an otherwise-successful index run.
            // best-effort のみ。stamp 失敗で index 全体を落とさない。
        }
    }

    private static void AddToGitExclude(string projectPath, string dbPath)
    {
        try
        {
            var projectRoot = Path.GetFullPath(projectPath);
            var gitDir = GitHelper.ResolveGitCommonDir(projectRoot);
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
        catch
        {
        }
    }

    private static CSharpStaticInterfaceWorkspaceSymbols BuildCSharpStaticInterfaceWorkspaceSymbols(
        DbWriter writer,
        FileIndexer indexer,
        string projectRoot,
        IEnumerable<string> filePaths,
        Action<string?>? reportCurrentFile = null,
        CancellationToken cancellationToken = default)
    {
        var pendingSymbols = new List<SymbolRecord>();
        var pendingPaths = new HashSet<string>(StringComparer.Ordinal);
        foreach (var filePath in filePaths)
        {
            cancellationToken.ThrowIfCancellationRequested();
            var absolutePath = Path.IsPathRooted(filePath)
                ? filePath
                : Path.Combine(projectRoot, filePath.Replace('/', Path.DirectorySeparatorChar));
            var relativePath = FileIndexer.NormalizePathSeparators(Path.GetRelativePath(projectRoot, absolutePath));
            if (!IsOutsideProjectRoot(relativePath))
                pendingPaths.Add(relativePath);

            var detection = FileIndexer.TryDetectLanguage(absolutePath);
            if (detection.Status != FileIndexer.FileProbeStatus.Supported
                || detection.Language != "csharp")
            {
                continue;
            }

            try
            {
                reportCurrentFile?.Invoke(relativePath);
                var (record, content, _, _) = indexer.BuildRecordWithRawBytes(absolutePath, cancellationToken);
                if (record.Lang != "csharp")
                    continue;

                if (!MayContainCSharpStaticInterfaceContract(content))
                    continue;

                pendingSymbols.AddRange(SymbolExtractor.Extract(0, record.Lang, content, record.Path, cancellationToken: cancellationToken));
            }
            catch (Exception ex) when (ex is IOException or UnauthorizedAccessException or InvalidOperationException)
            {
                // The real indexing pass reports file failures; this pre-pass only supplies
                // workspace symbols for cross-file static interface member matching.
            }
            finally
            {
                reportCurrentFile?.Invoke(null);
            }
        }

        var symbols = writer.LoadCSharpStaticInterfaceContractSymbols(pendingPaths);
        symbols.AddRange(pendingSymbols);
        var hadPendingContracts = writer.HasCSharpStaticInterfaceContractSymbolsInPaths(pendingPaths);
        return new CSharpStaticInterfaceWorkspaceSymbols(
            symbols,
            symbols.Any(IsCSharpStaticInterfaceContractSymbol) || hadPendingContracts);
    }

    internal static bool MayContainCSharpStaticInterfaceContract(string content)
    {
        var masked = MaskCSharpCommentsAndStrings(content);
        var index = 0;
        while ((index = IndexOfCSharpWord(masked, "interface", index)) >= 0)
        {
            var bodyStart = masked.IndexOf('{', index + "interface".Length);
            if (bodyStart < 0)
                return false;

            if (CSharpInterfaceBodyMayContainStaticContract(masked, bodyStart))
                return true;

            index = bodyStart + 1;
        }

        return false;
    }

    private static bool CSharpInterfaceBodyMayContainStaticContract(string masked, int bodyStart)
    {
        var depth = 1;
        var memberStart = bodyStart + 1;
        for (var index = bodyStart + 1; index < masked.Length; index++)
        {
            var ch = masked[index];
            if (ch == '{')
            {
                if (depth == 1 && CSharpMemberHeaderHasStaticContract(masked, memberStart, index))
                    return true;

                depth++;
            }
            else if (ch == '}')
            {
                depth--;
                if (depth == 0)
                    return false;

                if (depth == 1)
                    memberStart = index + 1;
            }
            else if (ch == ';' && depth == 1)
            {
                if (CSharpMemberHeaderHasStaticContract(masked, memberStart, index))
                    return true;

                memberStart = index + 1;
            }
        }

        return false;
    }

    private static bool CSharpMemberHeaderHasStaticContract(string masked, int start, int endExclusive)
    {
        if (start < 0 || endExclusive <= start || endExclusive > masked.Length)
            return false;

        var header = masked[start..endExclusive];
        return ContainsCSharpWord(header, "static")
               && (ContainsCSharpWord(header, "abstract")
                   || ContainsCSharpWord(header, "virtual"));
    }

    private static int IndexOfCSharpWord(string text, string word, int startIndex)
    {
        var index = Math.Max(0, startIndex);
        while (index < text.Length)
        {
            index = text.IndexOf(word, index, StringComparison.Ordinal);
            if (index < 0)
                return -1;

            var before = index == 0 ? '\0' : text[index - 1];
            var afterIndex = index + word.Length;
            var after = afterIndex >= text.Length ? '\0' : text[afterIndex];
            if (!IsCSharpIdentifierPart(before) && !IsCSharpIdentifierPart(after))
                return index;

            index += word.Length;
        }

        return -1;
    }

    private static string MaskCSharpCommentsAndStrings(string content)
    {
        var chars = content.ToCharArray();
        var inLineComment = false;
        var inBlockComment = false;
        var inString = false;
        var inChar = false;
        var inVerbatimString = false;
        var inRawString = false;
        var rawQuoteCount = 0;

        for (var index = 0; index < chars.Length; index++)
        {
            var ch = chars[index];
            var next = index + 1 < chars.Length ? chars[index + 1] : '\0';

            if (inLineComment)
            {
                if (ch is '\r' or '\n')
                {
                    inLineComment = false;
                }
                else
                {
                    chars[index] = ' ';
                }

                continue;
            }

            if (inBlockComment)
            {
                if (ch == '*' && next == '/')
                {
                    chars[index] = ' ';
                    chars[index + 1] = ' ';
                    index++;
                    inBlockComment = false;
                }
                else if (ch is not ('\r' or '\n'))
                {
                    chars[index] = ' ';
                }

                continue;
            }

            if (inRawString)
            {
                if (ch == '"' && HasConsecutiveQuotes(chars, index, rawQuoteCount))
                {
                    for (var quote = 0; quote < rawQuoteCount && index + quote < chars.Length; quote++)
                        chars[index + quote] = ' ';
                    index += rawQuoteCount - 1;
                    inRawString = false;
                }
                else if (ch is not ('\r' or '\n'))
                {
                    chars[index] = ' ';
                }

                continue;
            }

            if (inVerbatimString)
            {
                if (ch == '"' && next == '"')
                {
                    chars[index] = ' ';
                    chars[index + 1] = ' ';
                    index++;
                }
                else if (ch == '"')
                {
                    chars[index] = ' ';
                    inVerbatimString = false;
                }
                else if (ch is not ('\r' or '\n'))
                {
                    chars[index] = ' ';
                }

                continue;
            }

            if (inString)
            {
                if (ch == '\\' && next != '\0')
                {
                    chars[index] = ' ';
                    chars[index + 1] = ' ';
                    index++;
                }
                else if (ch == '"')
                {
                    chars[index] = ' ';
                    inString = false;
                }
                else if (ch is not ('\r' or '\n'))
                {
                    chars[index] = ' ';
                }

                continue;
            }

            if (inChar)
            {
                if (ch == '\\' && next != '\0')
                {
                    chars[index] = ' ';
                    chars[index + 1] = ' ';
                    index++;
                }
                else if (ch == '\'')
                {
                    chars[index] = ' ';
                    inChar = false;
                }
                else if (ch is not ('\r' or '\n'))
                {
                    chars[index] = ' ';
                }

                continue;
            }

            if (ch == '/' && next == '/')
            {
                chars[index] = ' ';
                chars[index + 1] = ' ';
                index++;
                inLineComment = true;
            }
            else if (ch == '/' && next == '*')
            {
                chars[index] = ' ';
                chars[index + 1] = ' ';
                index++;
                inBlockComment = true;
            }
            else if (ch == '@' && next == '"')
            {
                chars[index] = ' ';
                chars[index + 1] = ' ';
                index++;
                inVerbatimString = true;
            }
            else if (ch == '"' && HasConsecutiveQuotes(chars, index, 3))
            {
                rawQuoteCount = CountConsecutiveQuotes(chars, index);
                for (var quote = 0; quote < rawQuoteCount && index + quote < chars.Length; quote++)
                    chars[index + quote] = ' ';
                index += rawQuoteCount - 1;
                inRawString = true;
            }
            else if (ch == '"')
            {
                chars[index] = ' ';
                inString = true;
            }
            else if (ch == '\'')
            {
                chars[index] = ' ';
                inChar = true;
            }
        }

        return new string(chars);
    }

    private static bool HasConsecutiveQuotes(char[] chars, int index, int count)
    {
        if (index + count > chars.Length)
            return false;

        for (var offset = 0; offset < count; offset++)
        {
            if (chars[index + offset] != '"')
                return false;
        }

        return true;
    }

    private static int CountConsecutiveQuotes(char[] chars, int index)
    {
        var count = 0;
        while (index + count < chars.Length && chars[index + count] == '"')
            count++;
        return count;
    }

    private static bool IsCSharpStaticInterfaceContractSymbol(SymbolRecord symbol)
        => symbol.Kind is "function" or "operator" or "property"
           && symbol.ContainerKind == "interface"
           && !string.IsNullOrWhiteSpace(symbol.Signature)
           && ContainsCSharpWord(symbol.Signature!, "static")
           && (ContainsCSharpWord(symbol.Signature!, "abstract")
               || ContainsCSharpWord(symbol.Signature!, "virtual"));

    private static bool ContainsCSharpWord(string text, string word)
    {
        var index = 0;
        while (index < text.Length)
        {
            index = text.IndexOf(word, index, StringComparison.Ordinal);
            if (index < 0)
                return false;

            var before = index == 0 ? '\0' : text[index - 1];
            var afterIndex = index + word.Length;
            var after = afterIndex >= text.Length ? '\0' : text[afterIndex];
            if (!IsCSharpIdentifierPart(before) && !IsCSharpIdentifierPart(after))
                return true;

            index += word.Length;
        }

        return false;
    }

    private static bool IsCSharpIdentifierPart(char ch)
        => char.IsLetterOrDigit(ch) || ch == '_';

    private static string? TryDetectStatReusableLanguage(string absolutePath)
    {
        if (string.Equals(Path.GetExtension(absolutePath), ".h", StringComparison.OrdinalIgnoreCase))
            return null;

        var detection = FileIndexer.TryDetectLanguage(absolutePath);
        return detection.Status == FileIndexer.FileProbeStatus.Supported
            ? detection.Language
            : null;
    }

    private static long? TryGetUnchangedFileIdFromStat(
        DbWriter writer,
        string projectRoot,
        string absolutePath,
        string? language,
        bool allowReuse)
    {
        if (!allowReuse || language == null)
            return null;

        try
        {
            var info = new FileInfo(absolutePath);
            if (!info.Exists)
                return null;

            var relativePath = FileIndexer.NormalizePathSeparators(Path.GetRelativePath(projectRoot, absolutePath));
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

    private sealed record CSharpStaticInterfaceWorkspaceSymbols(
        IReadOnlyList<SymbolRecord> Symbols,
        bool HasStaticInterfaceContracts);

    private sealed record FullScanFileWorkItem(
        string FilePath,
        FileRecord? Record,
        string? Content,
        byte[]? RawBytes,
        string? Warning,
        IReadOnlyList<ChunkRecord>? Chunks,
        IReadOnlyList<SymbolRecord>? Symbols,
        IReadOnlyList<ReferenceRecord>? References,
        IReadOnlyList<FileIssue>? Issues,
        Exception? Exception)
    {
        public static FullScanFileWorkItem Success(
            string filePath,
            FileRecord record,
            string content,
            byte[] rawBytes,
            string? warning,
            IReadOnlyList<ChunkRecord>? chunks,
            IReadOnlyList<SymbolRecord>? symbols,
            IReadOnlyList<ReferenceRecord>? references,
            IReadOnlyList<FileIssue>? issues)
        {
            return new FullScanFileWorkItem(filePath, record, content, rawBytes, warning, chunks, symbols, references, issues, null);
        }

        public static FullScanFileWorkItem Failure(string filePath, Exception exception)
            => new(filePath, null, null, null, null, null, null, null, null, exception);

        public static FullScanFileWorkItem Skipped(string filePath, string warning)
            => new(filePath, null, null, null, warning, null, null, null, null, null);
    }

    private sealed record FoldOnlyRemediation(
        string DegradedReason,
        string RecommendedAction,
        string AlternativeAction);

    private sealed class IndexInterruptedException : OperationCanceledException
    {
        public IndexInterruptedException(int filesProcessed, int? filesTotal)
            : base("Indexing was interrupted.")
        {
            FilesProcessed = filesProcessed;
            FilesTotal = filesTotal;
        }

        public int FilesProcessed { get; }
        public int? FilesTotal { get; }
    }

    private sealed class IndexExtractionStalledException : Exception
    {
        public IndexExtractionStalledException(int filesProcessed, int? filesTotal, TimeSpan timeout, string? activePath)
            : base("Index extraction stalled.")
        {
            FilesProcessed = filesProcessed;
            FilesTotal = filesTotal;
            Timeout = timeout;
            ActivePath = activePath;
        }

        public int FilesProcessed { get; }
        public int? FilesTotal { get; }
        public TimeSpan Timeout { get; }
        public string? ActivePath { get; }
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
    public bool Force { get; init; }
    public bool ReadOnly { get; init; }
    public bool Yes { get; init; }
    public bool Watch { get; init; }
    public bool OptimizeOnly { get; init; }
    public int? WatchDebounceMs { get; init; }
    public DurationOutputFormat DurationFormat { get; init; } = DurationOutputFormat.Auto;
    public CompletionNotificationMode NotifyMode { get; init; } = CompletionNotificationMode.Auto;
    public long? MaxFileSizeBytes { get; init; }
    public int MaxSymbolsPerFile { get; init; } = IndexCommandRunner.DefaultMaxSymbolsPerFile;
    public int Parallelism { get; init; } = IndexCommandRunner.DefaultIndexParallelism();
    public bool MemoryTrace { get; init; }
    public FileIndexer.SymlinkPolicy SymlinkPolicy { get; init; } = FileIndexer.SymlinkPolicy.None;
    public SymbolKindFilter SymbolKindFilter { get; init; } = SymbolKindFilter.Empty;
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

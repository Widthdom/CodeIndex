using System.Diagnostics;
using System.Collections.Concurrent;
using System.Text.Json;
using CodeIndex.Database;
using CodeIndex.Indexer;
using CodeIndex.Models;
using Microsoft.Data.Sqlite;

namespace CodeIndex.Cli;

/// <summary>
/// Runs indexing CLI commands.
/// インデックス系CLIコマンドを実行する。
/// </summary>
public static class IndexCommandRunner
{
    public static int Run(string[] indexArgs, JsonSerializerOptions jsonOptions) =>
        Run(indexArgs, jsonOptions, cancellationForTesting: null);

    internal static int Run(string[] indexArgs, JsonSerializerOptions jsonOptions, CancellationTokenSource? cancellationForTesting)
    {
        var options = ParseArgs(indexArgs);
        var jsonContext = CliJsonSerializerContextFactory.Create(jsonOptions);
        using var ownedCancellation = cancellationForTesting == null ? new CancellationTokenSource() : null;
        var indexCancellation = cancellationForTesting ?? ownedCancellation!;
        using var cancelKeyPressRegistration = cancellationForTesting == null
            ? RegisterIndexCancelKeyPress(indexCancellation)
            : NullDisposable.Instance;

        if (options.ShowHelp)
        {
            ConsoleUi.PrintUsage();
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
        var dbPath = DbPathResolver.ResolveForIndex(options.ProjectPath, options.DbPath);
        var stopwatch = Stopwatch.StartNew();
        var isUpdateMode = options.Commits.Count > 0 || options.ChangedBetweenSpecified || options.UpdateFiles.Count > 0;
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

        if (options.Watch)
        {
            // --watch is the only mode that holds the process open after the initial scan, so
            // combining it with --commits, --changed-between, --files, or --dry-run produces ambiguous semantics:
            // those flags describe a one-shot snapshot. Reject the combination up front with
            // an actionable hint instead of silently picking one behavior.
            // --watch のみが初回スキャン後も常駐するため、ワンショット用の --commits / --changed-between / --files /
            // --dry-run と併用すると挙動が曖昧になる。ヒント付きで早期に拒否する。
            if (options.Commits.Count > 0 || options.ChangedBetweenSpecified || options.UpdateFiles.Count > 0 || options.DryRun)
            {
                const string watchConflictSynopsis =
                    "`cdidx index <projectPath> --watch [--debounce <ms>]` "
                    + "(omit --commits / --changed-between / --files / --dry-run; the initial scan plus continuous watch handles incremental refresh)";
                return WriteCommandError(
                    options.Json,
                    jsonOptions,
                    "--watch cannot be combined with --commits, --changed-between, --files, or --dry-run (watch already drives continuous incremental updates)",
                    CommandExitCodes.UsageError,
                    "Use " + watchConflictSynopsis + ".",
                    CommandErrorCodes.UsageError);
            }
        }

        if (options.Rebuild && isUpdateMode)
        {
            // Enumerate the three mutually-exclusive usage forms so the conflict error
            // does not require a second `--help` round-trip to find the correct command.
            const string rebuildConflictSynopsis =
                "`cdidx index <projectPath> --rebuild`, "
                + "`cdidx index <projectPath> --commits <id> [id ...]`, "
                + "`cdidx index <projectPath> --changed-between <old-ref> <new-ref>`, "
                + "or `cdidx index <projectPath> --files <path> [path ...]`";
            if (options.Json)
                Console.WriteLine(JsonSerializer.Serialize(new CommandErrorJsonResult(
                    "error",
                    "--rebuild cannot be used with --commits, --changed-between, or --files (rebuild requires a full rescan)",
                    "Use one of: " + rebuildConflictSynopsis + ".",
                    CommandErrorCodes.UsageError),
                    jsonContext.CommandErrorJsonResult));
            else
            {
                Console.Error.WriteLine($"Error [{CommandErrorCodes.UsageError}]: --rebuild cannot be used with --commits, --changed-between, or --files (rebuild requires a full rescan)");
                Console.Error.WriteLine("Hint: use one of: " + rebuildConflictSynopsis + ".");
            }
            return CommandExitCodes.UsageError;
        }

        if (options.ParseError != null)
        {
            return WriteCommandError(
                options.Json,
                jsonOptions,
                options.ParseError,
                CommandExitCodes.UsageError,
                "Rerun `cdidx index <projectPath> --commits <commit-id> [commit-id ...]` with 7-40 hex commit object IDs.",
                CommandErrorCodes.UsageError);
        }

        if (options.ChangedBetweenSpecified && options.ChangedBetweenRefs.Count != 2)
        {
            return WriteCommandError(
                options.Json,
                jsonOptions,
                "--changed-between requires exactly two refs",
                CommandExitCodes.UsageError,
                "Rerun `cdidx index <projectPath> --changed-between <old-ref> <new-ref>`.",
                CommandErrorCodes.UsageError);
        }

        if (!options.DryRun && DbPathResolver.UriRequestsReadOnly(dbPath))
        {
            return WriteCommandError(
                options.Json,
                jsonOptions,
                $"database must be writable for index: {dbPath}",
                CommandExitCodes.DatabaseError,
                "Point `--db` at a writable filesystem path, or omit `--db` to use `<projectPath>/.cdidx/codeindex.db`.",
                CommandErrorCodes.DbNotWritable);
        }

        dbPath = DbPathResolver.NormalizeDbPath(dbPath);
        var resolvedDbPath = Path.GetFullPath(dbPath);

        if (!options.Json && !options.Quiet)
        {
            ConsoleUi.PrintBanner();
            Console.WriteLine();
            Console.WriteLine($"  Project : {Path.GetFullPath(options.ProjectPath!)}");
            Console.WriteLine($"  Output  : {resolvedDbPath}");
            Console.WriteLine($"  Mode    : {mode}");
            Console.WriteLine();
        }

        var ignoreCase = GitHelper.ResolveIgnoreCase(options.ProjectPath);
        var ignoreRuleRoot = GitHelper.TryGetRepositoryRoot(options.ProjectPath) ?? Path.GetFullPath(options.ProjectPath!);

        // --dry-run: scan files but do not write to database / --dry-run: ファイルスキャンのみでDBに書き込まない
        if (options.DryRun)
        {
            var dryIndexer = new FileIndexer(options.ProjectPath, ignoreCase, ignoreRuleRoot, options.MaxFileSizeBytes);
            IReadOnlyList<string> dryCandidates;
            var errorList = new List<CliJsonMessage>();
            var dryScanErrorKeys = new HashSet<string>(StringComparer.Ordinal);

            void RecordDryRunScanErrors(IEnumerable<FileIndexer.ScanError> scanErrors)
            {
                foreach (var scanError in scanErrors)
                {
                    var key = $"{scanError.Path}\n{scanError.Message}";
                    if (!dryScanErrorKeys.Add(key))
                        continue;

                    errorList.Add(new CliJsonMessage(scanError.Path, scanError.Message));
                    if (!options.Json)
                        ConsoleUi.PrintWarning($"{scanError.Path}: {scanError.Message}");
                }
            }

            if (options.UpdateFiles.Count > 0)
            {
                // --files: only the specified files / --files: 指定ファイルのみ
                var relevantIgnoreFileChanged = ContainsRelevantIgnoreFileUpdate(options.ProjectPath, options.UpdateFiles);
                var updatePaths = NormalizeUpdateFileTargets(options.ProjectPath, options.UpdateFiles, options.Json);
                if (relevantIgnoreFileChanged || ContainsIgnoreFilePath(updatePaths))
                {
                    var scanResult = dryIndexer.ScanFilesDetailed();
                    dryCandidates = scanResult.Files;
                    RecordDryRunScanErrors(scanResult.Errors);
                }
                else
                {
                    dryCandidates = updatePaths
                        .Select(path => Path.Combine(options.ProjectPath, path.Replace('/', Path.DirectorySeparatorChar)))
                        .Where(p => File.Exists(LongPath.EnsureWindowsPrefix(p)))
                        .ToList();
                }
            }
            else if (options.Commits.Count > 0 || options.ChangedBetweenSpecified)
            {
                // Git update modes: files changed in commits or between refs.
                // Git更新モード: コミットまたはref間の変更ファイル。
                var changedFiles = new HashSet<string>(StringComparer.Ordinal);
                var relevantIgnoreFileChanged = false;
                var repoRoot = GitHelper.TryGetRepositoryRoot(options.ProjectPath) ?? Path.GetFullPath(options.ProjectPath!);
                try
                {
                    foreach (var commit in options.Commits)
                    {
                        var changed = GitHelper.GetChangedFilesFromCommit(options.ProjectPath, commit);
                        var normalized = NormalizeCommitFileTargets(options.ProjectPath, repoRoot, changed, out var commitTouchedRelevantIgnoreFile);
                        relevantIgnoreFileChanged |= commitTouchedRelevantIgnoreFile;
                        foreach (var path in normalized)
                            changedFiles.Add(path);
                    }
                }
                catch (Exception ex)
                {
                    return WriteCommandError(
                        options.Json,
                        jsonOptions,
                        $"failed to resolve changed files from git commits: {ex.Message}",
                        CommandExitCodes.UsageError,
                        "Check the commit IDs and rerun `cdidx index <projectPath> --commits <id> [id ...]`.",
                        CommandErrorCodes.UsageError);
                }
                if (options.ChangedBetweenRefs.Count == 2)
                {
                    try
                    {
                        var changed = GitHelper.GetChangedFilesBetweenRefs(options.ProjectPath, options.ChangedBetweenRefs[0], options.ChangedBetweenRefs[1]);
                        var normalized = NormalizeCommitFileTargets(options.ProjectPath, repoRoot, changed, out var rangeTouchedRelevantIgnoreFile);
                        relevantIgnoreFileChanged |= rangeTouchedRelevantIgnoreFile;
                        foreach (var path in normalized)
                            changedFiles.Add(path);
                    }
                    catch { /* ignore git errors in dry-run */ }
                }

                if (relevantIgnoreFileChanged || ContainsIgnoreFilePath(changedFiles))
                {
                    var scanResult = dryIndexer.ScanFilesDetailed();
                    dryCandidates = scanResult.Files;
                    RecordDryRunScanErrors(scanResult.Errors);
                }
                else
                {
                    dryCandidates = changedFiles
                        .Select(path => Path.Combine(options.ProjectPath, path.Replace('/', Path.DirectorySeparatorChar)))
                        .Where(p => File.Exists(LongPath.EnsureWindowsPrefix(p)))
                        .ToList();
                }
            }
            else
            {
                var scanResult = dryIndexer.ScanFilesDetailed();
                dryCandidates = scanResult.Files;
                RecordDryRunScanErrors(scanResult.Errors);
            }

            var dryFiles = new List<string>();
            var langCounts = new Dictionary<string, int>();
            foreach (var f in dryCandidates)
            {
                var pathFilter = dryIndexer.EvaluatePathFilter(f);
                RecordDryRunScanErrors(pathFilter.Errors);
                if (pathFilter.ShouldSkip)
                    continue;

                if (!TryProbeDryRunFile(dryIndexer, f, out var lang, out var message))
                {
                    if (message != null)
                    {
                        var displayPath = FileIndexer.NormalizePathSeparators(Path.GetRelativePath(options.ProjectPath, f));
                        errorList.Add(new CliJsonMessage(displayPath, message));
                        if (!options.Json && !options.Quiet)
                            ConsoleUi.PrintWarning($"{displayPath}: {message}");
                    }
                    continue;
                }

                dryFiles.Add(f);
                langCounts[lang] = langCounts.GetValueOrDefault(lang) + 1;
            }
            if (options.Json)
            {
                Console.WriteLine(JsonSerializer.Serialize(new IndexDryRunJsonResult
                {
                    Status = "dry_run",
                    FilesTotal = dryFiles.Count,
                    Languages = langCounts,
                    Errors = errorList.Count > 0 ? errorList : null,
                }, jsonContext.IndexDryRunJsonResult));
            }
            else
            {
                Console.WriteLine($"Dry run: {dryFiles.Count} files would be indexed");
                foreach (var (lang, count) in langCounts.OrderByDescending(kv => kv.Value))
                    Console.WriteLine($"  {lang,-12} {count,6}");
            }
            return CommandExitCodes.Success;
        }

        int initialExitCode;
        try
        {
            var dbDir = Path.GetDirectoryName(dbPath);
            if (!string.IsNullOrEmpty(dbDir))
                Directory.CreateDirectory(dbDir);

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
                using var db = new DbContext(dbPath);

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
        // Captured BEFORE `--rebuild` drops the DB so an incremental run can warn the user when
        // the worktree's HEAD has moved since the previously indexed snapshot. The same value
        // is read at `status` time (without `--check`) to surface a worktree branch / HEAD
        // switch via `worktree_head_changed`. Issues #1508 and #1512.
        // `--rebuild` が DB を消す前に取り出す。incremental 経路で HEAD 差分を検知し、`status`
        // (no `--check`) でも worktree の HEAD 切替検出に利用する。
        var priorIndexedHeadCommit = db.GetMetaString(DbContext.IndexedHeadCommitMetaKey);
        var currentHeadCommit = GitHelper.TryGetHeadCommit(options.ProjectPath);

        // Don't demote readiness yet. A transient usage error in update-mode preflight
        // (bad --commits hash, git unavailable, etc.) would permanently downgrade a healthy
        // DB even though no data was touched. Clearing happens just before the first
        // destructive / schema-changing operation, inside the mode-specific runner.
        // まだ clear しない。update モードの preflight が失敗しただけで healthy な DB を
        // 縮退状態に落とさないよう、clear は実際に書き込み直前で行う。

        if (options.Rebuild)
        {
            db.ClearReadyFlags();
            var rebuildWriter = new DbWriter(db);
            rebuildWriter.ClearHotspotFamilyReady();
            rebuildWriter.ClearMetadataTargetReady();
            db.DropAll();
        }

        db.InitializeSchema();
        AddToGitExclude(options.ProjectPath, dbPath);

        var writer = new DbWriter(db);
        var indexer = new FileIndexer(options.ProjectPath, ignoreCase, ignoreRuleRoot, options.MaxFileSizeBytes);
        var currentHotspotFamilyMarkerFingerprints = GetHotspotFamilyMarkerFingerprints(indexer);
        var projectRoot = Path.GetFullPath(options.ProjectPath!);

        initialExitCode = isUpdateMode
            ? RunUpdateMode(writer, indexer, projectRoot, resolvedDbPath, options, stopwatch, spinnerFrames, jsonOptions, priorReadiness, priorFoldVersion, priorFoldFingerprint, priorSymbolExtractorVersionsMatchCurrent, priorCSharpSymbolNameContractVersion, priorMetadataTargetCsharp, priorSqlGraphContractVersion, priorHotspotFamilyVersions, priorHotspotFamilyMarkerFingerprints, currentHotspotFamilyMarkerFingerprints, priorIndexedProjectRoot, priorIndexedHeadCommit, currentHeadCommit, initialCwd, indexCancellation.Token)
            : RunFullScan(writer, indexer, projectRoot, resolvedDbPath, options, stopwatch, spinnerFrames, jsonOptions, priorFoldVersion, priorFoldFingerprint, priorSymbolExtractorVersionsMatchCurrent, priorCSharpSymbolNameContractVersion, priorMetadataTargetCsharp, priorSqlGraphContractVersion, priorHotspotFamilyVersions, priorHotspotFamilyMarkerFingerprints, currentHotspotFamilyMarkerFingerprints, priorIndexedProjectRoot, priorIndexedHeadCommit, currentHeadCommit, initialCwd, indexCancellation.Token);
            }
        }
        catch (IndexInterruptedException ex)
        {
            return WriteInterruptedResult(options.Json, jsonOptions, ex.FilesProcessed, ex.FilesTotal);
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

    public static int RunBackfillFold(string[] cmdArgs, JsonSerializerOptions jsonOptions)
    {
        var options = ParseBackfillFoldArgs(cmdArgs);
        var jsonContext = CliJsonSerializerContextFactory.Create(jsonOptions);
        if (options.ShowHelp)
        {
            ConsoleUi.PrintUsage();
            return CommandExitCodes.Success;
        }

        if (options.ParseError != null)
            return WriteCommandError(
                options.Json,
                jsonOptions,
                options.ParseError,
                CommandExitCodes.UsageError,
                "Run `cdidx backfill-fold --help` to see the supported command shape.",
                CommandErrorCodes.UsageError);

        if (!DbContext.TryValidateExistingCodeIndexDb(options.DbPath, out var validationMessage, out var isNotFound))
            return WriteCommandError(
                options.Json,
                jsonOptions,
                validationMessage,
                isNotFound ? CommandExitCodes.NotFound : CommandExitCodes.DatabaseError,
                isNotFound
                    ? "Point `--db` at an existing `codeindex.db`, or run `cdidx index <projectPath>` first to create one."
                    : "Point `--db` at an existing CodeIndex database created by `cdidx index`, then retry `cdidx backfill-fold`.",
                isNotFound ? CommandErrorCodes.DbNotFound : CommandErrorCodes.DbError);

        try
        {
            using var db = new DbContext(options.DbPath);
            db.InitializeSchema();
            var writer = new DbWriter(db);

            var userVersionBefore = db.GetUserVersion();
            var currentFoldVersion = NameFold.Version.ToString(System.Globalization.CultureInfo.InvariantCulture);
            var currentFoldFingerprint = NameFold.Fingerprint();
            var storedFoldVersion = db.GetMetaString("fold_key_version");
            var storedFoldFingerprint = db.GetMetaString("fold_key_fingerprint");
            // Missing or mismatched fold metadata means persisted keys may have been generated
            // by a different fold algorithm/runtime, so refresh every row from source names.
            // fold metadata 未記録 / 不一致時は全行再計算して version/runtime skew を解消する。
            var rewriteAll = storedFoldVersion != currentFoldVersion
                || storedFoldFingerprint != currentFoldFingerprint;

            var (symbols, symbolReferences) = writer.BackfillFoldedColumns(rewriteAll);
            // MarkFoldReady re-verifies inside a BEGIN IMMEDIATE so a concurrent writer cannot
            // insert NULL-folded rows between the verify and the stamp. Issue #1535.
            // MarkFoldReady は BEGIN IMMEDIATE 内で再検証するため、concurrent writer による
            // NULL 行差し込みで fold_ready が嘘になるのを防ぐ。Issue #1535。
            var verified = writer.MarkFoldReady();
            if (!verified)
            {
                return WriteCommandError(
                    options.Json,
                    jsonOptions,
                    "folded-name backfill verification failed: some rows still have NULL folded values",
                    CommandExitCodes.DatabaseError,
                    "Retry `cdidx backfill-fold`. If the DB still does not verify, rebuild it with `cdidx index <projectPath> --rebuild`.",
                    CommandErrorCodes.DbError);
            }

            var userVersionAfter = db.GetUserVersion();

            if (options.Json)
            {
                Console.WriteLine(JsonSerializer.Serialize(new BackfillFoldJsonResult(
                    symbols,
                    symbolReferences,
                    rewriteAll,
                    verified,
                    userVersionBefore,
                    userVersionAfter,
                    true), jsonContext.BackfillFoldJsonResult));
            }
            else
            {
                Console.WriteLine("Backfilling folded-name columns ...");
                Console.WriteLine($"  symbols:            {ConsoleUi.Counted(symbols, "row", format: "N0")} rewritten");
                Console.WriteLine($"  symbol_references:  {ConsoleUi.Counted(symbolReferences, "row", format: "N0")} rewritten");
                if (rewriteAll)
                    Console.WriteLine("  mode:               full folded-key refresh (fold metadata missing or mismatched)");
                Console.WriteLine($"  verified:           {(verified ? "yes" : "no")}");
                Console.WriteLine($"  stamp:              FoldReady bit set (user_version: {userVersionBefore} -> {userVersionAfter})");
            }

            return CommandExitCodes.Success;
        }
        catch (Exception ex)
        {
            if (JsonOutputFailure.TryHandle(ex, out var exitCode))
                return exitCode;

            return WriteCommandError(
                options.Json,
                jsonOptions,
                $"failed to backfill folded-name columns: {ex.Message}",
                CommandExitCodes.DatabaseError,
                "Retry `cdidx backfill-fold`. If this persists, rebuild the index with `cdidx index <projectPath> --rebuild`.",
                CommandErrorCodes.DbError);
        }
    }


    // Index-mode flag names recognized by `ParseArgs`. Kept in sync with the switch above
    // so `Warning: unknown option ...` can suggest the closest accepted flag (#1582). Easter-egg
    // and random-spinner flags are excluded since they are intentionally undiscoverable.
    // `ParseArgs` の switch と同期した index 系の受理フラグ一覧。`unknown option` 警告で
    // 最も近い受理フラグを did-you-mean 提案するのに用いる (#1582)。
    // easter egg や random-spinner は意図的に未公開なので除外する。
    private static readonly string[] AcceptedIndexFlags =
    [
        "--db", "--rebuild", "--verbose", "--json", "--dry-run", "--force",
        "--watch", "--debounce", "--duration-format", "--max-file-bytes",
        "--parallelism",
        "--commits", "--changed-between", "--files", "--solution", "--project", "--help",
    ];

    private static readonly string[] AcceptedBackfillFoldFlags =
    [
        "--db", "--json", "--help",
    ];

    private static void WriteUnknownIndexOptionSuggestion(string token)
    {
        var name = TrimInlineValue(token);
        var suggestion = ConsoleUi.FindClosestMatch(name, AcceptedIndexFlags);
        if (suggestion != null)
            Console.Error.WriteLine($"Did you mean: {suggestion}?");
    }

    private static void WriteUnknownBackfillFoldOptionSuggestion(string token)
    {
        var name = TrimInlineValue(token);
        var suggestion = ConsoleUi.FindClosestMatch(name, AcceptedBackfillFoldFlags);
        if (suggestion != null)
            Console.Error.WriteLine($"Did you mean: {suggestion}?");
    }

    private static string TrimInlineValue(string token)
    {
        var eq = token.IndexOf('=');
        return eq < 0 ? token : token[..eq];
    }

    public static IndexCommandOptions ParseArgs(string[] args)
    {
        string? projectPath = null;
        string? dbPath = null;
        bool rebuild = false;
        bool verbose = false;
        bool json = false;
        bool quiet = false;
        bool dryRun = false;
        bool force = false;
        bool watch = false;
        int? watchDebounceMs = null;
        var durationFormat = DurationOutputFormat.Auto;
        long? maxFileSizeBytes = ReadMaxFileSizeBytesFromEnvironment();
        var parallelism = ReadIndexParallelismFromEnvironment();
        string? easterEgg = null;
        int spinnerFlagCount = 0;
        bool randomSpinner = false;
        var commits = new List<string>();
        var changedBetweenRefs = new List<string>();
        var changedBetweenSpecified = false;
        var updateFiles = new List<string>();
        var projectFilters = new List<string>();
        string? solutionPath = null;
        string? projectFilterError = null;
        string? parseError = null;

        for (int i = 0; i < args.Length; i++)
        {
            switch (args[i])
            {
                case "--db" when i + 1 < args.Length:
                    dbPath = args[++i];
                    break;
                case "--rebuild":
                    rebuild = true;
                    break;
                case "--verbose":
                    verbose = true;
                    break;
                case "--json":
                    json = true;
                    break;
                case "--quiet":
                    quiet = true;
                    break;
                case "--dry-run":
                    dryRun = true;
                    break;
                case "--force":
                    force = true;
                    break;
                case "--watch":
                    watch = true;
                    break;
                case "--debounce" when i + 1 < args.Length:
                    if (int.TryParse(args[i + 1], System.Globalization.NumberStyles.Integer, System.Globalization.CultureInfo.InvariantCulture, out var parsedDebounce) && parsedDebounce >= 0)
                    {
                        watchDebounceMs = parsedDebounce;
                        i++;
                    }
                    else
                    {
                        Console.Error.WriteLine($"Warning: invalid --debounce value '{args[i + 1]}' (ignored; must be a non-negative integer in milliseconds) / 不正な --debounce 値 '{args[i + 1]}'（無視。ミリ秒の0以上の整数を指定）");
                        i++;
                    }
                    break;
                case "--duration-format" when i + 1 < args.Length:
                    durationFormat = ParseDurationFormat(args[++i], durationFormat);
                    break;
                case var option when option.StartsWith("--duration-format=", StringComparison.Ordinal):
                    durationFormat = ParseDurationFormat(option["--duration-format=".Length..], durationFormat);
                    break;
                case "--max-file-bytes" when i + 1 < args.Length:
                    maxFileSizeBytes = ParseMaxFileBytes(args[++i], maxFileSizeBytes);
                    break;
                case var option when option.StartsWith("--max-file-bytes=", StringComparison.Ordinal):
                    maxFileSizeBytes = ParseMaxFileBytes(option["--max-file-bytes=".Length..], maxFileSizeBytes);
                    break;
                case "--parallelism" when i + 1 < args.Length:
                    parallelism = ParseIndexParallelism(args[++i], parallelism, "--parallelism");
                    break;
                case var option when option.StartsWith("--parallelism=", StringComparison.Ordinal):
                    parallelism = ParseIndexParallelism(option["--parallelism=".Length..], parallelism, "--parallelism");
                    break;
                case "--commits":
                    while (i + 1 < args.Length && !args[i + 1].StartsWith('-'))
                    {
                        var commit = args[++i];
                        commits.Add(commit);
                        if (!GitHelper.IsCommitObjectId(commit))
                            parseError ??= $"invalid --commits value '{commit}': expected a 7-40 character hex commit ID; ranges and tag refs are not accepted";
                    }
                    if (commits.Count == 0)
                        Console.Error.WriteLine("Warning: --commits specified but no commit IDs provided / --commits が指定されましたがコミットIDがありません");
                    break;
                case "--changed-between":
                    changedBetweenSpecified = true;
                    while (i + 1 < args.Length && !args[i + 1].StartsWith('-') && changedBetweenRefs.Count < 2)
                        changedBetweenRefs.Add(args[++i]);
                    if (changedBetweenRefs.Count != 2)
                        Console.Error.WriteLine("Warning: --changed-between requires exactly two refs / --changed-between は2つのrefが必要です");
                    break;
                case "--solution" when i + 1 < args.Length:
                    solutionPath = args[++i];
                    break;
                case var option when option.StartsWith("--solution=", StringComparison.Ordinal):
                    solutionPath = option["--solution=".Length..];
                    break;
                case "--project" when i + 1 < args.Length:
                    projectFilters.Add(args[++i]);
                    break;
                case var option when option.StartsWith("--project=", StringComparison.Ordinal):
                    projectFilters.Add(option["--project=".Length..]);
                    break;
                case "--files":
                    while (i + 1 < args.Length && !args[i + 1].StartsWith('-'))
                        updateFiles.Add(args[++i]);
                    if (updateFiles.Count == 0)
                        Console.Error.WriteLine("Warning: --files specified but no file paths provided / --files が指定されましたがファイルパスがありません");
                    break;
                case "--help" or "-h":
                    return new IndexCommandOptions { ShowHelp = true };
                case "--sushi" or "--coffee" or "--ramen" or "--wine" or "--beer" or "--matcha" or "--whisky":
                    easterEgg = args[i];
                    spinnerFlagCount++;
                    break;
                case "--random-spinner":
                    randomSpinner = true;
                    break;
                default:
                    if (args[i].StartsWith('-'))
                    {
                        Console.Error.WriteLine($"Warning: unknown option '{args[i]}' (ignored) / 不明なオプション '{args[i]}'（無視されます）");
                        WriteUnknownIndexOptionSuggestion(args[i]);
                    }
                    else
                        projectPath = args[i];
                    break;
            }
        }

        if (projectFilters.Count > 0 && projectPath != null)
        {
            try
            {
                updateFiles.AddRange(SolutionProjectResolver.ResolveProjectFiles(projectPath, projectFilters, solutionPath));
            }
            catch (Exception ex) when (ex is IOException or UnauthorizedAccessException or InvalidOperationException)
            {
                projectFilterError = ex.Message;
            }
        }

        if (spinnerFlagCount > 1)
        {
            Console.Error.WriteLine("\U0001f375 Simultaneous intake of beer and coffee is not recommended. How about some matcha instead?");
            Console.Error.WriteLine("   \u30d3\u30fc\u30eb\u3068\u30b3\u30fc\u30d2\u30fc\u306e\u540c\u6642\u6442\u53d6\u306f\u304a\u3059\u3059\u3081\u3057\u307e\u305b\u3093\u3002\u62b9\u8336\u306f\u3044\u304b\u304c\uff1f");
            easterEgg = "--matcha";
        }

        if (randomSpinner && easterEgg == null)
        {
            var themes = new[] { "--sushi", "--coffee", "--ramen", "--wine", "--beer", "--matcha", "--whisky" };
            easterEgg = themes[Random.Shared.Next(themes.Length)];
        }

        return new IndexCommandOptions
        {
            // Absolutize critical paths at the option-parsing boundary so a cwd shift after
            // this point (embedded host, signal handler, future plugin) cannot silently break
            // relative-path math in FileIndexer / GitHelper / DbPathResolver. Issue #1577.
            // オプション解析の境界で絶対化し、以降の cwd 変化で相対パス計算が崩れないようにする。
            ProjectPath = AbsolutizePathOption(projectPath),
            DbPath = AbsolutizeDbPathOption(dbPath),
            Rebuild = rebuild,
            Verbose = verbose,
            Json = json,
            Quiet = quiet,
            Commits = commits,
            ChangedBetweenSpecified = changedBetweenSpecified,
            ChangedBetweenRefs = changedBetweenRefs,
            UpdateFiles = updateFiles,
            ProjectFilters = projectFilters,
            SolutionPath = solutionPath,
            ProjectFilterError = projectFilterError,
            ParseError = parseError,
            EasterEgg = easterEgg,
            DryRun = dryRun,
            Force = force,
            Watch = watch,
            WatchDebounceMs = watchDebounceMs,
            DurationFormat = durationFormat,
            MaxFileSizeBytes = maxFileSizeBytes,
            Parallelism = parallelism,
        };
    }

    internal const string IndexParallelismEnvironmentVariable = "CDIDX_INDEX_PARALLELISM";

    internal static int DefaultIndexParallelism()
        => Math.Clamp(Environment.ProcessorCount, 1, 16);

    private static int ReadIndexParallelismFromEnvironment()
    {
        var fallback = DefaultIndexParallelism();
        var value = Environment.GetEnvironmentVariable(IndexParallelismEnvironmentVariable);
        if (string.IsNullOrWhiteSpace(value))
            return fallback;

        return ParseIndexParallelism(value, fallback, IndexParallelismEnvironmentVariable);
    }

    private static int ParseIndexParallelism(string value, int fallback, string source)
    {
        if (int.TryParse(value, System.Globalization.NumberStyles.Integer, System.Globalization.CultureInfo.InvariantCulture, out var parsed) && parsed > 0)
            return parsed;

        Console.Error.WriteLine($"Warning: invalid {source} value '{value}' (ignored; use a positive integer) / 不正な {source} 値 '{value}'（無視。正の整数を指定）");
        return fallback;
    }

    private static long? ReadMaxFileSizeBytesFromEnvironment()
    {
        var value = Environment.GetEnvironmentVariable(FileIndexer.MaxFileSizeEnvironmentVariable);
        if (string.IsNullOrWhiteSpace(value))
            return null;

        if (FileIndexer.TryParseMaxFileSizeBytes(value, out var parsed))
            return parsed;

        Console.Error.WriteLine($"Warning: invalid {FileIndexer.MaxFileSizeEnvironmentVariable} value '{value}' (ignored; use positive bytes or K/M/G suffixes) / 不正な {FileIndexer.MaxFileSizeEnvironmentVariable} 値 '{value}'（無視。正の byte 数または K/M/G 接尾辞を指定）");
        return null;
    }

    private static long? ParseMaxFileBytes(string value, long? fallback)
    {
        if (FileIndexer.TryParseMaxFileSizeBytes(value, out var parsed))
            return parsed;

        Console.Error.WriteLine($"Warning: invalid --max-file-bytes value '{value}' (ignored; use positive bytes or K/M/G suffixes) / 不正な --max-file-bytes 値 '{value}'（無視。正の byte 数または K/M/G 接尾辞を指定）");
        return fallback;
    }

    private static DurationOutputFormat ParseDurationFormat(string value, DurationOutputFormat fallback)
    {
        return value.Trim().ToLowerInvariant() switch
        {
            "auto" => DurationOutputFormat.Auto,
            "seconds" => DurationOutputFormat.Seconds,
            "hms" => DurationOutputFormat.Hms,
            _ => WarnInvalidDurationFormat(value, fallback),
        };
    }

    private static DurationOutputFormat WarnInvalidDurationFormat(string value, DurationOutputFormat fallback)
    {
        Console.Error.WriteLine($"Warning: invalid --duration-format value '{value}' (ignored; use auto, seconds, or hms) / 不正な --duration-format 値 '{value}'（無視。auto, seconds, hms のいずれかを指定）");
        return fallback;
    }

    private static string? AbsolutizePathOption(string? value)
    {
        if (string.IsNullOrWhiteSpace(value))
            return value;
        try
        {
            return Path.GetFullPath(value);
        }
        catch (Exception ex) when (ex is ArgumentException or NotSupportedException or PathTooLongException)
        {
            return value;
        }
    }

    private static string? AbsolutizeDbPathOption(string? value)
    {
        if (string.IsNullOrWhiteSpace(value))
            return value;
        if (value.StartsWith("file:", StringComparison.OrdinalIgnoreCase))
            return value;
        return AbsolutizePathOption(value);
    }

    internal static string? TryCaptureCurrentDirectory()
    {
        try
        {
            return Environment.CurrentDirectory;
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
        {
            return null;
        }
    }

    /// <summary>
    /// Build a warning message when the process cwd at the final write step differs from
    /// the cwd captured at the option-parsing boundary. Returns null when the two cwds
    /// are equal or either snapshot is missing. Issue #1577.
    /// </summary>
    internal static string? BuildCwdDriftNotice(string? initialCwd, string? currentCwd)
    {
        if (string.IsNullOrEmpty(initialCwd) || string.IsNullOrEmpty(currentCwd))
            return null;
        var comparison = OperatingSystem.IsWindows()
            ? StringComparison.OrdinalIgnoreCase
            : StringComparison.Ordinal;
        if (string.Equals(initialCwd, currentCwd, comparison))
            return null;
        return $"Process working directory changed during index (was {initialCwd}, now {currentCwd}). "
            + "Index/query paths were absolutized at the option-parsing boundary so this run "
            + "is unaffected, but later code paths that depend on Environment.CurrentDirectory "
            + "may misbehave. Restore the original working directory or re-resolve relative paths.";
    }

    private static BackfillFoldCommandOptions ParseBackfillFoldArgs(string[] args)
    {
        var dbPath = Path.Combine(".cdidx", "codeindex.db");
        var json = false;

        for (int i = 0; i < args.Length; i++)
        {
            switch (args[i])
            {
                case "--db" when i + 1 < args.Length:
                    dbPath = args[++i];
                    break;
                case "--json":
                    json = true;
                    break;
                case "--help" or "-h":
                    return new BackfillFoldCommandOptions { ShowHelp = true, DbPath = dbPath, Json = json };
                default:
                    if (args[i].StartsWith('-'))
                    {
                        Console.Error.WriteLine($"Warning: unknown option '{args[i]}' (ignored) / 不明なオプション '{args[i]}'（無視されます）");
                        WriteUnknownBackfillFoldOptionSuggestion(args[i]);
                    }
                    else
                        return new BackfillFoldCommandOptions
                        {
                            DbPath = dbPath,
                            Json = json,
                            ParseError = $"backfill-fold does not accept positional arguments: '{args[i]}'"
                        };
                    break;
            }
        }

        return new BackfillFoldCommandOptions
        {
            DbPath = dbPath,
            Json = json,
        };
    }

    private static int RunUpdateMode(
        DbWriter writer,
        FileIndexer indexer,
        string projectRoot,
        string resolvedDbPath,
        IndexCommandOptions options,
        Stopwatch stopwatch,
        string[] spinnerFrames,
        JsonSerializerOptions jsonOptions,
        int priorReadiness,
        string? priorFoldVersion,
        string? priorFoldFingerprint,
        bool priorSymbolExtractorVersionsMatchCurrent,
        string? priorCSharpSymbolNameContractVersion,
        string? priorMetadataTargetCsharp,
        string? priorSqlGraphContractVersion,
        IReadOnlyDictionary<string, string?> priorHotspotFamilyVersions,
        IReadOnlyDictionary<string, string?> priorHotspotFamilyMarkerFingerprints,
        IReadOnlyDictionary<string, string?> currentHotspotFamilyMarkerFingerprints,
        string? priorIndexedProjectRoot,
        string? priorIndexedHeadCommit,
        string? currentHeadCommit,
        string? initialCwd,
        CancellationToken cancellationToken)
    {
        var jsonContext = CliJsonSerializerContextFactory.Create(jsonOptions);
        var currentSqlGraphContractVersion = DbContext.SqlGraphContractVersion.ToString(System.Globalization.CultureInfo.InvariantCulture);
        var sqlGraphContractMatchesCurrent = priorSqlGraphContractVersion == currentSqlGraphContractVersion;
        var unresolvedMergeExitCode = RejectUnresolvedMergeState(projectRoot, options.Json, jsonOptions);
        if (unresolvedMergeExitCode != null)
            return unresolvedMergeExitCode.Value;

        var targetPaths = new HashSet<string>(StringComparer.Ordinal);
        var relevantIgnoreFileChanged = false;

        if (options.Commits.Count > 0)
        {
            CancellationTokenSource? spinnerCts = null;
            try
            {
                if (!options.Json)
                    spinnerCts = ConsoleUi.StartSpinner("Resolving changed files...", spinnerFrames);
                var repoRoot = GitHelper.TryGetRepositoryRoot(projectRoot) ?? Path.GetFullPath(projectRoot);
                foreach (var commit in options.Commits)
                {
                    var changedFiles = GitHelper.GetChangedFilesFromCommit(projectRoot, commit);
                    var normalized = NormalizeCommitFileTargets(projectRoot, repoRoot, changedFiles, out var commitTouchedRelevantIgnoreFile);
                    relevantIgnoreFileChanged |= commitTouchedRelevantIgnoreFile;
                    foreach (var f in normalized)
                        targetPaths.Add(f);
                }
            }
            catch (Exception ex)
            {
                return WriteCommandError(
                    options.Json,
                    jsonOptions,
                    $"failed to resolve changed files from git commits: {ex.Message}",
                    CommandExitCodes.UsageError,
                    "Check the commit IDs and rerun `cdidx index <projectPath> --commits <id> [id ...]`.",
                    CommandErrorCodes.UsageError);
            }
            finally
            {
                ConsoleUi.StopSpinner(spinnerCts);
            }
            if (!options.Json && !options.Quiet)
            {
                Console.WriteLine($"  Found {ConsoleUi.Counted(targetPaths.Count, "changed file")} from git");
                Console.WriteLine("  Note    : After reset/rebase/amend/switch/merge, prefer `cdidx .` over `--commits` for a full sync / 履歴改変やcheckout変更後は `--commits` より `cdidx .` を推奨");
            }
        }

        if (options.ChangedBetweenSpecified)
        {
            CancellationTokenSource? spinnerCts = null;
            WriteJsonLiveness("resolving changed files between git refs...");
            var resolveHeartbeat = StartJsonPhaseHeartbeat("resolving changed files between git refs");
            try
            {
                if (!options.Json)
                    spinnerCts = ConsoleUi.StartSpinner("Resolving changed files between refs...", spinnerFrames);
                var repoRoot = GitHelper.TryGetRepositoryRoot(projectRoot) ?? Path.GetFullPath(projectRoot);
                var changedFiles = GitHelper.GetChangedFilesBetweenRefs(projectRoot, options.ChangedBetweenRefs[0], options.ChangedBetweenRefs[1]);
                var normalized = NormalizeCommitFileTargets(projectRoot, repoRoot, changedFiles, out var rangeTouchedRelevantIgnoreFile);
                relevantIgnoreFileChanged |= rangeTouchedRelevantIgnoreFile;
                foreach (var f in normalized)
                    targetPaths.Add(f);
            }
            catch (Exception ex)
            {
                return WriteCommandError(
                    options.Json,
                    jsonOptions,
                    $"failed to resolve changed files between git refs: {ex.Message}",
                    CommandExitCodes.UsageError,
                    "Check the refs and rerun `cdidx index <projectPath> --changed-between <old-ref> <new-ref>`.",
                    CommandErrorCodes.UsageError);
            }
            finally
            {
                StopJsonPhaseHeartbeat(resolveHeartbeat);
                ConsoleUi.StopSpinner(spinnerCts);
            }

            WriteJsonLiveness($"found {ConsoleUi.Counted(targetPaths.Count, "changed file")}; preparing update...");
            if (!options.Json)
                Console.WriteLine($"  Found {targetPaths.Count} changed file(s) between git refs");
        }

        if (options.UpdateFiles.Count > 0)
        {
            relevantIgnoreFileChanged |= ContainsRelevantIgnoreFileUpdate(projectRoot, options.UpdateFiles);
            foreach (var relPath in NormalizeUpdateFileTargets(projectRoot, options.UpdateFiles, options.Json))
                targetPaths.Add(relPath);
        }

        var typeScriptJavaScriptConfigChanged = ContainsJavaScriptTypeScriptConfigPath(targetPaths);
        if (relevantIgnoreFileChanged || ContainsIgnoreFilePath(targetPaths) || typeScriptJavaScriptConfigChanged)
        {
            if (!options.Json && !options.Quiet)
            {
                var reason = typeScriptJavaScriptConfigChanged
                    ? "JavaScript/TypeScript config changes"
                    : "ignore-file changes";
                Console.WriteLine($"  Detected {reason}; falling back to a full scan to keep the index aligned.");
                Console.WriteLine();
            }

            return RunFullScan(
                writer,
                indexer,
                projectRoot,
                resolvedDbPath,
                options,
                stopwatch,
                spinnerFrames,
                jsonOptions,
                priorFoldVersion,
                priorFoldFingerprint,
                priorSymbolExtractorVersionsMatchCurrent,
                priorCSharpSymbolNameContractVersion,
                priorMetadataTargetCsharp,
                priorSqlGraphContractVersion,
                priorHotspotFamilyVersions,
                priorHotspotFamilyMarkerFingerprints,
                currentHotspotFamilyMarkerFingerprints,
                priorIndexedProjectRoot,
                priorIndexedHeadCommit,
                currentHeadCommit,
                initialCwd,
                cancellationToken);
        }

        if (!options.Json && !options.Quiet)
            Console.WriteLine($"Updating {ConsoleUi.Counted(targetPaths.Count, "file")}...");
        CancellationTokenSource? updateCts = null;
          var interactiveUpdateSpinner = !options.Json && !options.Quiet && ConsoleUi.ShouldUseInteractiveConsole();
        int updated = 0, removed = 0, skipped = 0, warnings = 0, errors = 0;
        var errorList = new List<CliJsonMessage>();
        var warningList = new List<CliJsonMessage>();
        var scanErrorKeys = new HashSet<string>(StringComparer.Ordinal);
        var visitedFileIdentities = new HashSet<FileIndexer.FileIdentity>();
        var readinessDemoted = false;
        var normalizedProjectRoot = Path.GetFullPath(projectRoot);
        var normalizedPriorIndexedProjectRoot = string.IsNullOrWhiteSpace(priorIndexedProjectRoot)
            ? null
            : Path.GetFullPath(priorIndexedProjectRoot);
        var projectRootWritten = PathsEqual(normalizedPriorIndexedProjectRoot, normalizedProjectRoot);
        var ftsMutated = false;
        var purgedRefs = 0;
        var supportedGraphLanguages = ReferenceExtractor.GetSupportedLanguages();
        var currentFoldVersion = NameFold.Version.ToString(System.Globalization.CultureInfo.InvariantCulture);
        var currentFoldFingerprint = NameFold.Fingerprint();
        var currentCSharpSymbolNameContractVersion = DbContext.CSharpSymbolNameContractVersion.ToString(System.Globalization.CultureInfo.InvariantCulture);
        var csharpSymbolNameContractMatchesCurrent = priorCSharpSymbolNameContractVersion == currentCSharpSymbolNameContractVersion;
        var currentMetadataTargetVersion = DbContext.MetadataTargetVersion.ToString(System.Globalization.CultureInfo.InvariantCulture);
        var priorMetadataTargetCsharpMatchesCurrent = priorMetadataTargetCsharp == currentMetadataTargetVersion;

        void WriteJsonLiveness(string message)
        {
            if (!options.Json || options.Quiet)
                return;

            Console.Error.WriteLine($"cdidx: {message}");
        }

        (CancellationTokenSource Cts, Task Task)? StartJsonPhaseHeartbeat(string phase, Func<string?>? detailProvider = null)
        {
            if (!options.Json || options.Quiet)
                return null;

            var cts = new CancellationTokenSource();
            var token = cts.Token;
            var task = Task.Run(async () =>
            {
                while (!token.IsCancellationRequested)
                {
                    try
                    {
                        await Task.Delay(TimeSpan.FromSeconds(5), token).ConfigureAwait(false);
                    }
                    catch (OperationCanceledException)
                    {
                        break;
                    }

                    if (token.IsCancellationRequested)
                        break;

                    var detail = detailProvider?.Invoke();
                    var suffix = string.IsNullOrWhiteSpace(detail) ? string.Empty : $": {detail}";
                    Console.Error.WriteLine($"cdidx: still {phase}{suffix}...");
                }
            }, token);
            return (cts, task);
        }

        void StopJsonPhaseHeartbeat((CancellationTokenSource Cts, Task Task)? heartbeat)
        {
            if (heartbeat == null)
                return;

            heartbeat.Value.Cts.Cancel();
            try
            {
                heartbeat.Value.Task.Wait(TimeSpan.FromSeconds(1));
            }
            catch (AggregateException ex) when (ex.InnerExceptions.All(inner => inner is OperationCanceledException or TaskCanceledException))
            {
            }
            heartbeat.Value.Cts.Dispose();
        }

        void DemoteReadinessOnce()
        {
            if (readinessDemoted)
                return;

            // Demote readiness in its own committed step once we know a real mutation is
            // about to happen. If the following file update rolls back, readers must still
            // see the DB as degraded rather than trusting stale ready bits. No-op updates
            // never call this path, so shared explicit DB metadata stays stable.
            // 実 mutation が必要と確定した時点で readiness を別コミットで下げる。直後の
            // file update が rollback しても reader は stale ready bit を信じない。
            // no-op update では呼ばないので、shared explicit DB の metadata も安定する。
            writer.ClearReadyFlags();
            writer.ClearHotspotFamilyReady();
            writer.ClearMetadataTargetReady();
            readinessDemoted = true;
        }

        void WriteProjectRootOnce()
        {
            if (!projectRootWritten)
            {
                writer.SetMeta(DbContext.IndexedProjectRootMetaKey, normalizedProjectRoot);
                projectRootWritten = true;
            }
        }

        void RecordScanErrors(IEnumerable<FileIndexer.ScanError> scanErrors)
        {
            foreach (var scanError in scanErrors)
            {
                var key = $"{scanError.Severity}\n{scanError.Path}\n{scanError.Message}";
                if (!scanErrorKeys.Add(key))
                    continue;

                if (scanError.IsFatal)
                {
                    DemoteReadinessOnce();
                    errors++;
                    errorList.Add(new CliJsonMessage(scanError.Path, scanError.Message));
                }
                else
                {
                    warnings++;
                    warningList.Add(new CliJsonMessage(scanError.Path, scanError.Message));
                }

                if (!options.Json)
                {
                    PauseUpdateSpinnerForConsoleWrite();
                    ConsoleUi.PrintWarning($"{scanError.Path}: {scanError.Message}");
                    ResumeUpdateSpinnerAfterConsoleWrite();
                }
            }
        }

        void StartUpdateSpinnerIfNeeded()
        {
            if (!interactiveUpdateSpinner || updateCts != null)
                return;

            updateCts = ConsoleUi.StartSpinner("Updating...", spinnerFrames);
        }

        void PauseUpdateSpinnerForConsoleWrite()
        {
            if (updateCts == null)
                return;

            ConsoleUi.StopSpinner(updateCts);
            updateCts = null;
        }

        void ResumeUpdateSpinnerAfterConsoleWrite()
        {
            if (!interactiveUpdateSpinner)
                return;

            StartUpdateSpinnerIfNeeded();
        }

        void ThrowIfUpdateCancelled()
        {
            if (!cancellationToken.IsCancellationRequested)
                return;

            PauseUpdateSpinnerForConsoleWrite();
            throw new IndexInterruptedException(updated + removed, targetPaths.Count);
        }

        ThrowIfUpdateCancelled();
        WriteJsonLiveness("checking C# workspace contracts...");
        var csharpWorkspaceHeartbeat = StartJsonPhaseHeartbeat("checking C# workspace contracts");
        CSharpStaticInterfaceWorkspaceSymbols csharpWorkspace;
        try
        {
            csharpWorkspace = BuildCSharpStaticInterfaceWorkspaceSymbols(
                writer,
                indexer,
                projectRoot,
                targetPaths,
                cancellationToken: cancellationToken);
        }
        catch (OperationCanceledException) when (cancellationToken.IsCancellationRequested)
        {
            throw new IndexInterruptedException(updated + removed, targetPaths.Count);
        }
        finally
        {
            StopJsonPhaseHeartbeat(csharpWorkspaceHeartbeat);
        }
        if (csharpWorkspace.HasStaticInterfaceContracts)
        {
            WriteJsonLiveness("expanding C# update set for static interface contracts...");
            var expandHeartbeat = StartJsonPhaseHeartbeat("expanding C# update set for static interface contracts");
            try
            {
                foreach (var filePath in indexer.ScanFilesDetailed().Files)
                {
                    var detection = FileIndexer.TryDetectLanguage(filePath);
                    if (detection.Status == FileIndexer.FileProbeStatus.Supported
                        && detection.Language == "csharp")
                    {
                        targetPaths.Add(filePath);
                    }
                }

                csharpWorkspace = BuildCSharpStaticInterfaceWorkspaceSymbols(
                    writer,
                    indexer,
                    projectRoot,
                    targetPaths,
                    cancellationToken: cancellationToken);
            }
            catch (OperationCanceledException) when (cancellationToken.IsCancellationRequested)
            {
                throw new IndexInterruptedException(updated + removed, targetPaths.Count);
            }
            finally
            {
                StopJsonPhaseHeartbeat(expandHeartbeat);
            }
        }

        if (writer.CountUnsupportedReferences(supportedGraphLanguages) > 0)
        {
            DemoteReadinessOnce();

            using var purgeTxn = writer.BeginTransaction();
            purgedRefs = writer.PurgeUnsupportedReferences(supportedGraphLanguages);
            if (purgedRefs > 0)
                purgeTxn.Commit();
        }

        StartUpdateSpinnerIfNeeded();

        WriteJsonLiveness($"updating {ConsoleUi.Counted(targetPaths.Count, "file")}...");
        string? currentUpdatePath = null;
        var updateHeartbeat = StartJsonPhaseHeartbeat(
            "updating index",
            () => currentUpdatePath == null
                ? $"{updated + removed + skipped:N0}/{targetPaths.Count:N0} files processed"
                : $"{updated + removed + skipped:N0}/{targetPaths.Count:N0} files processed, current {currentUpdatePath}");
        try
        {
            foreach (var relPath in targetPaths)
            {
                ThrowIfUpdateCancelled();
                StartUpdateSpinnerIfNeeded();
                currentUpdatePath = relPath;
                var absPath = Path.Combine(projectRoot, relPath.Replace('/', Path.DirectorySeparatorChar));
                try
                {
                    if (!File.Exists(LongPath.EnsureWindowsPrefix(absPath)))
                    {
                        if (!writer.HasFileAtPath(relPath))
                        {
                            skipped++;
                            if (options.Verbose && !options.Json && !options.Quiet)
                            {
                                PauseUpdateSpinnerForConsoleWrite();
                                Console.WriteLine($"  [SKIP] {relPath} (not in DB)");
                                ResumeUpdateSpinnerAfterConsoleWrite();
                            }
                            continue;
                        }

                        DemoteReadinessOnce();
                        using var deleteTxn = writer.BeginTransaction();
                        if (writer.DeleteFileByPath(relPath))
                        {
                            WriteProjectRootOnce();
                            deleteTxn.Commit();
                            removed++;
                            ftsMutated = true;
                            if (options.Verbose && !options.Json && !options.Quiet)
                            {
                                PauseUpdateSpinnerForConsoleWrite();
                                Console.WriteLine($"  [DEL ] {relPath}");
                                ResumeUpdateSpinnerAfterConsoleWrite();
                            }
                        }
                        else
                        {
                            skipped++;
                            if (options.Verbose && !options.Json && !options.Quiet)
                            {
                                PauseUpdateSpinnerForConsoleWrite();
                                Console.WriteLine($"  [SKIP] {relPath} (not in DB)");
                                ResumeUpdateSpinnerAfterConsoleWrite();
                            }
                        }
                        continue;
                    }

                    var pathFilter = indexer.EvaluatePathFilter(absPath);
                RecordScanErrors(pathFilter.Errors);
                if (pathFilter.ShouldSkip)
                {
                    if (!pathFilter.ShouldDeleteExisting)
                    {
                        skipped++;
                        if (options.Verbose && !options.Json && !options.Quiet)
                        {
                            PauseUpdateSpinnerForConsoleWrite();
                            Console.WriteLine($"  [SKIP] {relPath} ({DescribePathFilter(pathFilter.FilterKind)})");
                            ResumeUpdateSpinnerAfterConsoleWrite();
                        }
                        continue;
                    }

                    if (!writer.HasFileAtPath(relPath))
                    {
                        skipped++;
                        if (options.Verbose && !options.Json && !options.Quiet)
                        {
                            PauseUpdateSpinnerForConsoleWrite();
                            Console.WriteLine($"  [SKIP] {relPath} ({DescribePathFilter(pathFilter.FilterKind)})");
                            ResumeUpdateSpinnerAfterConsoleWrite();
                        }
                        continue;
                    }

                    DemoteReadinessOnce();
                    using var deleteTxn = writer.BeginTransaction();
                    if (writer.DeleteFileByPath(relPath))
                    {
                        WriteProjectRootOnce();
                        deleteTxn.Commit();
                        removed++;
                        ftsMutated = true;
                        if (options.Verbose && !options.Json && !options.Quiet)
                        {
                            PauseUpdateSpinnerForConsoleWrite();
                            Console.WriteLine($"  [DEL ] {relPath} ({DescribePathFilter(pathFilter.FilterKind)})");
                            ResumeUpdateSpinnerAfterConsoleWrite();
                        }
                    }
                    else
                    {
                        skipped++;
                        if (options.Verbose && !options.Json)
                        {
                            PauseUpdateSpinnerForConsoleWrite();
                            Console.WriteLine($"  [SKIP] {relPath} ({DescribePathFilter(pathFilter.FilterKind)})");
                            ResumeUpdateSpinnerAfterConsoleWrite();
                        }
                    }
                    continue;
                }

                var indexability = FileIndexer.GetFileIndexability(absPath);
                var detection = FileIndexer.TryDetectLanguage(absPath);
                if (indexability == FileIndexer.FileProbeStatus.ProbeFailed || detection.Status == FileIndexer.FileProbeStatus.ProbeFailed)
                {
                    DemoteReadinessOnce();

                    errors++;
                    errorList.Add(new CliJsonMessage(relPath, "Could not probe file for indexability/language."));
                    if (!options.Json)
                    {
                        PauseUpdateSpinnerForConsoleWrite();
                        if (options.Verbose)
                            Console.Error.WriteLine($"  [ERR ] {relPath}: Could not probe file for indexability/language.");
                        else
                            Console.Error.WriteLine($"  [ERR ] {relPath}: Could not probe file for indexability/language.");
                        ResumeUpdateSpinnerAfterConsoleWrite();
                    }
                    continue;
                }

                if (indexability != FileIndexer.FileProbeStatus.Supported || detection.Status != FileIndexer.FileProbeStatus.Supported)
                {
                    if (!writer.HasFileAtPath(relPath))
                    {
                        skipped++;
                        if (options.Verbose && !options.Json && !options.Quiet)
                        {
                            PauseUpdateSpinnerForConsoleWrite();
                            Console.WriteLine($"  [SKIP] {relPath} (unsupported type)");
                            ResumeUpdateSpinnerAfterConsoleWrite();
                        }
                        continue;
                    }

                    DemoteReadinessOnce();
                    using var deleteTxn = writer.BeginTransaction();
                    if (writer.DeleteFileByPath(relPath))
                    {
                        WriteProjectRootOnce();
                        deleteTxn.Commit();
                        removed++;
                        ftsMutated = true;
                        if (options.Verbose && !options.Json && !options.Quiet)
                        {
                            PauseUpdateSpinnerForConsoleWrite();
                            Console.WriteLine($"  [DEL ] {relPath} (no longer indexable)");
                            ResumeUpdateSpinnerAfterConsoleWrite();
                        }
                    }
                    else
                    {
                        skipped++;
                        if (options.Verbose && !options.Json)
                        {
                            PauseUpdateSpinnerForConsoleWrite();
                            Console.WriteLine($"  [SKIP] {relPath} (unsupported type)");
                            ResumeUpdateSpinnerAfterConsoleWrite();
                        }
                    }
                    continue;
                }

                if (FileIndexer.TryGetFileIdentity(absPath, out var identity) && !visitedFileIdentities.Add(identity))
                {
                    var message = "Skipped hardlinked file because the same file content was already indexed from another path.";
                    warnings++;
                    warningList.Add(new CliJsonMessage(relPath, message));
                    if (!options.Json && !options.Quiet)
                    {
                        PauseUpdateSpinnerForConsoleWrite();
                        ConsoleUi.PrintWarning($"{relPath}: {message}");
                        ResumeUpdateSpinnerAfterConsoleWrite();
                    }

                    if (!writer.HasFileAtPath(relPath))
                    {
                        skipped++;
                        continue;
                    }

                    DemoteReadinessOnce();
                    using var deleteTxn = writer.BeginTransaction();
                    if (writer.DeleteFileByPath(relPath))
                    {
                        WriteProjectRootOnce();
                        deleteTxn.Commit();
                        removed++;
                        ftsMutated = true;
                    }
                    else
                    {
                        skipped++;
                    }
                    continue;
                }

                var (record, content, rawBytes, warning) = indexer.BuildRecordWithRawBytes(absPath);

                if (warning != null && !options.Json && !options.Quiet)
                {
                    PauseUpdateSpinnerForConsoleWrite();
                    ConsoleUi.PrintWarning(warning);
                    ResumeUpdateSpinnerAfterConsoleWrite();
                }

                var existingId = writer.GetUnchangedFileId(
                    record.Path,
                    record.Modified,
                    record.Checksum,
                    language: record.Lang,
                    allowReuse: record.Lang is not ("javascript" or "typescript")
                        && (record.Lang != "csharp" || csharpSymbolNameContractMatchesCurrent)
                        && (record.Lang != "csharp" || !csharpWorkspace.HasStaticInterfaceContracts)
                        && (record.Lang != "sql" || sqlGraphContractMatchesCurrent));
                if (existingId != null)
                {
                    writer.PurgeStaleFilesSharingChecksum(projectRoot, record.Path, record.Checksum);
                    skipped++;
                    if (options.Verbose && !options.Json && !options.Quiet)
                    {
                        PauseUpdateSpinnerForConsoleWrite();
                        Console.WriteLine($"  [SKIP] {relPath} (unchanged)");
                        ResumeUpdateSpinnerAfterConsoleWrite();
                    }
                    continue;
                }

                DemoteReadinessOnce();
                using var txn = writer.BeginTransaction();
                writer.PurgeStaleFilesSharingChecksum(projectRoot, record.Path, record.Checksum);
                WriteProjectRootOnce();
                var fileId = writer.UpsertFile(record);
                var chunks = ChunkSplitter.Split(fileId, content);
                writer.InsertChunks(chunks);
                var symbols = SymbolExtractor.Extract(fileId, record.Lang, content, absPath, Path.GetFullPath(options.ProjectPath!));
                SymbolExtractor.ApplyFamilyScope(symbols, indexer.GetFamilyScopeKey(absPath, record.Lang));
                writer.InsertSymbols(symbols);
                var references = ReferenceExtractor.Extract(
                    fileId,
                    record.Lang,
                    content,
                    symbols,
                    record.Path,
                    record.Lang == "csharp" ? csharpWorkspace.Symbols : null);
                writer.InsertReferences(references);
                // Validate content for encoding issues / エンコーディング問題を検証
                var issues = FileIndexer.ValidateContent(record.Path, rawBytes, content);
                writer.InsertIssues(fileId, issues);
                txn.Commit();

                updated++;
                ftsMutated = true;
                ThrowIfUpdateCancelled();
                if (options.Verbose && !options.Json && !options.Quiet)
                {
                    PauseUpdateSpinnerForConsoleWrite();
                    Console.WriteLine($"  [OK  ] {relPath} ({chunks.Count} chunks, {symbols.Count} symbols, {references.Count} refs)");
                    ResumeUpdateSpinnerAfterConsoleWrite();
                }
            }
            catch (Exception ex)
            {
                DemoteReadinessOnce();

                errors++;
                errorList.Add(new CliJsonMessage(relPath, ex.Message));
                if (!options.Json)
                {
                    PauseUpdateSpinnerForConsoleWrite();
                    Console.Error.WriteLine(FormatPerFileErrorLine("ERR ", relPath, ex));
                    ResumeUpdateSpinnerAfterConsoleWrite();
                }
            }
        }
        }
        finally
        {
            StopJsonPhaseHeartbeat(updateHeartbeat);
        }

        ThrowIfUpdateCancelled();
        PauseUpdateSpinnerForConsoleWrite();

        if (purgedRefs > 0 && !options.Json && !options.Quiet)
            Console.WriteLine($"  Purged {purgedRefs:N0} stale references (unsupported language)");

        if (ftsMutated)
            writer.OptimizeFts();
        ThrowIfUpdateCancelled();
        // Only stamp readiness on a fully successful run (errors == 0). A partial / error
        // run leaves the DB unstamped so readers correctly treat graph / issues data as
        // degraded rather than authoritative. Interrupted runs also stay unstamped because
        // readiness was demoted before the first committed mutation.
        // errors==0 の成功 run のみマーカーを打つ。途中失敗は未 stamp のままで縮退扱い。
        var graphTableAvailableAfter = !readinessDemoted
            ? (priorReadiness & DbContext.GraphReadyFlag) != 0
            : false;
        var issuesTableAvailableAfter = !readinessDemoted
            ? (priorReadiness & DbContext.IssuesReadyFlag) != 0
            : false;
        var csharpSymbolNameReadyAfter = !writer.HasAnyFilesWithLanguage("csharp")
            || (!readinessDemoted && csharpSymbolNameContractMatchesCurrent);
        var csharpMetadataTargetReadyAfter = !writer.HasAnyFilesWithLanguage("csharp")
            || (!readinessDemoted && priorMetadataTargetCsharpMatchesCurrent);
        var foldReadyAfter = !readinessDemoted
            && (priorReadiness & DbContext.FoldReadyFlag) != 0
            && priorFoldVersion == currentFoldVersion
            && priorFoldFingerprint == currentFoldFingerprint
            && priorSymbolExtractorVersionsMatchCurrent;
        string? foldReadyReasonAfter = foldReadyAfter
            ? null
            : GetFoldReadyReason(
                (priorReadiness & DbContext.FoldReadyFlag) != 0,
                priorFoldVersion == currentFoldVersion,
                priorFoldFingerprint == currentFoldFingerprint);
        if (readinessDemoted && errors == 0)
        {
            // Restore each readiness bit independently based on what the DB carried BEFORE
            // ClearReadyFlags wiped them. A pre-#86 DB (user_version=3, i.e. Graph+Issues but
            // no Fold) must keep Graph+Issues after a successful partial update, even though
            // FoldReady can't be restamped. Codex #86 second-pass review: the old single-flag
            // `wasFullyReady` gate silently dropped Graph/Issues for the whole workspace on
            // such DBs, breaking references/callers/callees/impact.
            // Fold is the only bit that needs the runtime verify: the other two only require
            // that the DB previously reached end-of-run for those subsystems. Fold also
            // requires name_folded to be populated for every row, but the invariant holds
            // when the prior bit was set AND this update rewrote its touched rows with
            // name_folded populated, so no extra scan is needed here.
            // update mode は事前 bit を個別に復元。Graph/Issues は prior bit があれば復元、
            // Fold も prior bit があれば invariant を信じて restamp（codex 2nd review 対応）。
            // unreadable ignore file の true no-op skip は ClearReadyFlags 自体を避けるので、
            // ここでは通常どおり errors==0 の成功 run だけを復元対象にする。
            if ((priorReadiness & DbContext.GraphReadyFlag) != 0)
            {
                writer.MarkGraphReady();
                graphTableAvailableAfter = true;
            }
            if ((priorReadiness & DbContext.IssuesReadyFlag) != 0)
            {
                writer.MarkIssuesReady();
                issuesTableAvailableAfter = true;
            }
            if (sqlGraphContractMatchesCurrent || !writer.HasAnyFilesWithLanguage("sql"))
                writer.MarkSqlGraphContractReady();
            if (csharpSymbolNameContractMatchesCurrent || !writer.HasAnyFilesWithLanguage("csharp"))
            {
                writer.MarkCSharpSymbolNameContractReady();
                csharpSymbolNameReadyAfter = true;
            }
            // Issue #435: run the metadata-target resolver across all currently-indexed C#
            // class rows. This is always safe because the resolver rewrites every row, so
            // legacy NULL rows from a pre-#435 DB and untouched rows from this partial
            // update both end up authoritative. Only stamp readiness when the resolver
            // actually ran (i.e. there are C# files to resolve).
            // Issue #435: 成功 update の末尾で全 csharp class 行を resolver で再分類する。
            // resolver は全行を書き直すので pre-#435 DB の NULL 行と未更新行の両方が
            // authoritative になる。csharp ファイルがある場合のみ readiness も立てる。
            if (writer.HasAnyFilesWithLanguage("csharp"))
            {
                writer.ResolveCSharpMetadataTargets();
                writer.MarkMetadataTargetReady("csharp");
                csharpMetadataTargetReadyAfter = true;
            }
            else
            {
                csharpMetadataTargetReadyAfter = true;
            }
            writer.RebuildTypeScriptAugmentationReferences(projectRoot);
            RestampHotspotFamilyTrustForUpdate(
                writer,
                priorHotspotFamilyVersions,
                priorHotspotFamilyMarkerFingerprints,
                currentHotspotFamilyMarkerFingerprints);
            // FoldReady restamp requires both the prior stored version and fingerprint to
            // match the current binary/runtime. Otherwise untouched rows still carry keys
            // from an older fold implementation or runtime table set, and advertising
            // FoldReady would silently mismatch on --exact. Only full rebuild can re-fold all rows.
            // fold は version / fingerprint の両一致時のみ restamp。ズレた DB は rebuild まで
            // fold_ready=false のまま残す。
            if ((priorReadiness & DbContext.FoldReadyFlag) != 0
                && priorFoldVersion == currentFoldVersion
                && priorFoldFingerprint == currentFoldFingerprint
                && priorSymbolExtractorVersionsMatchCurrent)
            {
                // MarkFoldReady re-verifies inside BEGIN IMMEDIATE; a concurrent NULL-folded
                // insert during this restamp window leaves foldReadyAfter=false. Issue #1535.
                // MarkFoldReady は BEGIN IMMEDIATE 内で再検証する。restamp 窓の concurrent
                // 書き込みで NULL 行が残った場合は foldReadyAfter=false のまま。Issue #1535。
                foldReadyAfter = writer.MarkFoldReady();
            }
            writer.WriteCdidxWriterVersion(ConsoleUi.LoadVersion());
        }
        if (errors == 0)
            StampIndexedHeadMetadata(writer, projectRoot);
        stopwatch.Stop();
        // Detect cwd drift between option-parsing and finalize. Paths used in this run are
        // already absolute, but a drifted cwd is a strong signal that an embedded host or
        // signal handler mutated process state -- surface it so the operator can correct
        // their hosting code. Issue #1577.
        var finalCwd = TryCaptureCurrentDirectory();
        var cwdDriftNotice = BuildCwdDriftNotice(initialCwd, finalCwd);
        var cwdDriftDetected = cwdDriftNotice != null;
        if (cwdDriftDetected)
        {
            warningList.Add(new CliJsonMessage("<process_cwd>", cwdDriftNotice!));
            warnings++;
        }
        var (totalFiles, totalChunks, totalSymbols, totalReferences) = writer.GetCounts();
        var signalReader = new DbReader(writer.Connection);
        var sqlGraphContractSignalAfter = signalReader.GetSqlGraphContractSignal(lang: null);
        var hotspotFamilySignalAfter = signalReader.GetHotspotFamilySignal(lang: null);
        var sqlGraphContractReadyAfter = sqlGraphContractSignalAfter.Ready;
        var sqlGraphContractDegradedReasonAfter = sqlGraphContractSignalAfter.DegradedReason;
        var hotspotFamilyReadyAfter = hotspotFamilySignalAfter.Ready;
        var hotspotFamilyDegradedReasonAfter = hotspotFamilySignalAfter.DegradedReason;

        var foldOnlyRemediation = BuildFoldOnlyReadinessRemediation(
            graphTableAvailableAfter,
            issuesTableAvailableAfter,
            sqlGraphContractReadyAfter,
            hotspotFamilyReadyAfter,
            csharpSymbolNameReadyAfter,
            csharpMetadataTargetReadyAfter,
            foldReadyAfter,
            foldReadyReasonAfter,
            projectRoot,
            resolvedDbPath);

        if (options.Json)
        {
            Console.WriteLine(JsonSerializer.Serialize(new IndexUpdateJsonResult
            {
                Status = errors > 0 ? "partial" : "success",
                Mode = "update",
                Summary = new IndexUpdateSummaryJsonResult
                {
                    FilesTotal = totalFiles,
                    ChunksTotal = totalChunks,
                    SymbolsTotal = totalSymbols,
                    ReferencesTotal = totalReferences,
                    Updated = updated,
                    Removed = removed,
                    Skipped = skipped,
                    Warnings = warnings,
                    Errors = errors,
                },
                GraphTableAvailable = graphTableAvailableAfter,
                IssuesTableAvailable = issuesTableAvailableAfter,
                SqlGraphContractReady = sqlGraphContractReadyAfter,
                SqlGraphContractDegradedReason = sqlGraphContractDegradedReasonAfter,
                HotspotFamilyReady = hotspotFamilyReadyAfter,
                HotspotFamilyDegradedReason = hotspotFamilyDegradedReasonAfter,
                CSharpSymbolNameReady = csharpSymbolNameReadyAfter,
                CSharpMetadataTargetReady = csharpMetadataTargetReadyAfter,
                // #86 codex review: expose fold-readiness so AI clients can decide whether
                // `--exact` will use the Unicode fold path or fall back to ASCII NOCASE.
                // #86 codex: AI クライアントが --exact の経路を判断できるよう fold_ready を返す。
                FoldReady = foldReadyAfter,
                FoldReadyReason = foldReadyAfter ? null : foldReadyReasonAfter,
                DegradedReason = foldOnlyRemediation?.DegradedReason,
                RecommendedAction = foldOnlyRemediation?.RecommendedAction,
                AlternativeAction = foldOnlyRemediation?.AlternativeAction,
                CwdDriftDetected = cwdDriftDetected,
                CwdAtStart = initialCwd,
                CwdAtFinalize = finalCwd,
                CwdDriftNotice = cwdDriftNotice,
                Errors = errorList.Count > 0 ? errorList : null,
                Warnings = warningList.Count > 0 ? warningList : null,
                ElapsedMs = stopwatch.ElapsedMilliseconds,
            }, jsonContext.IndexUpdateJsonResult));
        }
        else
        {
            Console.WriteLine();
            Console.WriteLine();
            Console.WriteLine("Done.");
            Console.WriteLine();
            Console.WriteLine(ConsoleUi.FormatSummaryLine("Files", $"{totalFiles:N0} (total in DB)", indent: "  "));
            Console.WriteLine(ConsoleUi.FormatSummaryLine("Chunks", $"{totalChunks:N0}", indent: "  "));
            Console.WriteLine(ConsoleUi.FormatSummaryLine("Symbols", $"{totalSymbols:N0}", indent: "  "));
            Console.WriteLine(ConsoleUi.FormatSummaryLine("Refs", $"{totalReferences:N0}", indent: "  "));
            Console.WriteLine(ConsoleUi.FormatSummaryLine("Updated", $"{updated:N0}", indent: "  "));
            if (removed > 0) Console.WriteLine(ConsoleUi.FormatSummaryLine("Removed", $"{removed:N0}", indent: "  "));
            if (skipped > 0) Console.WriteLine(ConsoleUi.FormatSummaryLine("Skipped", $"{skipped:N0}", indent: "  "));
            if (warnings > 0) Console.WriteLine(ConsoleUi.FormatSummaryLine("Warnings", $"{warnings:N0}", indent: "  "));
            if (errors > 0) Console.WriteLine(ConsoleUi.FormatSummaryLine("Errors", $"{errors:N0}", indent: "  "));
            Console.WriteLine(ConsoleUi.FormatSummaryLine("Graph", graphTableAvailableAfter ? "ready" : "degraded", indent: "  "));
            Console.WriteLine(ConsoleUi.FormatSummaryLine("Issues", issuesTableAvailableAfter ? "ready" : "degraded", indent: "  "));
            Console.WriteLine(ConsoleUi.FormatSummaryLine("SQL graph", sqlGraphContractReadyAfter ? "ready" : "degraded", indent: "  "));
            Console.WriteLine(ConsoleUi.FormatSummaryLine("Hotspots", hotspotFamilyReadyAfter ? "ready" : "degraded", indent: "  "));
            Console.WriteLine(ConsoleUi.FormatSummaryLine("C# names", csharpSymbolNameReadyAfter ? "ready" : "degraded", indent: "  "));
            Console.WriteLine(ConsoleUi.FormatSummaryLine("C# meta", csharpMetadataTargetReadyAfter ? "ready" : "degraded", indent: "  "));
            Console.WriteLine(ConsoleUi.FormatSummaryLine("Fold", foldReadyAfter ? "ready" : "degraded", indent: "  "));
            Console.WriteLine(ConsoleUi.FormatSummaryLine("Elapsed", ConsoleUi.FormatDuration(stopwatch.Elapsed, options.DurationFormat), indent: "  "));
            Console.WriteLine();
            if (errors > 0)
                ConsoleUi.PrintWarning($"Some files failed to update. Fix the reported files or permissions, then rerun `cdidx index \"{projectRoot}\"` to restore a fully ready index.");
            if (!graphTableAvailableAfter || !issuesTableAvailableAfter || !sqlGraphContractReadyAfter || !hotspotFamilyReadyAfter || !csharpSymbolNameReadyAfter || !csharpMetadataTargetReadyAfter || !foldReadyAfter)
                ConsoleUi.PrintWarning(GetIndexReadinessWarning(graphTableAvailableAfter, issuesTableAvailableAfter, sqlGraphContractReadyAfter, hotspotFamilyReadyAfter, csharpSymbolNameReadyAfter, csharpMetadataTargetReadyAfter, foldReadyAfter, foldReadyReasonAfter, projectRoot, resolvedDbPath));
            if (cwdDriftDetected)
                ConsoleUi.PrintWarning(cwdDriftNotice!);
        }

        return CommandExitCodes.Success;
    }

    private static Dictionary<string, string?> GetHotspotFamilyMetaSnapshot(DbContext db, Func<string, string> keyFactory)
    {
        var values = new Dictionary<string, string?>(StringComparer.Ordinal);
        foreach (var lang in FileIndexer.GetHotspotFamilyMarkerLanguages())
            values[lang] = db.GetMetaString(keyFactory(lang));
        return values;
    }

    private static Dictionary<string, string?> GetHotspotFamilyMarkerFingerprints(FileIndexer indexer)
    {
        var values = new Dictionary<string, string?>(StringComparer.Ordinal);
        foreach (var lang in FileIndexer.GetHotspotFamilyMarkerLanguages())
            values[lang] = indexer.GetProjectMarkerFingerprint(lang);
        return values;
    }

    private static void RestampHotspotFamilyTrustForUpdate(
        DbWriter writer,
        IReadOnlyDictionary<string, string?> priorVersions,
        IReadOnlyDictionary<string, string?> priorFingerprints,
        IReadOnlyDictionary<string, string?> currentFingerprints)
    {
        var currentVersion = DbContext.HotspotFamilyVersion.ToString(System.Globalization.CultureInfo.InvariantCulture);
        foreach (var lang in FileIndexer.GetHotspotFamilyMarkerLanguages())
        {
            if (priorVersions.TryGetValue(lang, out var priorVersion)
                && priorFingerprints.TryGetValue(lang, out var priorFingerprint)
                && currentFingerprints.TryGetValue(lang, out var currentFingerprint)
                && priorVersion == currentVersion
                && priorFingerprint == currentFingerprint)
            {
                writer.MarkHotspotFamilyReady(lang, currentFingerprint);
            }
        }
    }

    private static void RestampHotspotFamilyTrustForFullScan(
        DbWriter writer,
        IReadOnlySet<string> reusedLanguages,
        IReadOnlyDictionary<string, string?> priorVersions,
        IReadOnlyDictionary<string, string?> priorFingerprints,
        IReadOnlyDictionary<string, string?> currentFingerprints)
    {
        var currentVersion = DbContext.HotspotFamilyVersion.ToString(System.Globalization.CultureInfo.InvariantCulture);
        foreach (var lang in FileIndexer.GetHotspotFamilyMarkerLanguages())
        {
            currentFingerprints.TryGetValue(lang, out var currentFingerprint);
            priorVersions.TryGetValue(lang, out var priorVersion);
            priorFingerprints.TryGetValue(lang, out var priorFingerprint);
            if (!reusedLanguages.Contains(lang) || (priorVersion == currentVersion && priorFingerprint == currentFingerprint))
                writer.MarkHotspotFamilyReady(lang, currentFingerprint);
        }
    }

    private static Dictionary<string, bool> GetHotspotFamilyTrustMatchesCurrent(
        IReadOnlyDictionary<string, string?> priorVersions,
        IReadOnlyDictionary<string, string?> priorFingerprints,
        IReadOnlyDictionary<string, string?> currentFingerprints)
    {
        var currentVersion = DbContext.HotspotFamilyVersion.ToString(System.Globalization.CultureInfo.InvariantCulture);
        var values = new Dictionary<string, bool>(StringComparer.Ordinal);
        foreach (var lang in FileIndexer.GetHotspotFamilyMarkerLanguages())
        {
            currentFingerprints.TryGetValue(lang, out var currentFingerprint);
            priorVersions.TryGetValue(lang, out var priorVersion);
            priorFingerprints.TryGetValue(lang, out var priorFingerprint);
            values[lang] = priorVersion == currentVersion && priorFingerprint == currentFingerprint;
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

    private static bool IsOutsideProjectRoot(string relativePath) =>
        relativePath == ".." || relativePath.StartsWith("../", StringComparison.Ordinal);

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
    private static void StampIndexedHeadMetadata(DbWriter writer, string projectRoot)
    {
        try
        {
            var headSha = GitHelper.TryGetHeadCommit(projectRoot);
            var headBranch = GitHelper.TryGetHeadBranch(projectRoot);
            var timestamp = headSha != null
                ? DateTime.UtcNow.ToString("o", System.Globalization.CultureInfo.InvariantCulture)
                : null;
            writer.SetMeta(DbContext.IndexedHeadShaMetaKey, headSha);
            writer.SetMeta(DbContext.IndexedHeadBranchMetaKey, headBranch);
            writer.SetMeta(DbContext.IndexedHeadTimestampMetaKey, timestamp);
        }
        catch
        {
            // Best-effort metadata only; never fail an otherwise-successful index run.
            // best-effort であり、stamp の失敗で index 全体を失敗扱いにしない。
        }
        StampWorkspacePathCaseSensitivity(writer, projectRoot);
    }

    // Issue #1546: capture the actual case-sensitivity of the workspace filesystem so
    // `cdidx status` can diagnose phantom path collapses on case-sensitive APFS / WSL
    // NTFS / ReFS volumes (where the OS-keyed heuristic would mismatch reality). Probed
    // via the same `core.ignorecase` + filesystem probe used by FileIndexer, then
    // persisted as "true" / "false" alongside the HEAD stamp. Failures are swallowed so
    // an unwritable git config / temp probe never blocks an otherwise-successful index.
    // #1546: workspace FS の大小区別を実プローブして codeindex_meta に保存する。
    // probe 失敗時は黙って null stamp にして index 本体は成功扱いのままとする。
    private static void StampWorkspacePathCaseSensitivity(DbWriter writer, string projectRoot)
    {
        try
        {
            var ignoreCase = GitHelper.ResolveIgnoreCase(projectRoot);
            PathCasing.SeedFromWorkspace(projectRoot, ignoreCase);
            var caseSensitive = (!ignoreCase).ToString(System.Globalization.CultureInfo.InvariantCulture);
            writer.SetMeta(DbContext.WorkspacePathCaseSensitiveMetaKey, caseSensitive);
        }
        catch
        {
            // Best-effort metadata only; never fail an otherwise-successful index run.
            // best-effort のみ。stamp 失敗で index 全体を落とさない。
        }
    }

    // Public stderr stays a single user-facing line; stack frames leak internal type names,
    // source paths, and line numbers to any stderr consumer (notably MCP clients that
    // capture child-process stderr for diagnostics), so we never append `ex.StackTrace`
    // here even under `--verbose`. CR/LF in `path` or `ex.Message` are collapsed to a
    // space so a multiline exception message cannot inject stack-like lines into the
    // captured stderr stream. Deeper diagnostics live in opt-in channels like
    // `cdidx report` or `CDIDX_DEBUG` (#1578).
    // 公開 stderr は 1 行のユーザー向けメッセージのみとし、`ex.StackTrace` は載せない。
    // 内部型名・ソースパス・行番号が stderr 取り込み側 (MCP クライアントなど) に漏れるため、
    // `--verbose` でも付加しない。`path` や `ex.Message` に含まれる CR/LF は空白へ畳み込み、
    // 複数行メッセージが疑似スタック行を注入できないようにする。詳細診断は
    // `cdidx report` / `CDIDX_DEBUG` で取得する (#1578)。
    internal static string FormatPerFileErrorLine(string label, string path, Exception ex) =>
        $"  [{label}] {CollapseLineBreaks(path)}: {CollapseLineBreaks(ex.Message)}";

    private static string CollapseLineBreaks(string value)
    {
        if (string.IsNullOrEmpty(value))
            return value;
        if (value.IndexOfAny(['\r', '\n']) < 0)
            return value;
        var buffer = new System.Text.StringBuilder(value.Length);
        foreach (var ch in value)
            buffer.Append(ch == '\r' || ch == '\n' ? ' ' : ch);
        return buffer.ToString();
    }

    private static int? RejectUnresolvedMergeState(string projectRoot, bool json, JsonSerializerOptions jsonOptions)
    {
        var status = GitHelper.TryGetWorktreeStatus(projectRoot);
        if (status == null || status.UnresolvedMergeFiles.Count == 0)
            return null;

        var paths = string.Join(", ", status.UnresolvedMergeFiles.Take(5));
        if (status.UnresolvedMergeFiles.Count > 5)
            paths += $", ... {status.UnresolvedMergeFiles.Count - 5:N0} more";

        return WriteCommandError(
            json,
            jsonOptions,
            $"unresolved merge conflicts detected; refusing to index conflicted files ({paths})",
            CommandExitCodes.UsageError,
            "Resolve the conflicts and run `git merge --continue`, or abort the merge with `git merge --abort`, then rerun `cdidx index`.",
            CommandErrorCodes.UsageError);
    }

    private static int WriteCommandError(bool json, JsonSerializerOptions jsonOptions, string message, int exitCode, string? hint = null, string? errorCode = null)
        => CommandErrorWriter.WriteJsonOrHuman(json, jsonOptions, message, exitCode, hint, errorCode: errorCode);

    private static int WriteInterruptedResult(bool json, JsonSerializerOptions jsonOptions, int filesProcessed, int? filesTotal)
    {
        var totalSuffix = filesTotal is > 0 ? $" of {filesTotal.Value:N0}" : string.Empty;
        return WriteCommandError(
            json,
            jsonOptions,
            $"Interrupted; partial progress saved ({filesProcessed:N0}{totalSuffix} files processed).",
            CommandExitCodes.Interrupted,
            "Rerun `cdidx index` to finish refreshing the index. Press Ctrl-C again during a future run to force-exit.",
            CommandErrorCodes.Interrupted);
    }

    internal static bool HandleIndexCancelKeyPress(CancellationTokenSource cancellation, ref bool firstCancelHandled)
    {
        if (!firstCancelHandled && !cancellation.IsCancellationRequested)
        {
            firstCancelHandled = true;
            cancellation.Cancel();
            return true;
        }

        return false;
    }

    private static IDisposable RegisterIndexCancelKeyPress(CancellationTokenSource cancellation)
    {
        var firstCancelHandled = false;
        ConsoleCancelEventHandler handler = (_, e) =>
        {
            e.Cancel = HandleIndexCancelKeyPress(cancellation, ref firstCancelHandled);
        };

        try
        {
            Console.CancelKeyPress += handler;
            return new CancelKeyPressRegistration(handler);
        }
        catch (PlatformNotSupportedException)
        {
            return NullDisposable.Instance;
        }
    }

    private static int WriteDatabaseFilesystemError(bool json, JsonSerializerOptions jsonOptions, string dbPath, Exception ex)
    {
        var transient = ex is SqliteException { SqliteErrorCode: 5 or 6 };
        return WriteCommandError(
            json,
            jsonOptions,
            $"database write failed for {dbPath}: {CollapseLineBreaks(ex.Message)}",
            transient ? CommandExitCodes.TransientDatabaseError : CommandExitCodes.DatabaseError,
            transient
                ? "Another process may be holding the database. Wait for it to finish, or retry with backoff."
                : "Check that the database file and parent directory exist and are writable, then retry `cdidx index`.",
            transient ? CommandErrorCodes.DbLocked : CommandErrorCodes.DbNotWritable);
    }

    private static bool IsDatabaseFilesystemError(Exception ex) =>
        ex is UnauthorizedAccessException
        || ex is IOException
        || ex is SqliteException { SqliteErrorCode: 5 or 6 or 8 or 10 or 14 };

    private static int RunFullScan(
        DbWriter writer,
        FileIndexer indexer,
        string projectRoot,
        string resolvedDbPath,
        IndexCommandOptions options,
        Stopwatch stopwatch,
        string[] spinnerFrames,
        JsonSerializerOptions jsonOptions,
        string? priorFoldVersion,
        string? priorFoldFingerprint,
        bool priorSymbolExtractorVersionsMatchCurrent,
        string? priorCSharpSymbolNameContractVersion,
        string? priorMetadataTargetCsharp,
        string? priorSqlGraphContractVersion,
        IReadOnlyDictionary<string, string?> priorHotspotFamilyVersions,
        IReadOnlyDictionary<string, string?> priorHotspotFamilyMarkerFingerprints,
        IReadOnlyDictionary<string, string?> currentHotspotFamilyMarkerFingerprints,
        string? priorIndexedProjectRoot,
        string? priorIndexedHeadCommit,
        string? currentHeadCommit,
        string? initialCwd,
        CancellationToken cancellationToken)
    {
        var jsonContext = CliJsonSerializerContextFactory.Create(jsonOptions);
        _ = priorMetadataTargetCsharp; // full-scan resolver runs unconditionally on success / 成功時に常に再解決するため不要
        var unresolvedMergeExitCode = RejectUnresolvedMergeState(projectRoot, options.Json, jsonOptions);
        if (unresolvedMergeExitCode != null)
            return unresolvedMergeExitCode.Value;

        var normalizedProjectRoot = Path.GetFullPath(projectRoot);
        var normalizedPriorIndexedProjectRoot = string.IsNullOrWhiteSpace(priorIndexedProjectRoot)
            ? null
            : Path.GetFullPath(priorIndexedProjectRoot);
        var projectRootWritten = PathsEqual(normalizedPriorIndexedProjectRoot, normalizedProjectRoot);
        var currentCSharpSymbolNameContractVersion = DbContext.CSharpSymbolNameContractVersion.ToString(System.Globalization.CultureInfo.InvariantCulture);
        var csharpSymbolNameContractMatchesCurrent = priorCSharpSymbolNameContractVersion == currentCSharpSymbolNameContractVersion;
        var currentSqlGraphContractVersion = DbContext.SqlGraphContractVersion.ToString(System.Globalization.CultureInfo.InvariantCulture);
        var sqlGraphContractMatchesCurrent = priorSqlGraphContractVersion == currentSqlGraphContractVersion;
        var hotspotFamilyTrustMatchesCurrent = GetHotspotFamilyTrustMatchesCurrent(
            priorHotspotFamilyVersions,
            priorHotspotFamilyMarkerFingerprints,
            currentHotspotFamilyMarkerFingerprints);

        // Detect HEAD divergence on the default incremental path (no `--rebuild`). `--rebuild`
        // already wipes the DB, so the prior captured HEAD is irrelevant there. We only signal
        // when both sides are known so legacy DBs / non-git workspaces never spuriously trigger.
        // Issue #1508.
        // 既定の incremental 経路で HEAD 差分を検出する。`--rebuild` は DB を消すので比較不要。
        // 双方の HEAD が分かるときのみ警告し、legacy DB / 非 git workspace では誤検知させない。
        var headChangeDetected = !options.Rebuild
            && !string.IsNullOrWhiteSpace(priorIndexedHeadCommit)
            && !string.IsNullOrWhiteSpace(currentHeadCommit)
            && !string.Equals(priorIndexedHeadCommit, currentHeadCommit, StringComparison.Ordinal);
        string? headChangeNotice = null;
        if (headChangeDetected)
        {
            headChangeNotice =
                $"Indexed HEAD changed since the last full scan (was {priorIndexedHeadCommit}, now {currentHeadCommit}). " +
                $"Incremental indexing only refreshes files it can scan in the current worktree, so rows for files that exist only on the previously indexed branch may remain. " +
                $"Run `cdidx index {QuoteCommandArgument(projectRoot)} --rebuild` to fully refresh the index.";
            if (!options.Json && !options.Quiet)
                ConsoleUi.PrintWarning(headChangeNotice);
        }

        void WriteProjectRootOnce()
        {
            if (!projectRootWritten)
            {
                writer.SetMeta(DbContext.IndexedProjectRootMetaKey, normalizedProjectRoot);
                projectRootWritten = true;
            }
        }

        void WriteJsonLiveness(string message)
        {
            if (!options.Json || options.Quiet)
                return;

            Console.Error.WriteLine($"cdidx: {message}");
        }

        (CancellationTokenSource Cts, Task Task)? StartJsonPhaseHeartbeat(string phase, Func<string?>? detailProvider = null)
        {
            if (!options.Json || options.Quiet)
                return null;

            var cts = new CancellationTokenSource();
            var token = cts.Token;
            var task = Task.Run(async () =>
            {
                while (!token.IsCancellationRequested)
                {
                    try
                    {
                        await Task.Delay(TimeSpan.FromSeconds(5), token).ConfigureAwait(false);
                    }
                    catch (OperationCanceledException)
                    {
                        break;
                    }

                    if (token.IsCancellationRequested)
                        break;

                    var detail = detailProvider?.Invoke();
                    var suffix = string.IsNullOrWhiteSpace(detail) ? string.Empty : $": {detail}";
                    Console.Error.WriteLine($"cdidx: still {phase}{suffix}...");
                }
            }, token);
            return (cts, task);
        }

        void StopJsonPhaseHeartbeat((CancellationTokenSource Cts, Task Task)? heartbeat)
        {
            if (heartbeat == null)
                return;

            heartbeat.Value.Cts.Cancel();
            try
            {
                heartbeat.Value.Task.Wait(TimeSpan.FromSeconds(1));
            }
            catch (AggregateException ex) when (ex.InnerExceptions.All(inner => inner is OperationCanceledException or TaskCanceledException))
            {
            }
            heartbeat.Value.Cts.Dispose();
        }

        CancellationTokenSource? spinnerCts = null;
        if (!options.Json && !options.Quiet)
            spinnerCts = ConsoleUi.StartSpinner("Scanning...", spinnerFrames);

        void ThrowIfFullScanCancelled(int filesProcessed, int? filesTotal)
        {
            if (!cancellationToken.IsCancellationRequested)
                return;

            ConsoleUi.StopSpinner(spinnerCts);
            throw new IndexInterruptedException(filesProcessed, filesTotal);
        }

        WriteJsonLiveness("scanning files...");
        var scanHeartbeat = StartJsonPhaseHeartbeat("scanning files");
        FileIndexer.ScanFilesResult scanResult;
        try
        {
            ThrowIfFullScanCancelled(0, null);
            scanResult = indexer.ScanFilesDetailed();
            ThrowIfFullScanCancelled(0, null);
        }
        finally
        {
            StopJsonPhaseHeartbeat(scanHeartbeat);
        }
        var files = scanResult.Files;
        ConsoleUi.StopSpinner(spinnerCts);
        WriteJsonLiveness($"found {ConsoleUi.Counted(files.Count, "file", format: "N0")}; preparing database...");
        var fatalScanErrors = scanResult.Errors
            .Where(error => error.IsFatal)
            .ToList();
        var warningScanErrors = scanResult.Errors
            .Where(error => !error.IsFatal)
            .ToList();
        var errorList = fatalScanErrors
            .Select(error => new CliJsonMessage(error.Path, error.Message))
            .ToList();
        var warningList = warningScanErrors
            .Select(error => new CliJsonMessage(error.Path, error.Message))
            .ToList();
        if (!options.Json && !options.Quiet)
        {
            Console.WriteLine($"  Found {ConsoleUi.Counted(files.Count, "file", format: "N0")}");
            foreach (var error in scanResult.Errors)
                ConsoleUi.PrintWarning($"{error.Path}: {error.Message}");
            Console.WriteLine();
        }

        // Full-scan commits to mutating the DB from here on. Demote readiness just before
        // the first write (PurgeStaleFiles). This is equivalent to clearing earlier in terms
        // of the --rebuild crash-window guard (the --rebuild path already cleared before
        // DropAll in RunIndex), but avoids downgrading a healthy DB if full-scan itself
        // never reaches this point for any reason.
        // 実書き込み直前で readiness をクリア。--rebuild 経路は RunIndex で既に clear 済み。
        ThrowIfFullScanCancelled(0, files.Count);
        writer.ClearReadyFlags();
        writer.ClearHotspotFamilyReady();
        writer.ClearMetadataTargetReady();

        CancellationTokenSource? purgeCts = null;
        if (!options.Json && !options.Quiet)
            purgeCts = ConsoleUi.StartSpinner("Cleaning up stale entries...", spinnerFrames);
        var purged = 0;
        var retainedPaths = files
            .Select(path => FileIndexer.NormalizePathSeparators(Path.GetRelativePath(projectRoot, path)))
            .ToHashSet(StringComparer.Ordinal);
        if (scanResult.HadErrors)
        {
            retainedPaths.UnionWith(scanResult.ProbeFailedFilePaths);

            foreach (var relPath in scanResult.NonIndexablePaths)
            {
                if (!writer.HasFileAtPath(relPath))
                    continue;

                if (writer.DeleteFileByPath(relPath))
                    purged++;
            }

            var authoritativeDirectories = scanResult.ListedDirectories
                .ToHashSet(StringComparer.Ordinal);
            var attributePrunedDirectories = scanResult.AttributePrunedDirectories
                .ToHashSet(StringComparer.Ordinal);
            purged += writer.PurgeFilesOutsideRetainedSetWithinListedDirectories(retainedPaths, authoritativeDirectories, attributePrunedDirectories);
        }
        else
        {
            purged = writer.PurgeFilesOutsideRetainedSet(retainedPaths);
        }
        if (purged > 0)
            WriteProjectRootOnce();
        ConsoleUi.StopSpinner(purgeCts);
        WriteJsonLiveness(purged > 0
            ? $"purged {purged:N0} stale file(s); preparing index writes..."
            : "preparing index writes...");
        if (!options.Json && !options.Quiet)
        {
            if (purged > 0)
            {
                var purgeMessage = scanResult.HadErrors
                    ? $"  Purged {purged:N0} previously indexed files that were positively observed as no longer indexable or missing from directories whose file listing completed successfully"
                    : $"  Purged {purged:N0} stale files (missing or no longer indexable)";
                Console.WriteLine(purgeMessage);
            }
            if (scanResult.HadErrors)
                ConsoleUi.PrintWarning("Skipped authoritative purge outside directories whose file listing completed successfully because some paths could not be scanned.");
        }

        // Purge references for languages no longer graph-supported / グラフ非対応になった言語の参照をパージ
        ThrowIfFullScanCancelled(0, files.Count);
        var purgedRefs = writer.PurgeUnsupportedReferences(ReferenceExtractor.GetSupportedLanguages());
        if (purgedRefs > 0 && !options.Json && !options.Quiet)
            Console.WriteLine($"  Purged {purgedRefs:N0} stale references (unsupported language)");

        CancellationTokenSource? indexCts = null;
        int processed = 0, skipped = 0, warnings = warningList.Count, errors = errorList.Count;

          var interactiveIndexSpinner = !options.Json && !options.Quiet && ConsoleUi.ShouldUseInteractiveConsole();
        var redirectedIndexingMessagePrinted = false;
        var indexProgressVisible = false;
        var reusedHotspotFamilyLanguages = new HashSet<string>(StringComparer.Ordinal);
        var skippedSymbolExtractorLanguages = new HashSet<string>(StringComparer.Ordinal);
        var lastJsonProgressAt = Stopwatch.GetTimestamp();
        string? currentJsonIndexFile = null;
        CancellationTokenSource? jsonHeartbeatCts = null;
        Task? jsonHeartbeatTask = null;
        var extractionParallelism = Math.Max(1, options.Parallelism);
        var parallelizeExtraction = options.Rebuild || writer.GetCounts().files == 0;

        void StartIndexSpinnerIfNeeded()
        {
            if (!interactiveIndexSpinner || indexCts != null)
                return;

            indexCts = ConsoleUi.StartSpinner("Indexing...", spinnerFrames);
        }

        void PauseIndexSpinnerForConsoleWrite()
        {
            if (indexCts == null)
                return;

            ConsoleUi.StopSpinner(indexCts);
            indexCts = null;
        }

        void ResumeIndexSpinnerAfterConsoleWrite()
        {
            if (!interactiveIndexSpinner || processed >= files.Count || indexProgressVisible)
                return;

            StartIndexSpinnerIfNeeded();
        }

        void EnsureIndexingActivityVisible()
        {
            if (options.Json || options.Quiet)
                return;

            if (indexProgressVisible)
                return;

            if (interactiveIndexSpinner)
            {
                StartIndexSpinnerIfNeeded();
                return;
            }

            if (redirectedIndexingMessagePrinted)
                return;

            Console.WriteLine("Indexing...");
            redirectedIndexingMessagePrinted = true;
        }

        void ReportJsonIndexProgressIfNeeded()
        {
            if (!options.Json || options.Quiet || files.Count == 0)
                return;

            var now = Stopwatch.GetTimestamp();
            if (processed == 0
                || processed == files.Count
                || processed % 100 == 0
                || Stopwatch.GetElapsedTime(lastJsonProgressAt, now) >= TimeSpan.FromSeconds(5))
            {
                Console.Error.WriteLine($"cdidx: indexed {processed:N0}/{files.Count:N0} file(s)...");
                lastJsonProgressAt = now;
            }
        }

        void StartJsonHeartbeatIfNeeded()
        {
            if (!options.Json || options.Quiet || files.Count == 0 || jsonHeartbeatCts != null)
                return;

            jsonHeartbeatCts = new CancellationTokenSource();
            var token = jsonHeartbeatCts.Token;
            jsonHeartbeatTask = Task.Run(async () =>
            {
                while (!token.IsCancellationRequested)
                {
                    try
                    {
                        await Task.Delay(TimeSpan.FromSeconds(5), token).ConfigureAwait(false);
                    }
                    catch (OperationCanceledException)
                    {
                        break;
                    }

                    if (token.IsCancellationRequested)
                        break;

                    var file = currentJsonIndexFile;
                    var fileSuffix = string.IsNullOrEmpty(file) ? string.Empty : $": {file}";
                    Console.Error.WriteLine($"cdidx: still indexing {processed:N0}/{files.Count:N0} file(s){fileSuffix}...");
                }
            }, token);
        }

        void StopJsonHeartbeat()
        {
            if (jsonHeartbeatCts == null)
                return;

            jsonHeartbeatCts.Cancel();
            try
            {
                jsonHeartbeatTask?.Wait(TimeSpan.FromSeconds(1));
            }
            catch (AggregateException ex) when (ex.InnerExceptions.All(inner => inner is OperationCanceledException or TaskCanceledException))
            {
            }
            jsonHeartbeatCts.Dispose();
            jsonHeartbeatCts = null;
            jsonHeartbeatTask = null;
        }

        WriteJsonLiveness("preparing C# workspace symbols...");
        string? currentCSharpWorkspaceFile = null;
        var csharpWorkspaceHeartbeat = StartJsonPhaseHeartbeat(
            "preparing C# workspace symbols",
            () => currentCSharpWorkspaceFile);
        CSharpStaticInterfaceWorkspaceSymbols csharpWorkspace;
        try
        {
            csharpWorkspace = BuildCSharpStaticInterfaceWorkspaceSymbols(
                writer,
                indexer,
                projectRoot,
                files,
                path => currentCSharpWorkspaceFile = path,
                cancellationToken);
        }
        catch (OperationCanceledException) when (cancellationToken.IsCancellationRequested)
        {
            throw new IndexInterruptedException(0, files.Count);
        }
        finally
        {
            currentCSharpWorkspaceFile = null;
            StopJsonPhaseHeartbeat(csharpWorkspaceHeartbeat);
        }

        EnsureIndexingActivityVisible();
        ReportJsonIndexProgressIfNeeded();
        StartJsonHeartbeatIfNeeded();

        try
        {
            if (!options.Json && !options.Quiet)
            {
                PauseIndexSpinnerForConsoleWrite();
                indexProgressVisible = true;
                ConsoleUi.PrintProgress(0, files.Count);
            }

            using var extractionResults = new BlockingCollection<FullScanFileWorkItem>(Math.Max(1, extractionParallelism * 4));
            var nextFileIndex = -1;
            var workers = Enumerable.Range(0, extractionParallelism)
                .Select(_ => Task.Factory.StartNew(() =>
                {
                    while (true)
                    {
                        cancellationToken.ThrowIfCancellationRequested();
                        var fileIndex = Interlocked.Increment(ref nextFileIndex);
                        if (fileIndex >= files.Count)
                            break;

                        var filePath = files[fileIndex];
                        try
                        {
                            var (record, content, rawBytes, warning) = indexer.BuildRecordWithRawBytes(filePath);
                            IReadOnlyList<ChunkRecord>? chunks = null;
                            IReadOnlyList<SymbolRecord>? symbols = null;
                            IReadOnlyList<ReferenceRecord>? references = null;
                            IReadOnlyList<FileIssue>? issues = null;
                            if (parallelizeExtraction)
                            {
                                chunks = ChunkSplitter.Split(0, content);
                                symbols = SymbolExtractor.Extract(0, record.Lang, content, filePath, Path.GetFullPath(options.ProjectPath!));
                                SymbolExtractor.ApplyFamilyScope(symbols, indexer.GetFamilyScopeKey(filePath, record.Lang));
                                references = ReferenceExtractor.Extract(
                                    0,
                                    record.Lang,
                                    content,
                                    symbols,
                                    record.Path,
                                    record.Lang == "csharp" ? csharpWorkspace.Symbols : null);
                                issues = FileIndexer.ValidateContent(record.Path, rawBytes, content);
                            }
                            extractionResults.Add(
                                FullScanFileWorkItem.Success(filePath, record, content, rawBytes, warning, chunks, symbols, references, issues),
                                cancellationToken);
                        }
                        catch (OperationCanceledException) when (cancellationToken.IsCancellationRequested)
                        {
                            throw;
                        }
                        catch (Exception ex)
                        {
                            extractionResults.Add(FullScanFileWorkItem.Failure(filePath, ex), cancellationToken);
                        }
                    }
                }, cancellationToken, TaskCreationOptions.LongRunning, TaskScheduler.Default))
                .ToArray();

            _ = Task.WhenAll(workers).ContinueWith(
                task =>
                {
                    extractionResults.CompleteAdding();
                },
                CancellationToken.None,
                TaskContinuationOptions.ExecuteSynchronously,
                TaskScheduler.Default);

            while (!extractionResults.IsCompleted)
            {
                ThrowIfFullScanCancelled(processed, files.Count);
                if (!extractionResults.TryTake(out var item, millisecondsTimeout: 100))
                    continue;

                currentJsonIndexFile = FileIndexer.NormalizePathSeparators(Path.GetRelativePath(projectRoot, item.FilePath));
                EnsureIndexingActivityVisible();
                try
                {
                    if (item.Exception != null)
                        throw item.Exception;

                    var record = item.Record!;
                    if (item.Warning != null && !options.Json && !options.Quiet)
                    {
                        PauseIndexSpinnerForConsoleWrite();
                        ConsoleUi.PrintWarning(item.Warning);
                        ResumeIndexSpinnerAfterConsoleWrite();
                    }

                    long? existingId = null;
                    if (!options.Rebuild)
                    {
                        existingId = writer.GetUnchangedFileId(
                            record.Path,
                            record.Modified,
                            record.Checksum,
                            language: record.Lang,
                            allowReuse: record.Lang is not ("javascript" or "typescript")
                        && (record.Lang != "csharp" || csharpSymbolNameContractMatchesCurrent)
                                && (record.Lang != "csharp" || !csharpWorkspace.HasStaticInterfaceContracts)
                                && (record.Lang != "sql" || sqlGraphContractMatchesCurrent)
                                && AllowReuseWithCurrentHotspotFamilyTrust(record.Lang, hotspotFamilyTrustMatchesCurrent));
                    }
                    if (existingId != null)
                    {
                        writer.PurgeStaleFilesSharingChecksum(projectRoot, record.Path, record.Checksum);
                        skipped++;
                        processed++;
                        if (!string.IsNullOrWhiteSpace(record.Lang))
                            skippedSymbolExtractorLanguages.Add(record.Lang);
                        if (FileIndexer.SupportsHotspotFamilyMarkerLanguage(record.Lang) && record.Lang != null)
                            reusedHotspotFamilyLanguages.Add(record.Lang);
                        if (options.Verbose && !options.Json && !options.Quiet)
                        {
                            PauseIndexSpinnerForConsoleWrite();
                            ConsoleUi.ClearProgressLine();
                            Console.WriteLine($"  [SKIP] {record.Path}");
                            ResumeIndexSpinnerAfterConsoleWrite();
                        }
                        if (!options.Json && !options.Quiet)
                        {
                            PauseIndexSpinnerForConsoleWrite();
                            ConsoleUi.PrintProgress(processed, files.Count);
                            ResumeIndexSpinnerAfterConsoleWrite();
                        }
                        ReportJsonIndexProgressIfNeeded();
                        currentJsonIndexFile = null;
                        continue;
                    }

                    using var txn = writer.BeginTransaction();
                    writer.PurgeStaleFilesSharingChecksum(projectRoot, record.Path, record.Checksum);
                    var fileId = writer.UpsertFile(record);
                    var chunks = item.Chunks == null
                        ? ChunkSplitter.Split(fileId, item.Content!)
                        : ReassignChunkFileIds(item.Chunks, fileId);
                    writer.InsertChunks(chunks);
                    var symbols = item.Symbols == null
                        ? SymbolExtractor.Extract(fileId, record.Lang, item.Content!, item.FilePath, Path.GetFullPath(options.ProjectPath!))
                        : ReassignSymbolFileIds(item.Symbols, fileId);
                    if (item.Symbols == null)
                        SymbolExtractor.ApplyFamilyScope(symbols, indexer.GetFamilyScopeKey(item.FilePath, record.Lang));
                    writer.InsertSymbols(symbols);
                    var references = item.References == null
                        ? ReferenceExtractor.Extract(
                            fileId,
                            record.Lang,
                            item.Content!,
                            symbols,
                            record.Path,
                            record.Lang == "csharp" ? csharpWorkspace.Symbols : null)
                        : ReassignReferenceFileIds(item.References, fileId);
                    writer.InsertReferences(references);
                    var issues = item.Issues ?? FileIndexer.ValidateContent(record.Path, item.RawBytes!, item.Content!);
                    writer.InsertIssues(fileId, issues);
                    WriteProjectRootOnce();
                    txn.Commit();

                    if (options.Verbose && !options.Json && !options.Quiet)
                    {
                        PauseIndexSpinnerForConsoleWrite();
                        ConsoleUi.ClearProgressLine();
                        Console.WriteLine($"  [OK  ] {record.Path} ({chunks.Count} chunks, {symbols.Count} symbols, {references.Count} refs)");
                        ResumeIndexSpinnerAfterConsoleWrite();
                    }
                }
                catch (Exception ex)
                {
                    errors++;
                    errorList.Add(new CliJsonMessage(item.FilePath, ex.Message));
                    if (!options.Json)
                    {
                        PauseIndexSpinnerForConsoleWrite();
                        ConsoleUi.ClearProgressLine();
                        Console.Error.WriteLine(FormatPerFileErrorLine("ERR ", item.FilePath, ex));
                        ResumeIndexSpinnerAfterConsoleWrite();
                    }
                }

                processed++;
                currentJsonIndexFile = null;
                ThrowIfFullScanCancelled(processed, files.Count);
                ReportJsonIndexProgressIfNeeded();
                if (!options.Json && !options.Quiet)
                {
                    PauseIndexSpinnerForConsoleWrite();
                    ConsoleUi.PrintProgress(processed, files.Count);
                    ResumeIndexSpinnerAfterConsoleWrite();
                }
            }
            Task.WaitAll(workers, cancellationToken);
        }
        finally
        {
            currentJsonIndexFile = null;
            StopJsonHeartbeat();
        }

        PauseIndexSpinnerForConsoleWrite();

        ThrowIfFullScanCancelled(processed, files.Count);
        WriteJsonLiveness("optimizing index...");
        var optimizeHeartbeat = StartJsonPhaseHeartbeat("optimizing index");
        try
        {
            writer.OptimizeFts();
        }
        finally
        {
            StopJsonPhaseHeartbeat(optimizeHeartbeat);
        }
        ThrowIfFullScanCancelled(processed, files.Count);
        // Only stamp readiness on a fully successful run (errors == 0). A partial / error
        // run leaves the DB unstamped so readers correctly treat graph / issues data as
        // degraded rather than authoritative. Interrupted runs also stay unstamped because
        // ClearReadyFlags() ran at the start.
        // errors==0 の成功 run のみマーカーを打つ。途中失敗は未 stamp のままで縮退扱い。
        var graphTableAvailableAfter = false;
        var issuesTableAvailableAfter = false;
        var csharpSymbolNameReadyAfter = !writer.HasAnyFilesWithLanguage("csharp");
        var csharpMetadataTargetReadyAfter = !writer.HasAnyFilesWithLanguage("csharp");
        var foldReadyAfter = false;
        string? foldReadyReasonAfter = null;
        if (errors == 0)
        {
            // Full-scan covers the whole repo, so it may always stamp Graph / Issues on
            // success regardless of what the DB carried before. Fold still gates on the
            // backfill verification below because incremental-by-default full scans skip
            // unchanged legacy files whose folded columns remain NULL.
            // full-scan は全repo をカバーするため、Graph / Issues は常に stamp。Fold のみ条件付き。
            writer.MarkGraphReady();
            writer.MarkIssuesReady();
            writer.MarkSqlGraphContractReady();
            writer.MarkCSharpSymbolNameContractReady();
            // Issue #435: resolve every C# class-like row and stamp readiness. Full-scan
            // touches the entire repo, so the resolver output is authoritative regardless
            // of which individual files were reparsed.
            // Issue #435: full-scan は全リポジトリを touch するため resolver の出力は
            // 全行 authoritative。必ず再解決して stamp する。
            if (writer.HasAnyFilesWithLanguage("csharp"))
            {
                writer.ResolveCSharpMetadataTargets();
                writer.MarkMetadataTargetReady("csharp");
                csharpMetadataTargetReadyAfter = true;
            }
            else
            {
                csharpMetadataTargetReadyAfter = true;
            }
            graphTableAvailableAfter = true;
            issuesTableAvailableAfter = true;
            csharpSymbolNameReadyAfter = true;
            writer.RebuildTypeScriptAugmentationReferences(projectRoot);
            RestampHotspotFamilyTrustForFullScan(
                writer,
                reusedHotspotFamilyLanguages,
                priorHotspotFamilyVersions,
                priorHotspotFamilyMarkerFingerprints,
                currentHotspotFamilyMarkerFingerprints);
            // FoldReady must reflect reality (#86). Full-scan is INCREMENTAL by default — it
            // skips unchanged files via GetUnchangedFileId, so a legacy DB's pre-#86 rows
            // keep NULL name_folded / *_folded values. Stamping FoldReady anyway would flip
            // readers onto the folded-equality path and silently miss those rows. Verify
            // every existing row has its folded column populated before stamping, and tell
            // the user how to upgrade when not (only --rebuild / a truly-fresh index can
            // guarantee 100% backfill on a legacy DB).
            // fold は実検証が通ったときだけ stamp。legacy DB で skip された行は NULL のため、
            // 黙って stamp すると reader が fold 経路で legacy 行を見逃す。codex #86 レビュー。
            var backfillReady = skipped == 0
                ? writer.AllFoldedColumnsBackfilled()
                : writer.AllFoldedColumnsBackfilled(skippedSymbolExtractorLanguages);
            var foldedKeysCurrent = skipped == 0 || writer.AllFoldedColumnValuesMatchCurrentFold();
            var currentFoldVersion = NameFold.Version.ToString(System.Globalization.CultureInfo.InvariantCulture);
            var currentFoldFingerprint = NameFold.Fingerprint();
            var foldVersionMatchesCurrent = priorFoldVersion == currentFoldVersion;
            var foldFingerprintMatchesCurrent = priorFoldFingerprint == currentFoldFingerprint;
            var canRestampExistingFoldTrust = foldVersionMatchesCurrent
                && foldFingerprintMatchesCurrent
                && writer.SymbolExtractorVersionsMatchCurrent(skippedSymbolExtractorLanguages);
            // A normal `index .` run still skips unchanged files. If the prior fold metadata
            // is stale, those skipped rows keep the old physical folded keys, so stamping the
            // NEW metadata for the whole DB would silently misadvertise trust. Only stamp when
            // every row was regenerated this run (skipped==0) or when the carried metadata is
            // already known-good for the current runtime, even if user_version was cleared by
            // an interrupted refresh before MarkFoldReady ran. Issue #97 codex review.
            // 通常の `index .` は unchanged 行を skip するため、事前 metadata が stale なら
            // skipped 行は旧 key のまま残る。全件再生成済み（skipped==0）か、事前 metadata が
            // current と一致しているときだけ FoldReady を stamp する。途中中断で
            // user_version だけ落ちた current DB もここで回復させる。
            if (backfillReady && foldedKeysCurrent && (skipped == 0 || canRestampExistingFoldTrust))
            {
                // MarkFoldReady re-verifies inside BEGIN IMMEDIATE; if a concurrent writer slipped
                // in a NULL-folded row between the upfront check and this stamp, the stamp is
                // skipped and we degrade to the legacy reason instead of silent misadvertisement.
                // Issue #1535.
                // BEGIN IMMEDIATE 内で再検証するため、concurrent writer による NULL 差し込みで
                // stamp は失敗し、silent な fold-trust 誤広告ではなく legacy 理由に降格する。Issue #1535。
                foldReadyAfter = writer.MarkFoldReady(stampCurrentSymbolExtractorVersions: skipped == 0);
                if (!foldReadyAfter)
                {
                    backfillReady = false;
                    foldReadyReasonAfter = GetFoldReadyReason(false, foldVersionMatchesCurrent, foldFingerprintMatchesCurrent);
                }
            }
            else
                foldReadyReasonAfter = GetFoldReadyReason(backfillReady, foldVersionMatchesCurrent, foldFingerprintMatchesCurrent);

            writer.WriteCdidxWriterVersion(ConsoleUi.LoadVersion());

            // Successful no-op full scans should repair stale / missing explicit-DB roots
            // only after readiness stamps succeed, so an interruption cannot rewrite trust
            // metadata ahead of the success markers.
            // no-op full-scan の explicit DB root backfill は readiness stamp 後に限定する。
            WriteProjectRootOnce();
            writer.SetMeta(
                DbContext.UnknownExtensionFileCountMetaKey,
                scanResult.UnknownExtensionFiles.Count.ToString(System.Globalization.CultureInfo.InvariantCulture));
            // Persist the current HEAD only after the run is fully successful (errors == 0).
            // We deliberately only stamp on full scans (rebuild or default incremental). Update
            // mode (`--commits` / `--files`) leaves the captured HEAD untouched so the next
            // default scan can still detect "branch moved since the last full scan." A
            // best-effort `null` from a non-git workspace simply clears the field. Issue #1508.
            // フル成功時のみ HEAD を記録する。partial update は HEAD を触らないので、後続の
            // full scan が「直近 full scan からブランチが動いた」をきちんと検知できる。
            // 非 git workspace で null になった場合はキーごとクリアされる。Issue #1508。
            writer.SetMeta(DbContext.IndexedHeadCommitMetaKey, currentHeadCommit);
            writer.SetMeta(DbContext.IndexedHeadCommitBranchMetaKey, GitHelper.TryGetHeadBranch(projectRoot));
            // #1509: also stamp the always-updated "last indexed HEAD" triple (SHA + branch +
            // timestamp). Unlike #1508's IndexedHeadCommitMetaKey which only fires here on
            // full scans, this triple is also stamped at the end of incremental update runs
            // (see RunUpdateMode) so cross-session `commits_ahead_of_indexed_head` always
            // reflects the true HEAD at the time of the most recent successful index.
            // #1509: あらゆる成功 index の終端で更新する HEAD トリプル (SHA + branch + 時刻) も
            // ここで stamp する。full scan / partial update を問わず最新の HEAD を保存する。
            StampIndexedHeadMetadata(writer, projectRoot);
        }
        stopwatch.Stop();
        // Detect cwd drift between option-parsing and finalize. See RunUpdateMode for the
        // rationale; the warning is informational because we already absolutized paths.
        // Issue #1577.
        var finalCwd = TryCaptureCurrentDirectory();
        var cwdDriftNotice = BuildCwdDriftNotice(initialCwd, finalCwd);
        var cwdDriftDetected = cwdDriftNotice != null;
        if (cwdDriftDetected)
        {
            warningList.Add(new CliJsonMessage("<process_cwd>", cwdDriftNotice!));
            warnings++;
        }
        var (totalFiles, totalChunks, totalSymbols, totalReferences) = writer.GetCounts();
        var signalReader = new DbReader(writer.Connection);
        var sqlGraphContractSignalAfter = signalReader.GetSqlGraphContractSignal(lang: null);
        var hotspotFamilySignalAfter = signalReader.GetHotspotFamilySignal(lang: null);
        var sqlGraphContractReadyAfter = sqlGraphContractSignalAfter.Ready;
        var sqlGraphContractDegradedReasonAfter = sqlGraphContractSignalAfter.DegradedReason;
        var hotspotFamilyReadyAfter = hotspotFamilySignalAfter.Ready;
        var hotspotFamilyDegradedReasonAfter = hotspotFamilySignalAfter.DegradedReason;

        var foldOnlyRemediation = BuildFoldOnlyReadinessRemediation(
            graphTableAvailableAfter,
            issuesTableAvailableAfter,
            sqlGraphContractReadyAfter,
            hotspotFamilyReadyAfter,
            csharpSymbolNameReadyAfter,
            csharpMetadataTargetReadyAfter,
            foldReadyAfter,
            foldReadyReasonAfter,
            projectRoot,
            resolvedDbPath);

        if (options.Json)
        {
            Console.WriteLine(JsonSerializer.Serialize(new IndexFullScanJsonResult
            {
                Status = errors > 0 ? "partial" : "success",
                Mode = options.Rebuild ? "rebuild" : "incremental",
                Summary = new IndexFullScanSummaryJsonResult
                {
                    FilesTotal = totalFiles,
                    ChunksTotal = totalChunks,
                    SymbolsTotal = totalSymbols,
                    ReferencesTotal = totalReferences,
                    FilesScanned = files.Count,
                    FilesSkipped = skipped,
                    FilesPurged = purged,
                    Warnings = warnings,
                    Errors = errors,
                },
                GraphTableAvailable = graphTableAvailableAfter,
                IssuesTableAvailable = issuesTableAvailableAfter,
                SqlGraphContractReady = sqlGraphContractReadyAfter,
                SqlGraphContractDegradedReason = sqlGraphContractDegradedReasonAfter,
                HotspotFamilyReady = hotspotFamilyReadyAfter,
                HotspotFamilyDegradedReason = hotspotFamilyDegradedReasonAfter,
                CSharpSymbolNameReady = csharpSymbolNameReadyAfter,
                CSharpMetadataTargetReady = csharpMetadataTargetReadyAfter,
                // #86 codex review: expose fold-readiness so AI clients can decide whether
                // `--exact` will use the Unicode fold path or fall back to ASCII NOCASE.
                // #86 codex: AI クライアントが --exact の経路を判断できるよう fold_ready を返す。
                FoldReady = foldReadyAfter,
                FoldReadyReason = foldReadyAfter ? null : foldReadyReasonAfter,
                DegradedReason = foldOnlyRemediation?.DegradedReason,
                RecommendedAction = foldOnlyRemediation?.RecommendedAction,
                AlternativeAction = foldOnlyRemediation?.AlternativeAction,
                HeadChanged = headChangeDetected,
                PriorIndexedHeadCommit = priorIndexedHeadCommit,
                CurrentHeadCommit = currentHeadCommit,
                HeadChangeNotice = headChangeNotice,
                CwdDriftDetected = cwdDriftDetected,
                CwdAtStart = initialCwd,
                CwdAtFinalize = finalCwd,
                CwdDriftNotice = cwdDriftNotice,
                Errors = errorList.Count > 0 ? errorList : null,
                Warnings = warningList.Count > 0 ? warningList : null,
                ElapsedMs = stopwatch.ElapsedMilliseconds,
            }, jsonContext.IndexFullScanJsonResult));
        }
        else if (!options.Quiet)
        {
            Console.WriteLine();
            Console.WriteLine();
            Console.WriteLine("Done.");
            Console.WriteLine();
            Console.WriteLine(ConsoleUi.FormatSummaryLine("Files", $"{totalFiles:N0}", indent: "  "));
            Console.WriteLine(ConsoleUi.FormatSummaryLine("Chunks", $"{totalChunks:N0}", indent: "  "));
            Console.WriteLine(ConsoleUi.FormatSummaryLine("Symbols", $"{totalSymbols:N0}", indent: "  "));
            Console.WriteLine(ConsoleUi.FormatSummaryLine("Refs", $"{totalReferences:N0}", indent: "  "));
            if (skipped > 0) Console.WriteLine(ConsoleUi.FormatSummaryLine("Skipped", $"{skipped:N0} (unchanged)", indent: "  "));
            if (options.Verbose && scanResult.UnknownExtensionFiles.Count > 0)
            {
                Console.WriteLine($"  Unknown extension files: {scanResult.UnknownExtensionFiles.Count:N0}");
                foreach (var relPath in scanResult.UnknownExtensionFiles.Take(5))
                    Console.WriteLine($"    {relPath}");
                if (scanResult.UnknownExtensionFiles.Count > 5)
                    Console.WriteLine($"    ... {scanResult.UnknownExtensionFiles.Count - 5:N0} more");
            }
            if (warnings > 0) Console.WriteLine(ConsoleUi.FormatSummaryLine("Warnings", $"{warnings:N0}", indent: "  "));
            if (errors > 0) Console.WriteLine(ConsoleUi.FormatSummaryLine("Errors", $"{errors:N0}", indent: "  "));
            Console.WriteLine(ConsoleUi.FormatSummaryLine("Graph", graphTableAvailableAfter ? "ready" : "degraded", indent: "  "));
            Console.WriteLine(ConsoleUi.FormatSummaryLine("Issues", issuesTableAvailableAfter ? "ready" : "degraded", indent: "  "));
            Console.WriteLine(ConsoleUi.FormatSummaryLine("SQL graph", sqlGraphContractReadyAfter ? "ready" : "degraded", indent: "  "));
            Console.WriteLine(ConsoleUi.FormatSummaryLine("Hotspots", hotspotFamilyReadyAfter ? "ready" : "degraded", indent: "  "));
            Console.WriteLine(ConsoleUi.FormatSummaryLine("C# names", csharpSymbolNameReadyAfter ? "ready" : "degraded", indent: "  "));
            Console.WriteLine(ConsoleUi.FormatSummaryLine("C# meta", csharpMetadataTargetReadyAfter ? "ready" : "degraded", indent: "  "));
            Console.WriteLine(ConsoleUi.FormatSummaryLine("Fold", foldReadyAfter ? "ready" : "degraded", indent: "  "));
            Console.WriteLine(ConsoleUi.FormatSummaryLine("Elapsed", ConsoleUi.FormatDuration(stopwatch.Elapsed, options.DurationFormat), indent: "  "));
            Console.WriteLine();
            if (errors > 0)
                ConsoleUi.PrintWarning($"Some files failed to index. Fix the reported files or permissions, then rerun `cdidx index \"{projectRoot}\"` to restore a fully ready index.");
            if (!graphTableAvailableAfter || !issuesTableAvailableAfter || !sqlGraphContractReadyAfter || !hotspotFamilyReadyAfter || !csharpSymbolNameReadyAfter || !csharpMetadataTargetReadyAfter || !foldReadyAfter)
                ConsoleUi.PrintWarning(GetIndexReadinessWarning(graphTableAvailableAfter, issuesTableAvailableAfter, sqlGraphContractReadyAfter, hotspotFamilyReadyAfter, csharpSymbolNameReadyAfter, csharpMetadataTargetReadyAfter, foldReadyAfter, foldReadyReasonAfter, projectRoot, resolvedDbPath));
            if (cwdDriftDetected)
                ConsoleUi.PrintWarning(cwdDriftNotice!);
        }

        return CommandExitCodes.Success;
    }

    private static bool PathsEqual(string? left, string? right)
    {
        if (left == null || right == null)
            return false;

        var comparison = OperatingSystem.IsWindows()
            ? StringComparison.OrdinalIgnoreCase
            : StringComparison.Ordinal;
        return string.Equals(left, right, comparison);
    }

    private static string GetFoldReadyReason(bool backfillReady, bool foldVersionMatchesCurrent, bool foldFingerprintMatchesCurrent)
    {
        if (!backfillReady)
            return DegradationReasonCodes.MissingFoldBackfill;

        if (!foldVersionMatchesCurrent)
            return DegradationReasonCodes.StaleFoldKeyVersion;

        if (!foldFingerprintMatchesCurrent)
            return DegradationReasonCodes.StaleFoldKeyFingerprint;

        return DegradationReasonCodes.FoldRowsNotRestamped;
    }

    private static string BuildFoldNotReadyExplanation(string? foldReadyReason)
        => DegradationReasonCodes.BuildFoldNotReadyExplanation(foldReadyReason);

    private static string BuildFoldBackfillCommand(string resolvedDbPath)
        => $"cdidx backfill-fold --db {QuoteCommandArgument(resolvedDbPath)}";

    private static string BuildFoldRebuildCommand(string projectRoot, string resolvedDbPath)
        => $"cdidx index {QuoteCommandArgument(projectRoot)} --db {QuoteCommandArgument(resolvedDbPath)} --rebuild";

    private static IReadOnlyList<ChunkRecord> ReassignChunkFileIds(IReadOnlyList<ChunkRecord> chunks, long fileId)
    {
        foreach (var chunk in chunks)
            chunk.FileId = fileId;
        return chunks;
    }

    private static IReadOnlyList<SymbolRecord> ReassignSymbolFileIds(IReadOnlyList<SymbolRecord> symbols, long fileId)
    {
        foreach (var symbol in symbols)
            symbol.FileId = fileId;
        return symbols;
    }

    private static IReadOnlyList<ReferenceRecord> ReassignReferenceFileIds(IReadOnlyList<ReferenceRecord> references, long fileId)
    {
        foreach (var reference in references)
            reference.FileId = fileId;
        return references;
    }

    private static FoldOnlyRemediation? BuildFoldOnlyReadinessRemediation(
        bool graphTableAvailable,
        bool issuesTableAvailable,
        bool sqlGraphContractReady,
        bool hotspotFamilyReady,
        bool csharpSymbolNameReady,
        bool csharpMetadataTargetReady,
        bool foldReady,
        string? foldReadyReason,
        string projectRoot,
        string resolvedDbPath)
    {
        if (!IsFoldOnlyReadinessDegraded(
                graphTableAvailable,
                issuesTableAvailable,
                sqlGraphContractReady,
                hotspotFamilyReady,
                csharpSymbolNameReady,
                csharpMetadataTargetReady,
                foldReady))
        {
            return null;
        }

        return new FoldOnlyRemediation(
            BuildFoldNotReadyExplanation(foldReadyReason),
            BuildFoldBackfillCommand(resolvedDbPath),
            BuildFoldRebuildCommand(projectRoot, resolvedDbPath));
    }

    private static bool IsFoldOnlyReadinessDegraded(
        bool graphTableAvailable,
        bool issuesTableAvailable,
        bool sqlGraphContractReady,
        bool hotspotFamilyReady,
        bool csharpSymbolNameReady,
        bool csharpMetadataTargetReady,
        bool foldReady)
        => !foldReady
           && graphTableAvailable
           && issuesTableAvailable
           && sqlGraphContractReady
           && hotspotFamilyReady
           && csharpSymbolNameReady
           && csharpMetadataTargetReady;

    private static string GetIndexReadinessWarning(bool graphTableAvailable, bool issuesTableAvailable, bool sqlGraphContractReady, bool hotspotFamilyReady, bool csharpSymbolNameReady, bool csharpMetadataTargetReady, bool foldReady, string? foldReadyReason, string projectRoot, string resolvedDbPath)
    {
        var foldOnlyRemediation = BuildFoldOnlyReadinessRemediation(
            graphTableAvailable,
            issuesTableAvailable,
            sqlGraphContractReady,
            hotspotFamilyReady,
            csharpSymbolNameReady,
            csharpMetadataTargetReady,
            foldReady,
            foldReadyReason,
            projectRoot,
            resolvedDbPath);
        if (foldOnlyRemediation != null)
        {
            return $"Index completed with fold-only degraded readiness (fold_ready=false). {foldOnlyRemediation.DegradedReason} Run `{foldOnlyRemediation.RecommendedAction}` to restamp folded-name columns in place, or `{foldOnlyRemediation.AlternativeAction}` for a full rebuild.";
        }

        var degradedParts = new List<string>();
        if (!graphTableAvailable)
            degradedParts.Add(DegradationReasonCodes.GraphTableMissing);
        if (!issuesTableAvailable)
            degradedParts.Add(DegradationReasonCodes.IssuesTableMissing);
        if (!sqlGraphContractReady)
            degradedParts.Add(DegradationReasonCodes.SqlGraphContractNotReady);
        if (!hotspotFamilyReady)
            degradedParts.Add(DegradationReasonCodes.HotspotFamilyNotReady);
        if (!csharpSymbolNameReady)
            degradedParts.Add(DegradationReasonCodes.CSharpSymbolNameNotReady);
        if (!csharpMetadataTargetReady)
            degradedParts.Add(DegradationReasonCodes.CSharpMetadataTargetNotReady);
        if (!foldReady)
            degradedParts.Add(DegradationReasonCodes.FoldReadyNotReady);

        return $"Index completed with degraded readiness ({string.Join(", ", degradedParts)}). Run `cdidx status --db \"{resolvedDbPath}\" --json` to inspect the current DB state.";
    }

    private static string QuoteCommandArgument(string value)
    {
        var fullPath = DbPathResolver.NormalizeDbPath(value);
        if (!fullPath.StartsWith("file:", StringComparison.OrdinalIgnoreCase))
            fullPath = Path.GetFullPath(fullPath);

        return fullPath.IndexOfAny([' ', '\t', '"']) >= 0
            ? $"\"{fullPath.Replace("\"", "\\\"", StringComparison.Ordinal)}\""
            : fullPath;
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
            var existingContent = File.Exists(ioExcludeFile) ? File.ReadAllText(ioExcludeFile) : "";
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
                var (record, content, _, _) = indexer.BuildRecordWithRawBytes(absolutePath);
                if (record.Lang != "csharp")
                    continue;

                if (!MayContainCSharpStaticInterfaceContract(content))
                    continue;

                pendingSymbols.AddRange(SymbolExtractor.Extract(0, record.Lang, content, record.Path));
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
        => symbol.Kind is "function" or "property"
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
    public bool Watch { get; init; }
    public int? WatchDebounceMs { get; init; }
    public DurationOutputFormat DurationFormat { get; init; } = DurationOutputFormat.Auto;
    public long? MaxFileSizeBytes { get; init; }
    public int Parallelism { get; init; } = IndexCommandRunner.DefaultIndexParallelism();
}

public sealed class BackfillFoldCommandOptions
{
    public bool ShowHelp { get; init; }
    public string DbPath { get; init; } = Path.Combine(".cdidx", "codeindex.db");
    public bool Json { get; init; }
    public string? ParseError { get; init; }
}

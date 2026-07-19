using System.Diagnostics;
using System.Globalization;
using System.Text;
using System.Text.Json;
using System.Text.Json.Nodes;
using System.Text.Json.Serialization.Metadata;
using CodeIndex.Diagnostics;
using CodeIndex.Indexer;

namespace CodeIndex.Cli;

/// <summary>
/// Runs the `cdidx index --watch` loop: watch the project tree with FileSystemWatcher,
/// coalesce events through a debounce window, and replay each batch as a partial
/// `cdidx index --files ...` update.
/// `cdidx index --watch` のループ実装。FileSystemWatcher で変更を観測し、debounce ウィンドウで
/// バッチ化したうえで部分更新 (`--files`) として再実行する。
/// </summary>
internal static class IndexWatchRunner
{
    internal enum WatchPathDisposition
    {
        Ignore,
        Index,
        Reconcile,
    }

    internal const int DefaultDebounceMs = 500;
    internal const int MaxDebounceMs = 60_000;
    internal const int DefaultWatchPendingPathLimit = 4096;
    internal const int MaxWatchPendingPathLimit = 262_144;
    internal const int MaxHumanSummarySubRunJsonChars = 64 * 1024;
    internal const int MaxHumanSummaryJsonDepth = 16;
    internal const int BatchPathSampleLimit = 20;
    internal const int MaxSubRunArgumentChars = 64 * 1024;
    internal const int BatchPathSampleMaxChars = 160;
    internal const int MaxWatchDiagnosticChars = 256;
    internal static Action<Action<string>>? WatchReadyForTesting { get; set; }
    private const int InternalBufferSize = 64 * 1024;
    private const int PollIntervalMs = 50;
    private const string WatchDiagnosticTruncationMarker = "...[truncated]";

    internal static bool DeleteSpoolFileForTesting(string? spoolPath, Action<string>? deleteOverride = null)
        => DeleteSpoolFile(spoolPath, deleteOverride);

    internal static IndexWatchContractJsonResult BuildWatchContractForTesting(
        TimeSpan debounce,
        int maxPendingPaths,
        bool ignoreCase)
        => BuildWatchContract(debounce, maxPendingPaths, ignoreCase);

    internal static bool ShouldIgnoreWatchInternalPathForTesting(
        string projectRoot,
        string resolvedDbPath,
        string fullPath,
        bool ignoreCase,
        bool dbPathExplicit)
        => ShouldIgnoreWatchInternalPath(projectRoot, resolvedDbPath, fullPath, ignoreCase, dbPathExplicit);

    internal static WatchPathDisposition ClassifyWatchPathForTesting(
        string projectRoot,
        string resolvedDbPath,
        string fullPath,
        bool ignoreCase,
        bool dbPathExplicit)
    {
        var fileIndexer = new FileIndexer(
            projectRoot,
            ignoreCase,
            projectRoot,
            maxFileSizeBytes: null,
            directoryIgnoreCaseProbe: null,
            internalIndexDatabasePath: resolvedDbPath);
        return ClassifyWatchPath(projectRoot, resolvedDbPath, fullPath, ignoreCase, dbPathExplicit, fileIndexer);
    }

    public static int Run(
        IndexCommandOptions baseOptions,
        JsonSerializerOptions jsonOptions,
        string projectRoot,
        string resolvedDbPath,
        CancellationToken cancellationToken)
    {
        try
        {
            return RunCore(baseOptions, jsonOptions, projectRoot, resolvedDbPath, cancellationToken);
        }
        catch (OperationCanceledException) when (cancellationToken.IsCancellationRequested)
        {
            return CommandExitCodes.Success;
        }
    }

    internal static int RunCore(
        IndexCommandOptions baseOptions,
        JsonSerializerOptions jsonOptions,
        string projectRoot,
        string resolvedDbPath,
        CancellationToken cancellationToken)
        // Preserve the existing synchronous CLI/test entry point while the watch loop itself
        // runs through RunCoreAsync so cancellation does not block on Task.Delay.
        => RunCoreAsync(baseOptions, jsonOptions, projectRoot, resolvedDbPath, cancellationToken)
            .GetAwaiter()
            .GetResult();

    internal static async Task<int> RunCoreAsync(
        IndexCommandOptions baseOptions,
        JsonSerializerOptions jsonOptions,
        string projectRoot,
        string resolvedDbPath,
        CancellationToken cancellationToken,
        Action<Action<string>>? startupHandoff = null)
    {
        var debounce = TimeSpan.FromMilliseconds(baseOptions.WatchDebounceMs ?? DefaultDebounceMs);
        var maxPendingPaths = baseOptions.WatchPendingPathLimit;
        var ignoreCase = GitHelper.ResolveIgnoreCase(projectRoot, cancellationToken);
        var dbPathExplicit = !string.IsNullOrWhiteSpace(baseOptions.DbPath);
        var batcher = new FileChangeBatcher(debounce, ignoreCase: ignoreCase, maxPendingPaths: maxPendingPaths);

        var ignoreRuleRoot = GitHelper.TryGetRepositoryRoot(projectRoot, cancellationToken) ?? Path.GetFullPath(projectRoot);
        var fileIndexer = new FileIndexer(
            projectRoot,
            ignoreCase,
            ignoreRuleRoot,
            maxFileSizeBytes: null,
            directoryIgnoreCaseProbe: null,
            internalIndexDatabasePath: resolvedDbPath);
        var watchExitCode = CommandExitCodes.Success;

        FileSystemWatcher? watcher = null;
        List<FileSystemWatcher>? ancestorIgnoreWatchers = null;
        try
        {
            watcher = new FileSystemWatcher(projectRoot)
            {
                IncludeSubdirectories = true,
                InternalBufferSize = InternalBufferSize,
                NotifyFilter = NotifyFilters.FileName
                    | NotifyFilters.DirectoryName
                    | NotifyFilters.LastWrite
                    | NotifyFilters.Size,
            };

            void Enqueue(string fullPath)
            {
                if (string.IsNullOrEmpty(fullPath))
                    return;

                try
                {
                    // Membership and extractor inputs are not source files, but they must reach
                    // the debounced update runner so it can reconcile the whole workspace. All
                    // other paths use the same FileIndexer membership filter as scan/status.
                    // membership / extractor 入力は source ではないが、workspace 全体の
                    // reconciliation を起動するため debounce 対象として保持する。それ以外は
                    // scan/status と同じ FileIndexer membership filter を使う。
                    var disposition = ClassifyWatchPath(
                        projectRoot,
                        resolvedDbPath,
                        fullPath,
                        ignoreCase,
                        dbPathExplicit,
                        fileIndexer);
                    if (disposition == WatchPathDisposition.Ignore)
                        return;
                }
                catch (Exception ex) when (ex is IOException or UnauthorizedAccessException or InvalidOperationException or ArgumentException or NotSupportedException)
                {
                    // Filter failures must not silently drop the event; defer to the sub-update
                    // pass to log a per-file warning if the path is genuinely broken.
                    // フィルタ失敗時はイベントを捨てずサブ更新へ委譲する。
                }

                batcher.Add(fullPath);
            }

            watcher.Created += (_, e) => Enqueue(e.FullPath);
            watcher.Changed += (_, e) => Enqueue(e.FullPath);
            watcher.Deleted += (_, e) => Enqueue(e.FullPath);
            watcher.Renamed += (_, e) =>
            {
                Enqueue(e.OldFullPath);
                Enqueue(e.FullPath);
            };
            watcher.Error += (_, e) =>
            {
                // Buffer overflows on Linux/inotify and macOS/FSEvents drop individual paths;
                // a full rescan is the only safe recovery. Surface the reason for users who
                // may need to raise fs.inotify.max_user_watches.
                // バッファ溢れ時は個別パスが失われるためフルスキャンへ。inotify 上限引き上げの
                // 必要性が判断できるよう理由も保持する。
                batcher.RequestFullRescan(e.GetException()?.Message);
            };

            watcher.EnableRaisingEvents = true;
            ancestorIgnoreWatchers = CreateAncestorIgnoreWatchers(
                projectRoot,
                ignoreRuleRoot,
                ignoreCase,
                Enqueue);
            startupHandoff?.Invoke(Enqueue);

            // The initial command scan completed before this watcher was subscribed. Re-scan
            // after subscription, then drain every event buffered during that reconciliation
            // before publishing the ready event. A callback that reaches Add after
            // the ready transition is an ordinary live update, not a startup handoff event.
            // 初回 command scan は watcher の subscribe より先に完了している。subscribe 後に
            // 再 scan し、その間に buffer された event をすべて drain してから ready event を
            // 公開する。ready 遷移後に Add へ到達した callback は通常の live update。
            if (File.Exists(DbPathResolver.NormalizeDbPath(resolvedDbPath)))
            {
                RecordSubRunExitCode(
                    ref watchExitCode,
                    RunFullRescan(baseOptions, jsonOptions, resolvedDbPath, cancellationToken, phase: "startup"));
            }

            // Close the startup generation by taking one atomic snapshot. Events arriving after
            // this boundary remain queued as ordinary live updates, so a continuously changing
            // workspace cannot prevent the watcher from ever becoming ready.
            // startup generation は atomic snapshot で閉じる。この境界後の event は通常の
            // live update として queue に残し、変更が連続する workspace でも ready を妨げない。
            if (!cancellationToken.IsCancellationRequested
                && batcher.TryDrainImmediately(out var startupBatch, out var startupFullRescan, out var startupOverflowReason))
            {
                if (startupFullRescan)
                {
                    EmitWatchOverflow(baseOptions, jsonOptions, startupOverflowReason, resolvedDbPath);
                    RecordSubRunExitCode(
                        ref watchExitCode,
                        RunFullRescan(baseOptions, jsonOptions, resolvedDbPath, cancellationToken, phase: "startup"));
                }
                else if (startupBatch.Count > 0)
                {
                    RecordSubRunExitCode(
                        ref watchExitCode,
                        RunPartialUpdate(baseOptions, jsonOptions, startupBatch, resolvedDbPath, cancellationToken, phase: "startup"));
                }
            }

            var ready = !cancellationToken.IsCancellationRequested
                && watchExitCode == CommandExitCodes.Success;
            if (ready)
            {
                EmitWatchStarted(baseOptions, jsonOptions, projectRoot, resolvedDbPath, debounce, maxPendingPaths, ignoreCase);
                WatchReadyForTesting?.Invoke(Enqueue);
            }

            while (ready && !cancellationToken.IsCancellationRequested)
            {
                try
                {
                    await Task.Delay(PollIntervalMs, cancellationToken).ConfigureAwait(false);
                }
                catch (OperationCanceledException)
                {
                    break;
                }

                if (!batcher.TryDrain(out var batch, out var fullRescan, out var overflowReason))
                    continue;

                if (fullRescan)
                {
                    EmitWatchOverflow(baseOptions, jsonOptions, overflowReason, resolvedDbPath);
                    RecordSubRunExitCode(ref watchExitCode, RunFullRescan(baseOptions, jsonOptions, resolvedDbPath, cancellationToken));
                    continue;
                }

                if (batch.Count == 0)
                    continue;

                RecordSubRunExitCode(ref watchExitCode, RunPartialUpdate(baseOptions, jsonOptions, batch, resolvedDbPath, cancellationToken));
            }
        }
        finally
        {
            if (ancestorIgnoreWatchers != null)
            {
                foreach (var ancestorWatcher in ancestorIgnoreWatchers)
                {
                    try { ancestorWatcher.EnableRaisingEvents = false; } catch (Exception ex) when (ex is IOException or InvalidOperationException or ObjectDisposedException) { }
                    ancestorWatcher.Dispose();
                }
            }
            if (watcher != null)
            {
                try { watcher.EnableRaisingEvents = false; } catch (Exception ex) when (ex is IOException or InvalidOperationException or ObjectDisposedException) { }
                watcher.Dispose();
            }
        }

        EmitWatchStopped(baseOptions, jsonOptions);
        return watchExitCode;
    }

    private static List<FileSystemWatcher> CreateAncestorIgnoreWatchers(
        string projectRoot,
        string ignoreRuleRoot,
        bool ignoreCase,
        Action<string> enqueue)
    {
        var watchers = new List<FileSystemWatcher>();
        var fullProjectRoot = Path.GetFullPath(projectRoot);
        var fullIgnoreRuleRoot = Path.GetFullPath(ignoreRuleRoot);
        var comparison = ignoreCase ? StringComparison.OrdinalIgnoreCase : StringComparison.Ordinal;
        if (string.Equals(fullProjectRoot, fullIgnoreRuleRoot, comparison))
            return watchers;

        var relativeProjectRoot = Path.GetRelativePath(fullIgnoreRuleRoot, fullProjectRoot);
        if (Path.IsPathRooted(relativeProjectRoot)
            || relativeProjectRoot == ".."
            || relativeProjectRoot.StartsWith($"..{Path.DirectorySeparatorChar}", StringComparison.Ordinal)
            || relativeProjectRoot.StartsWith($"..{Path.AltDirectorySeparatorChar}", StringComparison.Ordinal))
        {
            return watchers;
        }

        try
        {
            var directory = Directory.GetParent(fullProjectRoot);
            while (directory != null)
            {
                var ancestorWatcher = new FileSystemWatcher(directory.FullName)
                {
                    IncludeSubdirectories = false,
                    NotifyFilter = NotifyFilters.FileName | NotifyFilters.LastWrite | NotifyFilters.Size,
                };
                watchers.Add(ancestorWatcher);
                ancestorWatcher.Filters.Add(".gitignore");
                ancestorWatcher.Filters.Add(".cdidxignore");
                ancestorWatcher.Created += (_, e) => enqueue(e.FullPath);
                ancestorWatcher.Changed += (_, e) => enqueue(e.FullPath);
                ancestorWatcher.Deleted += (_, e) => enqueue(e.FullPath);
                ancestorWatcher.Renamed += (_, e) =>
                {
                    enqueue(e.OldFullPath);
                    enqueue(e.FullPath);
                };
                ancestorWatcher.EnableRaisingEvents = true;

                if (string.Equals(directory.FullName, fullIgnoreRuleRoot, comparison))
                    break;

                directory = directory.Parent;
            }
        }
        catch
        {
            foreach (var watcher in watchers)
                watcher.Dispose();
            throw;
        }

        return watchers;
    }

    private static int RunPartialUpdate(
        IndexCommandOptions baseOptions,
        JsonSerializerOptions jsonOptions,
        IReadOnlyList<string> changedPaths,
        string resolvedDbPath,
        CancellationToken cancellationToken,
        string phase = "incremental")
    {
        var baseArgs = BuildSubRunArgs(baseOptions, resolvedDbPath);
        var batches = BuildPartialUpdateBatches(baseArgs, changedPaths);
        if (batches == null)
            return RunFullRescan(baseOptions, jsonOptions, resolvedDbPath, cancellationToken);

        var exitCode = CommandExitCodes.Success;
        foreach (var batch in batches)
        {
            var stopwatch = Stopwatch.StartNew();
            var args = new List<string>(baseArgs.Count + 1 + batch.Count);
            args.AddRange(baseArgs);
            args.Add("--files");
            args.AddRange(batch);

            var subRunExitCode = InvokeSubRunAndEmit(baseOptions, jsonOptions, args, stopwatch, "updated", batch.Count, phase, batch, cancellationToken);
            RecordSubRunExitCode(ref exitCode, subRunExitCode);
            if (cancellationToken.IsCancellationRequested)
                break;
        }

        return exitCode;
    }

    internal static List<List<string>>? BuildPartialUpdateBatches(IReadOnlyList<string> baseArgs, IReadOnlyList<string> changedPaths)
    {
        var baseArgumentChars = EstimateSubRunArgumentChars(baseArgs) + EstimateSubRunArgumentChars("--files");
        var batches = new List<List<string>>();
        var current = new List<string>();
        var currentArgumentChars = baseArgumentChars;

        foreach (var path in changedPaths)
        {
            var pathArgumentChars = EstimateSubRunArgumentChars(path);
            if (baseArgumentChars + pathArgumentChars > MaxSubRunArgumentChars)
                return null;

            if (current.Count > 0 && currentArgumentChars + pathArgumentChars > MaxSubRunArgumentChars)
            {
                batches.Add(current);
                current = new List<string>();
                currentArgumentChars = baseArgumentChars;
            }

            current.Add(path);
            currentArgumentChars += pathArgumentChars;
        }

        if (current.Count > 0)
            batches.Add(current);
        return batches;
    }

    private static int EstimateSubRunArgumentChars(IEnumerable<string> args)
    {
        var total = 0;
        foreach (var arg in args)
            total += EstimateSubRunArgumentChars(arg);
        return total;
    }

    private static int EstimateSubRunArgumentChars(string arg)
        => arg.Length + 1;

    private static int RunFullRescan(
        IndexCommandOptions baseOptions,
        JsonSerializerOptions jsonOptions,
        string resolvedDbPath,
        CancellationToken cancellationToken,
        string phase = "incremental")
    {
        var stopwatch = Stopwatch.StartNew();
        var args = BuildSubRunArgs(baseOptions, resolvedDbPath);
        // No --files: this is a default incremental full scan.
        // --files を付けない: 通常のインクリメンタル全件スキャン。
        return InvokeSubRunAndEmit(baseOptions, jsonOptions, args, stopwatch, "rescanned", batchSize: null, phase, batchPaths: null, cancellationToken);
    }

    private static void RecordSubRunExitCode(ref int watchExitCode, int subRunExitCode)
    {
        if (subRunExitCode != CommandExitCodes.Success)
            watchExitCode = subRunExitCode;
    }

    private static bool ShouldIgnoreWatchInternalPath(
        string projectRoot,
        string resolvedDbPath,
        string fullPath,
        bool ignoreCase,
        bool dbPathExplicit)
    {
        var comparison = ignoreCase ? StringComparison.OrdinalIgnoreCase : StringComparison.Ordinal;
        var normalizedPath = Path.GetFullPath(fullPath);
        var normalizedProjectRoot = Path.GetFullPath(projectRoot);
        var normalizedDbPath = Path.GetFullPath(DbPathResolver.NormalizeDbPath(resolvedDbPath));

        var defaultDataDir = Path.Combine(normalizedProjectRoot, ".cdidx");
        var dbDirectory = Path.GetDirectoryName(normalizedDbPath);
        if (!dbPathExplicit && !string.IsNullOrEmpty(dbDirectory)
            && !IsSamePath(defaultDataDir, dbDirectory, comparison)
            && IsSameOrUnderDirectory(dbDirectory, normalizedPath, comparison))
        {
            return true;
        }

        if (IsSamePath(normalizedPath, normalizedDbPath, comparison))
            return true;

        foreach (var suffix in new[] { "-wal", "-shm", "-journal" })
        {
            if (IsSamePath(normalizedPath, normalizedDbPath + suffix, comparison))
                return true;
        }

        var lockPath = IndexLock.GetLockPath(normalizedDbPath);
        if (IsSamePath(normalizedPath, lockPath, comparison)
            || IsSamePath(normalizedPath, IndexLock.GetInfoPath(lockPath), comparison)
            || normalizedPath.StartsWith(lockPath + ".", comparison))
        {
            return true;
        }

        return false;
    }

    private static WatchPathDisposition ClassifyWatchPath(
        string projectRoot,
        string resolvedDbPath,
        string fullPath,
        bool ignoreCase,
        bool dbPathExplicit,
        FileIndexer fileIndexer)
    {
        var invalidation = FileIndexer.ClassifyIndexInputInvalidation(projectRoot, fullPath);
        if (invalidation != FileIndexer.IndexInputInvalidationKind.None)
            return WatchPathDisposition.Reconcile;

        if (ShouldIgnoreWatchInternalPath(projectRoot, resolvedDbPath, fullPath, ignoreCase, dbPathExplicit)
            || fileIndexer.ShouldSkipPath(fullPath))
        {
            return WatchPathDisposition.Ignore;
        }

        return WatchPathDisposition.Index;
    }

    private static bool IsSameOrUnderDirectory(string directory, string fullPath, StringComparison comparison)
    {
        var normalizedDirectory = Path.GetFullPath(directory);
        if (IsSamePath(normalizedDirectory, fullPath, comparison))
            return true;

        var directoryPrefix = Path.EndsInDirectorySeparator(normalizedDirectory)
            ? normalizedDirectory
            : normalizedDirectory + Path.DirectorySeparatorChar;
        return fullPath.StartsWith(directoryPrefix, comparison);
    }

    private static bool IsSamePath(string left, string right, StringComparison comparison)
        => string.Equals(
            TrimDirectorySeparators(left),
            TrimDirectorySeparators(right),
            comparison);

    private static string TrimDirectorySeparators(string value)
    {
        var root = Path.GetPathRoot(value);
        var trimmed = value.TrimEnd(Path.DirectorySeparatorChar, Path.AltDirectorySeparatorChar);
        return string.IsNullOrEmpty(trimmed) && !string.IsNullOrEmpty(root)
            ? root
            : trimmed;
    }

    private static List<string> BuildSubRunArgs(IndexCommandOptions baseOptions, string? resolvedDbPath = null)
    {
        // Always pass --json so sub-runs produce a single JSON-line summary on stdout. The
        // watch loop then either forwards that line (user --json) or extracts a one-line
        // human summary (user non-JSON). Otherwise each sub-run would reprint the banner.
        // 常に --json を付けてサブ実行の stdout を1行 JSON に揃える。watch ループ側で
        // 透過 or 整形してから出力する。
        var args = new List<string>(8) { baseOptions.ProjectPath!, "--json", "--quiet" };
        var dbPath = string.IsNullOrEmpty(resolvedDbPath) ? baseOptions.DbPath : resolvedDbPath;
        if (!string.IsNullOrEmpty(dbPath))
        {
            args.Add("--db");
            args.Add(dbPath!);
        }
        if (baseOptions.Verbose && baseOptions.Json)
            args.Add("--verbose");
        if (baseOptions.MaxFileSizeBytes is { } maxFileSizeBytes)
        {
            args.Add("--max-file-bytes");
            args.Add(maxFileSizeBytes.ToString(CultureInfo.InvariantCulture));
        }
        if (baseOptions.MaxSymbolsPerFile != IndexCommandRunner.DefaultMaxSymbolsPerFile)
        {
            args.Add("--max-symbols-per-file");
            args.Add(baseOptions.MaxSymbolsPerFile.ToString(CultureInfo.InvariantCulture));
        }
        if (baseOptions.MaxReferencesPerFile != IndexCommandRunner.DefaultMaxReferencesPerFile)
        {
            args.Add("--max-references-per-file");
            args.Add(baseOptions.MaxReferencesPerFile.ToString(CultureInfo.InvariantCulture));
        }
        if (baseOptions.Parallelism != IndexCommandRunner.DefaultIndexParallelism())
        {
            args.Add("--parallelism");
            args.Add(baseOptions.Parallelism.ToString(CultureInfo.InvariantCulture));
        }
        if (baseOptions.SymlinkPolicy != FileIndexer.SymlinkPolicy.None)
        {
            args.Add("--follow-symlinks");
            args.Add(baseOptions.SymlinkPolicy.ToString().ToLowerInvariant());
        }
        if (baseOptions.SymbolKindFilter.Include.Count > 0)
        {
            args.Add("--include-symbol-kind");
            args.Add(string.Join(",", baseOptions.SymbolKindFilter.Include));
        }
        if (baseOptions.SymbolKindFilter.Exclude.Count > 0)
        {
            args.Add("--exclude-symbol-kind");
            args.Add(string.Join(",", baseOptions.SymbolKindFilter.Exclude));
        }
        return args;
    }

    internal static int InvokeSubRunAndEmit(
        IndexCommandOptions baseOptions,
        JsonSerializerOptions jsonOptions,
        List<string> args,
        Stopwatch stopwatch,
        string status,
        int? batchSize,
        string phase,
        IReadOnlyList<string>? batchPaths,
        CancellationToken cancellationToken)
    {
        string capturedJson;
        string? spoolPath = null;
        int subRunExitCode;
        WatchSubRunCaptureWriter? captureWriter = null;
        try
        {
            TextWriter? spoolWriter = null;
            if (baseOptions.Json)
            {
                spoolPath = Path.Combine(Path.GetTempPath(), $"cdidx-watch-subrun-{Guid.NewGuid():N}.jsonl");
                spoolWriter = new StreamWriter(
                    CreateSubRunSpoolFileStream(spoolPath),
                    new UTF8Encoding(encoderShouldEmitUTF8Identifier: false));
            }

            captureWriter = new WatchSubRunCaptureWriter(MaxHumanSummarySubRunJsonChars + 1, spoolWriter);
            using var subRunCancellation = CancellationTokenSource.CreateLinkedTokenSource(cancellationToken);
            subRunExitCode = IndexCommandRunner.Run(args.ToArray(), jsonOptions, subRunCancellation, captureWriter);

            captureWriter.Flush();
            capturedJson = captureWriter.CapturedText;
        }
        finally
        {
            captureWriter?.Dispose();
        }
        stopwatch.Stop();
        var eventStatus = subRunExitCode == CommandExitCodes.Success ? status : "failed";
        var failureReason = subRunExitCode == CommandExitCodes.Success
            ? null
            : $"{status} sub-run exited with code {subRunExitCode.ToString(CultureInfo.InvariantCulture)}";
        var summary = ParseSubRunSummary(capturedJson);

        if (baseOptions.Json)
        {
            var pathSamples = BuildBatchPathSamples(baseOptions.ProjectPath!, batchPaths, out var pathSamplesTruncated);
            // Pre-pend a watch-event header line so MCP clients can distinguish watch
            // batches from the initial scan. The underlying sub-run result follows.
            // watch バッチであることを示すヘッダ行を先頭に流し、その後にサブ実行 JSON を出す。
            var watchEvent = new IndexWatchEventJsonResult
            {
                Status = eventStatus,
                Phase = phase,
                BatchSize = batchSize,
                BatchPathSamples = pathSamples.Count > 0 ? pathSamples : null,
                BatchPathSampleLimit = batchPaths == null ? null : BatchPathSampleLimit,
                BatchPathSamplesTruncated = batchPaths == null ? null : pathSamplesTruncated,
                ElapsedMs = stopwatch.ElapsedMilliseconds,
                ExitCode = subRunExitCode,
                Updated = summary.Updated,
                Removed = summary.Removed,
                Errors = summary.Errors,
                SubRunParseStatus = summary.ParseStatus,
                SubRunParseReason = summary.ParseReason,
                Reason = failureReason,
            };
            var payload = JsonSerializer
                .SerializeToNode(watchEvent, CliJsonSerializerContextFactory.Create(jsonOptions).IndexWatchEventJsonResult)!
                .AsObject();
            AddWatchSubRunSummaryFields(payload, status, subRunExitCode, summary);
            Console.Out.WriteLine(payload.ToJsonString(EnsureJsonNodeSerializerOptions(jsonOptions)));

            if (!TryWriteSpooledSubRunOutput(spoolPath, out var endedWithLineBreak))
            {
                var trimmed = capturedJson.TrimEnd('\r', '\n');
                if (!string.IsNullOrEmpty(trimmed))
                    Console.Out.WriteLine(trimmed);
            }
            else if (!endedWithLineBreak)
            {
                Console.Out.WriteLine();
            }
        }
        else
        {
            var human = FormatHumanSummary(eventStatus, batchSize, stopwatch.ElapsedMilliseconds, capturedJson, subRunExitCode);
            CommandErrorWriter.WriteStderr(human);
        }

        DeleteSpoolFile(spoolPath);
        return subRunExitCode;
    }

    internal static FileStream CreateSubRunSpoolFileStream(string spoolPath)
        => DataDirectorySecurity.OpenPrivateFileStream(spoolPath, FileMode.CreateNew, FileAccess.Write, FileShare.Read);

    private static bool TryWriteSpooledSubRunOutput(string? spoolPath, out bool endedWithLineBreak)
    {
        endedWithLineBreak = true;
        if (string.IsNullOrWhiteSpace(spoolPath) || !File.Exists(spoolPath) || new FileInfo(spoolPath).Length == 0)
            return false;

        var buffer = new char[8192];
        var wroteAny = false;
        char lastChar = '\0';
        using var reader = new StreamReader(spoolPath, Encoding.UTF8);
        int read;
        while ((read = reader.Read(buffer, 0, buffer.Length)) > 0)
        {
            Console.Out.Write(buffer, 0, read);
            wroteAny = true;
            lastChar = buffer[read - 1];
        }

        endedWithLineBreak = !wroteAny || lastChar is '\n' or '\r';
        return wroteAny;
    }

    private static bool DeleteSpoolFile(string? spoolPath, Action<string>? deleteOverride = null)
    {
        if (string.IsNullOrWhiteSpace(spoolPath))
            return false;

        return AtomicFileWriter.TryDeleteFile(
            spoolPath,
            ex => ReportSpoolCleanupFailure(spoolPath, ex),
            deleteOverride);
    }

    private static void ReportSpoolCleanupFailure(string spoolPath, Exception exception)
    {
        var target = FormatSpoolCleanupTarget(spoolPath);
        var reason = CommandErrorWriter.FormatSanitizedException(exception);
        GlobalToolLog.Error($"watch_spool_cleanup_failed target={target} reason={reason}");
        CommandErrorWriter.WriteStderr(
            $"Warning [watch_spool_cleanup_failed]: failed to delete watch spool file {target} ({reason}).");
    }

    private static string FormatSpoolCleanupTarget(string path)
    {
        try
        {
            var target = Path.GetFileName(path.TrimEnd(Path.DirectorySeparatorChar, Path.AltDirectorySeparatorChar));
            return ConsoleUi.FormatBoundedValue(string.IsNullOrWhiteSpace(target) ? "<spool>" : target);
        }
        catch (Exception ex) when (ex is ArgumentException or NotSupportedException)
        {
            return "<invalid>";
        }
    }

    internal sealed class WatchSubRunCaptureWriter : TextWriter
    {
        private readonly int _maxCapturedChars;
        private readonly TextWriter? _inner;
        private readonly StringBuilder _captured = new();

        internal WatchSubRunCaptureWriter(int maxCapturedChars, TextWriter? inner)
        {
            _maxCapturedChars = Math.Max(0, maxCapturedChars);
            _inner = inner;
        }

        public override Encoding Encoding => _inner?.Encoding ?? Encoding.UTF8;

        internal string CapturedText => _captured.ToString();

        internal bool Truncated { get; private set; }

        public override void Write(char value)
        {
            Capture(stackalloc char[] { value });
            _inner?.Write(value);
        }

        public override void Write(string? value)
        {
            if (value != null)
                Capture(value.AsSpan());
            _inner?.Write(value);
        }

        public override void Write(char[] buffer, int index, int count)
        {
            Capture(buffer.AsSpan(index, count));
            _inner?.Write(buffer, index, count);
        }

        public override void Flush()
        {
            _inner?.Flush();
            base.Flush();
        }

        protected override void Dispose(bool disposing)
        {
            if (disposing)
                _inner?.Dispose();
            base.Dispose(disposing);
        }

        private void Capture(ReadOnlySpan<char> value)
        {
            var remaining = _maxCapturedChars - _captured.Length;
            if (remaining <= 0)
            {
                if (value.Length > 0)
                    Truncated = true;
                return;
            }

            var take = Math.Min(remaining, value.Length);
            _captured.Append(value[..take]);
            if (take < value.Length)
                Truncated = true;
        }
    }


    private static void AddWatchSubRunSummaryFields(JsonObject payload, string requestedStatus, int subRunExitCode, WatchSubRunSummary summary)
    {
        if (!string.Equals(requestedStatus, "rescanned", StringComparison.Ordinal))
            return;

        payload["rescan_scope"] = "full_workspace";
        payload["rescan_completed"] = subRunExitCode == CommandExitCodes.Success && summary.ParseStatus == "parsed";

        if (summary.FilesTotal is long filesTotal)
            payload["files_total"] = filesTotal;
        if (summary.FilesScanned is int filesScanned)
            payload["files_scanned"] = filesScanned;
        if (summary.FilesSkipped is int filesSkipped)
        {
            payload["files_skipped"] = filesSkipped;
            if (filesSkipped > 0)
                payload["files_skipped_category"] = "unchanged_or_reused_files";
        }
        if (summary.FilesPurged is int filesPurged)
            payload["files_purged"] = filesPurged;
        if (summary.Warnings is int warnings)
            payload["warnings"] = warnings;
    }

    private static JsonSerializerOptions EnsureJsonNodeSerializerOptions(JsonSerializerOptions jsonOptions)
    {
        if (jsonOptions.TypeInfoResolver != null)
            return jsonOptions;

        return new JsonSerializerOptions(jsonOptions)
        {
            TypeInfoResolver = new DefaultJsonTypeInfoResolver(),
        };
    }

    private static string FormatHumanSummary(string status, int? batchSize, long elapsedMs, string subRunJson, int exitCode)
    {
        var prefix = status switch
        {
            "rescanned" => "[watch] rescanned",
            "failed" => "[watch] failed",
            _ => "[watch] updated",
        };
        var batchLabel = batchSize is int n
            ? $" {ConsoleUi.Counted(n, "path", format: "N0")}"
            : string.Empty;

        // Best-effort parse of the sub-run JSON to surface updated/removed/errors counts.
        // The summary is informational; a parse failure must not break the watch loop.
        // サブ実行 JSON から件数を best-effort で抽出。失敗してもループは続行する。
        var details = new List<string>
        {
            $"exit code {exitCode.ToString(CultureInfo.InvariantCulture)}",
        };
        var summary = ParseSubRunSummary(subRunJson);
        if (summary.ParseStatus == "parsed")
        {
            details.Add($"updated {summary.Updated.GetValueOrDefault()}");
            details.Add($"removed {summary.Removed.GetValueOrDefault()}");
            details.Add($"errors {summary.Errors.GetValueOrDefault()}");
            if (string.Equals(status, "rescanned", StringComparison.Ordinal))
            {
                if (summary.FilesScanned is int filesScanned)
                    details.Add($"scanned {filesScanned}");
                if (summary.FilesSkipped is int filesSkipped)
                    details.Add($"skipped {filesSkipped}");
                if (summary.FilesPurged is int filesPurged)
                    details.Add($"purged {filesPurged}");
            }
        }

        var detail = details.Count > 0 ? $" ({string.Join(", ", details)})" : string.Empty;
        return $"{prefix}{batchLabel}{detail} in {elapsedMs.ToString("N0", System.Globalization.CultureInfo.InvariantCulture)} ms";
    }

    private static WatchSubRunSummary ParseSubRunSummary(string subRunJson)
    {
        var trimmedLength = TrimTrailingLineBreaks(subRunJson);
        if (trimmedLength == 0)
            return WatchSubRunSummary.Unparsed("missing", "sub-run emitted no JSON");

        if (trimmedLength > MaxHumanSummarySubRunJsonChars)
            return WatchSubRunSummary.Unparsed("too_large", $"sub-run JSON exceeded {MaxHumanSummarySubRunJsonChars.ToString(CultureInfo.InvariantCulture)} characters");

        try
        {
            using var doc = BoundedJson.ParseDocument(
                subRunJson[..trimmedLength],
                MaxHumanSummarySubRunJsonChars * 4,
                MaxHumanSummaryJsonDepth);
            var root = doc.RootElement;
            if (root.ValueKind != JsonValueKind.Object || !root.TryGetProperty("summary", out var summary)
                || summary.ValueKind != JsonValueKind.Object)
            {
                return WatchSubRunSummary.Unparsed("missing_summary", "sub-run JSON did not contain an object summary");
            }

            return new WatchSubRunSummary(
                TryReadInt32(summary, "updated") ?? 0,
                TryReadInt32(summary, "removed") ?? 0,
                TryReadInt32(summary, "errors") ?? 0,
                TryReadInt64(summary, "files_total"),
                TryReadInt32(summary, "files_scanned"),
                TryReadInt32(summary, "files_skipped") ?? TryReadInt32(summary, "skipped"),
                TryReadInt32(summary, "files_purged"),
                TryReadInt32(summary, "warnings"),
                "parsed",
                null);
        }
        catch (Exception ex) when (ex is JsonException or InvalidDataException)
        {
            return WatchSubRunSummary.Unparsed("invalid_json", CommandErrorWriter.FormatSanitizedExceptionMessage(ex));
        }
    }

    private static int? TryReadInt32(JsonElement element, string propertyName)
        => element.TryGetProperty(propertyName, out var property) && property.TryGetInt32(out var value)
            ? value
            : null;

    private static long? TryReadInt64(JsonElement element, string propertyName)
        => element.TryGetProperty(propertyName, out var property) && property.TryGetInt64(out var value)
            ? value
            : null;

    private static List<string> BuildBatchPathSamples(string projectRoot, IReadOnlyList<string>? batchPaths, out bool truncated)
    {
        truncated = false;
        if (batchPaths == null || batchPaths.Count == 0)
            return [];

        truncated = batchPaths.Count > BatchPathSampleLimit;
        var samples = new List<string>(Math.Min(batchPaths.Count, BatchPathSampleLimit));
        foreach (var path in batchPaths.Take(BatchPathSampleLimit))
        {
            var sample = path;
            if (Path.IsPathRooted(path))
                sample = FileIndexer.GetRelativePathFromDirectory(projectRoot, path);
            sample = FileIndexer.NormalizePathSeparators(sample);
            var sanitized = DiagnosticRedactor.RedactSensitiveText(sample, "[redacted]", redactPaths: false);
            var bounded = BoundWatchDisplayText(sanitized, BatchPathSampleMaxChars, out var sampleTruncated);
            truncated |= sampleTruncated;
            samples.Add(bounded);
        }

        return samples;
    }

    internal static string? FormatWatchDiagnosticText(string? value)
    {
        if (string.IsNullOrWhiteSpace(value))
            return null;

        var redacted = DiagnosticRedactor.RedactSensitiveText(value, "[redacted]", redactPaths: true);
        return BoundWatchDisplayText(redacted, MaxWatchDiagnosticChars, out _);
    }

    private static string BoundWatchDisplayText(string value, int maxChars, out bool truncated)
    {
        if (maxChars < 0)
            throw new ArgumentOutOfRangeException(nameof(maxChars), maxChars, "Watch diagnostic limit must be non-negative.");

        var flattened = FlattenWatchDiagnosticControlChars(value);
        if (flattened.Length <= maxChars)
        {
            truncated = false;
            return flattened;
        }

        truncated = true;
        if (maxChars == 0)
            return string.Empty;

        if (maxChars <= WatchDiagnosticTruncationMarker.Length)
            return WatchDiagnosticTruncationMarker[..maxChars];

        return flattened[..(maxChars - WatchDiagnosticTruncationMarker.Length)] + WatchDiagnosticTruncationMarker;
    }

    private static string FlattenWatchDiagnosticControlChars(string value)
    {
        var builder = new System.Text.StringBuilder(value.Length);
        foreach (var c in value)
            builder.Append(char.IsControl(c) ? ' ' : c);
        return builder.ToString();
    }

    private static int TrimTrailingLineBreaks(string value)
    {
        var length = value.Length;
        while (length > 0 && (value[length - 1] == '\r' || value[length - 1] == '\n'))
            length--;

        return length;
    }

    private static void EmitWatchStarted(
        IndexCommandOptions baseOptions,
        JsonSerializerOptions jsonOptions,
        string projectRoot,
        string resolvedDbPath,
        TimeSpan debounce,
        int maxPendingPaths,
        bool ignoreCase)
    {
        if (baseOptions.Json)
        {
            Console.Out.WriteLine(JsonSerializer.Serialize(new IndexWatchStartedJsonResult
            {
                Status = "watching",
                Phase = "initial_scan",
                ProjectRoot = "[redacted]",
                Db = "[redacted]",
                DebounceMs = (int)debounce.TotalMilliseconds,
                WatchPendingPathLimit = maxPendingPaths,
                WatchContract = BuildWatchContract(debounce, maxPendingPaths, ignoreCase),
            }, CliJsonSerializerContextFactory.Create(jsonOptions).IndexWatchStartedJsonResult));
        }
        else
        {
            CommandErrorWriter.WriteStderr();
            CommandErrorWriter.WriteStderr($"[watch] Watching {projectRoot} for changes (debounce {(int)debounce.TotalMilliseconds} ms, pending path limit {maxPendingPaths.ToString("N0", CultureInfo.InvariantCulture)}). Press Ctrl+C to stop.");
        }
    }

    private static IndexWatchContractJsonResult BuildWatchContract(
        TimeSpan debounce,
        int maxPendingPaths,
        bool ignoreCase)
        => new()
        {
            Debounce = "quiet_window",
            DebounceMs = (int)debounce.TotalMilliseconds,
            MaxDebounceMs = IndexWatchRunner.MaxDebounceMs,
            PollIntervalMs = IndexWatchRunner.PollIntervalMs,
            WatchPendingPathLimit = maxPendingPaths,
            PathComparison = ignoreCase ? "ordinal_ignore_case" : "ordinal",
            ChangeCoalescing = "distinct_paths_refresh_debounce",
            RenameEvents = "old_and_new_paths",
            OverflowRecovery = "full_rescan_after_debounce",
            WatcherErrorRecovery = "full_rescan_after_debounce",
            Cancellation = "cancel_active_sub_run_then_emit_stopped",
            SubRunOutput = "json_quiet_sub_runs",
            McpWatchMode = "unsupported",
        };

    private static void EmitWatchOverflow(
        IndexCommandOptions baseOptions,
        JsonSerializerOptions jsonOptions,
        string? reason,
        string resolvedDbPath)
    {
        var safeReason = FormatWatchDiagnosticText(reason);
        if (baseOptions.Json)
        {
            Console.Out.WriteLine(JsonSerializer.Serialize(new IndexWatchEventJsonResult
            {
                Status = "overflow",
                Reason = safeReason,
                Phase = "incremental",
                OverflowReason = safeReason,
                WatchPendingPathLimit = baseOptions.WatchPendingPathLimit,
                RecoveryCommand = BuildOverflowRecoveryCommand(baseOptions, resolvedDbPath, redactPaths: true),
            }, CliJsonSerializerContextFactory.Create(jsonOptions).IndexWatchEventJsonResult));
        }
        else
        {
            var detail = string.IsNullOrEmpty(safeReason) ? string.Empty : $" ({safeReason})";
            CommandErrorWriter.WriteStderr($"[watch] Watcher buffer overflowed{detail}; falling back to full rescan.");
        }
    }

    private static void EmitWatchStopped(IndexCommandOptions baseOptions, JsonSerializerOptions jsonOptions)
    {
        if (baseOptions.Json)
        {
            Console.Out.WriteLine(JsonSerializer.Serialize(new IndexWatchEventJsonResult
            {
                Status = "stopped",
            }, CliJsonSerializerContextFactory.Create(jsonOptions).IndexWatchEventJsonResult));
        }
        else
        {
            CommandErrorWriter.WriteStderr("[watch] Stopped.");
        }
    }

    private static IndexWatchRecoveryCommandJsonResult BuildOverflowRecoveryCommand(IndexCommandOptions baseOptions, string resolvedDbPath, bool redactPaths = false)
    {
        var args = BuildSubRunArgs(baseOptions, resolvedDbPath);
        args.Insert(0, "index");
        if (redactPaths)
            RedactOverflowRecoveryPathArgs(args);
        return new IndexWatchRecoveryCommandJsonResult
        {
            Command = "cdidx",
            Args = args,
        };
    }

    private static void RedactOverflowRecoveryPathArgs(List<string> args)
    {
        if (args.Count > 1)
            args[1] = "[redacted]";

        for (var i = 0; i < args.Count - 1; i++)
        {
            if (string.Equals(args[i], "--db", StringComparison.Ordinal))
                args[i + 1] = "[redacted]";
        }
    }

    private readonly record struct WatchSubRunSummary(
        int? Updated,
        int? Removed,
        int? Errors,
        long? FilesTotal,
        int? FilesScanned,
        int? FilesSkipped,
        int? FilesPurged,
        int? Warnings,
        string ParseStatus,
        string? ParseReason)
    {
        internal static WatchSubRunSummary Unparsed(string parseStatus, string parseReason)
            => new(null, null, null, null, null, null, null, null, parseStatus, parseReason);
    }
}

/// <summary>
/// Thread-safe queue that coalesces FileSystemWatcher events into a single batch once the
/// stream has been quiet for the debounce interval. Extracted for unit testing without
/// touching the filesystem.
/// FileSystemWatcher イベントを debounce 期間の静穏まで蓄積し、まとめてバッチ化するスレッドセーフな
/// キュー。ファイルシステムに触れずユニットテストできるよう分離。
/// </summary>
internal sealed class FileChangeBatcher
{
    internal const int DefaultMaxPendingPaths = IndexWatchRunner.DefaultWatchPendingPathLimit;

    private readonly object _gate = new();
    private readonly HashSet<string> _pending;
    private long _lastEventTimestamp;
    private bool _hasLastEventTimestamp;
    private bool _overflowRequested;
    private string? _overflowReason;
    private readonly TimeSpan _debounce;
    private readonly TimeProvider _timeProvider;
    private readonly int _maxPendingPaths;

    public FileChangeBatcher(
        TimeSpan debounce,
        TimeProvider? timeProvider = null,
        bool ignoreCase = true,
        int maxPendingPaths = DefaultMaxPendingPaths)
    {
        if (maxPendingPaths <= 0)
            throw new ArgumentOutOfRangeException(nameof(maxPendingPaths), "Maximum pending path count must be positive.");

        _debounce = debounce;
        _timeProvider = timeProvider ?? TimeProvider.System;
        _maxPendingPaths = maxPendingPaths;
        // On case-sensitive filesystems (Linux ext4), `foo.py` and `Foo.py` are distinct files,
        // so coalescing them via OrdinalIgnoreCase would drop one rename leg and leave the
        // renamed-to file unindexed. The watch loop passes the filesystem's case sensitivity in.
        // 大小区別する FS (Linux ext4 など) では foo.py と Foo.py が別ファイルになるため、
        // OrdinalIgnoreCase で集約するとリネーム片方が落ち、リネーム先が索引されなくなる。
        _pending = new HashSet<string>(ignoreCase ? StringComparer.OrdinalIgnoreCase : StringComparer.Ordinal);
    }

    public void Add(string path)
    {
        lock (_gate)
        {
            if (_overflowRequested)
            {
                RecordEventTimestampLocked();
                return;
            }

            if (!_pending.Contains(path))
            {
                if (_pending.Count >= _maxPendingPaths)
                {
                    RequestFullRescanLocked(
                        $"pending path limit exceeded ({_maxPendingPaths.ToString("N0", CultureInfo.InvariantCulture)} paths)");
                    return;
                }

                _pending.Add(path);
            }

            RecordEventTimestampLocked();
        }
    }

    public void RequestFullRescan(string? reason = null)
    {
        lock (_gate)
        {
            RequestFullRescanLocked(reason);
        }
    }

    public bool TryDrain(out IReadOnlyList<string> batch, out bool fullRescan, out string? overflowReason)
        => TryDrainCore(requireDebounce: true, out batch, out fullRescan, out overflowReason);

    public bool TryDrainImmediately(out IReadOnlyList<string> batch, out bool fullRescan, out string? overflowReason)
        => TryDrainCore(requireDebounce: false, out batch, out fullRescan, out overflowReason);

    private bool TryDrainCore(
        bool requireDebounce,
        out IReadOnlyList<string> batch,
        out bool fullRescan,
        out string? overflowReason)
    {
        lock (_gate)
        {
            if (_pending.Count == 0 && !_overflowRequested)
            {
                batch = Array.Empty<string>();
                fullRescan = false;
                overflowReason = null;
                return false;
            }

            if (requireDebounce
                && _hasLastEventTimestamp
                && _timeProvider.GetElapsedTime(_lastEventTimestamp) < _debounce)
            {
                batch = Array.Empty<string>();
                fullRescan = false;
                overflowReason = null;
                return false;
            }

            var snapshot = new List<string>(_pending.Count);
            foreach (var path in _pending)
                snapshot.Add(path);
            batch = snapshot;
            fullRescan = _overflowRequested;
            overflowReason = _overflowReason;
            _pending.Clear();
            _overflowRequested = false;
            _overflowReason = null;
            return true;
        }
    }

    private void RequestFullRescanLocked(string? reason)
    {
        _pending.Clear();
        _overflowRequested = true;
        if (!string.IsNullOrEmpty(reason))
            _overflowReason = IndexWatchRunner.FormatWatchDiagnosticText(reason);
        RecordEventTimestampLocked();
    }

    private void RecordEventTimestampLocked()
    {
        _lastEventTimestamp = _timeProvider.GetTimestamp();
        _hasLastEventTimestamp = true;
    }
}

using System.Diagnostics;
using System.Globalization;
using System.Text;
using System.Text.Json;
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
    private const int InternalBufferSize = 64 * 1024;
    private const int PollIntervalMs = 50;
    private const string WatchDiagnosticTruncationMarker = "...[truncated]";

    internal static bool DeleteSpoolFileForTesting(string? spoolPath, Action<string>? deleteOverride = null)
        => DeleteSpoolFile(spoolPath, deleteOverride);

    public static int Run(
        IndexCommandOptions baseOptions,
        JsonSerializerOptions jsonOptions,
        string projectRoot,
        string resolvedDbPath)
    {
        using var cts = new CancellationTokenSource();
        ConsoleCancelEventHandler handler = (_, e) =>
        {
            e.Cancel = true;
            cts.Cancel();
        };
        Console.CancelKeyPress += handler;
        try
        {
            return RunCore(baseOptions, jsonOptions, projectRoot, resolvedDbPath, cts.Token);
        }
        catch (OperationCanceledException) when (cts.IsCancellationRequested)
        {
            return CommandExitCodes.Success;
        }
        finally
        {
            Console.CancelKeyPress -= handler;
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
        CancellationToken cancellationToken)
    {
        var debounce = TimeSpan.FromMilliseconds(baseOptions.WatchDebounceMs ?? DefaultDebounceMs);
        var maxPendingPaths = baseOptions.WatchPendingPathLimit;
        var ignoreCase = GitHelper.ResolveIgnoreCase(projectRoot, cancellationToken);
        var batcher = new FileChangeBatcher(debounce, ignoreCase: ignoreCase, maxPendingPaths: maxPendingPaths);

        var ignoreRuleRoot = GitHelper.TryGetRepositoryRoot(projectRoot, cancellationToken) ?? Path.GetFullPath(projectRoot);
        var fileIndexer = new FileIndexer(projectRoot, ignoreCase, ignoreRuleRoot);
        var watchExitCode = CommandExitCodes.Success;

        FileSystemWatcher? watcher = null;
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
                    // The watcher root encloses .git / .cdidx / build outputs; EvaluatePathFilter
                    // honors .gitignore / .cdidxignore / built-in SkipDirs, so we drop noisy
                    // events at the source instead of paying for a full sub-update every save.
                    // root は .git / .cdidx / ビルド出力も含むため、EvaluatePathFilter で除外して
                    // 余計なサブ更新を防ぐ。
                    var filter = fileIndexer.EvaluatePathFilter(fullPath);
                    if (filter.ShouldSkip)
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

            EmitWatchStarted(baseOptions, projectRoot, resolvedDbPath, debounce, maxPendingPaths);

            while (!cancellationToken.IsCancellationRequested)
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
                    EmitWatchOverflow(baseOptions, overflowReason, resolvedDbPath);
                    RecordSubRunExitCode(ref watchExitCode, RunFullRescan(baseOptions, jsonOptions, resolvedDbPath));
                    continue;
                }

                if (batch.Count == 0)
                    continue;

                RecordSubRunExitCode(ref watchExitCode, RunPartialUpdate(baseOptions, jsonOptions, batch, resolvedDbPath));
            }
        }
        finally
        {
            if (watcher != null)
            {
                try { watcher.EnableRaisingEvents = false; } catch (Exception ex) when (ex is IOException or InvalidOperationException or ObjectDisposedException) { }
                watcher.Dispose();
            }
        }

        EmitWatchStopped(baseOptions);
        return watchExitCode;
    }

    private static int RunPartialUpdate(
        IndexCommandOptions baseOptions,
        JsonSerializerOptions jsonOptions,
        IReadOnlyList<string> changedPaths,
        string resolvedDbPath)
    {
        var baseArgs = BuildSubRunArgs(baseOptions, resolvedDbPath);
        var batches = BuildPartialUpdateBatches(baseArgs, changedPaths);
        if (batches == null)
            return RunFullRescan(baseOptions, jsonOptions, resolvedDbPath);

        var exitCode = CommandExitCodes.Success;
        foreach (var batch in batches)
        {
            var stopwatch = Stopwatch.StartNew();
            var args = new List<string>(baseArgs.Count + 1 + batch.Count);
            args.AddRange(baseArgs);
            args.Add("--files");
            args.AddRange(batch);

            var subRunExitCode = InvokeSubRunAndEmit(baseOptions, jsonOptions, args, stopwatch, "updated", batch.Count, "incremental", batch);
            RecordSubRunExitCode(ref exitCode, subRunExitCode);
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
        string resolvedDbPath)
    {
        var stopwatch = Stopwatch.StartNew();
        var args = BuildSubRunArgs(baseOptions, resolvedDbPath);
        // No --files: this is a default incremental full scan.
        // --files を付けない: 通常のインクリメンタル全件スキャン。
        return InvokeSubRunAndEmit(baseOptions, jsonOptions, args, stopwatch, "rescanned", batchSize: null, "incremental", batchPaths: null);
    }

    private static void RecordSubRunExitCode(ref int watchExitCode, int subRunExitCode)
    {
        if (subRunExitCode != CommandExitCodes.Success)
            watchExitCode = subRunExitCode;
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

    private static int InvokeSubRunAndEmit(
        IndexCommandOptions baseOptions,
        JsonSerializerOptions jsonOptions,
        List<string> args,
        Stopwatch stopwatch,
        string status,
        int? batchSize,
        string phase,
        IReadOnlyList<string>? batchPaths)
    {
        string capturedJson;
        string? spoolPath = null;
        int subRunExitCode;
        var previousOut = Console.Out;
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
            Console.SetOut(captureWriter);
            try
            {
                subRunExitCode = IndexCommandRunner.Run(args.ToArray(), jsonOptions);
            }
            finally
            {
                Console.SetOut(previousOut);
            }

            captureWriter.Flush();
            capturedJson = captureWriter.CapturedText;
        }
        finally
        {
            Console.SetOut(previousOut);
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
            Console.Out.WriteLine(JsonSerializer.Serialize(new IndexWatchEventJsonResult
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
            }, CliJsonSerializerContextFactory.Create(jsonOptions).IndexWatchEventJsonResult));

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
        }

        var detail = details.Count > 0 ? $" ({string.Join(", ", details)})" : string.Empty;
        return $"{prefix}{batchLabel}{detail} in {elapsedMs.ToString("N0", System.Globalization.CultureInfo.InvariantCulture)} ms";
    }

    private static WatchSubRunSummary ParseSubRunSummary(string subRunJson)
    {
        var trimmedLength = TrimTrailingLineBreaks(subRunJson);
        if (trimmedLength == 0)
            return new WatchSubRunSummary(null, null, null, "missing", "sub-run emitted no JSON");

        if (trimmedLength > MaxHumanSummarySubRunJsonChars)
            return new WatchSubRunSummary(null, null, null, "too_large", $"sub-run JSON exceeded {MaxHumanSummarySubRunJsonChars.ToString(CultureInfo.InvariantCulture)} characters");

        try
        {
            using var doc = JsonDocument.Parse(
                subRunJson.AsMemory(0, trimmedLength),
                new JsonDocumentOptions { MaxDepth = MaxHumanSummaryJsonDepth });
            var root = doc.RootElement;
            if (root.ValueKind != JsonValueKind.Object || !root.TryGetProperty("summary", out var summary)
                || summary.ValueKind != JsonValueKind.Object)
            {
                return new WatchSubRunSummary(null, null, null, "missing_summary", "sub-run JSON did not contain an object summary");
            }

            return new WatchSubRunSummary(
                TryReadInt32(summary, "updated") ?? 0,
                TryReadInt32(summary, "removed") ?? 0,
                TryReadInt32(summary, "errors") ?? 0,
                "parsed",
                null);
        }
        catch (JsonException ex)
        {
            return new WatchSubRunSummary(null, null, null, "invalid_json", CommandErrorWriter.FormatSanitizedExceptionMessage(ex));
        }
    }

    private static int? TryReadInt32(JsonElement element, string propertyName)
        => element.TryGetProperty(propertyName, out var property) && property.TryGetInt32(out var value)
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
                sample = Path.GetRelativePath(projectRoot, path);
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
        string projectRoot,
        string resolvedDbPath,
        TimeSpan debounce,
        int maxPendingPaths)
    {
        if (baseOptions.Json)
        {
            var jsonOpts = new JsonSerializerOptions
            {
                PropertyNamingPolicy = JsonNamingPolicy.SnakeCaseLower,
            };
            Console.Out.WriteLine(JsonSerializer.Serialize(new IndexWatchEventJsonResult
            {
                Status = "watching",
                Phase = "initial_scan",
                ProjectRoot = "[redacted]",
                Db = "[redacted]",
                DebounceMs = (int)debounce.TotalMilliseconds,
                WatchPendingPathLimit = maxPendingPaths,
            }, CliJsonSerializerContextFactory.Create(jsonOpts).IndexWatchEventJsonResult));
        }
        else
        {
            CommandErrorWriter.WriteStderr();
            CommandErrorWriter.WriteStderr($"[watch] Watching {projectRoot} for changes (debounce {(int)debounce.TotalMilliseconds} ms, pending path limit {maxPendingPaths.ToString("N0", CultureInfo.InvariantCulture)}). Press Ctrl+C to stop.");
        }
    }

    private static void EmitWatchOverflow(IndexCommandOptions baseOptions, string? reason, string resolvedDbPath)
    {
        var safeReason = FormatWatchDiagnosticText(reason);
        if (baseOptions.Json)
        {
            var jsonOpts = new JsonSerializerOptions
            {
                PropertyNamingPolicy = JsonNamingPolicy.SnakeCaseLower,
            };
            Console.Out.WriteLine(JsonSerializer.Serialize(new IndexWatchEventJsonResult
            {
                Status = "overflow",
                Reason = safeReason,
                Phase = "incremental",
                OverflowReason = safeReason,
                WatchPendingPathLimit = baseOptions.WatchPendingPathLimit,
                RecoveryCommand = BuildOverflowRecoveryCommand(baseOptions, resolvedDbPath, redactPaths: true),
            }, CliJsonSerializerContextFactory.Create(jsonOpts).IndexWatchEventJsonResult));
        }
        else
        {
            var detail = string.IsNullOrEmpty(safeReason) ? string.Empty : $" ({safeReason})";
            CommandErrorWriter.WriteStderr($"[watch] Watcher buffer overflowed{detail}; falling back to full rescan.");
        }
    }

    private static void EmitWatchStopped(IndexCommandOptions baseOptions)
    {
        if (baseOptions.Json)
        {
            var jsonOpts = new JsonSerializerOptions
            {
                PropertyNamingPolicy = JsonNamingPolicy.SnakeCaseLower,
            };
            Console.Out.WriteLine(JsonSerializer.Serialize(new IndexWatchEventJsonResult
            {
                Status = "stopped",
            }, CliJsonSerializerContextFactory.Create(jsonOpts).IndexWatchEventJsonResult));
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
        string ParseStatus,
        string? ParseReason);
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
    private DateTime _lastEventUtc = DateTime.MinValue;
    private bool _overflowRequested;
    private string? _overflowReason;
    private readonly TimeSpan _debounce;
    private readonly Func<DateTime> _clock;
    private readonly int _maxPendingPaths;

    public FileChangeBatcher(
        TimeSpan debounce,
        Func<DateTime>? clock = null,
        bool ignoreCase = true,
        int maxPendingPaths = DefaultMaxPendingPaths)
    {
        if (maxPendingPaths <= 0)
            throw new ArgumentOutOfRangeException(nameof(maxPendingPaths), "Maximum pending path count must be positive.");

        _debounce = debounce;
        _clock = clock ?? (() => DateTime.UtcNow);
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
                _lastEventUtc = _clock();
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

            _lastEventUtc = _clock();
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

            if (_clock() - _lastEventUtc < _debounce)
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
        _lastEventUtc = _clock();
    }
}

using System.Diagnostics;
using System.Globalization;
using System.Text.Json;
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
    private const int InternalBufferSize = 64 * 1024;
    private const int PollIntervalMs = 50;

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
    {
        var debounce = TimeSpan.FromMilliseconds(baseOptions.WatchDebounceMs ?? DefaultDebounceMs);
        var ignoreCase = GitHelper.ResolveIgnoreCase(projectRoot);
        var batcher = new FileChangeBatcher(debounce, ignoreCase: ignoreCase);

        var ignoreRuleRoot = GitHelper.TryGetRepositoryRoot(projectRoot) ?? Path.GetFullPath(projectRoot);
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

            EmitWatchStarted(baseOptions, projectRoot, resolvedDbPath, debounce);

            while (!cancellationToken.IsCancellationRequested)
            {
                try
                {
                    Task.Delay(PollIntervalMs, cancellationToken).GetAwaiter().GetResult();
                }
                catch (OperationCanceledException)
                {
                    break;
                }

                if (!batcher.TryDrain(out var batch, out var fullRescan, out var overflowReason))
                    continue;

                if (fullRescan)
                {
                    EmitWatchOverflow(baseOptions, overflowReason);
                    RecordSubRunExitCode(ref watchExitCode, RunFullRescan(baseOptions, jsonOptions));
                    continue;
                }

                if (batch.Count == 0)
                    continue;

                RecordSubRunExitCode(ref watchExitCode, RunPartialUpdate(baseOptions, jsonOptions, batch));
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
        IReadOnlyList<string> changedPaths)
    {
        var stopwatch = Stopwatch.StartNew();
        var args = BuildSubRunArgs(baseOptions);
        args.Add("--files");
        foreach (var path in changedPaths)
            args.Add(path);

        return InvokeSubRunAndEmit(baseOptions, jsonOptions, args, stopwatch, "updated", changedPaths.Count);
    }

    private static int RunFullRescan(
        IndexCommandOptions baseOptions,
        JsonSerializerOptions jsonOptions)
    {
        var stopwatch = Stopwatch.StartNew();
        var args = BuildSubRunArgs(baseOptions);
        // No --files: this is a default incremental full scan.
        // --files を付けない: 通常のインクリメンタル全件スキャン。
        return InvokeSubRunAndEmit(baseOptions, jsonOptions, args, stopwatch, "rescanned", batchSize: null);
    }

    private static void RecordSubRunExitCode(ref int watchExitCode, int subRunExitCode)
    {
        if (subRunExitCode != CommandExitCodes.Success)
            watchExitCode = subRunExitCode;
    }

    private static List<string> BuildSubRunArgs(IndexCommandOptions baseOptions)
    {
        // Always pass --json so sub-runs produce a single JSON-line summary on stdout. The
        // watch loop then either forwards that line (user --json) or extracts a one-line
        // human summary (user non-JSON). Otherwise each sub-run would reprint the banner.
        // 常に --json を付けてサブ実行の stdout を1行 JSON に揃える。watch ループ側で
        // 透過 or 整形してから出力する。
        var args = new List<string>(8) { baseOptions.ProjectPath!, "--json", "--quiet" };
        if (!string.IsNullOrEmpty(baseOptions.DbPath))
        {
            args.Add("--db");
            args.Add(baseOptions.DbPath!);
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
        if (baseOptions.SymlinkPolicy != FileIndexer.SymlinkPolicy.None)
        {
            args.Add("--follow-symlinks");
            args.Add(baseOptions.SymlinkPolicy.ToString().ToLowerInvariant());
        }
        return args;
    }

    private static int InvokeSubRunAndEmit(
        IndexCommandOptions baseOptions,
        JsonSerializerOptions jsonOptions,
        List<string> args,
        Stopwatch stopwatch,
        string status,
        int? batchSize)
    {
        string capturedJson;
        int subRunExitCode;
        var previousOut = Console.Out;
        using var captureWriter = new StringWriter();
        Console.SetOut(captureWriter);
        try
        {
            subRunExitCode = IndexCommandRunner.Run(args.ToArray(), jsonOptions);
        }
        finally
        {
            Console.SetOut(previousOut);
        }
        stopwatch.Stop();
        capturedJson = captureWriter.ToString();
        var eventStatus = subRunExitCode == CommandExitCodes.Success ? status : "failed";
        var failureReason = subRunExitCode == CommandExitCodes.Success
            ? null
            : $"{status} sub-run exited with code {subRunExitCode.ToString(CultureInfo.InvariantCulture)}";

        if (baseOptions.Json)
        {
            // Pre-pend a watch-event header line so MCP clients can distinguish watch
            // batches from the initial scan. The underlying sub-run result follows.
            // watch バッチであることを示すヘッダ行を先頭に流し、その後にサブ実行 JSON を出す。
            Console.Out.WriteLine(JsonSerializer.Serialize(new IndexWatchEventJsonResult
            {
                Status = eventStatus,
                BatchSize = batchSize,
                ElapsedMs = stopwatch.ElapsedMilliseconds,
                ExitCode = subRunExitCode,
                Reason = failureReason,
            }, CliJsonSerializerContextFactory.Create(jsonOptions).IndexWatchEventJsonResult));

            var trimmed = capturedJson.TrimEnd('\r', '\n');
            if (!string.IsNullOrEmpty(trimmed))
                Console.Out.WriteLine(trimmed);
        }
        else
        {
            var human = FormatHumanSummary(eventStatus, batchSize, stopwatch.ElapsedMilliseconds, capturedJson, subRunExitCode);
            Console.Error.WriteLine(human);
        }

        return subRunExitCode;
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
        try
        {
            var trimmed = subRunJson.TrimEnd('\r', '\n');
            if (!string.IsNullOrEmpty(trimmed))
            {
                using var doc = JsonDocument.Parse(trimmed);
                var root = doc.RootElement;
                if (root.ValueKind == JsonValueKind.Object && root.TryGetProperty("summary", out var summary)
                    && summary.ValueKind == JsonValueKind.Object)
                {
                    int updated = summary.TryGetProperty("updated", out var u) && u.TryGetInt32(out var uv) ? uv : 0;
                    int removed = summary.TryGetProperty("removed", out var r) && r.TryGetInt32(out var rv) ? rv : 0;
                    int errors = summary.TryGetProperty("errors", out var er) && er.TryGetInt32(out var erv) ? erv : 0;
                    details.Add($"updated {updated}");
                    details.Add($"removed {removed}");
                    details.Add($"errors {errors}");
                }
            }
        }
        catch (JsonException)
        {
        }

        var detail = details.Count > 0 ? $" ({string.Join(", ", details)})" : string.Empty;
        return $"{prefix}{batchLabel}{detail} in {elapsedMs.ToString("N0", System.Globalization.CultureInfo.InvariantCulture)} ms";
    }

    private static void EmitWatchStarted(
        IndexCommandOptions baseOptions,
        string projectRoot,
        string resolvedDbPath,
        TimeSpan debounce)
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
                ProjectRoot = projectRoot,
                Db = resolvedDbPath,
                DebounceMs = (int)debounce.TotalMilliseconds,
            }, CliJsonSerializerContextFactory.Create(jsonOpts).IndexWatchEventJsonResult));
        }
        else
        {
            Console.Error.WriteLine();
            Console.Error.WriteLine($"[watch] Watching {projectRoot} for changes (debounce {(int)debounce.TotalMilliseconds} ms). Press Ctrl+C to stop.");
        }
    }

    private static void EmitWatchOverflow(IndexCommandOptions baseOptions, string? reason)
    {
        if (baseOptions.Json)
        {
            var jsonOpts = new JsonSerializerOptions
            {
                PropertyNamingPolicy = JsonNamingPolicy.SnakeCaseLower,
            };
            Console.Out.WriteLine(JsonSerializer.Serialize(new IndexWatchEventJsonResult
            {
                Status = "overflow",
                Reason = reason,
            }, CliJsonSerializerContextFactory.Create(jsonOpts).IndexWatchEventJsonResult));
        }
        else
        {
            var detail = string.IsNullOrEmpty(reason) ? string.Empty : $" ({reason})";
            Console.Error.WriteLine($"[watch] Watcher buffer overflowed{detail}; falling back to full rescan.");
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
            Console.Error.WriteLine("[watch] Stopped.");
        }
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
    internal const int DefaultMaxPendingPaths = 4096;

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
            _overflowReason = reason;
        _lastEventUtc = _clock();
    }
}

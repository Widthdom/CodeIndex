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
internal static partial class IndexWatchRunner
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

}

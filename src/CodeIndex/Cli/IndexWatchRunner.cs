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
        CancellationToken cancellationToken,
        Func<int>? baselineScan = null)
    {
        try
        {
            return RunCore(baseOptions, jsonOptions, projectRoot, resolvedDbPath, cancellationToken, baselineScan);
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
        CancellationToken cancellationToken,
        Func<int>? baselineScan = null)
        // Preserve the existing synchronous CLI/test entry point while the watch loop itself
        // runs through RunCoreAsync so cancellation does not block on Task.Delay.
        => RunCoreAsync(
                baseOptions,
                jsonOptions,
                projectRoot,
                resolvedDbPath,
                cancellationToken,
                baselineScan: baselineScan)
            .GetAwaiter()
            .GetResult();

    internal static async Task<int> RunCoreAsync(
        IndexCommandOptions baseOptions,
        JsonSerializerOptions jsonOptions,
        string projectRoot,
        string resolvedDbPath,
        CancellationToken cancellationToken,
        Action<Action<string>>? startupHandoff = null,
        Func<int>? baselineScan = null,
        Func<string, int>? recoveryScan = null)
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

        var baselineStateGate = new object();
        var baselineState = 0; // 0 = not started, 1 = running, 2 = complete
        Exception? startupBackendError = null;
        IWatchBackend? backend = null;
        string? backendName = null;
        string? startupRecoveryReason = null;
        try
        {
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

            void ReportBackendError(Exception? exception)
            {
                lock (baselineStateGate)
                {
                    if (baselineState == 0)
                    {
                        startupBackendError ??= exception
                            ?? new IOException("watch backend reported an unspecified startup error");
                        return;
                    }
                }

                // Once the baseline starts, an overflow/error creates an uncertainty window.
                // Collapse any number of callbacks into one justified recovery generation.
                // baseline 開始後の overflow/error は不確実区間を作る。複数 callback が来ても
                // justified recovery generation 1 回へ集約する。
                var recoveryReason = exception is InternalBufferOverflowException
                    ? "event_stream_overflow"
                    : "backend_error";
                batcher.RequestFullRescan(exception?.Message, recoveryReason);
            }

            for (var attempt = 0; attempt < 2 && !cancellationToken.IsCancellationRequested; attempt++)
            {
                startupBackendError = null;
                backend = CreateWatchBackend(projectRoot, ignoreRuleRoot, ignoreCase);
                backendName = backend.Name;
                try
                {
                    backend.Start(Enqueue, ReportBackendError);
                }
                catch (Exception ex) when (IsRecoverableWatchBackendStartException(ex))
                {
                    startupBackendError = ex;
                }

                Exception? startFailure;
                lock (baselineStateGate)
                {
                    startFailure = startupBackendError;
                    if (startFailure == null)
                        baselineState = 1;
                }

                if (startFailure == null)
                    break;

                startupRecoveryReason = "backend_start_failed";
                var detail = CommandErrorWriter.FormatSanitizedExceptionMessage(startFailure);
                backend.Dispose();
                backend = null;

                if (attempt == 0)
                {
                    EmitWatchBackendFallback(
                        baseOptions,
                        jsonOptions,
                        backendName,
                        startupRecoveryReason,
                        detail);
                    continue;
                }

                EmitWatchBackendFailure(
                    baseOptions,
                    jsonOptions,
                    backendName,
                    startupRecoveryReason,
                    detail);
                watchExitCode = CommandExitCodes.RuntimeError;
            }

            if (backend != null && !cancellationToken.IsCancellationRequested)
            {
                startupHandoff?.Invoke(Enqueue);

                var baselineExitCode = baselineScan != null
                    ? baselineScan()
                    : File.Exists(DbPathResolver.NormalizeDbPath(resolvedDbPath))
                        ? RunFullRescan(baseOptions, jsonOptions, resolvedDbPath, cancellationToken, phase: "startup")
                        : CommandExitCodes.Success;
                RecordSubRunExitCode(ref watchExitCode, baselineExitCode);
                lock (baselineStateGate)
                    baselineState = 2;

                // Close the startup generation by taking one atomic snapshot. Events arriving
                // after this boundary remain queued as ordinary live updates, so a continuously
                // changing workspace cannot prevent the watcher from ever becoming ready.
                // startup generation は atomic snapshot で閉じる。この境界後の event は通常の
                // live update として queue に残し、変更が連続する workspace でも ready を妨げない。
                if (watchExitCode == CommandExitCodes.Success
                    && !cancellationToken.IsCancellationRequested
                    && batcher.TryDrainImmediately(
                        out var startupBatch,
                        out var startupFullRescan,
                        out var startupRecoveryScanReason,
                        out var startupOverflowReason))
                {
                    if (startupFullRescan)
                    {
                        EmitWatchOverflow(
                            baseOptions,
                            jsonOptions,
                            startupOverflowReason,
                            resolvedDbPath,
                            phase: "startup",
                            backend: backendName,
                            recoveryReason: startupRecoveryScanReason);
                        RecordSubRunExitCode(
                            ref watchExitCode,
                            recoveryScan?.Invoke("startup")
                                ?? RunFullRescan(baseOptions, jsonOptions, resolvedDbPath, cancellationToken, phase: "startup"));
                    }
                    else if (startupBatch.Count > 0)
                    {
                        RecordSubRunExitCode(
                            ref watchExitCode,
                            RunPartialUpdate(baseOptions, jsonOptions, startupBatch, resolvedDbPath, cancellationToken, phase: "startup"));
                    }
                }
            }

            var ready = !cancellationToken.IsCancellationRequested
                && watchExitCode == CommandExitCodes.Success;
            if (ready)
            {
                EmitWatchStarted(
                    baseOptions,
                    jsonOptions,
                    projectRoot,
                    resolvedDbPath,
                    debounce,
                    maxPendingPaths,
                    ignoreCase,
                    backendName,
                    startupRecoveryReason);
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

                if (!batcher.TryDrain(
                        out var batch,
                        out var fullRescan,
                        out var recoveryReason,
                        out var overflowReason))
                    continue;

                if (fullRescan)
                {
                    EmitWatchOverflow(
                        baseOptions,
                        jsonOptions,
                        overflowReason,
                        resolvedDbPath,
                        phase: "incremental",
                        backend: backendName,
                        recoveryReason: recoveryReason);
                    RecordSubRunExitCode(
                        ref watchExitCode,
                        recoveryScan?.Invoke("incremental")
                            ?? RunFullRescan(baseOptions, jsonOptions, resolvedDbPath, cancellationToken));
                    continue;
                }

                if (batch.Count == 0)
                    continue;

                RecordSubRunExitCode(ref watchExitCode, RunPartialUpdate(baseOptions, jsonOptions, batch, resolvedDbPath, cancellationToken));
            }
        }
        finally
        {
            backend?.Dispose();
        }

        EmitWatchStopped(baseOptions, jsonOptions);
        return watchExitCode;
    }

    private static bool IsRecoverableWatchBackendStartException(Exception exception)
        => exception is IOException
            or UnauthorizedAccessException
            or InvalidOperationException
            or ArgumentException
            or NotSupportedException;
}

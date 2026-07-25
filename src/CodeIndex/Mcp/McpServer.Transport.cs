using System.Buffers.Binary;
using System.Collections.Concurrent;
using System.Diagnostics;
using System.Globalization;
using System.Runtime.InteropServices;
using System.Security.Cryptography;
using System.Text;
using System.Text.Json;
using System.Text.Json.Nodes;
using System.Text.Json.Serialization.Metadata;
using CodeIndex.Cli;
using CodeIndex.Database;
using CodeIndex.Diagnostics;
using CodeIndex.Indexer;
using CodeIndex.Indexer.Extensibility;

namespace CodeIndex.Mcp;

public partial class McpServer : IDisposable
{
    /// <summary>
    /// Run the MCP server loop on the default stdio transport. Kept as a thin wrapper around
    /// <see cref="RunAsync(IMcpTransport, CancellationToken)"/> so existing callers stay
    /// source-compatible after the #1558 transport refactor. SIGINT (Ctrl+C) and SIGTERM are
    /// translated into loop cancellation so orchestrators (systemd, launchd, supervisord) can
    /// achieve a clean shutdown instead of hanging until stdin closes (#1573).
    /// 既定の stdio トランスポートで MCP ループを動かす。#1558 のトランスポート抽象化後も
    /// 既存呼び出しがソース互換となるよう <see cref="RunAsync(IMcpTransport, CancellationToken)"/>
    /// のラッパとして残す。SIGINT (Ctrl+C) と SIGTERM をループキャンセルに変換し、stdin が閉じる
    /// まで固まる旧挙動を解消する（systemd / launchd / supervisord から graceful shutdown 可能に, #1573）。
    /// </summary>
    public async Task RunAsync()
    {
        await using var transport = new StdioMcpTransport(StdioBufferSize);
        using var cts = new CancellationTokenSource();
        using (RegisterShutdownHandlers(cts))
        {
            await RunAsync(transport, cts.Token).ConfigureAwait(false);
        }
    }

    /// <summary>
    /// Register cross-platform SIGINT (Ctrl+C) and SIGTERM handlers that cancel <paramref name="cts"/>
    /// so orchestrator-driven shutdowns drain the loop cleanly instead of leaving the MCP process
    /// hung on stdin or force-killed mid-iteration (#1573). The returned IDisposable removes the
    /// handlers; dispose it before disposing the CTS to avoid races between a late signal and CTS
    /// teardown.
    /// SIGINT (Ctrl+C) と SIGTERM を `cts` のキャンセルに変換するクロスプラットフォームハンドラを登録する
    /// （#1573）。返り値の IDisposable でハンドラを解除する。late signal と CTS 破棄の競合を避けるため、
    /// CTS の Dispose より先にこれを Dispose する。
    /// </summary>
    internal static IDisposable RegisterShutdownHandlers(CancellationTokenSource cts)
    {
        ArgumentNullException.ThrowIfNull(cts);

        ConsoleCancelEventHandler cancelHandler = (_, e) =>
        {
            if (cts.IsCancellationRequested)
                return;
            // Honour the signal without letting the .NET runtime terminate the process before
            // the loop has a chance to drain and dispose the shared DbContext.
            // .NET runtime の即時終了を抑え、ループが DbContext を片付ける猶予を確保する。
            e.Cancel = true;
            try { cts.Cancel(); }
            catch (ObjectDisposedException) { /* signal raced disposal — nothing to cancel. */ }
        };
        Console.CancelKeyPress += cancelHandler;

        PosixSignalRegistration? sigtermRegistration = null;
        try
        {
            sigtermRegistration = PosixSignalRegistration.Create(PosixSignal.SIGTERM, ctx =>
            {
                if (cts.IsCancellationRequested)
                    return;
                ctx.Cancel = true;
                try { cts.Cancel(); }
                catch (ObjectDisposedException) { /* see CancelKeyPress branch. */ }
            });
        }
        catch (PlatformNotSupportedException)
        {
            // PosixSignal.SIGTERM is supported on net8.0 across Windows/Linux/macOS, but a future
            // niche runtime might not implement it. Console.CancelKeyPress still covers Ctrl+C
            // everywhere, so degrade silently rather than refusing to start.
            // .NET 8 では SIGTERM がクロスプラットフォーム対応だが、将来の特殊ランタイムで未対応の
            // 可能性に備え、Console.CancelKeyPress による Ctrl+C カバレッジを残してサイレントに縮退する。
        }

        return new ShutdownHandlerRegistration(cancelHandler, sigtermRegistration);
    }

    private sealed class ShutdownHandlerRegistration : IDisposable
    {
        private ConsoleCancelEventHandler? _cancelHandler;
        private PosixSignalRegistration? _sigterm;

        public ShutdownHandlerRegistration(ConsoleCancelEventHandler cancelHandler, PosixSignalRegistration? sigterm)
        {
            _cancelHandler = cancelHandler;
            _sigterm = sigterm;
        }

        public void Dispose()
        {
            var handler = Interlocked.Exchange(ref _cancelHandler, null);
            if (handler != null)
                Console.CancelKeyPress -= handler;
            var sigterm = Interlocked.Exchange(ref _sigterm, null);
            sigterm?.Dispose();
        }
    }

    /// <summary>
    /// Run the MCP server loop on the supplied transport (issue #1558). Base transports use one
    /// read followed by one write; concurrent-capable transports bind a response writer to each
    /// frame. Notifications write null and end-of-stream terminates the loop.
    /// 指定トランスポート上で MCP ループを動かす (issue #1558)。基本 transport は「読み 1 回 →
    /// 書き 1 回」、並行対応 transport は frame ごとに response writer を紐付ける。通知は null を
    /// 書き、EOS でループを終える。
    /// </summary>
    internal async Task RunAsync(IMcpTransport transport, CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(transport);
        _enforceInitializationLifecycle = true;
        Volatile.Write(
            ref _activeTransportMaxResponseBytes,
            transport is IMcpResponseSizeLimitProvider responseLimitProvider
                ? responseLimitProvider.MaxResponseFrameBytes
                : 0);

        // Link the caller-supplied token (Ctrl+C / HTTP listener stop) with the server-internal
        // shutdown signal so `notifications/shutdown` also wakes any pending `ReadFrameAsync`.
        // The MCP spec leaves shutdown to the transport, but real deployments need a wire-level
        // way to drain in-flight work without killing the process (#1567).
        // Ctrl+C 等の外部 token と内部 shutdown signal をリンクし、`notifications/shutdown` でも
        // pending な `ReadFrameAsync` を unblock できるようにする (#1567)。
        using var linkedCts = CancellationTokenSource.CreateLinkedTokenSource(cancellationToken, _shutdownCts.Token);
        var loopToken = linkedCts.Token;

        // Use stderr for logging so stdout stays clean for JSON-RPC
        // stdoutをJSON-RPC用にクリーンに保つため、ログはstderrに出力
        ConsoleUi.TryWriteErrorLine($"[cdidx-mcp] Starting MCP server v{_version} (db: {FormatDbPathForLog(_dbPath)}, transport: {transport.Name} @ {transport.Endpoint}, max in-flight: {MaxConcurrency})");

        if (transport is HttpMcpTransport httpTransport)
        {
            httpTransport.OutOfBandFrameHandler = (frame, _) => ProcessFrameAsync(frame);
            httpTransport.HealthJsonProvider = () => BuildHealthJson(httpTransport);
            httpTransport.KeepAliveInterval = _keepAliveInterval;
            httpTransport.KeepAliveFrameProvider = BuildKeepAliveNotificationJson;
        }

        try
        {
            if (string.Equals(transport.Name, "stdio", StringComparison.OrdinalIgnoreCase)
                || transport is IConcurrentMcpTransport)
            {
                await RunConcurrentFrameLoopAsync(transport, loopToken, cancellationToken).ConfigureAwait(false);
                return;
            }

            Task? terminalTransportWriteTask = null;
            try
            {
                while (_running)
                {
                    // The full read/process/write iteration is wrapped in the same cancellation guard so
                    // a Ctrl+C that lands mid-iteration (e.g. while WriteFrameAsync is flushing) still
                    // exits the loop cleanly instead of bubbling OperationCanceledException out of the
                    // server and past ProgramRunner.RunMcpHttp's graceful-shutdown handler.
                    // Ctrl+C が WriteFrameAsync flush 中に来ても OperationCanceledException を呼び元に
                    // 漏らさず正常終了するよう、read/process/write 全体を同じ cancellation guard で囲む。
                    try
                    {
                        var frame = await transport.ReadFrameAsync(loopToken).ConfigureAwait(false);
                        if (frame == null)
                            break; // transport closed / トランスポートが閉じられた

                        string? response;
                        try
                        {
                            // Hand the per-request token to `WithDbReader` so SQLite work the tool kicks
                            // off can observe shutdown / client-disconnect cancellation through
                            // `DbReader.Cancellation` (#1567).
                            // ツールが起動する SQLite 作業が shutdown / 切断を観測できるよう per-request
                            // token を `WithDbReader` に渡す (#1567)。
                            _currentRequestToken.Value = loopToken;
                            _currentOutOfBandFrameWriter.Value = transport is IOutOfBandMcpTransport outOfBandTransport
                                ? (frameToWrite, writeToken) => outOfBandTransport.WriteOutOfBandFrameAsync(frameToWrite, writeToken)
                                : null;
                            _canAwaitClientResponses.Value = transport is IOutOfBandMcpTransport
                                && (transport is not HttpMcpTransport httpResponseTransport || httpResponseTransport.HasEventStreams);
                            BeginDeferredFrameLogs();
                            response = await ProcessFrameAsync(frame).ConfigureAwait(false);
                        }
                        finally
                        {
                            _currentRequestToken.Value = CancellationToken.None;
                            _currentOutOfBandFrameWriter.Value = null;
                            _canAwaitClientResponses.Value = false;
                        }

                        // Internal shutdown cancels `loopToken` to stop reads and request actions, but
                        // the initiating notification still owns one transport completion (HTTP 204).
                        // Use only the caller token for that completion; bounded teardown below still
                        // limits a writer that does not finish (#4543).
                        // internal shutdown では read/action 用 loopToken を cancel するが、起点の
                        // notification に対応する transport completion (HTTP 204) は完了させる。
                        // write は caller token のみを使い、停止しない writer は下の bounded teardown
                        // で制限する (#4543)。
                        var responseWriteTask = WriteFrameSafelyAsync(transport, response, cancellationToken);
                        if (!_running)
                        {
                            // Do not await an uncooperative base-transport shutdown completion inline:
                            // the common finally must own its bounded deadline (#4543).
                            // 応答しない base transport の shutdown completion を inline await せず、
                            // common finally の bounded deadline に委ねる (#4543)。
                            terminalTransportWriteTask = responseWriteTask;
                            break;
                        }

                        await responseWriteTask.ConfigureAwait(false);
                        FlushDeferredFrameLogs();

                        // `notifications/shutdown` flips `_running` inside `HandleMessage`; exit the loop
                        // immediately so a subsequent slow `ReadFrameAsync` does not extend the lifetime
                        // of a server that has been asked to stop.
                        // `notifications/shutdown` が `_running` を倒した直後にループを抜ける (#1567)。
                        if (!_running)
                            break;
                    }
                    catch (OperationCanceledException) when (loopToken.IsCancellationRequested)
                    {
                        break;
                    }
                    catch (DecoderFallbackException ex)
                    {
                        BeginDeferredFrameLogs();
                        terminalTransportWriteTask = WriteTerminalProtocolErrorAsync(
                            writeGate: null,
                            transport,
                            BuildInvalidUtf8ParseErrorResponse(ex),
                            cancellationToken);
                        break;
                    }
                    catch (BoundedLineLengthException ex)
                    {
                        BeginDeferredFrameLogs();
                        terminalTransportWriteTask = WriteTerminalProtocolErrorAsync(
                            writeGate: null,
                            transport,
                            BuildOversizedLineErrorResponse(ex),
                            cancellationToken);
                        break;
                    }
                }
            }
            finally
            {
                // Base transports have no detached request list, but shutdown cancellation
                // callbacks and malformed-input writes still participate in the same bounded
                // teardown contract as concurrent transports (#4543).
                // base transport に detached request list は無いが、shutdown callback と
                // malformed-input write は concurrent transport と同じ bounded teardown
                // 契約へ必ず流す (#4543)。
                await DrainInFlightTasksAsync(
                    [],
                    InFlightDrainGracePeriod,
                    InFlightPostCancelGracePeriod,
                    cancellationToken,
                    terminalTransportWriteTask).ConfigureAwait(false);
                FlushDeferredFrameLogs();
            }
        }
        finally
        {
            Volatile.Write(ref _activeTransportMaxResponseBytes, 0);
            if (transport is HttpMcpTransport httpTransportToClear)
            {
                httpTransportToClear.OutOfBandFrameHandler = null;
                httpTransportToClear.HealthJsonProvider = null;
                httpTransportToClear.KeepAliveInterval = null;
                httpTransportToClear.KeepAliveFrameProvider = null;
            }
        }

        CommandErrorWriter.WriteStderr("[cdidx-mcp] Server stopped. Restart `cdidx mcp` when your client reconnects.");
    }

    private async Task RunConcurrentFrameLoopAsync(
        IMcpTransport transport,
        CancellationToken loopToken,
        CancellationToken externalCancellationToken)
    {
        var state = new ConcurrentFrameLoopState(this, transport, loopToken, externalCancellationToken);

        try
        {
            while (_running)
            {
                PruneCompletedRequestTasks(state.Tasks);
                McpTransportFrame? transportFrame;
                try
                {
                    transportFrame = await ReadConcurrentTransportFrameAsync(transport, loopToken).ConfigureAwait(false);
                }
                catch (OperationCanceledException) when (loopToken.IsCancellationRequested)
                {
                    break;
                }
                catch (DecoderFallbackException ex)
                {
                    state.ScheduleTerminalProtocolError(BuildInvalidUtf8ParseErrorResponse(ex));
                    break;
                }
                catch (BoundedLineLengthException ex)
                {
                    state.ScheduleTerminalProtocolError(BuildOversizedLineErrorResponse(ex));
                    break;
                }
                if (transportFrame is null)
                    break;

                if (await TryProcessInlineConcurrentFrameAsync(state, transportFrame).ConfigureAwait(false))
                    continue;

                // Accepted frames are bounded independently from executing operations. The request
                // registers its id/cancellation state before awaiting protocol predecessors and the
                // execution gate, so a cancellation cannot expire while queued (#4536).
                // accepted frame と executing operation は別々に上限化する。request は protocol
                // predecessor / execution gate を待つ前に id と cancellation state を登録するため、
                // queue 中に cancellation が失効しない (#4536)。
                await StartAcceptedConcurrentFrameAsync(state, transportFrame).ConfigureAwait(false);
            }
        }
        catch (OperationCanceledException) when (loopToken.IsCancellationRequested)
        {
            // Every loop exit, including cancellation during inline control/overload writes,
            // reaches the bounded drain in finally (#4543).
        }
        finally
        {
            await state.DrainAndScheduleGateDisposalAsync().ConfigureAwait(false);
        }
        CommandErrorWriter.WriteStderr("[cdidx-mcp] Server stopped. Restart `cdidx mcp` when your client reconnects.");
    }

    private static async Task DisposeConcurrentLoopGatesAfterAsync(
        Task transportWork,
        SemaphoreSlim writeGate,
        SemaphoreSlim admissionGate)
    {
        try
        {
            await transportWork.ConfigureAwait(false);
        }
        catch
        {
            // Request faults are reported by the bounded drain; gate cleanup must still run.
        }
        finally
        {
            writeGate.Dispose();
            admissionGate.Dispose();
        }
    }

    private static async Task ObserveDetachedIsolatedActionsAsync(Task[] actions)
    {
        try
        {
            await Task.WhenAll(actions).ConfigureAwait(false);
        }
        catch
        {
            // Dispatch cleanup observes each action and owns its diagnostics. This aggregate is
            // only a transport resource-lifetime signal and must always settle successfully.
            // 各 action の例外と診断は dispatch cleanup が所有する。この aggregate は transport
            // resource lifetime の signal に限るため、常に正常完了させる。
            foreach (var action in actions)
            {
                if (action.IsFaulted)
                    _ = action.Exception;
            }
        }
    }

    internal static int PruneCompletedRequestTasks(List<Task> tasks)
    {
        var removed = 0;
        for (var i = tasks.Count - 1; i >= 0; i--)
        {
            var task = tasks[i];
            if (!task.IsCompleted)
                continue;

            ObserveCompletedRequestTask(task);
            tasks.RemoveAt(i);
            removed++;
        }

        return removed;
    }

    private static void ObserveCompletedRequestTask(Task task)
    {
        if (!task.IsFaulted)
            return;

        try
        {
            task.GetAwaiter().GetResult();
        }
        catch (Exception ex)
        {
            CommandErrorWriter.WriteStderr($"[cdidx-mcp] In-flight request ended during transport teardown ({ex.GetType().Name}).");
        }
    }

    private static async Task AwaitProtocolPredecessorsAsync(
        IReadOnlyCollection<Task> predecessors,
        CancellationToken cancellationToken)
    {
        if (predecessors.Count == 0)
            return;

        try
        {
            await Task.WhenAll(predecessors).WaitAsync(cancellationToken).ConfigureAwait(false);
        }
        catch (OperationCanceledException) when (cancellationToken.IsCancellationRequested)
        {
            throw;
        }
        catch
        {
            // A predecessor owns its own wire response and is observed by task pruning. An
            // unrelated fault must not permanently wedge the ordered session lane (#4536).
            // predecessor の fault は個別 response と task pruning で観測する。無関係な fault
            // により ordered session lane を永続停止させない (#4536)。
        }
    }

    private async Task WriteTerminalProtocolErrorAsync(
        SemaphoreSlim? writeGate,
        IMcpTransport transport,
        string response,
        CancellationToken cancellationToken)
    {
        var gateAcquired = false;
        try
        {
            if (writeGate is not null)
            {
                await writeGate.WaitAsync(cancellationToken).ConfigureAwait(false);
                gateAcquired = true;
            }

            await WriteFrameSafelyAsync(transport, response, cancellationToken).ConfigureAwait(false);
            FlushDeferredFrameLogs();
        }
        finally
        {
            if (gateAcquired)
                writeGate!.Release();
        }
    }

    internal async Task DrainInFlightTasksAsync(
        List<Task> tasks,
        TimeSpan gracePeriod,
        TimeSpan postCancelGracePeriod,
        CancellationToken externalCancellationToken = default,
        Task? terminalTransportWriteTask = null)
    {
        PruneCompletedRequestTasks(tasks);
        var shutdownCancellationTask = GetShutdownCancellationTask();
        var drainOperations = BuildDrainOperationsTask(tasks, terminalTransportWriteTask);

        // A shutdown notification may already have started cancellation before EOF reached this
        // method. In that case the post-cancel deadline begins immediately and includes callback
        // completion; running another pre-cancel grace window would extend teardown incorrectly.
        // shutdown notification が EOF より先に cancellation を開始済みなら、callback 完了も
        // post-cancel deadline に含め、pre-cancel grace を重ねない (#4543)。
        if (shutdownCancellationTask is not null)
        {
            await AwaitPostCancellationDrainAsync(
                tasks,
                drainOperations,
                terminalTransportWriteTask,
                shutdownCancellationTask,
                postCancelGracePeriod,
                externalCancellationToken).ConfigureAwait(false);
            return;
        }

        if (drainOperations.IsCompleted)
        {
            await ObserveCompletedDrainAndShutdownAsync(
                tasks,
                drainOperations,
                terminalTransportWriteTask,
                postCancelGracePeriod,
                externalCancellationToken).ConfigureAwait(false);
            return;
        }

        var graceDelay = Task.Delay(gracePeriod, externalCancellationToken);
        var completed = await Task.WhenAny(drainOperations, graceDelay).ConfigureAwait(false);
        if (completed == drainOperations)
        {
            await ObserveCompletedDrainAndShutdownAsync(
                tasks,
                drainOperations,
                terminalTransportWriteTask,
                postCancelGracePeriod,
                externalCancellationToken).ConfigureAwait(false);
            return;
        }
        if (graceDelay.IsCanceled)
        {
            ObserveLateInFlightTasks(drainOperations);
            return;
        }

        PruneCompletedRequestTasks(tasks);
        if (tasks.Count > 0)
        {
            CommandErrorWriter.WriteStderr(
                $"[cdidx-mcp] Transport teardown has {tasks.Count} in-flight request(s); cancelling after {gracePeriod.TotalMilliseconds:0}ms grace period.");
        }
        if (terminalTransportWriteTask is { IsCompleted: false })
        {
            CommandErrorWriter.WriteStderr(
                $"[cdidx-mcp] Transport response/completion write is still pending after {gracePeriod.TotalMilliseconds:0}ms grace period; cancelling transport teardown.");
        }

        shutdownCancellationTask = RequestShutdownCancellation();
        await AwaitPostCancellationDrainAsync(
            tasks,
            drainOperations,
            terminalTransportWriteTask,
            shutdownCancellationTask,
            postCancelGracePeriod,
            externalCancellationToken).ConfigureAwait(false);
    }

    private async Task ObserveCompletedDrainAndShutdownAsync(
        List<Task> tasks,
        Task drainOperations,
        Task? terminalTransportWriteTask,
        TimeSpan postCancelGracePeriod,
        CancellationToken externalCancellationToken)
    {
        await ObserveInFlightTasksAsync(drainOperations).ConfigureAwait(false);

        // A queued shutdown frame can start cancellation while the request drain is completing.
        // Re-read the task after all accepted work has finished; the original snapshot may have
        // been null even though a slow cancellation callback is now running (#4543).
        // queued shutdown frame は request drain 完了直前に cancellation を開始できるため、accepted
        // work 完了後に task を再取得する。初回 snapshot が null でも slow callback が実行中の
        // race を bounded post-cancel deadline へ含める (#4543)。
        var shutdownCancellationTask = GetShutdownCancellationTask();
        if (shutdownCancellationTask is null)
            return;

        await AwaitPostCancellationDrainAsync(
            tasks,
            drainOperations,
            terminalTransportWriteTask,
            shutdownCancellationTask,
            postCancelGracePeriod,
            externalCancellationToken).ConfigureAwait(false);
    }

    private static Task BuildDrainOperationsTask(IReadOnlyCollection<Task> tasks, Task? terminalTransportWriteTask)
    {
        if (terminalTransportWriteTask is null)
            return tasks.Count == 0 ? Task.CompletedTask : Task.WhenAll(tasks);

        var operations = new Task[tasks.Count + 1];
        var operationIndex = 0;
        foreach (var task in tasks)
            operations[operationIndex++] = task;
        operations[^1] = terminalTransportWriteTask;
        return Task.WhenAll(operations);
    }

    private async Task AwaitPostCancellationDrainAsync(
        List<Task> tasks,
        Task drainOperations,
        Task? terminalTransportWriteTask,
        Task shutdownCancellationTask,
        TimeSpan postCancelGracePeriod,
        CancellationToken externalCancellationToken)
    {
        // Internal shutdown cancels the linked loop token. Use the original caller token so it
        // cannot collapse this deadline, while Ctrl+C/SIGTERM/transport cancellation can still
        // interrupt it (#3400, #4543).
        // internal shutdown では post-cancel deadline を潰さず、外部 cancellation では中断可能にする。
        var postCancelWork = Task.WhenAll(drainOperations, shutdownCancellationTask);
        var postCancelDelay = Task.Delay(postCancelGracePeriod, externalCancellationToken);
        var completed = await Task.WhenAny(postCancelWork, postCancelDelay).ConfigureAwait(false);
        if (completed == postCancelWork)
        {
            await ObserveInFlightTasksAsync(drainOperations).ConfigureAwait(false);
            _ = shutdownCancellationTask.Exception;
            return;
        }
        if (postCancelDelay.IsCanceled)
        {
            ObserveLateInFlightTasks(postCancelWork);
            return;
        }

        PruneCompletedRequestTasks(tasks);
        if (tasks.Count > 0)
        {
            CommandErrorWriter.WriteStderr(
                $"[cdidx-mcp] Transport teardown final deadline expired with {tasks.Count} in-flight request(s) remaining after {postCancelGracePeriod.TotalMilliseconds:0}ms post-cancel grace period.");
        }
        if (terminalTransportWriteTask is { IsCompleted: false })
        {
            CommandErrorWriter.WriteStderr(
                $"[cdidx-mcp] Transport response/completion write is still pending after {postCancelGracePeriod.TotalMilliseconds:0}ms post-cancel grace period.");
        }
        if (!shutdownCancellationTask.IsCompleted)
        {
            CommandErrorWriter.WriteStderr(
                $"[cdidx-mcp] Shutdown cancellation callbacks are still running after {postCancelGracePeriod.TotalMilliseconds:0}ms post-cancel grace period.");
        }

        // Use an uncancelled observer so late faults are still observed after the bounded,
        // client-visible drain window (#3774, #4543).
        // bounded drain window 後の late fault も未キャンセル observer で観測する。
        ObserveLateInFlightTasks(postCancelWork);
    }

    private Task? GetShutdownCancellationTask()
    {
        lock (_shutdownCancellationGate)
            return _shutdownCancellationTask;
    }

    private Task RequestShutdownCancellation()
    {
        TaskCompletionSource completion;
        lock (_shutdownCancellationGate)
        {
            if (_shutdownCancellationTask is not null)
                return _shutdownCancellationTask;

            completion = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
            _shutdownCancellationTask = completion.Task;
        }

        _ = CompleteShutdownCancellationAsync(completion);
        return completion.Task;
    }

    private async Task CompleteShutdownCancellationAsync(TaskCompletionSource completion)
    {
        try
        {
            await _shutdownCts.CancelAsync().ConfigureAwait(false);
        }
        catch (ObjectDisposedException)
        {
            // Disposal won the race; cancellation can no longer be requested.
        }
        catch (Exception ex)
        {
            try
            {
                CommandErrorWriter.WriteStderr(
                    $"[cdidx-mcp] Shutdown cancellation callback failed during transport teardown ({ex.GetType().Name}).");
            }
            catch
            {
                // Diagnostics must never abort bounded teardown.
            }
        }
        finally
        {
            completion.TrySetResult();
        }
    }

    private static void ObserveLateInFlightTasks(Task tasks)
        => _ = tasks.ContinueWith(task =>
        {
            _ = task.Exception;
        }, CancellationToken.None, TaskContinuationOptions.ExecuteSynchronously, TaskScheduler.Default);

    private static async Task ObserveInFlightTasksAsync(Task tasks)
    {
        try
        {
            await tasks.ConfigureAwait(false);
        }
        catch (Exception ex)
        {
            CommandErrorWriter.WriteStderr($"[cdidx-mcp] In-flight request ended during transport teardown ({ex.GetType().Name}).");
        }
    }

    /// <summary>
    /// Process one MCP JSON-RPC line and write any response to the provided writer. Kept as a
    /// thin wrapper around <see cref="ProcessFrameAsync"/> so existing tests that drive a
    /// <see cref="TextWriter"/> directly stay source-compatible after the #1558 transport refactor.
    /// 1 行分の MCP JSON-RPC を処理して writer に書き込む薄いラッパ。#1558 のトランスポート抽象化後も
    /// 既存テストがソース互換となるよう、<see cref="ProcessFrameAsync"/> をそのまま呼び出す。
    /// </summary>
    internal async Task ProcessLineAsync(string line, TextWriter writer)
    {
        BeginDeferredFrameLogs();
        var response = await ProcessFrameAsync(line).ConfigureAwait(false);
        if (response != null)
        {
            try
            {
                await _textWriterGate.WaitAsync().ConfigureAwait(false);
                try
                {
                    await WriteJsonLineAsync(writer, response).ConfigureAwait(false);
                    FlushDeferredFrameLogs();
                }
                finally
                {
                    _textWriterGate.Release();
                }
            }
            catch (Exception ex) when (ex is IOException or ObjectDisposedException or OperationCanceledException)
            {
                WriteMcpLogLine(BuildResponseWriteErrorLog(DiagnosticRedactor.FormatExceptionMessage(ex)));
                FlushDeferredFrameLogs();
            }
        }
    }

    private static async Task WriteJsonLineAsync(TextWriter writer, string response)
    {
        await writer.WriteAsync(response).ConfigureAwait(false);
        await writer.WriteAsync('\n').ConfigureAwait(false);
        await writer.FlushAsync().ConfigureAwait(false);
    }

    private static async Task WriteFrameSafelyAsync(IMcpTransport transport, string? response, CancellationToken cancellationToken)
        => await WriteFrameSafelyAsync(transport.WriteFrameAsync, response, cancellationToken).ConfigureAwait(false);

    private static async Task WriteFrameSafelyAsync(
        Func<string?, CancellationToken, Task> writeFrameAsync,
        string? response,
        CancellationToken cancellationToken)
    {
        try
        {
            await writeFrameAsync(response, cancellationToken).ConfigureAwait(false);
        }
        catch (OperationCanceledException) when (!cancellationToken.IsCancellationRequested)
        {
            WriteMcpLogLine(BuildResponseWriteErrorLog("write operation was canceled"));
        }
        catch (Exception ex) when (ex is IOException or ObjectDisposedException or TimeoutException)
        {
            WriteMcpLogLine(BuildResponseWriteErrorLog(DiagnosticRedactor.FormatExceptionMessage(ex)));
        }
    }

    private static bool IsServerResponseFrame(string frame)
    {
        if (!JsonFrameParser.TryParseNode(frame, MaxJsonDepth, out var node, out _))
            return false;

        return node is JsonObject obj
            && obj.ContainsKey("id")
            && obj["method"] is null
            && (obj.ContainsKey("result") || obj.ContainsKey("error"));
    }

    private string BuildInvalidUtf8ParseErrorResponse(DecoderFallbackException ex)
    {
        DeferFrameLog(BuildInvalidUtf8ErrorLog(DiagnosticRedactor.FormatExceptionMessage(ex)));
        var errorResponse = CreateErrorResponse(hasId: true, id: null, code: -32700, message: "Parse error: invalid UTF-8 input",
            category: McpErrorEnvelope.CategoryParseError,
            suggestion: "Send one JSON-RPC 2.0 object per line encoded as valid UTF-8. Reject or re-encode malformed bytes before retrying.",
            retrySafe: false);
        return errorResponse.ToJsonString(_jsonOptions);
    }

    internal static string BuildInvalidUtf8ErrorLog(string detail)
        => $"[cdidx-mcp] JSON parse error: invalid UTF-8 input ({detail}). Send one UTF-8 JSON-RPC object per line; reject or re-encode malformed bytes before retrying.";

    private string BuildOversizedLineErrorResponse(BoundedLineLengthException ex)
        => BuildOversizedLineErrorResponse(ex.CharactersRead, ex.Utf8BytesRead);

    private string BuildOversizedLineErrorResponse(int charactersRead, int utf8BytesRead)
    {
        DeferFrameLog(BuildOversizedMessageLog(charactersRead, utf8BytesRead));
        var errorResponse = CreateErrorResponse(hasId: true, id: null, code: -32700, message: "Message too large",
            category: McpErrorEnvelope.CategoryMessageTooLarge,
            suggestion: $"JSON-RPC frame exceeds the {MaxLineCharacterCount} character or {MaxLineByteLength} byte cap. Split the request into smaller calls or use `batch_query` with smaller slots.",
            retrySafe: false);
        return errorResponse.ToJsonString(_jsonOptions);
    }

    /// <summary>
    /// Process one MCP JSON-RPC frame and return the wire-ready response string (or null when
    /// the request was a notification or otherwise yields no response). This synchronous wrapper
    /// is retained for compatibility tests and legacy in-process callers only; transports and
    /// request loops should call <see cref="ProcessFrameAsync"/> so cancellation and shutdown can
    /// flow without sync-over-async blocking (#3770).
    /// 1 フレーム分の MCP JSON-RPC を処理し、ワイヤー応答文字列を返す（通知などで応答なしの場合は null）。
    /// この同期ラッパは互換テストと legacy in-process 呼び出し専用に残す。transport と request loop は
    /// sync-over-async blocking を避けるため <see cref="ProcessFrameAsync"/> を await する (#3770)。
    /// </summary>
    internal string? ProcessFrame(string line)
        // Synchronous callers are compatibility entry points for tests and non-async hosts;
        // transport loops use ProcessFrameAsync directly so request handling stays async.
        => ProcessFrameAsync(line).GetAwaiter().GetResult();

    internal Task<string?> ProcessFrameAsync(string line)
        => ProcessFrameAsync(line, beforeDispatchAsync: null, rejectForCapacity: false);

    private async Task<string?> ProcessFrameAsync(
        string line,
        Func<CancellationToken, Task>? beforeDispatchAsync,
        bool rejectForCapacity,
        CapacityRejectedFrameRegistrations? capacityRejectedRegistrations = null)
    {
        if (string.IsNullOrWhiteSpace(line))
            return null;

        // Reject oversized messages to prevent memory exhaustion
        // メモリ枯渇を防ぐため巨大メッセージを拒否
        var byteLength = Encoding.UTF8.GetByteCount(line);
        if (line.Length > MaxLineCharacterCount || byteLength > MaxLineByteLength)
            return BuildOversizedLineErrorResponse(line.Length, byteLength);

        JsonNode? request = null;
        var responseHasId = true;
        JsonNode? responseId = null;
        IDisposable? frameCorrelationScope = null;
        var deferredInitializeCommits = new DeferredInitializeCommits();
        try
        {
            request = JsonFrameParser.ParseNode(line, MaxJsonDepth);
            if (request == null)
                return CreateExpectedJsonObjectErrorResponse().ToJsonString(_jsonOptions);

            if (TryCompletePendingClientRequest(request))
                return null;

            capacityRejectedRegistrations?.Register(request);
            ExtractResponseId(request, out responseHasId, out responseId);
            // A batch frame has no single JSON-RPC id. Invalid ids and malformed scalar frames
            // also use id:null only for the JSON-RPC error response; that wire fallback must not
            // be mistaken for an explicit null request id in telemetry. Batch items establish
            // their own valid-id contexts in HandleMessageAsync.
            // batch frame 自体には単一の JSON-RPC id がない。invalid id や scalar frame の
            // id:null は error response 専用で、telemetry 上の明示 null id と混同しない。
            // batch item は HandleMessageAsync で valid id ごとの context を作る。
            var frameHasRequestId = request is JsonObject requestObject
                && TryGetRequestId(requestObject, out var requestObjectHasId, out _)
                && requestObjectHasId;
            var frameHasCorrelation = responseHasId && request is not JsonArray;
            if (frameHasCorrelation && CurrentCorrelationContext.Value is null)
                frameCorrelationScope = BeginRequestCorrelation(responseId, frameHasRequestId);
            using var activity = StartMcpActivity(request, frameHasRequestId, responseId);
            var response = await HandleMessageAsync(
                request,
                isolateRequestDb: true,
                beforeDispatchAsync,
                rejectForCapacity,
                queuedBatchRegistration: null,
                deferredInitializeCommits).ConfigureAwait(false);
            activity?.SetTag("rpc.result", response is null ? "notification" : "response");
            if (response is null)
                return null;

            var serialized = SerializeResponseOrFallback(
                response,
                responseHasId,
                responseId,
                out var serializedOriginalResponse);
            if (serializedOriginalResponse)
            {
                foreach (var state in deferredInitializeCommits.GetIncludedStates(response))
                    CommitInitializeState(state);
            }

            return serialized;
        }
        catch (JsonException ex)
        {
            // Parse error / パースエラー
            DeferFrameLog(BuildJsonParseErrorLog(JsonFrameParser.FormatExceptionDetail(ex)));
            var errorResponse = CreateErrorResponse(hasId: true, id: null, code: -32700, message: "Parse error",
                category: McpErrorEnvelope.CategoryParseError,
                suggestion: $"For MCP stdio, send one UTF-8 JSON-RPC 2.0 object per LF-delimited line with nesting depth <= {MaxJsonDepth}. Do not send LSP Content-Length framing.",
                retrySafe: false);
            return errorResponse.ToJsonString(_jsonOptions);
        }
        catch (Exception ex)
        {
            // Stderr keeps the full message for local diagnostics, but the
            // wire response only carries the exception type so SQLite-style
            // "near 'foo': syntax error" detail or other content-bearing
            // strings cannot leak to the JSON-RPC client (#1530).
            // stderr には診断用に詳細を残すが、ネットワークに出るレスポンスには
            // 例外型のみを返し、SQLite の "near 'foo': syntax error" などを通じた
            // 内容漏れを防ぐ（#1530）。
            DeferFrameLog(BuildUnhandledLoopErrorLog(DiagnosticRedactor.FormatExceptionMessage(ex)));
            var classification = McpErrorEnvelope.ClassifyException(ex);
            var errorResponse = CreateErrorResponse(responseHasId, responseId, classification.JsonRpcCode,
                BuildSanitizedLoopErrorMessage(ex),
                category: classification.Category,
                suggestion: classification.Suggestion,
                retrySafe: classification.RetrySafe);
            return SerializeResponseOrFallback(
                errorResponse,
                responseHasId,
                responseId,
                out _);
        }
        finally
        {
            frameCorrelationScope?.Dispose();
        }
    }

    private static Activity? StartMcpActivity(JsonNode request, bool responseHasId, JsonNode? responseId)
    {
        var method = request is JsonObject obj ? TryGetStringMember(obj, "method") : null;
        var traceParent = TryGetMcpTraceParent(request);
        ActivityContext parentContext = default;
        if (traceParent != null)
            ActivityContext.TryParse(traceParent, traceState: null, out parentContext);

        var activity = parentContext != default
            ? CodeIndexTelemetry.ActivitySource.StartActivity("mcp.request", ActivityKind.Server, parentContext)
            : CodeIndexTelemetry.ActivitySource.StartActivity("mcp.request", ActivityKind.Server);
        activity?.SetTag("rpc.system", "jsonrpc");
        activity?.SetTag("rpc.service", "mcp");
        if (!string.IsNullOrWhiteSpace(method))
            activity?.SetTag("rpc.method", method);
        if (responseHasId)
        {
            var requestId = McpRequestIdTelemetry.Create(responseId);
            activity?.SetTag("rpc.request_id", requestId.Token);
            activity?.SetTag("rpc.request_id_type", requestId.Type);
            activity?.SetTag("rpc.request_id_length", requestId.Length);
        }
        return activity;
    }

    private bool TryCompletePendingClientRequest(JsonNode request)
    {
        if (request is not JsonObject obj
            || !obj.TryGetPropertyValue("id", out var id)
            || obj["method"] is not null)
            return false;

        if (!TrySerializeRequestId(id, out var serializedId, out _))
            return false;

        var key = serializedId ?? "null";
        if (!_pendingClientRequests.TryRemove(key, out var pending))
            return false;

        if (obj.TryGetPropertyValue("error", out var error) && error is not null)
        {
            if (!TrySerializeClientResponseError(error, out var serializedError, out var errorBytes))
            {
                DeferFrameLog(BuildClientResponseTooLargeLog("error", errorBytes));
                pending.TrySetException(new InvalidOperationException(BuildClientResponseTooLargeMessage(errorBytes)));
            }
            else
            {
                pending.TrySetException(new InvalidOperationException(serializedError));
            }
        }
        else if (!TryCloneClientResponsePayload(obj["result"], out var resultClone, out var resultBytes))
        {
            DeferFrameLog(BuildClientResponseTooLargeLog("result", resultBytes));
            pending.TrySetException(new InvalidOperationException(BuildClientResponseTooLargeMessage(resultBytes)));
        }
        else
        {
            pending.TrySetResult(resultClone);
        }
        return true;
    }

    internal Task<JsonNode?> RegisterPendingClientRequestForTests(string id)
    {
        var key = JsonSerializer.Serialize(id);
        var pending = new TaskCompletionSource<JsonNode?>(TaskCreationOptions.RunContinuationsAsynchronously);
        if (!_pendingClientRequests.TryAdd(key, pending))
            throw new InvalidOperationException($"Pending MCP client request already registered: {id}");
        return pending.Task;
    }

    private async Task<JsonNode?> SendClientRequestAsync(string method, JsonObject? @params, CancellationToken cancellationToken)
    {
        if (ClientRequestHandlerForTests is { } handler)
        {
            if (!TryCloneClientResponsePayload(handler(method, @params), out var handlerClone, out var handlerBytes))
            {
                DeferFrameLog(BuildClientResponseTooLargeLog("result", handlerBytes));
                return null;
            }
            return handlerClone;
        }

        var writer = _currentOutOfBandFrameWriter.Value;
        if (writer is null || !_canAwaitClientResponses.Value)
            return null;

        var id = "cdidx-" + Interlocked.Increment(ref s_nextClientRequestId).ToString(System.Globalization.CultureInfo.InvariantCulture);
        var key = JsonSerializer.Serialize(id);
        var pending = new TaskCompletionSource<JsonNode?>(TaskCreationOptions.RunContinuationsAsynchronously);
        if (!_pendingClientRequests.TryAdd(key, pending))
            return null;

        var request = new JsonObject
        {
            ["jsonrpc"] = "2.0",
            ["id"] = id,
            ["method"] = method,
        };
        if (@params is not null)
            request["params"] = @params;

        using var timeoutScope = OperationTimeoutScope.Create(
            OperationTimeoutCategories.McpClientRequest,
            TimeSpan.FromSeconds(10),
            cancellationToken);
        using var cancellationRegistration = timeoutScope.Token.Register(static state =>
        {
            var tuple = ((McpServer server, string key, TaskCompletionSource<JsonNode?> pending))state!;
            if (tuple.server._pendingClientRequests.TryRemove(tuple.key, out var _))
                tuple.pending.TrySetCanceled();
        }, (this, key, pending));

        try
        {
            await writer(request.ToJsonString(_jsonOptions), timeoutScope.Token).ConfigureAwait(false);
            return await pending.Task.ConfigureAwait(false);
        }
        catch (InvalidOperationException)
        {
            return null;
        }
        catch (OperationCanceledException)
        {
            return null;
        }
        finally
        {
            _pendingClientRequests.TryRemove(key, out var _);
        }
    }

    internal bool TryCloneClientResponsePayloadForTests(JsonNode? payload, out JsonNode? clone, out int bytesWritten)
        => TryCloneClientResponsePayload(payload, out clone, out bytesWritten);

    internal bool TrySerializeClientResponseErrorForTests(JsonNode error, out string? serialized, out int bytesWritten)
        => TrySerializeClientResponseError(error, out serialized, out bytesWritten);

    private bool TryCloneClientResponsePayload(JsonNode? payload, out JsonNode? clone, out int bytesWritten)
    {
        clone = null;
        bytesWritten = 0;
        if (payload is null)
            return true;

        if (!TryMeasureJsonUtf8BytesWithinLimit(payload, _jsonOptions, MaxClientResponseJsonBytes, out bytesWritten))
            return false;

        clone = McpJsonNode.Clone(payload);
        return true;
    }

    private bool TrySerializeClientResponseError(JsonNode error, out string? serialized, out int bytesWritten)
        => TrySerializeJsonNodeWithinByteLimit(error, _jsonOptions, MaxClientResponseJsonBytes, captureSerialized: true, out serialized, out bytesWritten);

    private static string? TryGetMcpTraceParent(JsonNode request)
    {
        if (request is not JsonObject obj ||
            obj["params"] is not JsonObject parameters ||
            parameters["_meta"] is not JsonObject meta)
            return null;

        if (meta["traceparent"] is not JsonValue valueNode ||
            !valueNode.TryGetValue<string>(out var value))
            return null;
        return string.IsNullOrWhiteSpace(value) ? null : value;
    }

    private string SerializeResponseOrFallback(
        JsonNode response,
        bool hasId,
        JsonNode? id,
        out bool serializedOriginalResponse)
    {
        serializedOriginalResponse = false;
        try
        {
            var responseLimit = GetMaxResponseBytes();
            if (_usesDefaultResponseSerializer)
            {
                if (!TrySerializeJsonNodeWithinByteLimit(response, _jsonOptions, responseLimit, captureSerialized: true, out var boundedSerialized, out var boundedResponseBytes))
                    return CreateResponseTooLargeError(hasId, id, boundedResponseBytes, responseLimit, actualBytesExact: false).ToJsonString(_jsonOptions);

                serializedOriginalResponse = true;
                return boundedSerialized!;
            }

            var serialized = _serializeResponse(response);
            var responseBytes = Encoding.UTF8.GetByteCount(serialized);
            if (responseBytes <= responseLimit)
            {
                serializedOriginalResponse = true;
                return serialized;
            }

            return CreateResponseTooLargeError(hasId, id, responseBytes, responseLimit).ToJsonString(_jsonOptions);
        }
        catch (Exception ex)
        {
            DeferFrameLog(BuildResponseSerializationErrorLog(DiagnosticRedactor.FormatExceptionMessage(ex)));
            return BuildMinimalInternalErrorResponse(hasId, id, ex);
        }
    }

    private void DeferFrameLog(string message)
        => DeferFrameLog(() => WriteMcpLogLine(message));

    private void DeferFrameLog(Action writeLog)
    {
        var context = CurrentCorrelationContext.Value;
        var logs = _deferredFrameLogs.Value;
        if (logs is null)
        {
            WriteWithCorrelationContext(context, writeLog);
            return;
        }

        logs.Add(() => WriteWithCorrelationContext(context, writeLog));
    }

    private static void WriteWithCorrelationContext(RequestCorrelationContext? context, Action writeLog)
    {
        var previous = CurrentCorrelationContext.Value;
        try
        {
            CurrentCorrelationContext.Value = context;
            writeLog();
        }
        finally
        {
            CurrentCorrelationContext.Value = previous;
        }
    }

    private void BeginDeferredFrameLogs()
        => _deferredFrameLogs.Value = new DeferredFrameLogBuffer();

    private void FlushDeferredFrameLogs()
    {
        var logs = _deferredFrameLogs.Value;
        if (logs is null)
            return;

        _deferredFrameLogs.Value = null;
        logs.ForwardTo(static log => log());
    }

    private sealed class DeferredFrameLogBuffer
    {
        private readonly object _gate = new();
        private List<Action>? _logs = [];
        private Action<Action>? _lateLogForwarder;

        public void Add(Action log)
        {
            Action<Action>? lateLogForwarder;
            lock (_gate)
            {
                if (_logs is not null)
                {
                    _logs.Add(log);
                    return;
                }

                lateLogForwarder = _lateLogForwarder;
            }

            (lateLogForwarder ?? (static lateLog => lateLog()))(log);
        }

        public void ForwardTo(Action<Action> lateLogForwarder)
        {
            lock (_gate)
            {
                if (_logs is null)
                    return;

                foreach (var log in _logs)
                    lateLogForwarder(log);
                _logs = null;
                _lateLogForwarder = lateLogForwarder;
            }
        }
    }

    private sealed class CapacityRejectedFrameRegistrations : IDisposable
    {
        private readonly McpServer _owner;
        private readonly HashSet<string> _requestKeys = new(StringComparer.Ordinal);
        private readonly List<QueuedBatchRequestRegistration> _registrations = [];
        private bool _disposed;

        internal CapacityRejectedFrameRegistrations(McpServer owner)
        {
            _owner = owner;
        }

        internal void Register(JsonNode request)
        {
            if (request is JsonArray batch)
            {
                foreach (var item in batch)
                    RegisterItem(item);
                return;
            }

            RegisterItem(request);
        }

        private void RegisterItem(JsonNode? item)
        {
            if (!BatchItemRequiresResponse(item, out var responseId)
                || SerializeRequestId(responseId) is not { } requestKey
                || !_requestKeys.Add(requestKey))
            {
                return;
            }

            if (_owner.TryRegisterQueuedBatchRequest(requestKey) is { } registration)
                _registrations.Add(registration);
        }

        public void Dispose()
        {
            if (_disposed)
                return;
            _disposed = true;

            foreach (var registration in _registrations)
                registration.DisposeIfUnclaimed();
        }
    }

    private sealed class QueuedBatchRequestRegistration
    {
        private readonly McpServer _owner;
        private readonly string _requestKey;
        private readonly CancellationTokenSource _cancellation;
        // 0 = queued, 1 = claimed by normal dispatch, 2 = cleaned before dispatch.
        private int _state;

        internal QueuedBatchRequestRegistration(
            McpServer owner,
            string requestKey,
            CancellationTokenSource cancellation)
        {
            _owner = owner;
            _requestKey = requestKey;
            _cancellation = cancellation;
        }

        internal CancellationToken Token => _cancellation.Token;

        internal bool TryCancel()
        {
            try
            {
                _cancellation.Cancel();
                return true;
            }
            catch (ObjectDisposedException)
            {
                // Dispatch won the move into `_activeRequests`; the caller will retry there.
                return false;
            }
        }

        internal bool TryClaim()
        {
            if (Interlocked.CompareExchange(ref _state, 1, 0) != 0)
                return false;
            RemoveAndDispose();
            return true;
        }

        internal void DisposeIfUnclaimed()
        {
            if (Interlocked.CompareExchange(ref _state, 2, 0) != 0)
                return;
            RemoveAndDispose();
        }

        private void RemoveAndDispose()
        {
            if (_owner._queuedBatchRequests.TryGetValue(_requestKey, out var current)
                && ReferenceEquals(current, this))
            {
                _owner._queuedBatchRequests.TryRemove(_requestKey, out _);
            }
            _cancellation.Dispose();
        }
    }

    private static void WriteMcpLogLine(string message)
    {
        var line = AddCorrelationPrefix(message);
        try
        {
            CommandErrorWriter.WriteStderr(line);
        }
        catch (Exception ex) when (ex is IOException or ObjectDisposedException)
        {
            // Best-effort diagnostics: a closed redirected stderr must not break the MCP request.
        }
        GlobalToolLog.Info(line);
    }

    private static string AddCorrelationPrefix(string message)
    {
        var context = CurrentCorrelationContext.Value;
        if (context is null)
            return message;

        var requestId = context.TelemetryRequestId;
        var prefix = requestId is { } presentRequestId
            ? $"[rid={presentRequestId.Token} rid_type={presentRequestId.Type} rid_length={presentRequestId.Length.ToString(CultureInfo.InvariantCulture)} cid={context.CorrelationId}] "
            : $"[cid={context.CorrelationId}] ";
        return message.StartsWith("[cdidx-mcp] ", StringComparison.Ordinal)
            ? "[cdidx-mcp] " + prefix + message["[cdidx-mcp] ".Length..]
            : prefix + message;
    }

    private static void ExtractResponseId(JsonNode request, out bool hasId, out JsonNode? id)
    {
        if (request is JsonObject obj)
        {
            if (TryGetRequestId(obj, out hasId, out var requestId))
                id = McpJsonNode.Clone(requestId);
            else
                id = null;
            return;
        }

        // For malformed non-object JSON values, JSON-RPC error responses should still carry
        // id:null instead of disappearing when handling or serialization fails.
        hasId = true;
        id = null;
    }

    private static string BuildMinimalInternalErrorResponse(bool hasId, JsonNode? id, Exception ex)
    {
        var message = $"Internal error while serializing MCP response ({ex.GetType().Name}). See cdidx server stderr for details.";
        var builder = new StringBuilder("{\"jsonrpc\":\"2.0\",\"error\":{\"code\":-32603,\"message\":");
        builder.Append(JsonSerializer.Serialize(message));
        AppendMinimalCorrelationData(builder);
        builder.Append('}');
        if (hasId)
        {
            builder.Append(",\"id\":");
            builder.Append(id is null ? "null" : id.ToJsonString());
        }
        builder.Append('}');
        return builder.ToString();
    }

    private static void AppendMinimalCorrelationData(StringBuilder builder)
    {
        var context = CurrentCorrelationContext.Value;
        if (context is null)
            return;

        builder.Append(",\"data\":{\"correlation_id\":");
        builder.Append(JsonSerializer.Serialize(context.CorrelationId));
        if (context.WireRequestId != null)
        {
            builder.Append(",\"request_id\":");
            builder.Append(JsonSerializer.Serialize(context.WireRequestId));
        }
        builder.Append('}');
    }

}

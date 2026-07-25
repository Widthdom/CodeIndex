using System.Collections.Concurrent;

namespace CodeIndex.Mcp;

public partial class McpServer
{
    private sealed class ConcurrentFrameLoopState
    {
        private readonly McpServer _server;
        private readonly bool _hasRequestScopedWriters;

        public ConcurrentFrameLoopState(
            McpServer server,
            IMcpTransport transport,
            CancellationToken loopToken,
            CancellationToken externalCancellationToken)
        {
            _server = server;
            Transport = transport;
            LoopToken = loopToken;
            ExternalCancellationToken = externalCancellationToken;
            _hasRequestScopedWriters = transport is IConcurrentMcpTransport;
            AdmissionGate = new SemaphoreSlim(
                server.MaxAcceptedConcurrentFrames,
                server.MaxAcceptedConcurrentFrames);
        }

        public IMcpTransport Transport { get; }
        public CancellationToken LoopToken { get; }
        public CancellationToken ExternalCancellationToken { get; }
        public SemaphoreSlim WriteGate { get; } = new(1, 1);
        public SemaphoreSlim AdmissionGate { get; }
        public List<Task> Tasks { get; } = [];
        public Task ProtocolBarrier { get; private set; } = Task.CompletedTask;
        public Task? TerminalTransportWriteTask { get; private set; }

        public bool TryAdmit()
        {
            if (!AdmissionGate.Wait(0))
                return false;

            Interlocked.Increment(ref _server._acceptedConcurrentFrameCount);
            return true;
        }

        public void ReleaseAdmission()
        {
            Interlocked.Decrement(ref _server._acceptedConcurrentFrameCount);
            AdmissionGate.Release();
        }

        public Lazy<Task> CreateProtocolPredecessor(bool isProtocolBarrier)
        {
            var precedingBarrier = ProtocolBarrier;
            var tasksAcceptedBeforeBarrier = isProtocolBarrier ? Tasks.ToArray() : [];
            Func<CancellationToken, Task> awaitPredecessorsAsync = isProtocolBarrier
                ? token => AwaitProtocolPredecessorsAsync(tasksAcceptedBeforeBarrier, token)
                : token => AwaitProtocolPredecessorsAsync([precedingBarrier], token);
            return new Lazy<Task>(
                () => awaitPredecessorsAsync(LoopToken),
                LazyThreadSafetyMode.ExecutionAndPublication);
        }

        public void TrackRequest(Task requestTask, bool isProtocolBarrier)
        {
            Tasks.Add(requestTask);
            if (isProtocolBarrier)
                ProtocolBarrier = requestTask;
        }

        public void ScheduleTerminalProtocolError(string response)
        {
            _server.BeginDeferredFrameLogs();
            TerminalTransportWriteTask = _server.WriteTerminalProtocolErrorAsync(
                WriteGate,
                Transport,
                response,
                ExternalCancellationToken);
        }

        public async Task WriteResponseAsync(
            Func<string?, CancellationToken, Task> writeResponseAsync,
            string? response)
        {
            // Concurrent transports provide one writer per request, so serializing those writers
            // behind the base-transport gate lets an unrelated stuck response retain later HTTP
            // request resources. Base transports (notably stdio) still require the shared gate.
            // concurrent transport は request ごとの writer を持つため、base transport 用 gate
            // に直列化すると無関係な stuck response が後続 HTTP resource を保持してしまう。
            // stdio 等の base transport だけ shared gate を維持する (#4546)。
            if (_hasRequestScopedWriters)
            {
                await WriteFrameSafelyAsync(
                    writeResponseAsync,
                    response,
                    ExternalCancellationToken).ConfigureAwait(false);
                _server.FlushDeferredFrameLogs();
                return;
            }

            await WriteGate.WaitAsync(ExternalCancellationToken).ConfigureAwait(false);
            try
            {
                await WriteFrameSafelyAsync(
                    writeResponseAsync,
                    response,
                    ExternalCancellationToken).ConfigureAwait(false);
                _server.FlushDeferredFrameLogs();
            }
            finally
            {
                WriteGate.Release();
            }
        }

        public Func<string, CancellationToken, Task>? CreateOutOfBandFrameWriter()
        {
            if (Transport is IOutOfBandMcpTransport outOfBandTransport)
                return (frame, token) => outOfBandTransport.WriteOutOfBandFrameAsync(frame, token);
            if (!string.Equals(Transport.Name, "stdio", StringComparison.OrdinalIgnoreCase))
                return null;

            return async (frame, token) =>
            {
                await WriteGate.WaitAsync(token).ConfigureAwait(false);
                try
                {
                    await Transport.WriteFrameAsync(frame, token).ConfigureAwait(false);
                }
                finally
                {
                    WriteGate.Release();
                }
            };
        }

        public async Task DrainAndScheduleGateDisposalAsync()
        {
            try
            {
                await _server.DrainInFlightTasksAsync(
                    Tasks,
                    _server.InFlightDrainGracePeriod,
                    _server.InFlightPostCancelGracePeriod,
                    ExternalCancellationToken,
                    TerminalTransportWriteTask).ConfigureAwait(false);
            }
            finally
            {
                // The bounded EOF drain can intentionally leave late request tasks running. Those
                // tasks can still own the write gate or reach the stdio writer until their finally
                // blocks run. Publish that aggregate even if draining itself exits unexpectedly,
                // then clean up the gates only after every accepted task is done (#3999, #4543).
                // bounded EOF drain は late request task を残すことがある。finally が走るまで gate や
                // stdio writer を使い得るため、drain 自体が異常終了しても aggregate を公開し、全
                // accepted task 完了後に gate を dispose する (#3999, #4543)。
                var transportWork = BuildDrainOperationsTask(Tasks, TerminalTransportWriteTask);
                if (Transport is StdioMcpTransport stdioTransport)
                    stdioTransport.DeferDisposalUntil(transportWork);
                _ = DisposeConcurrentLoopGatesAfterAsync(transportWork, WriteGate, AdmissionGate);
            }
        }
    }

    private static async Task<McpTransportFrame?> ReadConcurrentTransportFrameAsync(
        IMcpTransport transport,
        CancellationToken cancellationToken)
    {
        if (transport is IConcurrentMcpTransport concurrentTransport)
            return await concurrentTransport.ReadConcurrentFrameAsync(cancellationToken).ConfigureAwait(false);

        var frame = await transport.ReadFrameAsync(cancellationToken).ConfigureAwait(false);
        return frame is null ? null : new McpTransportFrame(frame, transport.WriteFrameAsync);
    }

    private async Task<bool> TryProcessInlineConcurrentFrameAsync(
        ConcurrentFrameLoopState state,
        McpTransportFrame transportFrame)
    {
        var frame = transportFrame.Frame;
        if (IsCancellationFrame(frame) || IsServerResponseFrame(frame))
        {
            try
            {
                BeginDeferredFrameLogs();
                var response = await ProcessFrameAsync(frame).ConfigureAwait(false);
                await state.WriteResponseAsync(transportFrame.WriteResponseAsync, response).ConfigureAwait(false);
            }
            finally
            {
                transportFrame.CompleteResourceRetentionWhen(Task.CompletedTask);
            }
            return true;
        }

        // Admission is deliberately non-blocking: waiting here would prevent a later
        // cancellation/client-response frame from being read while execution is saturated.
        // Excess ordinary work receives a retry-safe JSON-RPC overload response instead of
        // retaining another frame/task/HTTP context without bound (#4536).
        // admission は non-blocking にする。ここで待つと execution 飽和中に後続の
        // cancellation/client-response frame を読めなくなるため。上限超過 work は task や
        // HTTP context を保持し続けず、retry-safe overload response を返す (#4536)。
        if (state.TryAdmit())
            return false;

        try
        {
            // Keep every response-bearing id registered until its retry-safe overload
            // response has reached the transport. A cancellation before or during that
            // write then belongs to this rejected occurrence instead of poisoning a later
            // same-id retry (#4536, #4545).
            // retry-safe overload 応答が transport へ届くまで response-bearing id を登録する。
            // reject 前または write 中の cancel をこの occurrence に束縛し、同じ id の後続
            // retry へ持ち越さない (#4536, #4545)。
            using var capacityRejectedRegistrations = new CapacityRejectedFrameRegistrations(this);
            BeginDeferredFrameLogs();
            var response = await ProcessFrameAsync(
                frame,
                beforeDispatchAsync: null,
                rejectForCapacity: true,
                capacityRejectedRegistrations: capacityRejectedRegistrations).ConfigureAwait(false);
            await state.WriteResponseAsync(transportFrame.WriteResponseAsync, response).ConfigureAwait(false);
        }
        finally
        {
            transportFrame.CompleteResourceRetentionWhen(Task.CompletedTask);
        }
        return true;
    }

    private async Task StartAcceptedConcurrentFrameAsync(
        ConcurrentFrameLoopState state,
        McpTransportFrame transportFrame)
    {
        var requestTaskStarted = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        var isProtocolBarrier = IsProtocolOrderingBarrierFrame(transportFrame.Frame);
        var predecessorTask = state.CreateProtocolPredecessor(isProtocolBarrier);
        Task requestTask;
        try
        {
            requestTask = Task.Run(
                () => ExecuteAcceptedConcurrentFrameAsync(
                    state,
                    transportFrame,
                    predecessorTask,
                    requestTaskStarted),
                CancellationToken.None);
        }
        catch
        {
            transportFrame.CompleteResourceRetentionWhen(Task.CompletedTask);
            state.ReleaseAdmission();
            throw;
        }

        state.TrackRequest(requestTask, isProtocolBarrier);
        await requestTaskStarted.Task.ConfigureAwait(false);
    }

    private async Task ExecuteAcceptedConcurrentFrameAsync(
        ConcurrentFrameLoopState state,
        McpTransportFrame transportFrame,
        Lazy<Task> predecessorTask,
        TaskCompletionSource requestTaskStarted)
    {
        var detachedIsolatedActions = new ConcurrentQueue<Task>();
        var previousDetachedIsolatedActions = _currentDetachedIsolatedActions.Value;
        try
        {
            requestTaskStarted.TrySetResult();
            using var frameCts = transportFrame.RequestCancellationToken.CanBeCanceled
                ? CancellationTokenSource.CreateLinkedTokenSource(
                    state.LoopToken,
                    transportFrame.RequestCancellationToken)
                : null;
            var frameToken = frameCts?.Token ?? state.LoopToken;
            string? response = null;
            try
            {
                _currentDetachedIsolatedActions.Value = detachedIsolatedActions;
                _currentRequestToken.Value = frameToken;
                _currentOutOfBandFrameWriter.Value = state.CreateOutOfBandFrameWriter();
                _canAwaitClientResponses.Value = _currentOutOfBandFrameWriter.Value is not null
                    && (state.Transport is not HttpMcpTransport httpResponseTransport || httpResponseTransport.HasEventStreams);
                BeginDeferredFrameLogs();
                response = await ProcessFrameAsync(
                    transportFrame.Frame,
                    token => predecessorTask.Value.WaitAsync(token),
                    rejectForCapacity: false).ConfigureAwait(false);
            }
            catch (OperationCanceledException) when (frameToken.IsCancellationRequested)
            {
                // Keep the transport's strict one-frame/one-writer contract. HTTP observes its
                // own terminal reason and aborts/finalizes the response when the per-request
                // lifetime expires (#4546).
                // transport の frame/writer 対応を維持する。request lifetime 期限切れ時は HTTP 側が
                // terminal reason を観測して response を abort/finalize する。
                response = null;
            }
            finally
            {
                _currentDetachedIsolatedActions.Value = previousDetachedIsolatedActions;
                _currentRequestToken.Value = CancellationToken.None;
                _canAwaitClientResponses.Value = false;
                _currentOutOfBandFrameWriter.Value = null;
            }

            // Malformed/unauthorized frames can return before normal dispatch. Start their
            // predecessor wait here so such a frame cannot collapse a protocol barrier.
            // malformed / unauthorized frame が dispatch 前に return しても protocol
            // barrier を消してしまわないよう、未開始ならここで predecessor を待つ。
            if (!predecessorTask.IsValueCreated)
            {
                try
                {
                    await predecessorTask.Value.WaitAsync(frameToken).ConfigureAwait(false);
                }
                catch (OperationCanceledException) when (frameToken.IsCancellationRequested)
                {
                    // A canceled frame no longer needs protocol ordering, but its
                    // request-scoped writer still owns mandatory response cleanup.
                    // cancel 済み frame は protocol ordering を待たず、対応 writer
                    // による必須 cleanup だけを完了させる (#4546)。
                    response = null;
                }
            }

            await state.WriteResponseAsync(transportFrame.WriteResponseAsync, response).ConfigureAwait(false);
        }
        finally
        {
            var retainedWork = detachedIsolatedActions.IsEmpty
                ? Task.CompletedTask
                : ObserveDetachedIsolatedActionsAsync(detachedIsolatedActions.ToArray());
            transportFrame.CompleteResourceRetentionWhen(retainedWork);
            state.ReleaseAdmission();

            // A canceled or timed-out isolated action may still be unwinding durable writer
            // cleanup after its response has been sent. Release frame admission and the transport
            // resource callback first, then keep the outer request task attached to that cleanup
            // so EOF's bounded drain cannot return while the action is restoring database state.
            // cancel / timeout 応答後も isolated action が永続 writer cleanup を unwind 中の場合がある。
            // frame admission と transport resource callback を先に解放し、その後 outer request
            // task を cleanup に接続して、EOF の bounded drain が database 復元中に戻らないようにする。
            await retainedWork.ConfigureAwait(false);
        }
    }
}

using System.Diagnostics;
using System.Text.Json.Nodes;

namespace CodeIndex.Mcp;

public partial class McpServer
{
    private sealed class RequestDispatchState
    {
        public RequestDispatchState(
            string? requestKey,
            McpRequestIdTelemetryData telemetryRequestId,
            CancellationTokenSource cancellation,
            bool registeredRequest)
        {
            RequestKey = requestKey;
            TelemetryRequestId = telemetryRequestId;
            Cancellation = cancellation;
            RegisteredRequest = registeredRequest;
        }

        public string? RequestKey { get; }
        public McpRequestIdTelemetryData TelemetryRequestId { get; }
        public CancellationTokenSource Cancellation { get; }
        public bool RegisteredRequest { get; }
        public Stopwatch? Stopwatch { get; set; }
        public bool ExecutionSlotAcquired { get; set; }
        public bool CleanupNow { get; private set; } = true;
        public bool ReleaseExecutionSlotNow { get; private set; } = true;

        public void DeferCleanup()
        {
            CleanupNow = false;
            ReleaseExecutionSlotNow = false;
        }
    }

    private async Task<JsonNode> DispatchWithRequestCancellationAsync(
        JsonNode? id,
        bool isolateRequestDb,
        Func<CancellationToken, Task>? beforeDispatchAsync,
        QueuedBatchRequestRegistration? queuedBatchRegistration,
        Func<Task<JsonNode>> action)
    {
        if (!TryCreateRequestDispatchState(id, queuedBatchRegistration, out var state, out var duplicateError))
            return duplicateError!;

        var previousToken = _currentRequestToken.Value;
        try
        {
            _currentRequestToken.Value = state!.Cancellation.Token;
            state.Cancellation.Token.ThrowIfCancellationRequested();
            if (beforeDispatchAsync is not null)
                await beforeDispatchAsync(state.Cancellation.Token).ConfigureAwait(false);
            await _concurrencyGate.WaitAsync(state.Cancellation.Token).ConfigureAwait(false);
            state.ExecutionSlotAcquired = true;
            state.Cancellation.Token.ThrowIfCancellationRequested();
            state.Stopwatch = Stopwatch.StartNew();

            return isolateRequestDb
                ? await ExecuteIsolatedRequestActionAsync(id, state, action).ConfigureAwait(false)
                : await ExecuteInlineRequestActionAsync(id, state, action).ConfigureAwait(false);
        }
        catch (OperationCanceledException) when (state!.Cancellation.IsCancellationRequested)
        {
            if (state.Stopwatch is not null
                && !previousToken.IsCancellationRequested
                && !_shutdownCts.IsCancellationRequested
                && state.Stopwatch.Elapsed >= _requestTimeout)
            {
                return CreateRequestTimeoutResponse(id, state.Stopwatch.Elapsed);
            }
            return CreateCancelledResponse(id);
        }
        finally
        {
            _currentRequestToken.Value = previousToken;
            CleanupRequestDispatchState(state!);
        }
    }

    private bool TryCreateRequestDispatchState(
        JsonNode? id,
        QueuedBatchRequestRegistration? queuedBatchRegistration,
        out RequestDispatchState? state,
        out JsonNode? duplicateError)
    {
        var requestKey = SerializeRequestId(id);
        var cancellation = queuedBatchRegistration is null
            ? CancellationTokenSource.CreateLinkedTokenSource(_currentRequestToken.Value, _shutdownCts.Token)
            : CancellationTokenSource.CreateLinkedTokenSource(
                _currentRequestToken.Value,
                _shutdownCts.Token,
                queuedBatchRegistration.Token);
        var registeredRequest = false;
        if (requestKey is not null)
        {
            if (!_activeRequests.TryAdd(requestKey, cancellation))
            {
                cancellation.Dispose();
                state = null;
                duplicateError = CreateErrorResponse(
                    hasId: true,
                    id,
                    code: -32600,
                    message: "Duplicate in-flight request id",
                    category: McpErrorEnvelope.CategoryInvalidRequest,
                    suggestion: "JSON-RPC request ids must be unique while a previous request with the same id is still running.",
                    retrySafe: true);
                return false;
            }

            registeredRequest = true;
            if (queuedBatchRegistration is not null && !queuedBatchRegistration.TryClaim())
                CancelRequestCts(cancellation);
            if (TryConsumePendingRequestCancellation(requestKey))
                CancelRequestCts(cancellation);
            RequestRegisteredForTests?.Invoke(id);
        }

        state = new RequestDispatchState(
            requestKey,
            McpRequestIdTelemetry.Create(id),
            cancellation,
            registeredRequest);
        duplicateError = null;
        return true;
    }

    private async Task<JsonNode> ExecuteInlineRequestActionAsync(
        JsonNode? id,
        RequestDispatchState state,
        Func<Task<JsonNode>> action)
    {
        state.Cancellation.CancelAfter(_requestTimeout);
        var previousIsolation = _isolateDbForCurrentRequest.Value;
        _isolateDbForCurrentRequest.Value = false;
        try
        {
            await DelayRequestForTestsAsync(id, state.Cancellation.Token).ConfigureAwait(false);
            return await action().ConfigureAwait(false);
        }
        finally
        {
            _isolateDbForCurrentRequest.Value = previousIsolation;
        }
    }

    private async Task<JsonNode> ExecuteIsolatedRequestActionAsync(
        JsonNode? id,
        RequestDispatchState state,
        Func<Task<JsonNode>> action)
    {
        var actionTask = Task.Run(async () =>
        {
            var previousIsolation = _isolateDbForCurrentRequest.Value;
            _isolateDbForCurrentRequest.Value = true;
            try
            {
                await DelayRequestForTestsAsync(id, state.Cancellation.Token).ConfigureAwait(false);
                return await action().ConfigureAwait(false);
            }
            finally
            {
                _isolateDbForCurrentRequest.Value = previousIsolation;
            }
        }, state.Cancellation.Token);

        using var timeoutDelayCts = new CancellationTokenSource();
        var remainingTimeout = _requestTimeout - state.Stopwatch!.Elapsed;
        var timeoutTask = remainingTimeout <= TimeSpan.Zero
            ? Task.CompletedTask
            : Task.Delay(remainingTimeout, timeoutDelayCts.Token);
        var cancellationSignal = new TaskCompletionSource<bool>(TaskCreationOptions.RunContinuationsAsynchronously);
        using var cancellationRegistration = state.Cancellation.Token.Register(
            static signal => ((TaskCompletionSource<bool>)signal!).TrySetResult(true),
            cancellationSignal);
        var cancellationTask = cancellationSignal.Task;
        var completed = await Task.WhenAny(actionTask, timeoutTask, cancellationTask).ConfigureAwait(false);
        try { timeoutDelayCts.Cancel(); }
        catch (ObjectDisposedException) { /* the timeout signal has already completed. */ }

        if (completed == cancellationTask && _shutdownCts.IsCancellationRequested)
        {
            // EOF/server shutdown owns the bounded outer request-task drain. Keep this dispatch
            // attached to non-cooperative work so teardown cannot race a terminal write (#4543).
            return await actionTask.ConfigureAwait(false);
        }
        if (completed == actionTask)
            return await actionTask.ConfigureAwait(false);

        var timedOut = completed == timeoutTask;
        if (timedOut)
            CancelRequestCts(state.Cancellation);
        var elapsed = state.Stopwatch.Elapsed;
        if (timedOut)
            RecordTimedOutIsolatedActionDraining(state.TelemetryRequestId, elapsed);

        state.DeferCleanup();
        _currentDetachedIsolatedActions.Value?.Enqueue(actionTask);
        RegisterDetachedRequestCleanup(state, actionTask, timedOut);
        return timedOut
            ? CreateRequestTimeoutResponse(id, elapsed, isolatedActionDraining: true)
            : CreateCancelledResponse(id);
    }

    private void RegisterDetachedRequestCleanup(
        RequestDispatchState state,
        Task<JsonNode> actionTask,
        bool timedOut)
    {
        // Cleanup must run after request timeout/shutdown cancellation. The execution lease remains
        // held until the underlying action ends so live handlers cannot exceed MaxConcurrency.
        // timeout / cancel 応答後も underlying action 終了まで cleanup と execution lease を保持する。
        _ = actionTask.ContinueWith(task =>
        {
            try
            {
                _ = task.Exception;
                if (state.RegisteredRequest)
                    _activeRequests.TryRemove(state.RequestKey!, out _);
                if (timedOut)
                    RecordTimedOutIsolatedActionDrained(state.TelemetryRequestId, task);
            }
            finally
            {
                state.Cancellation.Dispose();
                _concurrencyGate.Release();
            }
        }, CancellationToken.None, TaskContinuationOptions.ExecuteSynchronously, TaskScheduler.Default);
    }

    private void CleanupRequestDispatchState(RequestDispatchState state)
    {
        if (state.ExecutionSlotAcquired && state.ReleaseExecutionSlotNow)
            _concurrencyGate.Release();
        if (!state.CleanupNow)
            return;

        if (state.RegisteredRequest)
            _activeRequests.TryRemove(state.RequestKey!, out _);
        state.Cancellation.Dispose();
    }
}

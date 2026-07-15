namespace CodeIndex.Mcp;

internal sealed partial class HttpMcpTransport
{
    public ValueTask DisposeAsync()
    {
        Task disposeTask;
        lock (_disposeSync)
        {
            disposeTask = _disposeTask ?? StartDisposeLocked();
        }

        return new ValueTask(disposeTask);
    }

    private Task StartDisposeLocked()
    {
        Volatile.Write(ref _disposeStarted, 1);
        var disposeTask = DisposeCoreAsync();
        _disposeTask = disposeTask;
        return disposeTask;
    }

    private async Task DisposeCoreAsync()
    {
        CancelAcceptLoopForDispose();
        StopListenerForDispose();
        var acceptLoopCompleted = await WaitForAcceptLoopForDisposeAsync().ConfigureAwait(false);
        var requestQueueReleased = acceptLoopCompleted
            && await ReleasePendingAndQueuedRequestsForDisposeAsync().ConfigureAwait(false);

        if (acceptLoopCompleted)
            _acceptCts.Dispose();

        await CompleteRequestLogQueueForDisposeAsync().ConfigureAwait(false);

        if (requestQueueReleased && await WaitForOwnedSemaphoreGatesIdleAsync().ConfigureAwait(false))
        {
            _queueSlots.Dispose();
            // A ReadFrameAsync caller can pass the disposed check immediately before shutdown
            // and begin waiting after the drain briefly owns this semaphore. SemaphoreSlim has
            // no unmanaged resource until AvailableWaitHandle is requested (which we never do),
            // so leaving this coordination-only gate undisposed avoids racing a late waiter.
            // ReadFrameAsync が disposed check 通過直後に shutdown drain と交差し得る。
            // unmanaged resource を持たない coordination-only gate は dispose せず race を避ける。
            _handlerSemaphore.Dispose();
            _eventStreamHandlerSemaphore.Dispose();
            Volatile.Write(ref _ownedSemaphoreGatesDisposed, true);
        }
    }

    private void CancelAcceptLoopForDispose()
    {
        try { _acceptCts.Cancel(); } catch { /* ignore */ }
    }

    private void StopListenerForDispose()
    {
        try
        {
            var pendingRequest = Interlocked.Exchange(ref _pendingRequest, null);
            if (pendingRequest is not null)
            {
                AbortResponseBestEffort(pendingRequest.Context.Response, "pending request disposal");
                ReleasePendingInitialize(pendingRequest);
                ReleaseRequestBodyReservation(pendingRequest);
            }

            _listener.Close();
        }
        catch
        {
            // Disposal must not throw; parent server shutdown is already in progress.
            // dispose は例外を投げない方針。親サーバーは既に終了処理中なので。
        }
    }

    private async Task<bool> ReleasePendingAndQueuedRequestsForDisposeAsync()
    {
        if (!await _requestQueueReaderSemaphore.WaitAsync(DisposeAcceptLoopTimeout).ConfigureAwait(false))
            return false;

        try
        {
            var pendingRequest = Interlocked.Exchange(ref _pendingRequest, null);
            if (pendingRequest is not null)
            {
                AbortResponseBestEffort(pendingRequest.Context.Response, "pending request disposal after reader drain");
                ReleasePendingInitialize(pendingRequest);
                ReleaseRequestBodyReservation(pendingRequest);
            }

            while (_requestQueue.Reader.TryRead(out var queuedRequest))
            {
                Interlocked.Decrement(ref _queuedRequestCount);
                _queueSlots.Release();
                AbortResponseBestEffort(queuedRequest.Context.Response, "queued request disposal");
                ReleasePendingInitialize(queuedRequest);
                ReleaseRequestBodyReservation(queuedRequest);
            }

            return true;
        }
        finally
        {
            _requestQueueReaderSemaphore.Release();
        }
    }

    private async Task<bool> WaitForAcceptLoopForDisposeAsync()
    {
        try
        {
            await _acceptLoop.WaitAsync(DisposeAcceptLoopTimeout).ConfigureAwait(false);
            return true;
        }
        catch (TimeoutException)
        {
            // Best-effort disposal must not hang on platform-delayed listener teardown.
            // best-effort な dispose は、プラットフォーム都合の listener 終了遅延で停止しない。
            return false;
        }
        catch
        {
            return true;
        }
    }

    private async Task CompleteRequestLogQueueForDisposeAsync()
    {
        if (_requestLogQueue is null)
            return;

        _requestLogQueue.Writer.TryComplete();
        try
        {
            if (_requestLogTask is not null)
                await _requestLogTask.WaitAsync(DisposeAcceptLoopTimeout).ConfigureAwait(false);
        }
        catch
        {
            // Request logging is best-effort; shutdown must not wait indefinitely.
            // request log は best-effort。shutdown を無期限に待たせない。
        }
    }

    private async Task<bool> WaitForOwnedSemaphoreGatesIdleAsync()
    {
        var deadline = DateTimeOffset.UtcNow.Add(DisposeAcceptLoopTimeout);
        while (_queueSlots.CurrentCount != _maxQueuedRequests
            || _handlerSemaphore.CurrentCount != _maxConcurrentHandlers
            || _eventStreamHandlerSemaphore.CurrentCount != _maxEventStreams)
        {
            if (DateTimeOffset.UtcNow >= deadline)
                return false;
            await Task.Delay(10).ConfigureAwait(false);
        }

        return true;
    }
}

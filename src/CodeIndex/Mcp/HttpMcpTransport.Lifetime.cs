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
        // Do not deliver cancellation callbacks on the DisposeAsync caller's thread. A callback
        // belongs to application code and may be slow or non-cooperative; yielding first keeps
        // DisposeAsync itself prompt while the bounded shutdown below proceeds asynchronously.
        // cancellation callback は application code に属し、遅い／非協調的な場合がある。
        // 最初に yield して DisposeAsync 自体を即時に返し、以降を bounded shutdown とする。
        await Task.Yield();

        CompleteRequestQueueForDispose();
        var cancellationDeliveryTask = BeginAcceptLoopCancellationForDispose();
        var listenerRequestReleased = StopListenerForDispose();
        var activeConcurrentRequestsReleased = ReleaseActiveConcurrentRequestsForDispose();

        // Start every bounded wait before awaiting any one phase. In particular, queue cleanup
        // must still be attempted when a platform-delayed accept loop misses its deadline.
        // 各 bounded wait を先に開始し、accept loop が期限超過しても queue cleanup は必ず試行する。
        var acceptLoopCompletionTask = WaitForAcceptLoopForDisposeAsync();
        var cancellationDeliveryCompletionTask =
            WaitForAcceptLoopCancellationForDisposeAsync(cancellationDeliveryTask);
        var requestQueueReleaseTask = ReleasePendingAndQueuedRequestsForDisposeAsync();

        await CompleteRequestLogQueueForDisposeAsync().ConfigureAwait(false);

        var acceptLoopCompleted = await acceptLoopCompletionTask.ConfigureAwait(false);
        var cancellationDeliveryCompleted = await cancellationDeliveryCompletionTask.ConfigureAwait(false);
        var requestQueueReleased = await requestQueueReleaseTask.ConfigureAwait(false)
            && listenerRequestReleased
            && activeConcurrentRequestsReleased;

        if (acceptLoopCompleted
            && cancellationDeliveryCompleted
            && requestQueueReleased
            && await WaitForOwnedSemaphoreGatesIdleAsync().ConfigureAwait(false))
        {
            try
            {
                _acceptCts.Dispose();
                _queueSlots.Dispose();
                // A ReadFrameAsync caller can pass the disposed check immediately before shutdown
                // and begin waiting after the drain briefly owns this semaphore. SemaphoreSlim has
                // no unmanaged resource until AvailableWaitHandle is requested (which we never do),
                // so leaving this coordination-only gate undisposed avoids racing a late waiter.
                // ReadFrameAsync が disposed check 通過直後に shutdown drain と交差し得る。
                // unmanaged resource を持たない coordination-only gate は dispose せず race を避ける。
                _handlerSemaphore.Dispose();
                _eventStreamHandlerSemaphore.Dispose();
                _eventStreamRejectionSemaphore.Dispose();
                _retainedResponseOutputOperationSlots.Dispose();
                Volatile.Write(ref _ownedSemaphoreGatesDisposed, true);
            }
            catch
            {
                // Disposal is best-effort and must never fault the parent server shutdown.
                // dispose は best-effort とし、親 server の shutdown を失敗させない。
            }
        }
    }

    private void CompleteRequestQueueForDispose()
    {
        try { CompleteRequestQueue(); } catch { /* ignore */ }
    }

    private Task BeginAcceptLoopCancellationForDispose()
    {
        try
        {
            return _acceptCts.CancelAsync();
        }
        catch
        {
            // A concurrently disposed CTS is already terminal from the transport's perspective.
            // 同時 dispose 済み CTS は transport から見て既に terminal とみなす。
            return Task.CompletedTask;
        }
    }

    private static async Task<bool> WaitForAcceptLoopCancellationForDisposeAsync(Task cancellationDeliveryTask)
    {
        try
        {
            await cancellationDeliveryTask.WaitAsync(DisposeAcceptLoopTimeout).ConfigureAwait(false);
            return true;
        }
        catch (TimeoutException)
        {
            // A hostile or non-cooperative callback must not make disposal unbounded.
            // hostile／非協調的 callback によって dispose を無期限化させない。
            ObserveLateCancellationDeliveryFailure(cancellationDeliveryTask);
            return false;
        }
        catch
        {
            // CancelAsync reports callback exceptions only after delivery has completed.
            // CancelAsync の callback 例外は delivery 完了後に報告される。
            return cancellationDeliveryTask.IsCompleted;
        }
    }

    private static void ObserveLateCancellationDeliveryFailure(Task cancellationDeliveryTask)
    {
        _ = cancellationDeliveryTask.ContinueWith(
            static completed => _ = completed.Exception,
            CancellationToken.None,
            TaskContinuationOptions.OnlyOnFaulted | TaskContinuationOptions.ExecuteSynchronously,
            TaskScheduler.Default);
    }

    private bool StopListenerForDispose()
    {
        // Close first: request cleanup may dispose a lifetime CTS whose callback is application
        // code, so it must not delay listener teardown and continued admission shutdown.
        // request cleanup の lifetime CTS callback が listener teardown を遅らせないよう先に閉じる。
        try { _listener.Close(); } catch { /* ignore */ }

        try
        {
            var pendingRequest = Interlocked.Exchange(ref _pendingRequest, null);
            return pendingRequest is null
                || ReleaseRequestForDispose(pendingRequest, "pending request disposal");
        }
        catch
        {
            // Disposal must not throw; parent server shutdown is already in progress.
            // dispose は例外を投げない方針。親サーバーは既に終了処理中なので。
            return false;
        }
    }

    private bool ReleaseActiveConcurrentRequestsForDispose()
    {
        var releasedAll = true;
        foreach (var request in _activeConcurrentRequests.Keys)
        {
            if (!_activeConcurrentRequests.TryRemove(request, out _))
                continue;
            releasedAll &= ReleaseRequestForDispose(request, "active concurrent request disposal");
        }

        return releasedAll;
    }

    private async Task<bool> ReleasePendingAndQueuedRequestsForDisposeAsync()
    {
        var readerGateAcquired = false;

        try
        {
            readerGateAcquired = await _requestQueueReaderSemaphore
                .WaitAsync(DisposeAcceptLoopTimeout)
                .ConfigureAwait(false);
            if (!readerGateAcquired)
                return false;

            var releasedAll = true;
            var pendingRequest = Interlocked.Exchange(ref _pendingRequest, null);
            if (pendingRequest is not null)
                releasedAll &= ReleaseRequestForDispose(
                    pendingRequest,
                    "pending request disposal after reader drain");

            List<PendingRequest> queuedRequests;
            lock (_requestQueueSync)
            {
                queuedRequests = _requestQueue.ToList();
                _requestQueue.Clear();
                foreach (var queuedRequest in queuedRequests)
                    queuedRequest.QueueNode = null;
                ResetRequestAvailableSignalIfQueueEmpty();
            }

            foreach (var queuedRequest in queuedRequests)
            {
                Interlocked.Decrement(ref _queuedRequestCount);
                try { _queueSlots.Release(); } catch { releasedAll = false; }
                releasedAll &= ReleaseRequestForDispose(queuedRequest, "queued request disposal");
            }

            return releasedAll;
        }
        catch
        {
            return false;
        }
        finally
        {
            if (readerGateAcquired)
            {
                try { _requestQueueReaderSemaphore.Release(); } catch { /* ignore */ }
            }
        }
    }

    private bool ReleaseRequestForDispose(PendingRequest request, string abortReason)
    {
        var released = true;
        // Claim and publish the request-local shutdown cancellation before any lifetime state can
        // be finalized. TryCancel is non-blocking: callback delivery and queue cleanup continue on
        // their own worker while the transport performs bounded disposal (#4546).
        // lifetime state を finalize する前に request-local shutdown cancellation を確定・公開する。
        // TryCancel は非同期配送のため、transport の bounded dispose を callback が塞がない (#4546)。
        try
        {
            if (request.TryCancel(TransportShutdownDiagnostic)
                || request.CancellationReason is not null)
            {
                TrackRequestCancellationDelivery(request.CancellationDelivery);
            }
        }
        catch { released = false; }
        try { AbortResponseBestEffort(request.Context.Response, abortReason); } catch { released = false; }
        try { ReleasePendingInitialize(request); } catch { released = false; }
        request.Body = null;
        try { ReleaseRequestBodyReservation(request); } catch { released = false; }
        try { request.DisposeLifetime(); } catch { released = false; }
        return released;
    }

    private void TrackRequestCancellationDelivery(Task delivery)
    {
        if (!_requestCancellationDeliveries.TryAdd(delivery, 0))
            return;

        _ = delivery.ContinueWith(
            static (completed, state) =>
            {
                _ = completed.Exception;
                var (transport, trackedDelivery) = ((HttpMcpTransport, Task))state!;
                transport._requestCancellationDeliveries.TryRemove(trackedDelivery, out _);
            },
            (this, delivery),
            CancellationToken.None,
            TaskContinuationOptions.ExecuteSynchronously,
            TaskScheduler.Default);
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
            || QueuedRequestCount != 0
            || _handlerSemaphore.CurrentCount != _maxConcurrentHandlers
            || _eventStreamHandlerSemaphore.CurrentCount != _maxEventStreams
            || _eventStreamRejectionSemaphore.CurrentCount != EventStreamRejectionConcurrency
            || _retainedResponseOutputOperationSlots.CurrentCount != RetainedResponseOutputOperationCapacity
            || !_requestCancellationDeliveries.IsEmpty
            || !_abandonedResponseOutputOperations.IsEmpty)
        {
            if (DateTimeOffset.UtcNow >= deadline)
                return false;
            await Task.Delay(10).ConfigureAwait(false);
        }

        return true;
    }
}

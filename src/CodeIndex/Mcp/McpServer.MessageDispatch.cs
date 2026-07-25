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
    /// Route a JSON-RPC message to the appropriate handler. This synchronous wrapper is retained
    /// for compatibility tests and legacy in-process callers only; transports should prefer
    /// <see cref="HandleMessageAsync(JsonNode)"/> to avoid sync-over-async dispatch (#3770).
    /// JSON-RPCメッセージを適切なハンドラにルーティング。この同期ラッパは互換テストと legacy
    /// in-process 呼び出し専用に残し、transport は sync-over-async dispatch を避けるため
    /// <see cref="HandleMessageAsync(JsonNode)"/> を優先する (#3770)。
    /// </summary>
    internal JsonNode? HandleMessage(JsonNode request)
        // Keep this sync wrapper for existing in-process callers; async transports call
        // HandleMessageAsync so server loops do not need a sync-over-async bridge.
        => HandleMessageAsync(
            request,
            isolateRequestDb: false,
            beforeDispatchAsync: null,
            rejectForCapacity: false,
            queuedBatchRegistration: null,
            deferredInitializeCommits: null).GetAwaiter().GetResult();

    internal Task<JsonNode?> HandleMessageAsync(JsonNode request)
        => HandleMessageAsync(
            request,
            isolateRequestDb: false,
            beforeDispatchAsync: null,
            rejectForCapacity: false,
            queuedBatchRegistration: null,
            deferredInitializeCommits: null);


    private static JsonObject CreateExpectedJsonObjectErrorResponse()
        => CreateErrorResponse(hasId: true, id: null, code: -32600, message: "Invalid request: expected JSON object",
            category: McpErrorEnvelope.CategoryInvalidRequest,
            suggestion: "Send a JSON-RPC 2.0 object (e.g. {\"jsonrpc\":\"2.0\",\"id\":1,\"method\":\"initialize\"}).",
            retrySafe: false);

    private static bool IsStateChangingNotification(string? method)
        => method is "$/cancelRequest"
            or "notifications/cancelled"
            or "notifications/roots/list_changed"
            or "notifications/shutdown"
            or "notifications/exit";

    private static JsonObject CreateServerBusyResponse(JsonNode? id)
        => CreateErrorResponse(
            hasId: true,
            id,
            McpErrorEnvelope.CodeServerBusy,
            "Server busy: MCP request backlog is full",
            category: McpErrorEnvelope.CategoryServerBusy,
            suggestion: "Retry after one or more in-flight MCP requests complete.",
            retrySafe: true,
            extraData: new JsonObject { ["retry_after_ms"] = 1000 });

    private string BuildHealthJson(HttpMcpTransport? httpTransport = null)
        => BuildHealthResult(httpTransport).ToJsonString(_jsonOptions);

    private string BuildKeepAliveNotificationJson()
    {
        var now = _timeProvider.GetUtcNow();
        var notification = new JsonObject
        {
            ["jsonrpc"] = "2.0",
            ["method"] = "notifications/keep_alive",
            ["params"] = new JsonObject
            {
                ["server_time"] = now.ToString("O", System.Globalization.CultureInfo.InvariantCulture),
                ["uptime_s"] = Math.Max(0, (long)Math.Floor((now - _startedAt).TotalSeconds)),
            }
        };
        return notification.ToJsonString(_jsonOptions);
    }

    private static TimeSpan? ReadKeepAliveIntervalFromEnvironment()
    {
        var raw = global::CodeIndex.EnvironmentAccess.GetProcessEnvironmentVariable(KeepAliveIntervalEnvironmentVariable);
        if (string.IsNullOrWhiteSpace(raw))
            return null;
        if (!double.TryParse(raw, System.Globalization.NumberStyles.Float, System.Globalization.CultureInfo.InvariantCulture, out var seconds)
            || !double.IsFinite(seconds)
            || seconds < MinKeepAliveIntervalSeconds
            || seconds > MaxKeepAliveIntervalSeconds)
        {
            var displayValue = DiagnosticRedactor.FormatEnvironmentValue(KeepAliveIntervalEnvironmentVariable, raw);
            CommandErrorWriter.WriteStderr(
                $"[cdidx-mcp] Ignoring invalid {KeepAliveIntervalEnvironmentVariable}='{displayValue}'. Expected a finite value between {MinKeepAliveIntervalSeconds.ToString(System.Globalization.CultureInfo.InvariantCulture)} and {MaxKeepAliveIntervalSeconds.ToString(System.Globalization.CultureInfo.InvariantCulture)} seconds. Keep-alive notifications stay disabled.");
            return null;
        }
        return TimeSpan.FromSeconds(seconds);
    }

    private JsonObject BuildHealthResult(HttpMcpTransport? httpTransport = null)
    {
        var now = _timeProvider.GetUtcNow();
        var dbOpen = ProbeDbHealth(out var dbError);
        var httpResponseCleanupDegraded = httpTransport?.ResponseCleanupDegraded ?? false;
        var httpRequestLogDegraded = httpTransport?.RequestLogDegraded ?? false;
        var auditLogDiagnostics = _auditLog?.SnapshotDiagnostics();
        var auditLogDegraded = IsAuditLogDegraded(auditLogDiagnostics);
        DateTimeOffset lastRequestAt;
        lock (_healthStateGate)
            lastRequestAt = _lastRequestAt;
        var result = new JsonObject
        {
            ["status"] = dbOpen && !httpResponseCleanupDegraded && !httpRequestLogDegraded && !auditLogDegraded ? "ok" : "degraded",
            ["uptime_s"] = Math.Max(0, (long)Math.Floor((now - _startedAt).TotalSeconds)),
            ["last_request_at"] = lastRequestAt.ToString("O", System.Globalization.CultureInfo.InvariantCulture),
            ["db_open"] = dbOpen,
            ["last_db_check_at"] = now.ToString("O", System.Globalization.CultureInfo.InvariantCulture),
            ["transport_ready"] = _running,
        };
        if (httpTransport is not null)
        {
            result["http_max_request_body_bytes"] = httpTransport.MaxRequestBodyBytes;
            result["http_request_body_idle_timeout_ms"] = (long)httpTransport.RequestBodyIdleTimeout.TotalMilliseconds;
            result["http_request_lifetime_timeout_ms"] = (long)httpTransport.RequestLifetimeTimeout.TotalMilliseconds;
            result["http_request_body_budget_limit_bytes"] = httpTransport.MaxInFlightRequestBodyBytes;
            result["http_request_body_bytes_in_flight"] = httpTransport.InFlightRequestBodyBytes;
            result["http_request_body_process_bytes_in_flight"] = httpTransport.ProcessInFlightRequestBodyBytes;
            result["http_request_body_peak_bytes"] = httpTransport.PeakInFlightRequestBodyBytes;
            result["http_request_body_budget_scope"] = "process";
            result["http_request_body_budget_rejection_count"] = httpTransport.RequestBodyBudgetLimitRejectionCount;
            result["http_request_body_idle_timeout_count"] = httpTransport.RequestBodyIdleTimeoutCount;
            result["http_request_lifetime_timeout_count"] = httpTransport.RequestLifetimeTimeoutCount;
            result["http_client_disconnect_count"] = httpTransport.ClientDisconnectCount;
            result["http_queued_request_cancellation_count"] = httpTransport.QueuedRequestCancellationCount;
            result["http_event_stream_count"] = httpTransport.EventStreamCount;
            result["http_event_stream_limit"] = httpTransport.MaxEventStreams;
            result["http_max_concurrent_handlers"] = httpTransport.MaxConcurrentHandlers;
            result["http_post_handler_capacity"] = httpTransport.PostHandlerCapacity;
            result["http_event_stream_handler_capacity"] = httpTransport.EventStreamHandlerCapacity;
            result["http_separate_event_stream_handlers"] = httpTransport.UsesSeparateEventStreamHandlers;
            result["http_queued_request_count"] = httpTransport.QueuedRequestCount;
            result["http_request_queue_limit"] = httpTransport.MaxQueuedRequests;
            result["http_request_log_queue_depth"] = httpTransport.RequestLogQueueDepth;
            result["http_request_log_queue_capacity"] = httpTransport.RequestLogQueueCapacity;
            result["http_request_log_dropped_count"] = httpTransport.RequestLogDroppedCount;
            result["http_request_log_queue_full_drop_count"] = httpTransport.RequestLogQueueFullDropCount;
            result["http_request_log_callback_failure_count"] = httpTransport.RequestLogCallbackFailureCount;
            result["http_request_log_degraded"] = httpRequestLogDegraded;
            if (!string.IsNullOrWhiteSpace(httpTransport.LastRequestLogDropReason))
                result["http_request_log_last_drop_reason"] = httpTransport.LastRequestLogDropReason;
            result["http_concurrent_handler_rejection_count"] = httpTransport.ConcurrentHandlerLimitRejectionCount;
            result["http_request_queue_rejection_count"] = httpTransport.RequestQueueLimitRejectionCount;
            result["http_event_stream_rejection_count"] = httpTransport.EventStreamLimitRejectionCount;
            result["http_event_stream_drop_count"] = httpTransport.EventStreamDropCount;
            result["http_event_stream_write_failure_drop_count"] = httpTransport.EventStreamWriteFailureDropCount;
            if (!string.IsNullOrWhiteSpace(httpTransport.LastEventStreamDropReason))
                result["http_event_stream_last_drop_reason"] = httpTransport.LastEventStreamDropReason;
            result["http_auth_denial_count"] = httpTransport.AuthDenialCount;
            result["http_auth_denial_missing_count"] = httpTransport.AuthDenialMissingCount;
            result["http_auth_denial_ambiguous_count"] = httpTransport.AuthDenialAmbiguousCount;
            result["http_auth_denial_wrong_scheme_count"] = httpTransport.AuthDenialWrongSchemeCount;
            result["http_auth_denial_malformed_token_count"] = httpTransport.AuthDenialMalformedTokenCount;
            result["http_auth_denial_oversized_token_count"] = httpTransport.AuthDenialOversizedTokenCount;
            result["http_auth_denial_wrong_token_count"] = httpTransport.AuthDenialWrongTokenCount;
            if (!string.IsNullOrWhiteSpace(httpTransport.LastAuthDenialReason))
                result["http_auth_denial_last_reason"] = httpTransport.LastAuthDenialReason;
            result["http_auth_required"] = httpTransport.RequiresBearerToken;
            result["http_auth_disabled"] = httpTransport.AuthDisabled;
            if (!string.IsNullOrWhiteSpace(httpTransport.AuthDisabledWarning))
                result["http_auth_disabled_warning"] = httpTransport.AuthDisabledWarning;
            result["http_response_cleanup_degraded"] = httpResponseCleanupDegraded;
            result["http_response_abort_cleanup_failure_count"] = httpTransport.ResponseAbortCleanupFailureCount;
            result["http_response_close_cleanup_failure_count"] = httpTransport.ResponseCloseCleanupFailureCount;
            if (!string.IsNullOrWhiteSpace(httpTransport.LastResponseAbortCleanupFailure))
                result["http_response_abort_cleanup_last_error"] = httpTransport.LastResponseAbortCleanupFailure;
            if (!string.IsNullOrWhiteSpace(httpTransport.LastResponseCloseCleanupFailure))
                result["http_response_close_cleanup_last_error"] = httpTransport.LastResponseCloseCleanupFailure;
        }
        if (auditLogDiagnostics is not null)
            result["audit_log"] = BuildAuditLogStatus(auditLogDiagnostics);
        result["metrics"] = BuildMetricsStatus(MetricsSink.SnapshotDiagnostics());
        if (!string.IsNullOrWhiteSpace(dbError))
            result["db_error"] = dbError;
        return result;
    }

    private bool ProbeDbHealth(out string? error)
    {
        var ok = false;
        string? probeError = null;
        try
        {
            using var connection = DbConnectionFactory.CreateArtifactPreservingQueryOnlyConnection(
                _dbPath,
                pooling: false,
                out _,
                out _);
            connection.Open();
            using var command = SqliteConnectionPolicy.CreateCommand(connection);
            command.CommandText = "SELECT 1;";
            _ = command.ExecuteScalar();
            ok = true;
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException or Microsoft.Data.Sqlite.SqliteException or InvalidOperationException)
        {
            probeError = ex.GetType().Name;
        }

        error = probeError;
        return ok;
    }


    private QueuedBatchRequestRegistration? TryRegisterQueuedBatchRequest(string requestKey)
    {
        var cancellation = CancellationTokenSource.CreateLinkedTokenSource(
            _currentRequestToken.Value,
            _shutdownCts.Token);
        var registration = new QueuedBatchRequestRegistration(this, requestKey, cancellation);
        if (!_queuedBatchRequests.TryAdd(requestKey, registration))
        {
            registration.DisposeIfUnclaimed();
            return null;
        }

        if (TryConsumePendingRequestCancellation(requestKey))
            registration.TryCancel();
        return registration;
    }

    private JsonNode? BuildBatchResponse(
        IReadOnlyList<JsonNode?> responsesByIndex,
        IReadOnlyList<BatchResponseBudgetSlot?>? budgetSlots,
        IReadOnlyList<int?>? responseItemLimits,
        int batchResponseLimit)
    {
        var responses = new JsonArray();
        for (var index = 0; index < responsesByIndex.Count; index++)
        {
            var response = responsesByIndex[index];
            if (response is not null
                && budgetSlots?[index] is { } slot
                && responseItemLimits?[index] is { } itemResponseLimit
                && !TryMeasureJsonUtf8BytesWithinLimit(
                    response,
                    _jsonOptions,
                    itemResponseLimit,
                    out _)
                && (slot.CanShapeResourcesReadResponse
                    || (slot.CanShapeResourcesListResponse
                        && IsResourcesListSuccessResponse(response))))
            {
                response = slot.ErrorResponse;
            }

            if (response is not null)
                responses.Add(response);
        }

        if (responses.Count == 0)
            return null;
        if (batchResponseLimit > 0
            && !TryMeasureJsonUtf8BytesWithinLimit(responses, _jsonOptions, batchResponseLimit, out _))
        {
            // Generic and state-changing responses are never rewritten item-by-item. If their
            // aggregate exceeds the cap, report an unknown completion state so clients do not
            // retry effects unsafely. generic / state-changing response は item ごとに書き換えず、
            // aggregate 超過時は completion unknown を返して危険な retry を防ぐ。
            return CreateBatchEnvelopeBudgetError(batchResponseLimit, retrySafe: false);
        }
        return responses;
    }

    private static JsonObject CreateInvalidBatchItemResponse(bool nestedBatch)
        => CreateErrorResponse(
            hasId: true,
            id: null,
            code: -32600,
            message: nestedBatch ? "Invalid request: nested batches are not supported" : "Invalid request: expected JSON object",
            category: McpErrorEnvelope.CategoryInvalidRequest,
            suggestion: nestedBatch
                ? "JSON-RPC batch items must be request objects, not nested arrays."
                : "Each JSON-RPC batch item must be a request object.",
            retrySafe: false);

    private static bool IsCancellationItem(JsonObject item)
        => TryGetStringMember(item, "method") is "$/cancelRequest" or "notifications/cancelled";

    private async Task ExecuteBatchSegmentAsync(
        JsonArray batch,
        IReadOnlyList<int> indexes,
        bool isolateRequestDb,
        JsonNode?[] responsesByIndex,
        DeferredFrameLogBuffer?[] logsByIndex,
        QueuedBatchRequestRegistration?[] queuedRegistrations,
        int?[]? responseItemMaxBytes,
        DeferredInitializeCommits? deferredInitializeCommits,
        Func<CancellationToken, Task>? beforeDispatchAsync)
    {
        if (indexes.Count == 0)
            return;

        var nextIndex = -1;
        var workers = new Task[Math.Min(indexes.Count, MaxConcurrency)];
        for (var workerIndex = 0; workerIndex < workers.Length; workerIndex++)
        {
            workers[workerIndex] = Task.Run(async () =>
            {
                while (true)
                {
                    var segmentIndex = Interlocked.Increment(ref nextIndex);
                    if (segmentIndex >= indexes.Count)
                        return;

                    var batchIndex = indexes[segmentIndex];
                    await ExecuteBatchItemAsync(
                        batch[batchIndex]!,
                        batchIndex,
                        isolateRequestDb,
                        responsesByIndex,
                        logsByIndex,
                        beforeDispatchAsync,
                        queuedRegistrations[batchIndex],
                        responseItemMaxBytes?[batchIndex],
                        deferredInitializeCommits).ConfigureAwait(false);
                }
            }, CancellationToken.None);
        }

        await Task.WhenAll(workers).ConfigureAwait(false);
    }

    private async Task ExecuteBatchItemAsync(
        JsonNode item,
        int index,
        bool isolateRequestDb,
        JsonNode?[] responsesByIndex,
        DeferredFrameLogBuffer?[] logsByIndex,
        Func<CancellationToken, Task>? beforeDispatchAsync,
        QueuedBatchRequestRegistration? queuedBatchRegistration,
        int? responseItemMaxBytes,
        DeferredInitializeCommits? deferredInitializeCommits)
    {
        var result = await ExecuteBatchItemAsync(
            item,
            index,
            isolateRequestDb,
            beforeDispatchAsync,
            rejectForCapacity: false,
            queuedBatchRegistration,
            responseItemMaxBytes,
            deferredInitializeCommits).ConfigureAwait(false);
        responsesByIndex[index] = result.Response;
        logsByIndex[index] = result.Logs;
    }

    private async Task<(JsonNode? Response, DeferredFrameLogBuffer Logs)> ExecuteBatchItemAsync(
        JsonNode item,
        int index,
        bool isolateRequestDb,
        Func<CancellationToken, Task>? beforeDispatchAsync,
        bool rejectForCapacity,
        QueuedBatchRequestRegistration? queuedBatchRegistration,
        int? responseItemMaxBytes,
        DeferredInitializeCommits? deferredInitializeCommits)
    {
        var parentLogs = _deferredFrameLogs.Value;
        var previousBatchResponseItemMaxBytes = _currentBatchResponseItemMaxBytes.Value;
        var itemLogs = new DeferredFrameLogBuffer();
        _deferredFrameLogs.Value = itemLogs;
        _currentBatchResponseItemMaxBytes.Value = responseItemMaxBytes;
        Database.DbDebug.ResetContext();
        ExtractResponseId(item, out var hasId, out var id);
        var hasTelemetryRequestId = item is JsonObject itemObject
            && TryGetRequestId(itemObject, out var itemHasId, out _)
            && itemHasId;
        using var correlationScope = BeginBatchItemCorrelation(id, index, hasTelemetryRequestId);
        try
        {
            var response = await HandleMessageAsync(
                item,
                isolateRequestDb,
                beforeDispatchAsync,
                rejectForCapacity,
                queuedBatchRegistration,
                deferredInitializeCommits).ConfigureAwait(false);
            return (response, itemLogs);
        }
        catch (Exception ex)
        {
            DeferFrameLog(BuildUnhandledLoopErrorLog(DiagnosticRedactor.FormatExceptionMessage(ex)));
            if (!hasId)
                return (null, itemLogs);

            var classification = McpErrorEnvelope.ClassifyException(ex);
            return (CreateErrorResponse(
                hasId: true,
                id,
                classification.JsonRpcCode,
                BuildSanitizedLoopErrorMessage(ex),
                category: classification.Category,
                suggestion: classification.Suggestion,
                retrySafe: classification.RetrySafe), itemLogs);
        }
        finally
        {
            Database.DbDebug.ResetContext();
            _currentBatchResponseItemMaxBytes.Value = previousBatchResponseItemMaxBytes;
            _deferredFrameLogs.Value = parentLogs;
            queuedBatchRegistration?.DisposeIfUnclaimed();
        }
    }

    private void MergeBatchItemLogs(IReadOnlyList<DeferredFrameLogBuffer?> logsByIndex)
    {
        var parentLogs = _deferredFrameLogs.Value;
        Action<Action> forward = parentLogs is null
            ? static log => log()
            : parentLogs.Add;
        foreach (var itemLogs in logsByIndex)
            itemLogs?.ForwardTo(forward);
    }

    private bool TryCreateBatchResponseBudgetSlot(JsonNode? item, out BatchResponseBudgetSlot slot)
    {
        slot = default;
        if (!BatchItemRequiresResponse(item, out var responseId))
            return false;

        var canShapeResourcesListResponse = CanShapeResourcesListResponse(item);
        var canShapeResourcesReadResponse = CanShapeResourcesReadResponse(item);
        var errorResponse = canShapeResourcesReadResponse
            ? CreateResourceReadBatchItemBudgetError(responseId)
            : CreateBatchItemBudgetError(responseId);
        _ = TryMeasureJsonUtf8BytesWithinLimit(errorResponse, _jsonOptions, int.MaxValue, out var errorResponseBytes);
        slot = new BatchResponseBudgetSlot(
            errorResponse,
            errorResponseBytes,
            canShapeResourcesListResponse,
            canShapeResourcesReadResponse);
        return true;
    }

    private static bool CanShapeResourcesListResponse(JsonNode? item)
        => item is JsonObject request
            && TryGetRequestId(request, out var hasId, out _)
            && hasId
            && TryGetStringMember(request, "jsonrpc") == "2.0"
            && TryGetStringMember(request, "method") == "resources/list";

    private static bool CanShapeResourcesReadResponse(JsonNode? item)
        => item is JsonObject request
            && TryGetRequestId(request, out var hasId, out _)
            && hasId
            && TryGetStringMember(request, "jsonrpc") == "2.0"
            && TryGetStringMember(request, "method") == "resources/read";

    private static bool IsResourcesListSuccessResponse(JsonNode response)
        => response is JsonObject responseObject
            && responseObject["result"] is JsonObject result
            && result["resources"] is JsonArray;

    private static bool BatchItemRequiresResponse(JsonNode? item, out JsonNode? responseId)
    {
        responseId = null;
        if (item is not JsonObject request)
            return true;

        if (!TryGetRequestId(request, out var hasId, out var id)
            || TryGetStringMember(request, "jsonrpc") != "2.0")
        {
            return true;
        }

        var method = TryGetStringMember(request, "method");
        if (method is "$/cancelRequest"
            or "notifications/cancelled"
            or "notifications/initialized"
            or "notifications/roots/list_changed"
            or "notifications/shutdown"
            or "notifications/exit")
        {
            return false;
        }
        if (!hasId)
            return false;

        responseId = McpJsonNode.Clone(id);
        return true;
    }

    private static JsonObject CreateBatchItemBudgetError(JsonNode? id)
        => CreateErrorResponse(
            hasId: true,
            id,
            code: -32603,
            message: "resources/list could not fit within its share of the active batch response byte limit.",
            category: McpErrorEnvelope.CategoryInvalidArgument,
            suggestion: "Request a smaller resources/list page, split the batch, or raise the applicable MCP or transport response byte limit.",
            retrySafe: true,
            extraData: new JsonObject
            {
                ["reason"] = "batch_response_budget_exceeded",
            });

    private static JsonObject CreateResourceReadBatchItemBudgetError(JsonNode? id)
        => CreateErrorResponse(
            hasId: true,
            id,
            code: -32603,
            message: "Batch response budget too small.",
            category: McpErrorEnvelope.CategoryInternalError,
            suggestion: "Use a smaller JSON-RPC batch and retry.",
            retrySafe: false,
            extraData: new JsonObject
            {
                ["reason"] = "batch_response_budget_too_small",
            });

    private static JsonObject CreateBatchEnvelopeBudgetError(int batchResponseLimit, bool retrySafe)
        => CreateErrorResponse(
            hasId: true,
            id: null,
            code: -32603,
            message: retrySafe
                ? "The JSON-RPC batch cannot fit within the active response byte limit."
                : "The completed JSON-RPC batch exceeded the active response byte limit.",
            category: McpErrorEnvelope.CategoryInvalidArgument,
            suggestion: retrySafe
                ? "Split the batch into fewer requests or raise the applicable MCP or transport response byte limit."
                : "Do not automatically retry state-changing items; their completion state is unknown. Split future batches into fewer requests.",
            retrySafe,
            extraData: new JsonObject
            {
                ["reason"] = retrySafe
                    ? "batch_response_budget_too_small"
                    : "batch_response_budget_exceeded",
                ["limit_bytes"] = batchResponseLimit,
                ["completion_state"] = retrySafe ? "not_started" : "unknown",
            });

    private readonly record struct BatchResponseBudgetSlot(
        JsonObject ErrorResponse,
        int ErrorResponseBytes,
        bool CanShapeResourcesListResponse,
        bool CanShapeResourcesReadResponse);

    private async Task<JsonNode> DispatchWithRequestCancellationAsync(
        JsonNode? id,
        bool isolateRequestDb,
        Func<CancellationToken, Task>? beforeDispatchAsync,
        QueuedBatchRequestRegistration? queuedBatchRegistration,
        Func<Task<JsonNode>> action)
    {
        var requestKey = SerializeRequestId(id);
        var telemetryRequestId = McpRequestIdTelemetry.Create(id);
        var requestCts = queuedBatchRegistration is null
            ? CancellationTokenSource.CreateLinkedTokenSource(_currentRequestToken.Value, _shutdownCts.Token)
            : CancellationTokenSource.CreateLinkedTokenSource(
                _currentRequestToken.Value,
                _shutdownCts.Token,
                queuedBatchRegistration.Token);
        var registeredRequest = false;
        if (requestKey is not null)
        {
            if (!_activeRequests.TryAdd(requestKey, requestCts))
            {
                requestCts.Dispose();
                return CreateErrorResponse(hasId: true, id: id, code: -32600, message: "Duplicate in-flight request id",
                    category: McpErrorEnvelope.CategoryInvalidRequest,
                    suggestion: "JSON-RPC request ids must be unique while a previous request with the same id is still running.",
                    retrySafe: true);
            }
            registeredRequest = true;
            if (queuedBatchRegistration is not null && !queuedBatchRegistration.TryClaim())
                CancelRequestCts(requestCts);
            if (TryConsumePendingRequestCancellation(requestKey))
                CancelRequestCts(requestCts);
            RequestRegisteredForTests?.Invoke(id);
        }

        var previousToken = _currentRequestToken.Value;
        Stopwatch? stopwatch = null;
        var cleanupNow = true;
        var executionSlotAcquired = false;
        var releaseExecutionSlotNow = true;
        try
        {
            _currentRequestToken.Value = requestCts.Token;
            requestCts.Token.ThrowIfCancellationRequested();
            if (beforeDispatchAsync is not null)
                await beforeDispatchAsync(requestCts.Token).ConfigureAwait(false);
            await _concurrencyGate.WaitAsync(requestCts.Token).ConfigureAwait(false);
            executionSlotAcquired = true;
            requestCts.Token.ThrowIfCancellationRequested();
            stopwatch = Stopwatch.StartNew();

            if (!isolateRequestDb)
            {
                requestCts.CancelAfter(_requestTimeout);
                var previousIsolation = _isolateDbForCurrentRequest.Value;
                _isolateDbForCurrentRequest.Value = false;
                try
                {
                    await DelayRequestForTestsAsync(id, requestCts.Token).ConfigureAwait(false);
                    return await action().ConfigureAwait(false);
                }
                finally
                {
                    _isolateDbForCurrentRequest.Value = previousIsolation;
                }
            }

            var actionTask = Task.Run(async () =>
            {
                var previousIsolation = _isolateDbForCurrentRequest.Value;
                _isolateDbForCurrentRequest.Value = isolateRequestDb;
                try
                {
                    await DelayRequestForTestsAsync(id, requestCts.Token).ConfigureAwait(false);
                    return await action().ConfigureAwait(false);
                }
                finally
                {
                    _isolateDbForCurrentRequest.Value = previousIsolation;
                }
            }, requestCts.Token);
            using var timeoutDelayCts = new CancellationTokenSource();
            var remainingTimeout = _requestTimeout - stopwatch.Elapsed;
            var timeoutTask = remainingTimeout <= TimeSpan.Zero
                ? Task.CompletedTask
                : Task.Delay(remainingTimeout, timeoutDelayCts.Token);
            var cancellationSignal = new TaskCompletionSource<bool>(TaskCreationOptions.RunContinuationsAsynchronously);
            using var cancellationRegistration = requestCts.Token.Register(
                static state => ((TaskCompletionSource<bool>)state!).TrySetResult(true),
                cancellationSignal);
            var cancellationTask = cancellationSignal.Task;
            var completed = await Task.WhenAny(actionTask, timeoutTask, cancellationTask).ConfigureAwait(false);
            try { timeoutDelayCts.Cancel(); }
            catch (ObjectDisposedException) { /* the timeout signal has already completed. */ }
            if (completed == cancellationTask && _shutdownCts.IsCancellationRequested)
            {
                // EOF/server shutdown owns the bounded outer request-task drain. Keep this
                // dispatch attached to non-cooperative work so teardown does not manufacture a
                // late cancellation response or race a terminal protocol-error write (#4543).
                // EOF/server shutdown は外側の bounded request-task drain が所有する。非協調 work を
                // detach せず、遅延 cancel response や terminal protocol-error write との race を防ぐ。
                return await actionTask.ConfigureAwait(false);
            }
            if (completed != actionTask)
            {
                var timedOut = completed == timeoutTask;
                if (timedOut)
                    CancelRequestCts(requestCts);
                var elapsed = stopwatch.Elapsed;
                if (timedOut)
                    RecordTimedOutIsolatedActionDraining(telemetryRequestId, elapsed);
                cleanupNow = false;
                releaseExecutionSlotNow = false;
                _currentDetachedIsolatedActions.Value?.Enqueue(actionTask);
                // This cleanup must run even after request timeout/shutdown cancellation;
                // otherwise `_activeRequests`, the linked CTS, and the execution lease would leak
                // when an isolated action eventually observes cancellation and exits. The lease
                // intentionally remains held until the underlying action actually ends so timeout
                // responses cannot let live handlers exceed MaxConcurrency (#3722, #4536, #4545).
                // request timeout / shutdown cancellation 後でも cleanup は必ず実行する。
                // underlying action が実際に終了するまで execution lease も保持し、timeout response
                // の後に live handler が MaxConcurrency を超えないようにする (#3722, #4536, #4545)。
                _ = actionTask.ContinueWith(task =>
                {
                    try
                    {
                        _ = task.Exception;
                        if (registeredRequest)
                            _activeRequests.TryRemove(requestKey!, out _);
                        if (timedOut)
                            RecordTimedOutIsolatedActionDrained(telemetryRequestId, task);
                    }
                    finally
                    {
                        requestCts.Dispose();
                        _concurrencyGate.Release();
                    }
                }, CancellationToken.None, TaskContinuationOptions.ExecuteSynchronously, TaskScheduler.Default);
                return timedOut
                    ? CreateRequestTimeoutResponse(id, elapsed, isolatedActionDraining: true)
                    : CreateCancelledResponse(id);
            }

            return await actionTask.ConfigureAwait(false);
        }
        catch (OperationCanceledException) when (requestCts.IsCancellationRequested)
        {
            if (stopwatch is not null
                && !previousToken.IsCancellationRequested
                && !_shutdownCts.IsCancellationRequested
                && stopwatch.Elapsed >= _requestTimeout)
                return CreateRequestTimeoutResponse(id, stopwatch.Elapsed);
            return CreateCancelledResponse(id);
        }
        finally
        {
            _currentRequestToken.Value = previousToken;
            if (executionSlotAcquired && releaseExecutionSlotNow)
                _concurrencyGate.Release();
            if (cleanupNow)
            {
                if (registeredRequest)
                    _activeRequests.TryRemove(requestKey!, out _);
                requestCts.Dispose();
            }
        }
    }

    private Task DelayRequestForTestsAsync(JsonNode? id, CancellationToken cancellationToken)
    {
        if (RequestDelayForTestsWithId is { } delayWithId)
            return delayWithId(McpJsonNode.Clone(id), cancellationToken);
        return RequestDelayForTests is { } delay
            ? delay(cancellationToken)
            : Task.CompletedTask;
    }

    private static JsonObject CreateRequestTimeoutResponse(JsonNode? id, TimeSpan elapsed, bool isolatedActionDraining = false)
        => CreateErrorResponse(hasId: true, id: id, code: -32603, message: "Request timed out",
            category: McpErrorEnvelope.CategoryInternalError,
            suggestion: "Retry with a narrower query, refresh the index if it is degraded, or increase the MCP request timeout before retrying.",
            retrySafe: true,
            extraData: new JsonObject
            {
                ["reason"] = "timeout",
                ["timeout_category"] = OperationTimeoutCategories.McpRequest,
                ["elapsed_ms"] = (long)Math.Ceiling(elapsed.TotalMilliseconds),
                ["isolated_action_draining"] = isolatedActionDraining,
            });

    private void RecordTimedOutIsolatedActionDraining(McpRequestIdTelemetryData requestId, TimeSpan elapsed)
    {
        var elapsedMs = (long)Math.Ceiling(elapsed.TotalMilliseconds);
        Interlocked.Increment(ref _timedOutIsolatedActionDrainingCount);
        lock (_requestTimeoutDiagnosticsGate)
        {
            _lastRequestTimeoutDrainDiagnostic = new RequestTimeoutDrainDiagnostic(
                requestId,
                elapsedMs,
                "draining");
        }
        CommandErrorWriter.WriteStderr(BuildTimedOutIsolatedActionDrainingLog(requestId, elapsedMs));
    }

    private void RecordTimedOutIsolatedActionDrained(McpRequestIdTelemetryData requestId, Task task)
    {
        Interlocked.Decrement(ref _timedOutIsolatedActionDrainingCount);
        Interlocked.Increment(ref _timedOutIsolatedActionDrainedCount);
        var state = task.IsCanceled ? "canceled" : task.IsFaulted ? "faulted" : "completed";
        lock (_requestTimeoutDiagnosticsGate)
        {
            _lastRequestTimeoutDrainDiagnostic = new RequestTimeoutDrainDiagnostic(
                requestId,
                null,
                state);
        }
    }

    internal JsonObject BuildRequestTimeoutDiagnosticsStatus()
    {
        RequestTimeoutDrainDiagnostic? last;
        lock (_requestTimeoutDiagnosticsGate)
        {
            last = _lastRequestTimeoutDrainDiagnostic;
        }

        var payload = new JsonObject
        {
            ["isolated_action_draining_count"] = Interlocked.Read(ref _timedOutIsolatedActionDrainingCount),
            ["isolated_action_drained_count"] = Interlocked.Read(ref _timedOutIsolatedActionDrainedCount),
            ["timeout_ms"] = (long)Math.Ceiling(_requestTimeout.TotalMilliseconds),
        };
        if (last is not null)
        {
            payload["last"] = new JsonObject
            {
                ["request_id"] = last.RequestId.Token,
                ["request_id_type"] = last.RequestId.Type,
                ["request_id_length"] = last.RequestId.Length,
                ["elapsed_ms"] = last.ElapsedMs.HasValue ? JsonValue.Create(last.ElapsedMs.Value) : null,
                ["state"] = last.State,
            };
        }
        return payload;
    }

    internal static string BuildTimedOutIsolatedActionDrainingLog(McpRequestIdTelemetryData requestId, long elapsedMs)
        => $"[cdidx-mcp] Request timed out while isolated action is still draining: request_id={requestId.Token} request_id_type={requestId.Type} request_id_length={requestId.Length.ToString(CultureInfo.InvariantCulture)} elapsed_ms={elapsedMs}. The response has been sent; cleanup will continue in the background.";

    private static IDisposable BeginRequestCorrelation(JsonNode? id, bool includeRequestId = true)
    {
        var previous = CurrentCorrelationContext.Value;
        CurrentCorrelationContext.Value = new RequestCorrelationContext(
            SerializeRequestId(id),
            includeRequestId ? McpRequestIdTelemetry.Create(id) : null,
            Guid.NewGuid().ToString("D"));
        return new CorrelationScope(previous);
    }

    private static IDisposable BeginBatchItemCorrelation(JsonNode? id, int itemIndex, bool includeRequestId = false)
    {
        var previous = CurrentCorrelationContext.Value;
        var correlationId = previous is null
            ? Guid.NewGuid().ToString("D")
            : $"{previous.CorrelationId}.{itemIndex.ToString(System.Globalization.CultureInfo.InvariantCulture)}";
        CurrentCorrelationContext.Value = new RequestCorrelationContext(
            SerializeRequestId(id),
            includeRequestId ? McpRequestIdTelemetry.Create(id) : null,
            correlationId);
        return new CorrelationScope(previous);
    }

    private static IDisposable BeginChildCorrelation(int childIndex)
    {
        var previous = CurrentCorrelationContext.Value;
        var requestId = previous?.WireRequestId;
        var telemetryRequestId = previous?.TelemetryRequestId;
        var correlationId = previous == null
            ? Guid.NewGuid().ToString("D")
            : $"{previous.CorrelationId}.{childIndex.ToString(System.Globalization.CultureInfo.InvariantCulture)}";
        CurrentCorrelationContext.Value = new RequestCorrelationContext(requestId, telemetryRequestId, correlationId);
        return new CorrelationScope(previous);
    }

    private sealed record RequestCorrelationContext(
        string? WireRequestId,
        McpRequestIdTelemetryData? TelemetryRequestId,
        string CorrelationId);
    private sealed record RequestTimeoutDrainDiagnostic(
        McpRequestIdTelemetryData RequestId,
        long? ElapsedMs,
        string State);

    private sealed class CorrelationScope : IDisposable
    {
        private readonly RequestCorrelationContext? _previous;
        private bool _disposed;

        public CorrelationScope(RequestCorrelationContext? previous)
        {
            _previous = previous;
        }

        public void Dispose()
        {
            if (_disposed)
                return;
            CurrentCorrelationContext.Value = _previous;
            _disposed = true;
        }
    }

    private void TryCancelRequest(JsonNode? cancelParams)
    {
        var requestId = cancelParams?["id"] ?? cancelParams?["requestId"];
        var requestKey = SerializeRequestId(requestId);
        if (requestKey == null)
            return;
        if (_activeRequests.TryGetValue(requestKey, out var cts))
        {
            CancelRequestCts(cts);
            return;
        }
        if (_queuedBatchRequests.TryGetValue(requestKey, out var queuedRequest)
            && queuedRequest.TryCancel())
        {
            return;
        }

        CancellationRegistriesMissedForTests?.Invoke();
        RememberPendingRequestCancellation(requestKey);
        if (_activeRequests.TryGetValue(requestKey, out cts))
        {
            _ = TryConsumePendingRequestCancellation(requestKey);
            CancelRequestCts(cts);
            return;
        }
        if (_queuedBatchRequests.TryGetValue(requestKey, out queuedRequest))
        {
            // The target can enter the durable registry after the first lookup but before the
            // bounded tombstone insertion. Recheck it independently of tombstone capacity so a
            // full cache cannot discard cancellation for an already-queued batch item (#4545).
            // target は初回 lookup 後、bounded tombstone 挿入前に durable registry へ入り得る。
            // tombstone capacity と独立して再確認し、満杯でも登録済み batch item の cancel を
            // 失わないようにする (#4545)。
            _ = TryConsumePendingRequestCancellation(requestKey);
            if (queuedRequest.TryCancel())
                return;
            if (_activeRequests.TryGetValue(requestKey, out cts))
            {
                CancelRequestCts(cts);
                return;
            }
        }
    }

    private void RememberPendingRequestCancellation(string requestKey)
    {
        var now = _timeProvider.GetUtcNow();
        PrunePendingRequestCancellations(now);
        if (_pendingRequestCancellations.Count < MaxPendingRequestCancellationCount)
            _pendingRequestCancellations[requestKey] = now;
    }

    private bool TryConsumePendingRequestCancellation(string requestKey)
    {
        var now = _timeProvider.GetUtcNow();
        PrunePendingRequestCancellations(now);
        if (!_pendingRequestCancellations.TryGetValue(requestKey, out var cancelledAt))
            return false;
        if (now - cancelledAt > PendingRequestCancellationTtl)
        {
            _pendingRequestCancellations.TryRemove(requestKey, out _);
            return false;
        }

        return _pendingRequestCancellations.TryRemove(requestKey, out _);
    }

    private void PrunePendingRequestCancellations(DateTimeOffset now)
    {
        foreach (var entry in _pendingRequestCancellations)
        {
            if (now - entry.Value > PendingRequestCancellationTtl)
                _pendingRequestCancellations.TryRemove(entry.Key, out _);
        }
    }

    private static void CancelRequestCts(CancellationTokenSource cts)
    {
        try { cts.Cancel(); }
        catch (ObjectDisposedException) { /* completed while cancellation was being delivered. */ }
    }

    private static bool IsCancellationFrame(string frame)
    {
        if (!JsonFrameParser.TryParseNode(frame, MaxJsonDepth, out var node, out _)
            || node is not JsonObject obj)
            return false;

        var method = TryGetStringMember(obj, "method");
        return string.Equals(method, "$/cancelRequest", StringComparison.Ordinal)
            || string.Equals(method, "notifications/cancelled", StringComparison.Ordinal);
    }

    private static bool IsProtocolOrderingBarrierFrame(string frame)
    {
        if (!JsonFrameParser.TryParseNode(frame, MaxJsonDepth, out var node, out _))
            return false;

        if (node is JsonArray batch)
            return batch.Any(IsProtocolOrderingBarrierItem);
        return IsProtocolOrderingBarrierItem(node);
    }

    private static bool IsProtocolOrderingBarrierItem(JsonNode? node)
    {
        if (node is not JsonObject obj)
            return false;

        return TryGetStringMember(obj, "method") switch
        {
            "initialize" or
            "logging/setLevel" or
            "notifications/initialized" or
            "notifications/roots/list_changed" or
            "notifications/shutdown" or
            "notifications/exit" => true,
            _ => false,
        };
    }

    // Safe accessor that returns null instead of throwing when `name` is missing OR present
    // with a non-string value. JsonNode's `GetValue<string>()` throws InvalidOperationException
    // on non-string scalars, which would bubble out of HandleMessage and turn into -32603
    // before the auth gate runs.
    // `name` が無いケースと文字列以外で存在するケースのどちらでも null を返す安全アクセサ。
    // JsonNode の `GetValue<string>()` は非文字列で例外を投げ、認証ゲート前に -32603 化して
    // しまう。
    private static string? TryGetStringMember(JsonObject obj, string name)
    {
        if (!obj.TryGetPropertyValue(name, out var node) || node is null)
            return null;
        try
        {
            return node.GetValue<string>();
        }
        catch
        {
            return null;
        }
    }

    // Cap on the logged `method` label. Long enough for every spec method (`notifications/cancelled`
    // is 23 chars) and any plausible client extension, short enough to keep one log line readable.
    // ログ出力する `method` の長さ上限。仕様メソッド全てと拡張も収まる長さで、1 行を読みやすく保つ。
    private const int LoggedMethodMaxLength = 64;

    // Strip caller-controlled control characters from `method` and clamp its length before
    // interpolating into a stderr log line. Prevents log forging: a malicious client could
    // otherwise send `"method":"evil\n[forged]"` and split the diagnostic across two lines
    // (#1559).
    // stderr 行に method を埋め込む前に制御文字を除去し、長さを切る。これをしないと
    // `"method":"evil\n[forged]"` で診断ログを 2 行に分割するログ偽造ができてしまう (#1559)。
    internal static string SanitizeMethodForLog(string? method)
    {
        if (string.IsNullOrEmpty(method))
            return "(none)";
        var sb = new StringBuilder(Math.Min(method.Length, LoggedMethodMaxLength));
        var truncated = false;
        foreach (var ch in method)
        {
            if (sb.Length >= LoggedMethodMaxLength)
            {
                truncated = true;
                break;
            }
            if (ch < 0x20 || ch == 0x7F)
                sb.Append('?');
            else
                sb.Append(ch);
        }
        if (truncated)
            sb.Append('…');
        return sb.ToString();
    }

    // Stderr log for an auth failure. Mirrors the #1530 sanitization pattern: keep the
    // wire response generic and put the detail on stderr for local diagnostics. The method
    // label is run through SanitizeMethodForLog because it is caller-controlled and reaches
    // stderr before any allow-list check (#1559).
    // 認証失敗の stderr ログ。#1530 のサニタイズ方針に倣い、ワイヤ応答は一般化したまま
    // 詳細だけを stderr に残す。method は認証前に通るため SanitizeMethodForLog で
    // 制御文字除去と長さ切詰めを行う (#1559)。
    internal static string BuildAuthFailureLog(string? method, string? reason) =>
        $"[cdidx-mcp] Auth failed for method {SanitizeMethodForLog(method)}: {reason ?? "(unspecified)"}. Set CDIDX_MCP_AUTH_TOKEN on the server and include a matching params.auth.token on each request.";

}

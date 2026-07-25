using System.Text.Json.Nodes;

namespace CodeIndex.Mcp;

public partial class McpServer
{
    private sealed record McpRequestEnvelope(
        JsonObject Object,
        string? Method,
        bool HasId,
        JsonNode? Id);

    private async Task<JsonNode?> HandleMessageAsync(
        JsonNode request,
        bool isolateRequestDb,
        Func<CancellationToken, Task>? beforeDispatchAsync,
        bool rejectForCapacity,
        QueuedBatchRequestRegistration? queuedBatchRegistration,
        DeferredInitializeCommits? deferredInitializeCommits)
    {
        if (request is JsonArray batch)
        {
            return await HandleBatchFrameAsync(
                batch,
                isolateRequestDb,
                beforeDispatchAsync,
                rejectForCapacity,
                deferredInitializeCommits).ConfigureAwait(false);
        }

        if (!TryCreateRequestEnvelope(request, out var envelope, out var validationError))
            return validationError;

        using var correlationScope = envelope!.HasId && CurrentCorrelationContext.Value is null
            ? BeginRequestCorrelation(envelope.Id)
            : null;
        if (await TryHandleNotificationAsync(
                envelope,
                beforeDispatchAsync,
                rejectForCapacity).ConfigureAwait(false))
        {
            return null;
        }

        if (TryAuthenticateRespondedRequest(envelope, out var authenticationError))
            return authenticationError;
        if (rejectForCapacity)
            return CreateServerBusyResponse(envelope.Id);
        if (envelope.Method is null)
        {
            return CreateErrorResponse(hasId: true, id: envelope.Id, code: -32600, message: "Invalid request: missing method",
                category: McpErrorEnvelope.CategoryInvalidRequest,
                suggestion: "JSON-RPC 2.0 requires a string `method` field.",
                retrySafe: false);
        }

        return await DispatchRespondedRequestAsync(
            envelope,
            isolateRequestDb,
            beforeDispatchAsync,
            queuedBatchRegistration,
            deferredInitializeCommits).ConfigureAwait(false);
    }

    private async Task<JsonNode?> HandleBatchFrameAsync(
        JsonArray batch,
        bool isolateRequestDb,
        Func<CancellationToken, Task>? beforeDispatchAsync,
        bool rejectForCapacity,
        DeferredInitializeCommits? deferredInitializeCommits)
    {
        if (deferredInitializeCommits is null)
        {
            return await HandleBatchMessageAsync(
                batch,
                isolateRequestDb,
                beforeDispatchAsync,
                rejectForCapacity,
                deferredInitializeCommits).ConfigureAwait(false);
        }

        var previousFrameInitializeState = _frameInitializeState.Value;
        var initialFrameInitializeState = CurrentInitializeState;
        var frameInitializeState = new FrameInitializeState(
            initialFrameInitializeState,
            isProvisionalGeneration: false);
        _frameInitializeState.Value = frameInitializeState;
        var batchBeforeDispatchAsync = beforeDispatchAsync;
        if (beforeDispatchAsync is not null)
        {
            batchBeforeDispatchAsync = async cancellationToken =>
            {
                await beforeDispatchAsync(cancellationToken).ConfigureAwait(false);
                // The concurrent loop accepts and pre-registers a batch before its protocol
                // predecessor finishes. Advance only this batch's original generation after
                // that predecessor commits; timed-out older frames retain their own holders,
                // and an in-batch initialize replaces this holder instead of being overwritten.
                // concurrent loop は protocol predecessor 完了前に batch を受理・事前登録する。
                // predecessor の commit 後、この batch の元 generation だけを進める。timeout
                // 後の旧 frame は別 holder を保持し、batch 内 initialize は holder 自体を置換する。
                frameInitializeState.TryAdvanceToPublishedGeneration(
                    initialFrameInitializeState,
                    PublishedInitializeState);
            };
        }
        try
        {
            return await HandleBatchMessageAsync(
                batch,
                isolateRequestDb,
                batchBeforeDispatchAsync,
                rejectForCapacity,
                deferredInitializeCommits).ConfigureAwait(false);
        }
        finally
        {
            _frameInitializeState.Value = previousFrameInitializeState;
        }
    }

    private bool TryCreateRequestEnvelope(
        JsonNode request,
        out McpRequestEnvelope? envelope,
        out JsonNode? validationError)
    {
        envelope = null;
        if (request is not JsonObject obj)
        {
            validationError = CreateExpectedJsonObjectErrorResponse();
            return false;
        }

        lock (_healthStateGate)
            _lastRequestAt = _timeProvider.GetUtcNow();

        // Extract `method` defensively: a non-string `method` (e.g. `"method":42`) must not
        // throw before the auth gate runs, otherwise a token-protected server would surface
        // an internal error to an unauthenticated caller and leak dispatch internals (#1559).
        // `method` は防御的に取り出し、非文字列でも認証ゲート前に例外を投げない (#1559)。
        var method = TryGetStringMember(obj, "method");
        if (!TryGetRequestId(obj, out var hasId, out var id, out var idError))
        {
            validationError = CreateErrorResponse(hasId: true, id: null, code: -32600, message: BuildInvalidRequestIdMessage(idError),
                category: McpErrorEnvelope.CategoryInvalidRequest,
                suggestion: BuildInvalidRequestIdSuggestion(idError),
                retrySafe: false,
                extraData: BuildInvalidRequestIdData(idError));
            return false;
        }

        if (TryGetStringMember(obj, "jsonrpc") != "2.0")
        {
            validationError = CreateErrorResponse(hasId: true, id: null, code: -32600, message: "Invalid request: jsonrpc must be exactly \"2.0\"",
                category: McpErrorEnvelope.CategoryInvalidRequest,
                suggestion: "Set the top-level `jsonrpc` member to the string `2.0`.",
                retrySafe: false);
            return false;
        }

        envelope = new McpRequestEnvelope(obj, method, hasId, id);
        validationError = null;
        return true;
    }

    private async Task<bool> TryHandleNotificationAsync(
        McpRequestEnvelope request,
        Func<CancellationToken, Task>? beforeDispatchAsync,
        bool rejectForCapacity)
    {
        var method = request.Method;
        // A JSON-RPC notification cannot carry an error response, but state-changing
        // notifications must still authenticate before mutating cancellation, roots, or lifecycle
        // state. On denial, emit only the bounded local diagnostic (#4537).
        // JSON-RPC notification はエラー応答を持てないが、state-changing notification は
        // server state を変更する前に認証し、拒否時は bounded なローカル診断だけを残す。
        if (IsStateChangingNotification(method))
        {
            var notificationAuth = _authenticator.Authenticate(request.Object);
            if (!notificationAuth.IsAuthenticated)
            {
                WriteMcpLogLine(BuildAuthFailureLog(method, notificationAuth.FailureReason));
                return true;
            }
        }

        if (method == "$/cancelRequest" || method == "notifications/cancelled")
        {
            TryCancelRequest(request.Object["params"]);
            return true;
        }

        if (rejectForCapacity && IsStateChangingNotification(method))
        {
            // Eager cancellation is handled above. Other state notifications are dropped on
            // admission overflow without mutating roots or lifecycle state (#4536, #4545).
            return true;
        }

        var protocolPredecessorAwaited = false;
        if (IsStateChangingNotification(method) && beforeDispatchAsync is not null)
        {
            // Cancellation controls intentionally bypass protocol barriers, but roots/lifecycle
            // notifications must not mutate state before an earlier initialize commits.
            await beforeDispatchAsync(_currentRequestToken.Value).ConfigureAwait(false);
            protocolPredecessorAwaited = true;
        }

        if (!request.HasId)
        {
            if (rejectForCapacity)
                return true;
            if (!protocolPredecessorAwaited && beforeDispatchAsync is not null)
                await beforeDispatchAsync(_currentRequestToken.Value).ConfigureAwait(false);
        }

        if (method == "notifications/initialized")
            return true;
        if (method == "notifications/roots/list_changed")
        {
            MarkClientRootsStale();
            _frameInitializeState.Value?.MarkRootsChangeAccepted();
            return true;
        }

        if (string.Equals(method, "notifications/shutdown", StringComparison.Ordinal)
            || string.Equals(method, "notifications/exit", StringComparison.Ordinal))
        {
            WriteMcpLogLine($"[cdidx-mcp] Received {method}; draining in-flight work and shutting down.");
            _running = false;
            _ = RequestShutdownCancellation();
            return true;
        }

        if (request.HasId)
            return false;
        if (method != null && method.StartsWith("notifications/", StringComparison.OrdinalIgnoreCase))
            WriteMcpLogLine(BuildUnknownNotificationLog(method));
        return true;
    }

    private bool TryAuthenticateRespondedRequest(
        McpRequestEnvelope request,
        out JsonNode? authenticationError)
    {
        // Authenticate every responded request before dispatch, even when `method` is missing or
        // malformed, so token-protected servers do not leak method-shape errors (#1559).
        var authResult = _authenticator.Authenticate(request.Object);
        if (authResult.IsAuthenticated)
        {
            authenticationError = null;
            return false;
        }

        DeferFrameLog(BuildAuthFailureLog(request.Method, authResult.FailureReason));
        authenticationError = CreateErrorResponse(
            hasId: true,
            id: request.Id,
            code: McpErrorEnvelope.CodeUnauthorized,
            message: "Unauthorized",
            category: McpErrorEnvelope.CategoryPermissionDenied,
            suggestion: "Set CDIDX_MCP_AUTH_TOKEN on the server and include a matching params.auth.token (or an `Authorization: Bearer <token>` header for HTTP) on each request.",
            retrySafe: false);
        return true;
    }

    private Task<JsonNode> DispatchRespondedRequestAsync(
        McpRequestEnvelope request,
        bool isolateRequestDb,
        Func<CancellationToken, Task>? beforeDispatchAsync,
        QueuedBatchRequestRegistration? queuedBatchRegistration,
        DeferredInitializeCommits? deferredInitializeCommits)
        => DispatchWithRequestCancellationAsync(
            request.Id,
            isolateRequestDb,
            beforeDispatchAsync,
            queuedBatchRegistration,
            () => DispatchRequestMethodAsync(request, deferredInitializeCommits));

    private Task<JsonNode> DispatchRequestMethodAsync(
        McpRequestEnvelope request,
        DeferredInitializeCommits? deferredInitializeCommits)
    {
        var method = request.Method!;
        if (_enforceInitializationLifecycle && !CurrentInitializeState.Initialized && method != "initialize")
        {
            return Task.FromResult<JsonNode>(CreateErrorResponse(
                hasId: true,
                id: request.Id,
                code: -32002,
                message: "Server not initialized",
                category: McpErrorEnvelope.CategoryInvalidRequest,
                suggestion: "Send a successful `initialize` request before calling other MCP methods.",
                retrySafe: true));
        }

        return method switch
        {
            "initialize" => Task.FromResult<JsonNode>(HandleInitialize(
                request.Id,
                request.Object["params"],
                deferredInitializeCommits)),
            "tools/list" => Task.FromResult<JsonNode>(HandleToolsList(request.Id, request.Object["params"])),
            "tools/call" => HandleToolsCallAsync(request.HasId, request.Id, request.Object["params"]),
            "resources/list" => Task.FromResult<JsonNode>(HandleResourcesList(request.Id, request.Object["params"])),
            "resources/templates/list" => Task.FromResult<JsonNode>(HandleResourceTemplatesList(request.Id, request.Object["params"])),
            "resources/read" => Task.FromResult<JsonNode>(HandleResourcesRead(request.Id, request.Object["params"])),
            "prompts/list" => Task.FromResult<JsonNode>(HandlePromptsList(request.Id)),
            "prompts/get" => Task.FromResult<JsonNode>(HandlePromptsGet(request.Id, request.Object["params"])),
            "logging/setLevel" => HandleLoggingSetLevelAsync(request.Id, request.Object["params"]),
            "ping" => Task.FromResult<JsonNode>(CreateSuccessResponse(request.HasId, request.Id, BuildHealthResult())),
            _ => Task.FromResult<JsonNode>(CreateErrorResponse(
                hasId: true,
                id: request.Id,
                code: -32601,
                message: $"Method not found: {method}",
                category: McpErrorEnvelope.CategoryMethodNotFound,
                suggestion: "Supported methods: initialize, tools/list, tools/call, resources/list, resources/templates/list, resources/read, prompts/list, prompts/get, logging/setLevel, ping, notifications/initialized, notifications/cancelled, notifications/roots/list_changed, notifications/shutdown.",
                retrySafe: false)),
        };
    }
}

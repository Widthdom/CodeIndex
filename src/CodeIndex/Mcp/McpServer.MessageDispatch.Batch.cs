using System.Text.Json.Nodes;

namespace CodeIndex.Mcp;

public partial class McpServer
{
    private sealed record BatchResponseBudgetPlan(
        BatchResponseBudgetSlot?[]? Slots,
        int?[]? ItemLimits,
        JsonObject? PreflightError,
        int ResponseLimit);

    private sealed class BatchDispatchState
    {
        public BatchDispatchState(
            JsonArray batch,
            bool isolateRequestDb,
            bool[] completed,
            BatchResponseBudgetPlan budget)
        {
            Batch = batch;
            Completed = completed;
            Budget = budget;
            IsolateItems = isolateRequestDb || batch.Count > 1;
            Responses = new JsonNode?[batch.Count];
            Logs = new DeferredFrameLogBuffer?[batch.Count];
            OrderingFences = new bool[batch.Count];
            CancellationItems = new bool[batch.Count];
            QueuedRegistrations = new QueuedBatchRequestRegistration?[batch.Count];
        }

        public JsonArray Batch { get; }
        public bool[] Completed { get; }
        public BatchResponseBudgetPlan Budget { get; }
        public bool IsolateItems { get; }
        public JsonNode?[] Responses { get; }
        public DeferredFrameLogBuffer?[] Logs { get; }
        public bool[] OrderingFences { get; }
        public bool[] CancellationItems { get; }
        public QueuedBatchRequestRegistration?[] QueuedRegistrations { get; }

        public void Complete(int index, (JsonNode? Response, DeferredFrameLogBuffer Logs) result)
        {
            Responses[index] = result.Response;
            Logs[index] = result.Logs;
            Completed[index] = true;
        }

        public void DisposeQueuedRegistrations()
        {
            foreach (var registration in QueuedRegistrations)
                registration?.DisposeIfUnclaimed();
        }
    }

    private async Task<JsonNode?> HandleBatchMessageAsync(
        JsonArray batch,
        bool isolateRequestDb,
        Func<CancellationToken, Task>? beforeDispatchAsync,
        bool rejectForCapacity,
        DeferredInitializeCommits? deferredInitializeCommits)
    {
        if (TryValidateBatchRequest(batch) is { } validationError)
            return validationError;

        var completed = ConsumeCompletedClientReplies(batch);
        var state = new BatchDispatchState(
            batch,
            isolateRequestDb,
            completed,
            CreateBatchResponseBudgetPlan(batch, completed));

        PrepareBatchItems(state, rejectForCapacity);
        await ExecuteBatchCancellationItemsAsync(state, deferredInitializeCommits).ConfigureAwait(false);

        if (state.Budget.PreflightError is not null)
        {
            state.DisposeQueuedRegistrations();
            MergeBatchItemLogs(state.Logs);
            return state.Budget.PreflightError;
        }

        if (rejectForCapacity)
        {
            await ExecuteCapacityRejectedBatchAsync(state, deferredInitializeCommits).ConfigureAwait(false);
        }
        else
        {
            await ExecuteOrderedBatchItemsAsync(
                state,
                beforeDispatchAsync,
                deferredInitializeCommits).ConfigureAwait(false);
        }

        MergeBatchItemLogs(state.Logs);
        return BuildBatchResponse(
            state.Responses,
            state.Budget.Slots,
            state.Budget.ItemLimits,
            state.Budget.ResponseLimit);
    }

    private JsonObject? TryValidateBatchRequest(JsonArray batch)
    {
        if (batch.Count == 0)
        {
            return CreateErrorResponse(hasId: true, id: null, code: -32600, message: "Invalid request: empty batch",
                category: McpErrorEnvelope.CategoryInvalidRequest,
                suggestion: "JSON-RPC 2.0 batch requests must contain at least one request object.",
                retrySafe: false);
        }

        if (batch.Count <= MaxBatchRequestCount)
            return null;

        return CreateErrorResponse(hasId: true, id: null, code: -32600, message: "Invalid request: batch too large",
            category: McpErrorEnvelope.CategoryInvalidRequest,
            suggestion: $"JSON-RPC batch requests are limited to {MaxBatchRequestCount} items.",
            retrySafe: false);
    }

    private bool[] ConsumeCompletedClientReplies(JsonArray batch)
    {
        // Client replies complete server-initiated requests and never produce a response item.
        // Consume matched replies before reserving response bytes; unmatched response-shaped
        // objects remain ordinary invalid requests and retain their budget slot.
        // client reply は server 起点 request を完了し response item を生成しないため、response
        // budget 予約前に matched reply を consume する。unmatched object は invalid request として残す。
        var completed = new bool[batch.Count];
        for (var index = 0; index < batch.Count; index++)
        {
            if (batch[index] is JsonObject itemObject
                && TryCompletePendingClientRequest(itemObject))
            {
                completed[index] = true;
            }
        }
        return completed;
    }

    private BatchResponseBudgetPlan CreateBatchResponseBudgetPlan(
        JsonArray batch,
        IReadOnlyList<bool> completed)
    {
        if (!_usesDefaultResponseSerializer)
            return new BatchResponseBudgetPlan(null, null, null, ResponseLimit: 0);

        // The complete JSON array owns one response budget. Reserve brackets, commas, and a
        // bounded error for every response-bearing item, then divide the remaining bytes
        // deterministically before concurrent dispatch. JSON 配列全体で 1 つの response
        // budget を共有する。bracket、comma、各 response item の bounded error を予約し、
        // 残りを concurrent dispatch 前に決定的に分配する。
        var responseLimit = GetMaxResponseBytes();
        var activeTransportMaxResponseBytes = Volatile.Read(ref _activeTransportMaxResponseBytes);
        if (activeTransportMaxResponseBytes > 0)
            responseLimit = Math.Min(activeTransportMaxResponseBytes, responseLimit);

        var slots = new BatchResponseBudgetSlot?[batch.Count];
        var itemLimits = new int?[batch.Count];
        long reservedErrorBytes = 0;
        var responseCount = 0;
        for (var index = 0; index < batch.Count; index++)
        {
            if (completed[index] || !TryCreateBatchResponseBudgetSlot(batch[index], out var slot))
                continue;

            slots[index] = slot;
            reservedErrorBytes += slot.ErrorResponseBytes;
            responseCount++;
        }

        if (responseCount == 0)
            return new BatchResponseBudgetPlan(slots, itemLimits, null, responseLimit);

        var payloadBytes = responseLimit - 2L - (responseCount - 1L);
        if (payloadBytes < reservedErrorBytes)
        {
            // Defer the terminal budget error until request IDs are durably registered
            // and cancellation controls have run. No ordinary or state-changing work is
            // dispatched on this path (#4544, #4545).
            // terminal budget error は request ID の durable 登録と cancellation control
            // 実行後まで保留し、通常処理や他の state mutation は開始しない。
            return new BatchResponseBudgetPlan(
                slots,
                itemLimits,
                CreateBatchEnvelopeBudgetError(responseLimit, retrySafe: true),
                responseLimit);
        }

        var distributableBytes = payloadBytes - reservedErrorBytes;
        var fairShareBytes = distributableBytes / responseCount;
        var remainderBytes = distributableBytes % responseCount;
        for (var index = 0; index < batch.Count; index++)
        {
            if (slots[index] is not { } slot)
                continue;

            var itemExtraBytes = fairShareBytes;
            if (remainderBytes > 0)
            {
                itemExtraBytes++;
                remainderBytes--;
            }
            itemLimits[index] = checked((int)(slot.ErrorResponseBytes + itemExtraBytes));
        }

        RedistributeResourceListBudgetSlack(slots, itemLimits);
        return new BatchResponseBudgetPlan(slots, itemLimits, null, responseLimit);
    }

    private static void RedistributeResourceListBudgetSlack(
        IReadOnlyList<BatchResponseBudgetSlot?> slots,
        IList<int?> itemLimits)
    {
        // Equal caps can strand the same resource-serialization fragment in every slot.
        // Move one minimum page quantum from the first resources/list slot to the last so
        // one concurrent page can consume that deterministic slack without exceeding the
        // aggregate cap. 等分時に各 slot へ同じ serialization 断片が残るのを避けるため、
        // 最初の resources/list から最後へ最小 page 予算 1 単位を移す。
        var firstResourceIndex = -1;
        var lastResourceIndex = -1;
        for (var index = 0; index < slots.Count; index++)
        {
            if (slots[index]?.CanShapeResourcesListResponse != true)
                continue;
            if (firstResourceIndex < 0)
                firstResourceIndex = index;
            lastResourceIndex = index;
        }
        if (firstResourceIndex < 0 || lastResourceIndex == firstResourceIndex)
            return;

        var donorSlot = slots[firstResourceIndex]!.Value;
        var donorLimit = itemLimits[firstResourceIndex]!.Value;
        var transferableBytes = Math.Min(
            MinResourceListMaxBytes,
            donorLimit - donorSlot.ErrorResponseBytes);
        itemLimits[firstResourceIndex] = donorLimit - transferableBytes;
        itemLimits[lastResourceIndex] = checked(
            itemLimits[lastResourceIndex]!.Value + transferableBytes);
    }

    private void PrepareBatchItems(BatchDispatchState state, bool rejectForCapacity)
    {
        // A batch is one wire frame but each item is an independently bounded JSON-RPC
        // operation (#4545). Invalid items are materialized immediately, cancellation controls
        // run eagerly, and state-changing items split the remaining work into ordered segments.
        // Response nodes are retained by input index so completion timing cannot reorder the wire
        // response. バッチは 1 wire frame だが、各 item を独立した bounded operation として扱う。
        // 不正 item は即時確定し、cancel control は先行処理し、状態変更 item で順序 segment を区切る。
        var seenRequestIds = new HashSet<string>(StringComparer.Ordinal);
        for (var index = 0; index < state.Batch.Count; index++)
        {
            if (state.Completed[index])
                continue;

            var item = state.Batch[index];
            if (item is null || item is not JsonObject and not JsonArray)
            {
                using (BeginBatchItemCorrelation(id: null, index))
                    state.Responses[index] = CreateInvalidBatchItemResponse(nestedBatch: false);
                state.Completed[index] = true;
                continue;
            }
            if (item is JsonArray)
            {
                using (BeginBatchItemCorrelation(id: null, index))
                    state.Responses[index] = CreateInvalidBatchItemResponse(nestedBatch: true);
                state.Completed[index] = true;
                continue;
            }

            var itemObject = (JsonObject)item;
            if (IsCancellationItem(itemObject))
            {
                // Execute controls only after this pass has durably registered every unique
                // request ID. This preserves eager cancellation even when the control precedes
                // its target and the short tombstone cache is full (#4545).
                // 全 unique request ID を durable 登録してから control を実行する。cancel が target
                // より先でも、短命 tombstone cache が満杯でも eager cancellation を保つ。
                state.CancellationItems[index] = true;
                continue;
            }

            state.OrderingFences[index] = IsProtocolOrderingBarrierItem(itemObject);
            if (!TryGetRequestId(itemObject, out var hasId, out var id)
                || !hasId
                || SerializeRequestId(id) is not { } requestKey)
            {
                continue;
            }

            if (!seenRequestIds.Add(requestKey))
            {
                // Preserve the pre-concurrency behavior for duplicate ids in one batch: the
                // later occurrence starts only after the earlier occurrence has completed.
                // 同一 batch 内の重複 id は、後続を fence にして従来の逐次 semantics を保つ。
                state.OrderingFences[index] = true;
            }
            else if (!rejectForCapacity)
            {
                state.QueuedRegistrations[index] = TryRegisterQueuedBatchRequest(requestKey);
            }
        }
    }

    private async Task ExecuteBatchCancellationItemsAsync(
        BatchDispatchState state,
        DeferredInitializeCommits? deferredInitializeCommits)
    {
        for (var index = 0; index < state.Batch.Count; index++)
        {
            if (!state.CancellationItems[index])
                continue;

            var result = await ExecuteBatchItemAsync(
                state.Batch[index]!,
                index,
                isolateRequestDb: true,
                beforeDispatchAsync: null,
                rejectForCapacity: false,
                queuedBatchRegistration: null,
                responseItemMaxBytes: state.Budget.ItemLimits?[index],
                deferredInitializeCommits).ConfigureAwait(false);
            state.Complete(index, result);
        }
    }

    private async Task ExecuteCapacityRejectedBatchAsync(
        BatchDispatchState state,
        DeferredInitializeCommits? deferredInitializeCommits)
    {
        for (var index = 0; index < state.Batch.Count; index++)
        {
            if (state.Completed[index])
                continue;

            var result = await ExecuteBatchItemAsync(
                state.Batch[index]!,
                index,
                state.IsolateItems,
                beforeDispatchAsync: null,
                rejectForCapacity: true,
                queuedBatchRegistration: null,
                responseItemMaxBytes: state.Budget.ItemLimits?[index],
                deferredInitializeCommits).ConfigureAwait(false);
            state.Complete(index, result);
        }
    }

    private async Task ExecuteOrderedBatchItemsAsync(
        BatchDispatchState state,
        Func<CancellationToken, Task>? beforeDispatchAsync,
        DeferredInitializeCommits? deferredInitializeCommits)
    {
        var independentSegment = new List<int>();
        for (var index = 0; index < state.Batch.Count; index++)
        {
            if (state.Completed[index])
                continue;

            if (!state.OrderingFences[index])
            {
                independentSegment.Add(index);
                continue;
            }

            await ExecuteBatchSegmentAsync(
                state.Batch,
                independentSegment,
                state.IsolateItems,
                state.Responses,
                state.Logs,
                state.QueuedRegistrations,
                state.Budget.ItemLimits,
                deferredInitializeCommits,
                beforeDispatchAsync).ConfigureAwait(false);
            independentSegment.Clear();
            await ExecuteBatchItemAsync(
                state.Batch[index]!,
                index,
                state.IsolateItems,
                state.Responses,
                state.Logs,
                beforeDispatchAsync,
                state.QueuedRegistrations[index],
                state.Budget.ItemLimits?[index],
                deferredInitializeCommits).ConfigureAwait(false);
            ApplyBatchFenceState(state.Responses[index], deferredInitializeCommits);
        }

        await ExecuteBatchSegmentAsync(
            state.Batch,
            independentSegment,
            state.IsolateItems,
            state.Responses,
            state.Logs,
            state.QueuedRegistrations,
            state.Budget.ItemLimits,
            deferredInitializeCommits,
            beforeDispatchAsync).ConfigureAwait(false);
    }

    private void ApplyBatchFenceState(
        JsonNode? fenceResponse,
        DeferredInitializeCommits? deferredInitializeCommits)
    {
        if (fenceResponse is not null
            && deferredInitializeCommits?.TryGetRegisteredState(fenceResponse, out var initializeState) == true)
        {
            _frameInitializeState.Value = new FrameInitializeState(
                BuildCommittedInitializeState(initializeState),
                isProvisionalGeneration: true);
        }
        else if (_frameInitializeState.Value is { } currentFrameState
            && currentFrameState.TryConsumeAcceptedRootsChange())
        {
            var nextState = currentFrameState.IsProvisionalGeneration
                ? currentFrameState.Current with { ClientRootsStale = true }
                : PublishedInitializeState;
            _frameInitializeState.Value = new FrameInitializeState(
                nextState,
                currentFrameState.IsProvisionalGeneration);
        }
    }
}

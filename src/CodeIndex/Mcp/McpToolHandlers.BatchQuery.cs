using System.Diagnostics;
using System.Text.Json.Nodes;

namespace CodeIndex.Mcp;

public partial class McpServer
{
    private JsonNode ExecuteBatchQuery(JsonNode? id, JsonNode? args)
    {
        if (!TryReadBatchQueries(id, args, out var queries, out var validationError))
            return validationError!;

        var totalStopwatch = Stopwatch.StartNew();
        var adjustments = new ArgumentAdjustmentCollector();
        var responseByteLimit = ReadBatchQueryResponseByteLimit(args, adjustments);
        if (args?["estimateOnly"]?.GetValue<bool>() ?? false)
            return ExecuteBatchQueryEstimate(id, queries!, responseByteLimit, adjustments);

        var collector = new BatchQueryResultCollector(
            this,
            id,
            queries!,
            responseByteLimit,
            adjustments);
        ExecuteBatchSlots(queries!, collector);
        totalStopwatch.Stop();
        return collector.BuildResponse(totalStopwatch.ElapsedMilliseconds);
    }

    private bool TryReadBatchQueries(
        JsonNode? id,
        JsonNode? args,
        out JsonArray? queries,
        out JsonNode? validationError)
    {
        var queriesNode = args?["queries"];
        queries = queriesNode as JsonArray;
        validationError = null;
        if (queriesNode is null)
            validationError = CreateToolErrorResponse(id, "Missing or empty required parameter: queries");
        else if (queries is null)
            validationError = CreateToolErrorResponse(id, "Invalid type for argument 'queries' on tool 'batch_query'. Expected array.");
        else if (queries.Count == 0)
            validationError = CreateToolErrorResponse(id, "Missing or empty required parameter: queries");
        else if (queries.Count > MaxBatchQuerySize)
            validationError = CreateToolErrorResponse(id, $"Batch too large: {queries.Count} queries (max {MaxBatchQuerySize})");
        return validationError is null;
    }

    private void ExecuteBatchSlots(JsonArray queries, BatchQueryResultCollector collector)
    {
        var caller = CurrentInitializeState.Caller;
        for (var requestIndex = 0; requestIndex < queries.Count; requestIndex++)
        {
            using var slotCorrelation = BeginChildCorrelation(requestIndex + 1);
            var slot = CreateBatchQuerySlot(queries[requestIndex], requestIndex);
            if (collector.IsTruncated)
            {
                collector.AppendCascadedSlot(slot);
                continue;
            }

            ExecuteBatchSlot(slot, collector, caller);
        }
    }

    private static BatchQuerySlot CreateBatchQuerySlot(JsonNode? query, int requestIndex)
    {
        var queryObject = query as JsonObject;
        var toolName = queryObject?["tool"] is JsonValue toolValue
            && toolValue.TryGetValue<string>(out var parsedToolName)
                ? parsedToolName
                : null;
        return new BatchQuerySlot(
            requestIndex,
            queryObject,
            toolName,
            queryObject?["arguments"],
            ReadBatchSlotId(queryObject),
            Stopwatch.StartNew());
    }

    private void ExecuteBatchSlot(BatchQuerySlot slot, BatchQueryResultCollector collector, string caller)
    {
        var toolName = slot.ToolName;
        if (string.IsNullOrEmpty(toolName))
        {
            var message = slot.QueryObject is null
                ? "Each query must be an object with a string tool name."
                : "Missing tool name";
            collector.AppendError(slot, message,
                category: McpErrorEnvelope.CategoryMissingParameter,
                suggestion: "Each batch_query slot must include a string `tool` field.",
                retrySafe: false);
            return;
        }

        if (toolName.Length > McpBoundedText.MaxToolNameChars)
        {
            collector.AppendError(slot, BuildUnknownToolMessage(toolName),
                category: McpErrorEnvelope.CategoryToolUnknown,
                suggestion: "Call tools/list to see the tool catalog. Slot tool names are case-sensitive.",
                retrySafe: false);
            return;
        }

        if (ValidateToolArguments(toolName, slot.ToolArgs) is JsonObject argumentError)
        {
            collector.AppendError(slot, argumentError["message"]!.GetValue<string>(),
                category: McpErrorEnvelope.CategoryInvalidArgument,
                suggestion: "Use exactly the argument names advertised by tools/list for this tool.",
                retrySafe: false,
                extraData: argumentError);
            return;
        }

        if (ValidateCommonListArguments(slot.ToolArgs) is JsonObject listArgumentError)
        {
            collector.AppendError(slot, listArgumentError["message"]!.GetValue<string>(),
                category: McpErrorEnvelope.CategoryInvalidArgument,
                suggestion: "Send only non-empty string entries within the documented MCP array bounds.",
                retrySafe: false,
                extraData: listArgumentError);
            return;
        }

        // Keep enablement ahead of write rejection so disabled write tools retain -32601.
        if (McpToolFilter.IsKnownTool(toolName) && !_toolFilter.IsEnabled(toolName))
        {
            collector.AppendError(slot, $"Tool not enabled: {toolName}", code: -32601,
                category: McpErrorEnvelope.CategoryToolDisabled,
                suggestion: "This tool is disabled on the server. Ask the operator to enable it or remove the slot.",
                retrySafe: false);
            return;
        }

        if (toolName is "index" or "backfill_fold" or "suggest_improvement")
        {
            collector.AppendError(slot, $"{toolName} is not allowed in batch_query (write operation)",
                category: McpErrorEnvelope.CategoryInvalidArgument,
                suggestion: "Call write tools (index / backfill_fold / suggest_improvement) directly via tools/call, not inside batch_query.",
                retrySafe: false);
            return;
        }

        // Reject recursion before consuming the inner tool bucket.
        if (toolName == "batch_query")
        {
            collector.AppendError(slot, "batch_query cannot be nested inside batch_query.",
                category: McpErrorEnvelope.CategoryInvalidArgument,
                suggestion: "Flatten the nested batch_query into top-level slots.",
                retrySafe: false);
            return;
        }

        var slotBucketName = ResolveKnownRateLimitBucketName(toolName) ?? RateLimiter.InvalidToolBucketName;
        var slotDecision = RateLimiter.TryAcquire(slotBucketName, caller);
        if (!slotDecision.Allowed)
        {
            collector.AppendRateLimited(slot, slotDecision.RetryAfterMs);
            return;
        }

        if (ValidateProjectFilterArguments(slot.ToolArgs) is JsonObject projectFilterError)
        {
            collector.AppendError(slot, projectFilterError["message"]!.GetValue<string>(),
                category: McpErrorEnvelope.CategoryInvalidArgument,
                suggestion: "Use a project name or project path from the current workspace, or correct the solution filter.",
                retrySafe: false,
                extraData: projectFilterError);
            return;
        }

        ExecuteAcceptedBatchSlot(slot, collector, toolName);
    }

    private void ExecuteAcceptedBatchSlot(
        BatchQuerySlot slot,
        BatchQueryResultCollector collector,
        string toolName)
    {
        try
        {
            var response = DispatchBatchSlotTool(toolName, slot.ToolArgs);
            if (response is null)
            {
                collector.AppendError(slot, BuildUnknownToolMessage(toolName),
                    category: McpErrorEnvelope.CategoryToolUnknown,
                    suggestion: "Call tools/list to see the tool catalog. Slot tool names are case-sensitive.",
                    retrySafe: false);
                return;
            }

            if (response["result"]?["isError"]?.GetValue<bool>() ?? false)
            {
                var errorText = response["result"]?["content"]?[0]?["text"]?.GetValue<string>() ?? "Unknown error";
                var structured = response["result"]?["structuredContent"] as JsonObject;
                bool? retrySafe = null;
                if (structured?["retry_safe"] is JsonValue retryValue
                    && retryValue.TryGetValue<bool>(out var parsedRetrySafe))
                {
                    retrySafe = parsedRetrySafe;
                }
                collector.AppendError(slot, errorText,
                    category: structured?["category"]?.GetValue<string>(),
                    suggestion: structured?["suggestion"]?.GetValue<string>(),
                    retrySafe: retrySafe);
                return;
            }

            collector.AppendSuccess(slot, response);
        }
        catch (Exception ex)
        {
            var dbDebugDump = Database.DbDebug.CaptureDump(ex);
            DeferFrameLog(() =>
            {
                WriteMcpLogLine(BuildToolErrorLog(toolName, ex));
                Database.DbDebug.WriteCapturedDumpToStderr(dbDebugDump);
            });
            var classification = McpErrorEnvelope.ClassifyException(ex);
            collector.AppendError(slot, BuildSanitizedToolErrorMessage(toolName, ex),
                category: classification.Category,
                suggestion: classification.Suggestion,
                retrySafe: classification.RetrySafe);
        }
    }

    private JsonNode? DispatchBatchSlotTool(string toolName, JsonNode? toolArgs)
    {
        var previousToolOutputName = _currentToolOutputName.Value;
        _currentToolOutputName.Value = toolName;
        try
        {
            return DispatchSynchronousToolCall(toolName, id: null, toolArgs);
        }
        finally
        {
            _currentToolOutputName.Value = previousToolOutputName;
        }
    }

    private readonly record struct BatchQuerySlot(
        int RequestIndex,
        JsonObject? QueryObject,
        string? ToolName,
        JsonNode? ToolArgs,
        string? SlotId,
        Stopwatch Stopwatch);
}

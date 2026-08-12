using System.Text.Json.Nodes;

namespace CodeIndex.Mcp;

public partial class McpServer
{
    private sealed class BatchQueryResultCollector
    {
        private readonly BatchResponseBudget _responseBudget;

        internal BatchQueryResultCollector(
            McpServer server,
            JsonNode? id,
            JsonArray queries,
            int responseByteLimit,
            ArgumentAdjustmentCollector adjustments)
        {
            _responseBudget = new BatchResponseBudget(
                server,
                id,
                queries.Count,
                responseByteLimit,
                adjustments);
        }

        internal int SuccessCount { get; private set; }
        internal int FailureCount { get; private set; }
        internal bool IsTruncated => _responseBudget.IsTruncated;

        internal void AppendCascadedSlot(BatchQuerySlot slot)
            => _responseBudget.AppendCascadedSlot(slot);

        internal void AppendError(
            BatchQuerySlot slot,
            string errorMessage,
            int? code = null,
            string? category = null,
            string? suggestion = null,
            bool? retrySafe = null,
            JsonObject? extraData = null)
        {
            slot.Stopwatch.Stop();
            var entry = new JsonObject
            {
                ["request_index"] = slot.RequestIndex,
                ["ok"] = false,
                ["correlation_id"] = CurrentCorrelationContext.Value?.CorrelationId,
                ["args_summary"] = BuildArgsSummary(slot.ToolArgs),
                ["elapsed_ms"] = slot.Stopwatch.ElapsedMilliseconds,
                ["error"] = errorMessage,
            };
            AddBatchSlotId(entry, slot.SlotId);
            AddToolDisplayData(entry, slot.ToolName);
            CopySlotErrorData(entry, extraData);
            if (code.HasValue)
                entry["code"] = code.Value;
            if (category is not null)
                entry["category"] = category;
            if (suggestion is not null)
                entry["suggestion"] = suggestion;
            if (retrySafe.HasValue)
                entry["retry_safe"] = retrySafe.Value;

            _responseBudget.TryAppend(
                entry,
                slot,
                SuccessCount,
                FailureCount + 1);
            FailureCount++;
        }

        internal void AppendRateLimited(BatchQuerySlot slot, long retryAfterMs)
        {
            slot.Stopwatch.Stop();
            var toolDisplay = slot.ToolName is null
                ? "(missing)"
                : BoundToolNameForDisplay(slot.ToolName).Text;
            var entry = new JsonObject
            {
                ["request_index"] = slot.RequestIndex,
                ["ok"] = false,
                ["correlation_id"] = CurrentCorrelationContext.Value?.CorrelationId,
                ["args_summary"] = BuildArgsSummary(slot.ToolArgs),
                ["elapsed_ms"] = slot.Stopwatch.ElapsedMilliseconds,
                ["error"] = $"Rate limit exceeded for tool '{toolDisplay}' (retry after {retryAfterMs} ms).",
                ["error_category"] = "rate_limited",
                ["retry_after_ms"] = retryAfterMs,
                ["category"] = McpErrorEnvelope.CategoryRateLimited,
                ["suggestion"] = $"Back off for at least {retryAfterMs} ms before retrying this tool.",
                ["retry_safe"] = true,
            };
            AddBatchSlotId(entry, slot.SlotId);
            AddToolDisplayData(entry, slot.ToolName);
            _responseBudget.TryAppend(
                entry,
                slot,
                SuccessCount,
                FailureCount + 1);
            FailureCount++;
        }

        internal void AppendSuccess(BatchQuerySlot slot, JsonNode response)
        {
            slot.Stopwatch.Stop();
            var entry = new JsonObject
            {
                ["request_index"] = slot.RequestIndex,
                ["ok"] = true,
                ["correlation_id"] = CurrentCorrelationContext.Value?.CorrelationId,
                ["args_summary"] = BuildArgsSummary(slot.ToolArgs),
                ["elapsed_ms"] = slot.Stopwatch.ElapsedMilliseconds,
                ["summary"] = response["result"]?["content"]?[0]?["text"]?.GetValue<string>(),
                ["result"] = response["result"]?["structuredContent"]?.DeepClone(),
            };
            AddBatchSlotId(entry, slot.SlotId);
            AddToolDisplayData(entry, slot.ToolName);
            _responseBudget.TryAppend(
                entry,
                slot,
                SuccessCount + 1,
                FailureCount);
            SuccessCount++;
        }

        internal JsonNode BuildResponse(long totalElapsedMs)
            => _responseBudget.BuildResponse(SuccessCount, FailureCount, totalElapsedMs);

        private static void CopySlotErrorData(JsonObject entry, JsonObject? extraData)
        {
            if (extraData is null)
                return;

            foreach (var (key, value) in extraData)
            {
                if (key is "message" or "jsonrpc_invalid_params" || entry.ContainsKey(key))
                    continue;
                entry[key] = value?.DeepClone();
            }
        }
    }
}

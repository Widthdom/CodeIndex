using System.Text.Json.Nodes;

namespace CodeIndex.Mcp;

public partial class McpServer
{
    private sealed class BatchResponseBudget
    {
        private readonly McpServer _server;
        private readonly JsonNode? _id;
        private readonly int _submittedCount;
        private readonly int _responseByteLimit;
        private readonly ArgumentAdjustmentCollector _adjustments;
        private readonly JsonArray _results = [];
        private readonly JsonArray _truncatedQueries = [];
        private int _estimatedResponseBytes;
        private int? _cascadeStartedAtIndex;

        internal BatchResponseBudget(
            McpServer server,
            JsonNode? id,
            int submittedCount,
            int responseByteLimit,
            ArgumentAdjustmentCollector adjustments)
        {
            _server = server;
            _id = id;
            _submittedCount = submittedCount;
            _responseByteLimit = responseByteLimit;
            _adjustments = adjustments;
            _estimatedResponseBytes = server.EstimateBatchResponseBytes(
                id,
                "Executed 0 queries.",
                submittedCount,
                successCount: 0,
                failureCount: 0,
                GetBatchFailureScope(submittedCount, 0, 0, cascadeStartedAtIndex: null),
                cascadeStartedAtIndex: null,
                responseByteLimit,
                _results,
                truncated: false,
                _truncatedQueries,
                adjustments);
        }

        internal bool IsTruncated { get; private set; }

        internal bool TryAppend(
            JsonObject entry,
            BatchQuerySlot slot,
            int candidateSuccessCount,
            int candidateFailureCount)
        {
            var candidateBytes = _server.EstimateBatchAppendBytes(
                _estimatedResponseBytes,
                entry,
                candidateSuccessCount + candidateFailureCount,
                candidateSuccessCount,
                candidateFailureCount);
            if (candidateBytes > _responseByteLimit)
            {
                IsTruncated = true;
                _cascadeStartedAtIndex ??= slot.RequestIndex;
                _truncatedQueries.Add(BuildTruncatedEntry(slot, "response_byte_limit_exceeded"));
                return false;
            }

            _estimatedResponseBytes = candidateBytes;
            _results.Add(entry);
            return true;
        }

        internal void AppendCascadedSlot(BatchQuerySlot slot)
        {
            slot.Stopwatch.Stop();
            _cascadeStartedAtIndex ??= slot.RequestIndex;
            _truncatedQueries.Add(BuildTruncatedEntry(slot, "response_byte_limit_already_exceeded"));
        }

        internal JsonNode BuildResponse(int successCount, int failureCount, long totalElapsedMs)
        {
            var compactSummary = false;
            var omitSplitHint = false;
            JsonObject payload;
            string summary;
            while (true)
            {
                payload = BuildPayload(successCount, failureCount, totalElapsedMs, omitSplitHint);
                summary = BuildSummary(successCount, failureCount, totalElapsedMs, compactSummary);
                _estimatedResponseBytes = _server.EstimateJsonUtf8Bytes(
                    _server.CreateToolResult(_id, summary, payload.DeepClone()),
                    _responseByteLimit);
                if (_estimatedResponseBytes <= _responseByteLimit)
                    break;
                if (RemoveLastResultForFinalBudget())
                    continue;
                if (RemoveBatchTruncatedQueryToolDisplay(_truncatedQueries))
                    continue;
                if (_truncatedQueries.Count > 1)
                {
                    _truncatedQueries.RemoveAt(_truncatedQueries.Count - 1);
                    continue;
                }
                if (CompactBatchTruncatedQueryArgsSummaries(_truncatedQueries))
                    continue;
                if (IsTruncated && !compactSummary)
                {
                    compactSummary = true;
                    continue;
                }
                if (IsTruncated && !omitSplitHint)
                {
                    omitSplitHint = true;
                    continue;
                }
                break;
            }

            ((JsonObject)payload["metadata"]!)["estimated_response_bytes"] = _estimatedResponseBytes;
            return _server.CreateToolResult(_id, summary, payload);
        }

        private JsonObject BuildPayload(
            int successCount,
            int failureCount,
            long totalElapsedMs,
            bool omitSplitHint)
        {
            var payload = new JsonObject
            {
                ["count"] = _results.Count,
                ["total_count"] = _submittedCount,
                ["success_count"] = successCount,
                ["failure_count"] = failureCount,
                ["partial_failure"] = failureCount > 0 || _cascadeStartedAtIndex.HasValue,
                ["failure_scope"] = GetBatchFailureScope(
                    _submittedCount,
                    successCount,
                    failureCount,
                    _cascadeStartedAtIndex),
                ["cascade_started_at_index"] = _cascadeStartedAtIndex,
                ["metadata"] = new JsonObject
                {
                    ["submitted"] = _submittedCount,
                    ["executed"] = successCount + failureCount,
                    ["errors"] = failureCount,
                    ["total_elapsed_ms"] = totalElapsedMs,
                    ["success_count"] = successCount,
                    ["failure_count"] = failureCount,
                    ["response_byte_limit"] = _responseByteLimit,
                    ["estimated_response_bytes"] = _responseByteLimit,
                },
                ["results"] = _results.DeepClone(),
            };
            _adjustments.ApplyTo(payload);
            if (IsTruncated)
            {
                payload["truncated"] = true;
                payload["truncated_queries"] = _truncatedQueries.DeepClone();
                if (!omitSplitHint)
                {
                    payload["split_hint"] = BuildBatchSplitHint(
                        _submittedCount,
                        _cascadeStartedAtIndex,
                        _results.Count);
                }
            }
            return payload;
        }

        private string BuildSummary(
            int successCount,
            int failureCount,
            long totalElapsedMs,
            bool compactSummary)
        {
            if (compactSummary)
                return $"Response truncated at {_responseByteLimit} bytes.";

            var baseSummary = failureCount == 0
                ? $"Executed {successCount + failureCount} of {_submittedCount} queries in {totalElapsedMs} ms (all succeeded)."
                : $"Executed {successCount + failureCount} of {_submittedCount} queries in {totalElapsedMs} ms ({successCount} succeeded, {failureCount} failed).";
            return IsTruncated
                ? baseSummary + $" Response truncated at {_responseByteLimit} bytes; split the batch or lower per-slot limits."
                : baseSummary;
        }

        private bool RemoveLastResultForFinalBudget()
        {
            if (_results.Count == 0)
                return false;

            var removed = _results[_results.Count - 1];
            IsTruncated = true;
            if (removed?["request_index"] is JsonValue requestIndexValue
                && requestIndexValue.TryGetValue<int>(out var removedRequestIndex))
            {
                _cascadeStartedAtIndex = _cascadeStartedAtIndex.HasValue
                    ? Math.Min(_cascadeStartedAtIndex.Value, removedRequestIndex)
                    : removedRequestIndex;
            }
            _truncatedQueries.Insert(0, new JsonObject
            {
                ["request_index"] = removed?["request_index"]?.DeepClone(),
                ["slot_id"] = removed?["slot_id"]?.DeepClone(),
                ["tool"] = removed?["tool"]?.DeepClone(),
                ["args_summary"] = removed?["args_summary"]?.DeepClone(),
                ["reason"] = "final_response_byte_limit_exceeded",
            });
            _results.RemoveAt(_results.Count - 1);
            return true;
        }

        private static JsonObject BuildTruncatedEntry(BatchQuerySlot slot, string reason)
        {
            var entry = new JsonObject
            {
                ["request_index"] = slot.RequestIndex,
                ["args_summary"] = BuildArgsSummary(slot.ToolArgs),
                ["reason"] = reason,
            };
            AddBatchSlotId(entry, slot.SlotId);
            AddToolDisplayData(entry, slot.ToolName);
            return entry;
        }
    }
}

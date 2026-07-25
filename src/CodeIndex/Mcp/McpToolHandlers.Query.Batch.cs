using System.Globalization;
using System.Text;
using System.Text.Json;
using System.Text.Json.Nodes;
using System.Text.RegularExpressions;
using CodeIndex.Cli;
using CodeIndex.Database;
using CodeIndex.Diagnostics;
using CodeIndex.Indexer;
using CodeIndex.Indexer.Extensibility;
using CodeIndex.Indexer.Hooks;
using CodeIndex.Models;

namespace CodeIndex.Mcp;

public partial class McpServer
{

    private JsonNode ExecuteBatchQueryEstimate(JsonNode? id, JsonArray queries, int responseByteLimit, ArgumentAdjustmentCollector adjustments)
    {
        var slotEstimates = new JsonArray();
        for (var requestIndex = 0; requestIndex < queries.Count; requestIndex++)
        {
            var queryObject = queries[requestIndex] as JsonObject;
            slotEstimates.Add(BuildBatchSlotDescriptor(requestIndex, queryObject));
        }

        var payload = new JsonObject
        {
            ["count"] = 0,
            ["total_count"] = queries.Count,
            ["success_count"] = 0,
            ["failure_count"] = 0,
            ["partial_failure"] = false,
            ["failure_scope"] = "none",
            ["cascade_started_at_index"] = null,
            ["estimate_only"] = true,
            ["metadata"] = new JsonObject
            {
                ["submitted"] = queries.Count,
                ["executed"] = 0,
                ["errors"] = 0,
                ["total_elapsed_ms"] = 0,
                ["success_count"] = 0,
                ["failure_count"] = 0,
                ["response_byte_limit"] = responseByteLimit,
                ["estimated_response_bytes"] = responseByteLimit,
            },
            ["slot_estimates"] = slotEstimates,
            ["results"] = new JsonArray(),
        };
        adjustments.ApplyTo(payload);

        var summary = $"Estimated batch_query envelope for {queries.Count} query slot(s); no slots executed.";
        var estimatedResponseBytes = EstimateJsonUtf8Bytes(CreateToolResult(id, summary, payload.DeepClone()), responseByteLimit);
        ((JsonObject)payload["metadata"]!)["estimated_response_bytes"] = estimatedResponseBytes;
        payload["estimate_exceeds_response_byte_limit"] = estimatedResponseBytes > responseByteLimit;
        return CreateToolResult(id, summary, payload);
    }

    private static int ReadBatchQueryResponseByteLimit(JsonNode? args, ArgumentAdjustmentCollector adjustments)
    {
        var serverLimit = GetBatchQueryResponseByteLimit();
        var requested = ReadOptionalIntArgument(args, "maxResponseBytes");
        if (!requested.HasValue)
            return serverLimit;
        var effective = Math.Min(requested.Value, serverLimit);
        if (effective != requested.Value)
            adjustments.AddClamped("maxResponseBytes", requested.Value, effective, 1, serverLimit);
        return effective;
    }

    private static JsonObject BuildBatchSlotDescriptor(int requestIndex, JsonObject? queryObject)
    {
        var toolName = queryObject?["tool"] is JsonValue toolValue && toolValue.TryGetValue<string>(out var parsedToolName)
            ? parsedToolName
            : null;
        var toolArgs = queryObject?["arguments"];
        var descriptor = new JsonObject
        {
            ["request_index"] = requestIndex,
            ["args_summary"] = BuildArgsSummary(toolArgs),
        };
        AddBatchSlotId(descriptor, ReadBatchSlotId(queryObject));
        AddToolDisplayData(descriptor, toolName);
        return descriptor;
    }

    private static JsonObject BuildBatchSplitHint(int submittedCount, int? cascadeStartedAtIndex, int retainedResultCount)
    {
        var nextRequestIndex = cascadeStartedAtIndex ?? submittedCount;
        return new JsonObject
        {
            ["reason"] = "response_byte_limit_exceeded",
            ["next_request_index"] = nextRequestIndex,
            ["suggested_query_count"] = Math.Max(1, retainedResultCount),
            ["resume_cursor"] = $"batch_query:v1:{nextRequestIndex}",
        };
    }

    private static bool RemoveBatchTruncatedQueryToolDisplay(JsonArray truncatedQueries)
    {
        var changed = false;
        foreach (var item in truncatedQueries)
        {
            if (item is JsonObject entry)
                changed |= entry.Remove("tool");
        }
        return changed;
    }

    private static bool CompactBatchTruncatedQueryArgsSummaries(JsonArray truncatedQueries)
    {
        var changed = false;
        foreach (var item in truncatedQueries)
        {
            if (item is JsonObject entry
                && entry["args_summary"] is JsonValue value
                && value.TryGetValue<string>(out var summary)
                && summary.Length > 0)
            {
                entry["args_summary"] = string.Empty;
                changed = true;
            }
        }
        return changed;
    }

    private static string? ReadBatchSlotId(JsonObject? queryObject)
    {
        if (TryReadBatchSlotIdValue(queryObject?["slotId"], out var slotId)
            || TryReadBatchSlotIdValue(queryObject?["id"], out slotId))
            return McpBoundedText.ForDisplay(slotId!, MaxRequestIdCharacterCount).Text;
        return null;
    }

    private static bool TryReadBatchSlotIdValue(JsonNode? node, out string? slotId)
    {
        slotId = null;
        if (node is not JsonValue value)
            return false;
        if (value.TryGetValue<string>(out var text))
        {
            if (string.IsNullOrWhiteSpace(text))
                return false;
            slotId = text;
            return true;
        }
        if (value.TryGetValue<int>(out var intValue))
        {
            slotId = intValue.ToString(CultureInfo.InvariantCulture);
            return true;
        }
        if (value.TryGetValue<long>(out var longValue))
        {
            slotId = longValue.ToString(CultureInfo.InvariantCulture);
            return true;
        }
        return false;
    }

    private static void AddBatchSlotId(JsonObject entry, string? slotId)
    {
        if (!string.IsNullOrEmpty(slotId))
            entry["slot_id"] = slotId;
    }

    private static int GetBatchQueryResponseByteLimit()
        => ReadPositiveIntEnvironmentLimit(
            BatchQueryResponseByteLimitEnvVar,
            DefaultBatchQueryResponseByteLimit,
            MaxBatchQueryResponseByteLimit,
            "MCP batch_query response byte limit");

    private int EstimateJsonUtf8Bytes(JsonNode node, int maxBytes = MaxBatchQueryResponseByteLimit)
    {
        _ = TryMeasureJsonUtf8BytesWithinLimit(node, _jsonOptions, maxBytes, out var bytesWritten);
        return bytesWritten;
    }

    private int EstimateBatchResponseBytes(JsonNode? id, string summary, int submittedCount, int successCount, int failureCount,
        string failureScope, int? cascadeStartedAtIndex, int responseByteLimit, JsonArray resultsArray, bool truncated, JsonArray truncatedQueries,
        ArgumentAdjustmentCollector? adjustments = null)
    {
        var payload = new JsonObject
        {
            ["count"] = resultsArray.Count,
            ["total_count"] = submittedCount,
            ["success_count"] = successCount,
            ["failure_count"] = failureCount,
            ["partial_failure"] = failureCount > 0 || cascadeStartedAtIndex.HasValue,
            ["failure_scope"] = failureScope,
            ["cascade_started_at_index"] = cascadeStartedAtIndex,
            ["metadata"] = new JsonObject
            {
                ["submitted"] = submittedCount,
                ["executed"] = successCount + failureCount,
                ["errors"] = failureCount,
                ["total_elapsed_ms"] = 0,
                ["success_count"] = successCount,
                ["failure_count"] = failureCount,
                ["response_byte_limit"] = responseByteLimit,
                ["estimated_response_bytes"] = responseByteLimit,
            },
            ["results"] = resultsArray.DeepClone(),
        };
        if (truncated)
        {
            payload["truncated"] = true;
            payload["truncated_queries"] = truncatedQueries.DeepClone();
        }
        adjustments?.ApplyTo(payload);

        return EstimateJsonUtf8Bytes(CreateToolResult(id, summary, payload), responseByteLimit);
    }

    private int EstimateBatchAppendBytes(int currentEstimateBytes, JsonObject entry, int executedCount, int successCount, int failureCount)
    {
        var entryBytes = EstimateJsonUtf8Bytes(entry);
        var digitGrowth = CountDecimalDigits(executedCount) + CountDecimalDigits(successCount) + CountDecimalDigits(failureCount);
        return SaturatingAdd(
            currentEstimateBytes,
            entryBytes,
            BatchQueryIncrementalEstimatePaddingBytes,
            digitGrowth);
    }

    private static int CountDecimalDigits(int value)
    {
        var digits = 1;
        while (value >= 10)
        {
            value /= 10;
            digits++;
        }
        return digits;
    }

    private static int SaturatingAdd(params int[] values)
    {
        var total = 0L;
        foreach (var value in values)
        {
            total += value;
            if (total >= int.MaxValue)
                return int.MaxValue;
        }
        return (int)total;
    }

    private static string GetBatchFailureScope(int submittedCount, int successCount, int failureCount, int? cascadeStartedAtIndex)
    {
        if (cascadeStartedAtIndex.HasValue && cascadeStartedAtIndex.Value < submittedCount)
            return "cascading";
        return failureCount == 0 ? "none" : "isolated";
    }

    /// <summary>
    /// Build a compact, single-line summary string of a batch slot's arguments
    /// so callers can correlate per-slot timings with what was requested
    /// without re-parsing the original payload.
    /// バッチスロットの arguments を1行で要約し、呼び出し側がペイロードを
    /// 再解析せずスロット別時間と対応付けられるようにする。
    /// </summary>
    private const int BatchArgsSummaryMaxLength = 200;
    private static string BuildArgsSummary(JsonNode? toolArgs)
    {
        if (toolArgs is not JsonObject obj)
            return string.Empty;
        if (obj.Count == 0)
            return string.Empty;
        var parts = new List<string>(obj.Count);
        foreach (var kv in obj)
        {
            var key = McpBoundedText.ForDisplay(kv.Key).Text;
            var rendered = RenderBatchArgumentSummaryValue(kv.Value);
            parts.Add($"{key}={rendered}");
        }
        var joined = string.Join(", ", parts);
        if (joined.Length > BatchArgsSummaryMaxLength)
            joined = joined.Substring(0, BatchArgsSummaryMaxLength - 1) + "…";
        return joined;
    }

    private static string RenderBatchArgumentSummaryValue(JsonNode? value)
    {
        if (value is null)
            return "null";
        if (value is JsonArray arr)
            return $"[{arr.Count}]";
        if (value is JsonObject inner)
            return $"{{{inner.Count}}}";
        if (value is not JsonValue jsonValue)
            return "<json>";

        return jsonValue.GetValueKind() switch
        {
            JsonValueKind.String => jsonValue.TryGetValue<string>(out var text)
                ? JsonSerializer.Serialize(McpBoundedText.ForDisplay(text).Text)
                : "\"<string>\"",
            JsonValueKind.True => "true",
            JsonValueKind.False => "false",
            JsonValueKind.Null => "null",
            JsonValueKind.Number => RenderBatchNumericArgument(jsonValue),
            _ => "<json>",
        };
    }

    private static string RenderBatchNumericArgument(JsonValue value)
    {
        if (value.TryGetValue<int>(out var intValue))
            return intValue.ToString(CultureInfo.InvariantCulture);
        if (value.TryGetValue<long>(out var longValue))
            return longValue.ToString(CultureInfo.InvariantCulture);
        if (value.TryGetValue<decimal>(out var decimalValue))
            return decimalValue.ToString(CultureInfo.InvariantCulture);
        if (value.TryGetValue<double>(out var doubleValue) && double.IsFinite(doubleValue))
            return doubleValue.ToString("R", CultureInfo.InvariantCulture);
        return "<number>";
    }

}

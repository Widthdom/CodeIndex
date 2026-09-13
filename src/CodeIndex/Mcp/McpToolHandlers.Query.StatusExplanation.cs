using System.Text.Json.Nodes;
using CodeIndex.Cli;
using CodeIndex.Database;

namespace CodeIndex.Mcp;

public partial class McpServer
{
    private JsonNode ExecuteStatusFieldExplanation(JsonNode? id, JsonObject args)
    {
        // Static explanations deliberately bypass the database and runtime diagnostic enrichment.
        // 静的な説明では DB 読み取りと実行時診断の付加を行わない。
        foreach (var option in new[] { "check", "scopes", "staleAfterSeconds", "explain", "config", "logPath", "updateCheck", "fields" })
        {
            if (args.ContainsKey(option))
                return StatusExplanationError($"explainField cannot be combined with {option}.");
        }
        if (args["explainField"] is not JsonValue fieldValue || !fieldValue.TryGetValue<string>(out var field))
            return StatusExplanationError("explainField must be a string.");
        var format = ReadResponseFormat(args);
        if (format is not ("full" or "compact"))
            return StatusExplanationError("format must be one of full, compact.");
        int? maxBytes = null;
        if (args.ContainsKey("maxBytes"))
        {
            if (args["maxBytes"] is not JsonValue byteValue
                || !byteValue.TryGetValue<int>(out var requestedBytes) || requestedBytes <= 0)
                return StatusExplanationError("maxBytes must be a positive integer.");
            maxBytes = requestedBytes;
        }

        _currentRequestToken.Value.ThrowIfCancellationRequested();
        // Use the CLI naming policy even when a host uses camelCase MCP serialization.
        var explanation = QueryCommandRunner.BuildStatusFieldExplanationJson(field, ProgramRunner.CreateDefaultJsonOptions());
        var isError = explanation.ContainsKey("error_code");
        if (isError)
        {
            explanation["category"] = McpErrorEnvelope.CategoryInvalidArgument;
            explanation["suggestion"] = explanation["hint"]!.DeepClone();
            explanation["retry_safe"] = false;
        }
        var compact = !isError && (format == "compact" || maxBytes.HasValue);
        var payload = compact ? CompactStatusExplanation(explanation) : explanation.DeepClone().AsObject();
        var response = BuildStaticStatusExplanationResponse(id, payload, isError);
        var responseLimit = GetEffectiveResourceReadResponseLimit();
        var fitsFrame = TryMeasureJsonUtf8BytesWithinLimit(response, _jsonOptions, responseLimit, out _);
        if (!fitsFrame && !compact && !isError)
        {
            payload = CompactStatusExplanation(explanation);
            response = BuildStaticStatusExplanationResponse(id, payload, isError: false);
            fitsFrame = TryMeasureJsonUtf8BytesWithinLimit(response, _jsonOptions, responseLimit, out _);
        }
        var fitsPayload = !maxBytes.HasValue
            || TryMeasureJsonUtf8BytesWithinLimit(payload, _jsonOptions, maxBytes.Value - 1, out _);
        if (fitsFrame && fitsPayload)
            return response;

        // The explanation/candidate catalog and request ID are bounded before materialization.
        // 説明・候補一覧と request ID は、この計測より前に上限が適用される。
        TryMeasureJsonUtf8BytesWithinLimit(payload, _jsonOptions, int.MaxValue, out var payloadBytes);
        TryMeasureJsonUtf8BytesWithinLimit(response, _jsonOptions, int.MaxValue, out var frameBytes);
        var minimum = fitsFrame ? (long)payloadBytes + 1 : frameBytes;
        // A JSON-RPC batch allocation is not the configurable whole-response limit.
        // JSON-RPC バッチ内の割当量は、設定可能な応答全体の上限とは異なる。
        var batchLimited = !fitsFrame && _currentBatchResponseItemMaxBytes.Value.HasValue;
        var retry = batchLimited
            ? new JsonObject
            {
                ["action"] = "split_batch",
                ["request_mode"] = "individual",
                ["minimum_response_budget_bytes"] = frameBytes,
            }
            : new JsonObject
            {
                ["action"] = fitsFrame ? "increase_max_bytes" : "increase_response_budget",
                ["recommended_bytes"] = minimum,
            };
        if (batchLimited && maxBytes.HasValue)
            retry["minimum_max_bytes"] = (long)payloadBytes + 1;
        var budgetError = new JsonObject
        {
            ["error_code"] = CommandErrorCodes.ResponseBudgetTooSmall,
            ["category"] = McpErrorEnvelope.CategoryInvalidArgument,
            ["suggestion"] = batchLimited
                ? "Retry this call individually outside the JSON-RPC batch. Set the response budget to at least minimum_response_bytes and any maxBytes to at least minimum_structured_content_bytes."
                : "Increase maxBytes and the server/transport response budget, then retry the same explainField.",
            ["retry_safe"] = true,
            ["requested_bytes"] = maxBytes,
            ["effective_bytes"] = fitsFrame ? maxBytes : responseLimit,
            ["minimum_required_bytes"] = minimum,
            ["minimum_required_bytes_known"] = true,
            ["minimum_required_bytes_uncertain"] = false,
            ["budget_scope"] = fitsFrame ? "structured_content_with_newline" : batchLimited ? "json_rpc_batch_item" : "json_rpc_response",
            ["minimum_structured_content_bytes"] = (long)payloadBytes + 1,
            ["minimum_response_bytes"] = frameBytes,
            ["retry"] = retry,
        };
        return BuildStaticStatusExplanationResponse(id, budgetError, isError: true);

        JsonNode StatusExplanationError(string message)
            => BuildStaticStatusExplanationResponse(id, new JsonObject
            {
                ["message"] = message,
                ["error_code"] = CommandErrorCodes.UsageError,
                ["category"] = McpErrorEnvelope.CategoryInvalidArgument,
                ["suggestion"] = "Use explainField with only format and maxBytes; request runtime status separately.",
                ["retry_safe"] = false,
            }, isError: true);
    }

    private static JsonObject CompactStatusExplanation(JsonObject explanation)
    {
        var fields = ProjectionFieldRegistry.GetStatusExplainCompactFields();
        var row = new JsonObject();
        foreach (var field in fields)
            row[field] = explanation[field]!.DeepClone();
        var metadata = new JsonObject();
        ProjectionFieldRegistry.AddStatusExplainCompactMetadata(metadata,
            explanation.Select(property => property.Key).Except(fields, StringComparer.Ordinal).ToArray());
        return new JsonObject { ["metadata"] = metadata, ["results"] = new JsonArray(row) };
    }

    private static JsonObject BuildStaticStatusExplanationResponse(JsonNode? id, JsonObject payload, bool isError)
    {
        payload["api_version"] = JsonOutputContract.ApiVersion;
        payload["tool"] = "status";
        var result = new JsonObject
        {
            ["content"] = new JsonArray(new JsonObject
            {
                ["type"] = "text",
                ["mimeType"] = "application/json",
                ["text"] = isError ? payload["message"]?.GetValue<string>() ?? "Status field explanation exceeds the response byte budget." : "Status field explanation returned.",
            }),
            ["structuredContent"] = payload,
        };
        if (isError)
            result["isError"] = true;
        return CreateSuccessResponse(true, id, result);
    }
}

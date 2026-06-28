using System.Diagnostics;
using System.Text.Json.Nodes;

namespace CodeIndex.Mcp;

public partial class McpServer
{
    private JsonNode ExecuteBatchQuery(JsonNode? id, JsonNode? args)
    {
        var queriesNode = args?["queries"];
        if (queriesNode is null)
            return CreateToolErrorResponse(id, "Missing or empty required parameter: queries");
        if (queriesNode is not JsonArray queries)
            return CreateToolErrorResponse(id, "Invalid type for argument 'queries' on tool 'batch_query'. Expected array.");
        if (queries.Count == 0)
            return CreateToolErrorResponse(id, "Missing or empty required parameter: queries");

        if (queries.Count > MaxBatchQuerySize)
            return CreateToolErrorResponse(id, $"Batch too large: {queries.Count} queries (max {MaxBatchQuerySize})");

        var resultsArray = new JsonArray();
        var truncatedQueries = new JsonArray();
        var totalStopwatch = Stopwatch.StartNew();
        var adjustments = new ArgumentAdjustmentCollector();
        int successCount = 0;
        int failureCount = 0;
        int? cascadeStartedAtIndex = null;
        var truncated = false;
        var responseByteLimit = ReadBatchQueryResponseByteLimit(args, adjustments);
        var estimateOnly = args?["estimateOnly"]?.GetValue<bool>() ?? false;
        if (estimateOnly)
            return ExecuteBatchQueryEstimate(id, queries, responseByteLimit, adjustments);
        var estimatedResponseBytes = EstimateBatchResponseBytes(id, "Executed 0 queries.", queries.Count, successCount, failureCount,
            GetBatchFailureScope(queries.Count, successCount, failureCount, cascadeStartedAtIndex), cascadeStartedAtIndex,
            responseByteLimit, resultsArray, truncated: false, truncatedQueries, adjustments);

        bool TryAppendResult(JsonObject entry, string? toolName, JsonNode? toolArgs, int requestIndex, string? slotId, bool successfulSlot = false, bool failedSlot = false)
        {
            var candidateSuccessCount = successCount + (successfulSlot ? 1 : 0);
            var candidateFailureCount = failureCount + (failedSlot ? 1 : 0);
            var candidateExecutedCount = candidateSuccessCount + candidateFailureCount;
            var candidateBytes = EstimateBatchAppendBytes(
                estimatedResponseBytes,
                entry,
                candidateExecutedCount,
                candidateSuccessCount,
                candidateFailureCount);
            if (candidateBytes > responseByteLimit)
            {
                truncated = true;
                cascadeStartedAtIndex ??= requestIndex;
                var truncatedEntry = new JsonObject
                {
                    ["request_index"] = requestIndex,
                    ["args_summary"] = BuildArgsSummary(toolArgs),
                    ["reason"] = "response_byte_limit_exceeded",
                };
                AddBatchSlotId(truncatedEntry, slotId);
                AddToolDisplayData(truncatedEntry, toolName);
                truncatedQueries.Add(truncatedEntry);
                return false;
            }

            estimatedResponseBytes = candidateBytes;
            resultsArray.Add(entry);
            return true;
        }

        static void CopySlotErrorData(JsonObject entry, JsonObject? extraData)
        {
            if (extraData is null)
                return;

            foreach (var (key, value) in extraData)
            {
                if (key is "message" or "jsonrpc_invalid_params")
                    continue;
                if (entry.ContainsKey(key))
                    continue;
                entry[key] = value?.DeepClone();
            }
        }

        void AppendSlotError(int requestIndex, string? slotId, string? toolName, JsonNode? toolArgs, Stopwatch slotStopwatch, string errorMessage,
            int? code = null, string? category = null, string? suggestion = null, bool? retrySafe = null, JsonObject? extraData = null)
        {
            slotStopwatch.Stop();
            var entry = new JsonObject
            {
                ["request_index"] = requestIndex,
                ["ok"] = false,
                ["correlation_id"] = CurrentCorrelationContext.Value?.CorrelationId,
                ["args_summary"] = BuildArgsSummary(toolArgs),
                ["elapsed_ms"] = slotStopwatch.ElapsedMilliseconds,
                ["error"] = errorMessage,
            };
            AddBatchSlotId(entry, slotId);
            AddToolDisplayData(entry, toolName);
            CopySlotErrorData(entry, extraData);
            if (code.HasValue)
                entry["code"] = code.Value;
            // #1581: batch_query slot errors also carry the canonical envelope so clients
            // get the same `category` / `suggestion` / `retry_safe` decision shape on every
            // failure path. Defaults stay null when the call site cannot classify safely.
            // #1581: スロットエラーにも canonical envelope を付与し、失敗経路を問わずクライアント
            // が同じ判定形状を扱えるようにする。分類できない呼び出し元は null のまま渡す。
            if (category != null)
                entry["category"] = category;
            if (suggestion != null)
                entry["suggestion"] = suggestion;
            if (retrySafe.HasValue)
                entry["retry_safe"] = retrySafe.Value;
            TryAppendResult(entry, toolName, toolArgs, requestIndex, slotId, failedSlot: true);
            failureCount++;
        }

        // Rate-limited slot error variant. Mirrors the shape of `AppendSlotError` so existing
        // clients keep working, but also surfaces `error_category` + `retry_after_ms` next to
        // `error` so well-behaved clients can detect throttling and back off per-slot instead
        // of inferring it from the human-readable message. The outer call also consumes a
        // batch_query token, so spamming `batch_query` with N inner calls is bounded by both
        // the batch_query bucket and per-tool buckets (#1560).
        // レート制限スロット用の AppendSlotError 変種。既存クライアント互換のため `error` を
        // そのまま維持しつつ、`error_category` と `retry_after_ms` を併記して、スロット単位での
        // 検出・バックオフを可能にする。外側の batch_query 自体もトークンを消費するため、
        // N 個の内側呼び出しを含むスパムは batch_query バケットとツール別バケットの両方で
        // 上限が掛かる（#1560）。
        void AppendRateLimitedSlot(int requestIndex, string? slotId, string? toolName, JsonNode? toolArgs, Stopwatch slotStopwatch, long retryAfterMs)
        {
            slotStopwatch.Stop();
            var toolDisplay = toolName is null ? "(missing)" : BoundToolNameForDisplay(toolName).Text;
            // #1581: emit the canonical envelope (`category`, `suggestion`, `retry_safe`)
            // next to the legacy #1560 fields so clients have a single decision shape across
            // top-level and slot-level rate-limit errors.
            // #1581: 既存の #1560 フィールド（`error_category`, `retry_after_ms`）と並べて
            // canonical envelope（`category`, `suggestion`, `retry_safe`）も書き、トップレベル
            // とスロット単位のレート制限エラーで判定形状を揃える。
            var entry = new JsonObject
            {
                ["request_index"] = requestIndex,
                ["ok"] = false,
                ["correlation_id"] = CurrentCorrelationContext.Value?.CorrelationId,
                ["args_summary"] = BuildArgsSummary(toolArgs),
                ["elapsed_ms"] = slotStopwatch.ElapsedMilliseconds,
                ["error"] = $"Rate limit exceeded for tool '{toolDisplay}' (retry after {retryAfterMs} ms).",
                ["error_category"] = "rate_limited",
                ["retry_after_ms"] = retryAfterMs,
                ["category"] = McpErrorEnvelope.CategoryRateLimited,
                ["suggestion"] = $"Back off for at least {retryAfterMs} ms before retrying this tool.",
                ["retry_safe"] = true,
            };
            AddBatchSlotId(entry, slotId);
            AddToolDisplayData(entry, toolName);
            TryAppendResult(entry, toolName, toolArgs, requestIndex, slotId, failedSlot: true);
            failureCount++;
        }

        for (var requestIndex = 0; requestIndex < queries.Count; requestIndex++)
        {
            using var slotCorrelation = BeginChildCorrelation(requestIndex + 1);
            var q = queries[requestIndex];
            var queryObject = q as JsonObject;
            var toolName = queryObject?["tool"] is JsonValue toolValue && toolValue.TryGetValue<string>(out var parsedToolName)
                ? parsedToolName
                : null;
            var toolArgs = queryObject?["arguments"];
            var slotId = ReadBatchSlotId(queryObject);
            var slotStopwatch = Stopwatch.StartNew();

            if (truncated)
            {
                slotStopwatch.Stop();
                cascadeStartedAtIndex ??= requestIndex;
                var truncatedEntry = new JsonObject
                {
                    ["request_index"] = requestIndex,
                    ["args_summary"] = BuildArgsSummary(toolArgs),
                    ["reason"] = "response_byte_limit_already_exceeded",
                };
                AddBatchSlotId(truncatedEntry, slotId);
                AddToolDisplayData(truncatedEntry, toolName);
                truncatedQueries.Add(truncatedEntry);
                continue;
            }

            if (string.IsNullOrEmpty(toolName))
            {
                var message = queryObject is null ? "Each query must be an object with a string tool name." : "Missing tool name";
                AppendSlotError(requestIndex, slotId, toolName, toolArgs, slotStopwatch, message,
                    category: McpErrorEnvelope.CategoryMissingParameter,
                    suggestion: "Each batch_query slot must include a string `tool` field.",
                    retrySafe: false);
                continue;
            }
            if (toolName.Length > McpBoundedText.MaxToolNameChars)
            {
                AppendSlotError(requestIndex, slotId, toolName, toolArgs, slotStopwatch, BuildUnknownToolMessage(toolName),
                    category: McpErrorEnvelope.CategoryToolUnknown,
                    suggestion: "Call tools/list to see the tool catalog. Slot tool names are case-sensitive.",
                    retrySafe: false);
                continue;
            }

            if (ValidateToolArguments(toolName, toolArgs) is JsonObject argumentError)
            {
                AppendSlotError(requestIndex, slotId, toolName, toolArgs, slotStopwatch, argumentError["message"]!.GetValue<string>(),
                    category: McpErrorEnvelope.CategoryInvalidArgument,
                    suggestion: "Use exactly the argument names advertised by tools/list for this tool.",
                    retrySafe: false,
                    extraData: argumentError);
                continue;
            }

            if (ValidateCommonListArguments(toolArgs) is JsonObject listArgumentError)
            {
                AppendSlotError(requestIndex, slotId, toolName, toolArgs, slotStopwatch, listArgumentError["message"]!.GetValue<string>(),
                    category: McpErrorEnvelope.CategoryInvalidArgument,
                    suggestion: "Send only non-empty string entries within the documented MCP array bounds.",
                    retrySafe: false,
                    extraData: listArgumentError);
                continue;
            }

            // Honor the per-deployment enablement gate inside batch_query too (#1561). Without
            // this, an operator who disabled a tool through `CDIDX_MCP_TOOLS_ALLOW` /
            // `CDIDX_MCP_TOOLS_DENY` could still reach it by smuggling the name into a batch
            // slot. Only intercept known-but-disabled tools so unknown names still surface as
            // the existing "Unknown tool" slot error below. The gate runs BEFORE the
            // write-operation guard so a disabled write tool (e.g. `index` excluded via deny)
            // surfaces as the structured `code: -32601` "Tool not enabled" — the operator's
            // intent is "this tool is not on offer", which is more informative for AI clients
            // than the generic write-in-batch message.
            // batch_query 内でもデプロイ単位の有効化ゲートを尊重する (#1561)。これが無いと、
            // `CDIDX_MCP_TOOLS_ALLOW` / `CDIDX_MCP_TOOLS_DENY` で無効化したツールに batch 経由で
            // 到達できてしまう。既知だが無効なツールだけを捕まえ、未知名は既存の "Unknown tool"
            // slot エラーに任せる。書き込みツールであっても gate で無効化されていれば、より
            // 情報量のある `code: -32601` "Tool not enabled" を返したいので、書き込みガードより
            // 前にこのゲートを置く。
            if (McpToolFilter.IsKnownTool(toolName) && !_toolFilter.IsEnabled(toolName))
            {
                AppendSlotError(requestIndex, slotId, toolName, toolArgs, slotStopwatch, $"Tool not enabled: {toolName}", code: -32601,
                    category: McpErrorEnvelope.CategoryToolDisabled,
                    suggestion: "This tool is disabled on the server. Ask the operator to enable it or remove the slot.",
                    retrySafe: false);
                continue;
            }

            // Block write operations in batch / バッチ内では書き込み操作をブロック
            if (toolName == "index" || toolName == "backfill_fold" || toolName == "suggest_improvement")
            {
                AppendSlotError(requestIndex, slotId, toolName, toolArgs, slotStopwatch, $"{toolName} is not allowed in batch_query (write operation)",
                    category: McpErrorEnvelope.CategoryInvalidArgument,
                    suggestion: "Call write tools (index / backfill_fold / suggest_improvement) directly via tools/call, not inside batch_query.",
                    retrySafe: false);
                continue;
            }

            // Reject nested batch_query before the rate-limit consumption so the per-(tool,
            // caller) bucket cannot be drained by recursive expansion (and so the failure
            // message is clear instead of the generic "Unknown tool: batch_query") (#1560).
            // 再帰展開でバケットを消費させないため、レート制限消費の前に内側 batch_query を
            // 明示的に拒否する。エラーメッセージも "Unknown tool: batch_query" の汎用ではなく
            // ネスト禁止の明示文に揃える（#1560）。
            if (toolName == "batch_query")
            {
                AppendSlotError(requestIndex, slotId, toolName, toolArgs, slotStopwatch, "batch_query cannot be nested inside batch_query.",
                    category: McpErrorEnvelope.CategoryInvalidArgument,
                    suggestion: "Flatten the nested batch_query into top-level slots.",
                    retrySafe: false);
                continue;
            }

            // Throttle each inner slot too, otherwise a single allowed batch_query call could
            // still drive N inner searches through and defeat the per-(tool, caller) limiter
            // the outer dispatch enforces. The decision is per (inner-tool, caller) so an
            // over-quota slot can coexist with allowed slots in the same batch (#1560).
            // 内側スロット単位でもスロットルする。これを行わないと外側の batch_query が 1 回通った
            // だけで N 個の内側呼び出しが素通りし、(tool, caller) 制限が batch_query 経由で
            // 迂回されてしまう。判定は (内側ツール, caller) 単位なので、同一バッチ内で許可スロット
            // と超過スロットを併存させられる（#1560）。
            var slotDecision = RateLimiter.TryAcquire(toolName, _caller);
            if (!slotDecision.Allowed)
            {
                AppendRateLimitedSlot(requestIndex, slotId, toolName, toolArgs, slotStopwatch, slotDecision.RetryAfterMs);
                continue;
            }

            if (ValidateProjectFilterArguments(toolArgs) is JsonObject projectFilterError)
            {
                AppendSlotError(requestIndex, slotId, toolName, toolArgs, slotStopwatch, projectFilterError["message"]!.GetValue<string>(),
                    category: McpErrorEnvelope.CategoryInvalidArgument,
                    suggestion: "Use a project name or project path from the current workspace, or correct the solution filter.",
                    retrySafe: false,
                    extraData: projectFilterError);
                continue;
            }

            try
            {
                // Execute the tool and extract the structured content / ツールを実行し構造化コンテンツを抽出
                var response = toolName switch
                {
                    "search" => ExecuteSearch(null, toolArgs),
                    "definition" => ExecuteDefinition(null, toolArgs),
                    "references" => ExecuteReferences(null, toolArgs),
                    "callers" => ExecuteCallers(null, toolArgs),
                    "callees" => ExecuteCallees(null, toolArgs),
                    "symbols" => ExecuteSymbols(null, toolArgs),
                    "files" => ExecuteFiles(null, toolArgs),
                    "find_in_file" => ExecuteFindInFile(null, toolArgs),
                    "excerpt" => ExecuteExcerpt(null, toolArgs),
                    "map" => ExecuteMap(null, toolArgs),
                    "analyze_symbol" => ExecuteAnalyzeSymbol(null, toolArgs),
                    "status" => ExecuteStatus(null, toolArgs),
                    "outline" => ExecuteOutline(null, toolArgs),
                    "deps" => ExecuteDeps(null, toolArgs),
                    "impact_analysis" => ExecuteImpactAnalysis(null, toolArgs),
                    "languages" => ExecuteLanguages(null, toolArgs),
                    "validate" => ExecuteValidate(null, toolArgs),
                    "unused_symbols" => ExecuteUnusedSymbols(null, toolArgs),
                    "symbol_hotspots" => ExecuteSymbolHotspots(null, toolArgs),
                    "ping" => ExecutePing(null),
                    _ => null,
                };

                if (response == null)
                {
                    AppendSlotError(requestIndex, slotId, toolName, toolArgs, slotStopwatch, BuildUnknownToolMessage(toolName),
                        category: McpErrorEnvelope.CategoryToolUnknown,
                        suggestion: "Call tools/list to see the tool catalog. Slot tool names are case-sensitive.",
                        retrySafe: false);
                    continue;
                }

                // Check for tool-level errors (validation failures return isError=true)
                // ツールレベルのエラーを確認（バリデーション失敗は isError=true を返す）
                var isError = response["result"]?["isError"]?.GetValue<bool>() ?? false;
                if (isError)
                {
                    var errorText = response["result"]?["content"]?[0]?["text"]?.GetValue<string>() ?? "Unknown error";
                    // #1581: lift the inner tool's structured envelope into the batch slot so
                    // the slot carries the same category/suggestion/retry_safe the standalone
                    // tools/call response would have. Missing fields (older inner tools) fall
                    // back to AppendSlotError defaults.
                    // #1581: 内側ツールの structured envelope をスロットに転写し、tools/call
                    // 単体呼び出しと同じカテゴリ判定をスロットでも提供する。フィールドが無い
                    // 旧経路は AppendSlotError の既定（null = 未指定）に戻す。
                    var innerStructured = response["result"]?["structuredContent"] as JsonObject;
                    var innerCategory = innerStructured?["category"]?.GetValue<string>();
                    var innerSuggestion = innerStructured?["suggestion"]?.GetValue<string>();
                    bool? innerRetrySafe = null;
                    if (innerStructured?["retry_safe"] is JsonValue rv && rv.TryGetValue<bool>(out var rb))
                        innerRetrySafe = rb;
                    AppendSlotError(requestIndex, slotId, toolName, toolArgs, slotStopwatch, errorText,
                        category: innerCategory,
                        suggestion: innerSuggestion,
                        retrySafe: innerRetrySafe);
                    continue;
                }

                slotStopwatch.Stop();
                var structured = response["result"]?["structuredContent"];
                var slotSummary = response["result"]?["content"]?[0]?["text"]?.GetValue<string>();
                var entry = new JsonObject
                {
                    ["request_index"] = requestIndex,
                    ["ok"] = true,
                    ["correlation_id"] = CurrentCorrelationContext.Value?.CorrelationId,
                    ["args_summary"] = BuildArgsSummary(toolArgs),
                    ["elapsed_ms"] = slotStopwatch.ElapsedMilliseconds,
                    ["summary"] = slotSummary,
                    ["result"] = structured?.DeepClone(),
                };
                AddBatchSlotId(entry, slotId);
                AddToolDisplayData(entry, toolName);
                TryAppendResult(entry, toolName, toolArgs, requestIndex, slotId, successfulSlot: true);
                successCount++;
            }
            catch (Exception ex)
            {
                // #2849: classify and sanitize slot exceptions the same way standalone
                // tools/call does, so bound values, paths, and SQL/content snippets stay
                // in stderr instead of the batch_query response.
                DeferFrameLog(() =>
                {
                    WriteMcpLogLine(BuildToolErrorLog(toolName, ex));
                    Database.DbDebug.DumpToStderr(ex);
                });
                var classification = McpErrorEnvelope.ClassifyException(ex);
                AppendSlotError(requestIndex, slotId, toolName, toolArgs, slotStopwatch, BuildSanitizedToolErrorMessage(toolName, ex),
                    category: classification.Category,
                    suggestion: classification.Suggestion,
                    retrySafe: classification.RetrySafe);
            }
        }

        totalStopwatch.Stop();
        var totalElapsedMs = totalStopwatch.ElapsedMilliseconds;
        JsonObject BuildPayload()
        {
            var payload = new JsonObject
            {
                ["count"] = resultsArray.Count,
                ["total_count"] = queries.Count,
                ["success_count"] = successCount,
                ["failure_count"] = failureCount,
                ["partial_failure"] = failureCount > 0 || cascadeStartedAtIndex.HasValue,
                ["failure_scope"] = GetBatchFailureScope(queries.Count, successCount, failureCount, cascadeStartedAtIndex),
                ["cascade_started_at_index"] = cascadeStartedAtIndex,
                ["metadata"] = new JsonObject
                {
                    ["submitted"] = queries.Count,
                    ["executed"] = successCount + failureCount,
                    ["errors"] = failureCount,
                    ["total_elapsed_ms"] = totalElapsedMs,
                    ["success_count"] = successCount,
                    ["failure_count"] = failureCount,
                    ["response_byte_limit"] = responseByteLimit,
                    ["estimated_response_bytes"] = responseByteLimit,
                },
                ["results"] = resultsArray.DeepClone(),
            };
            adjustments.ApplyTo(payload);
            return payload;
        }

        string BuildSummary()
        {
            var baseSummary = failureCount == 0
                ? $"Executed {successCount + failureCount} of {queries.Count} queries in {totalElapsedMs} ms (all succeeded)."
                : $"Executed {successCount + failureCount} of {queries.Count} queries in {totalElapsedMs} ms ({successCount} succeeded, {failureCount} failed).";
            return truncated
                ? baseSummary + $" Response truncated at {responseByteLimit} bytes; split the batch or lower per-slot limits."
                : baseSummary;
        }

        JsonObject payload;
        string summary;
        var compactSummary = false;
        while (true)
        {
            payload = BuildPayload();
            if (truncated)
            {
                payload["truncated"] = true;
                payload["truncated_queries"] = truncatedQueries.DeepClone();
                payload["split_hint"] = BuildBatchSplitHint(queries.Count, cascadeStartedAtIndex, resultsArray.Count);
            }

            summary = BuildSummary();
            if (compactSummary)
                summary = $"Response truncated at {responseByteLimit} bytes.";
            estimatedResponseBytes = EstimateJsonUtf8Bytes(CreateToolResult(id, summary, payload.DeepClone()), responseByteLimit);
            if (estimatedResponseBytes <= responseByteLimit)
                break;
            if (resultsArray.Count > 0)
            {
                var removed = resultsArray[resultsArray.Count - 1];
                truncated = true;
                if (removed?["request_index"] is JsonValue requestIndexValue
                    && requestIndexValue.TryGetValue<int>(out var removedRequestIndex))
                {
                    cascadeStartedAtIndex = cascadeStartedAtIndex.HasValue
                        ? Math.Min(cascadeStartedAtIndex.Value, removedRequestIndex)
                        : removedRequestIndex;
                }
                truncatedQueries.Insert(0, new JsonObject
                {
                    ["request_index"] = removed?["request_index"]?.DeepClone(),
                    ["slot_id"] = removed?["slot_id"]?.DeepClone(),
                    ["tool"] = removed?["tool"]?.DeepClone(),
                    ["args_summary"] = removed?["args_summary"]?.DeepClone(),
                    ["reason"] = "final_response_byte_limit_exceeded",
                });
                resultsArray.RemoveAt(resultsArray.Count - 1);
                continue;
            }
            if (RemoveBatchTruncatedQueryToolDisplay(truncatedQueries))
                continue;
            if (truncatedQueries.Count > 1)
            {
                truncatedQueries.RemoveAt(truncatedQueries.Count - 1);
                continue;
            }
            if (CompactBatchTruncatedQueryArgsSummaries(truncatedQueries))
                continue;
            if (truncated && !compactSummary)
            {
                compactSummary = true;
                continue;
            }
            break;
        }

        ((JsonObject)payload["metadata"]!)["estimated_response_bytes"] = estimatedResponseBytes;
        return CreateToolResult(id, summary, payload);
    }
}

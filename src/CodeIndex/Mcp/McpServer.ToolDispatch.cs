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


    // Tool definitions are in McpToolDefinitions.cs / ツール定義は McpToolDefinitions.cs に分離


    /// <summary>
    /// Execute a tool call.
    /// ツール呼び出しを実行。
    /// </summary>
    private async Task<JsonNode> HandleToolsCallAsync(bool hasId, JsonNode? id, JsonNode? callParams)
    {
        _currentIndexAuditContext.Value = new IndexAuditContext();
        var callParamsObject = callParams as JsonObject;
        var args = callParamsObject?["arguments"];
        var toolName = callParamsObject?["name"] is JsonValue toolNameValue
            && toolNameValue.TryGetValue<string>(out var parsedToolName)
                ? parsedToolName
                : null;
        var observedToolName = toolName ?? "(missing)";

        Database.DbDebug.ResetContext();
        var metricsStartedAt = _timeProvider.GetUtcNow();
        var metricsStopwatch = System.Diagnostics.Stopwatch.StartNew();
        string? metricsError = null;
        JsonNode response;
        JsonObject CreateUnknownToolResponseForMetrics()
        {
            metricsError = "unknown_tool";
            return CreateUnknownToolErrorResponse(hasId: true, id: id, observedToolName);
        }

        try
        {
            var caller = CurrentInitializeState.Caller;
            // Charge every direct tools/call to one caller-wide bucket before detailed
            // name, enablement, or argument validation. Canonical known tools then retain
            // their existing secondary per-tool limit. This prevents a caller from rotating
            // malformed requests across known names to multiply its effective burst (#4547).
            // direct tools/call はすべて、名前・enablement・argument の詳細検証前に caller-wide
            // bucket へ課金する。canonical な既知 tool は既存の secondary per-tool 制限も維持し、
            // malformed request の既知名ローテーションによる burst 増幅を防ぐ（#4547）。
            var decision = RateLimiter.TryAcquireHierarchy(
                RateLimiter.ToolsCallPreValidationBucketName,
                ResolveKnownRateLimitBucketName(toolName),
                caller);
            if (!decision.Allowed)
            {
                metricsError = "rate_limited";
                DeferFrameLog(BuildRateLimitedLog(observedToolName, caller, decision.RetryAfterMs));
                response = CreateRateLimitedErrorResponse(id, observedToolName, caller, decision.RetryAfterMs);
            }
            else if (toolName is null)
            {
                metricsError = "missing_tool_name";
                response = CreateErrorResponse(hasId: true, id: id, code: -32602, message: "Missing tool name",
                    category: McpErrorEnvelope.CategoryMissingParameter,
                    suggestion: "tools/call requires `params.name`. Send the tool identifier (e.g. \"search\", \"definition\") as a string.",
                    retrySafe: false);
            }
            // Per-deployment enablement gate (#1561). The rate-limit check deliberately runs
            // first so disabled-tool retries cannot bypass request-cost protection (#4547).
            // デプロイ単位の有効化ゲート (#1561)。disabled tool の再試行で request-cost
            // protection を回避できないよう、rate-limit check を先に実行する（#4547）。
            else if (McpToolFilter.IsKnownTool(toolName) && !_toolFilter.IsEnabled(toolName))
            {
                metricsError = "tool_disabled";
                response = CreateErrorResponse(hasId: true, id: id, code: -32601, message: $"Tool not enabled: {toolName}",
                    category: McpErrorEnvelope.CategoryToolDisabled,
                    suggestion: "This tool is disabled on the server (CDIDX_MCP_TOOLS_ALLOW / CDIDX_MCP_TOOLS_DENY). Ask the operator to enable it or use a different tool.",
                    retrySafe: false,
                    extraData: new JsonObject { ["tool"] = toolName });
            }
            else
            {
                var progressToken = TryReadProgressToken(callParamsObject);
                var toolNameTooLong = toolName.Length > McpBoundedText.MaxToolNameChars;
                if (toolNameTooLong)
                {
                    response = CreateUnknownToolResponseForMetrics();
                }
                else if (ValidateToolArguments(toolName, args) is JsonObject argumentError)
                {
                    metricsError = "invalid_argument";
                    if (argumentError["jsonrpc_invalid_params"] is JsonValue invalidParamsMarker
                        && invalidParamsMarker.TryGetValue<bool>(out var invalidParams)
                        && invalidParams)
                    {
                        argumentError.Remove("jsonrpc_invalid_params");
                        response = CreateErrorResponse(hasId: true, id: id, code: -32602, message: argumentError["message"]!.GetValue<string>(),
                            category: McpErrorEnvelope.CategoryInvalidArgument,
                            suggestion: "Use the JSON types advertised by tools/list for this tool.",
                            retrySafe: false,
                            extraData: argumentError);
                    }
                    else
                    {
                        response = CreateToolErrorResponse(id, argumentError["message"]!.GetValue<string>(),
                            category: McpErrorEnvelope.CategoryInvalidArgument,
                            suggestion: "Use exactly the argument names advertised by tools/list for this tool.",
                            retrySafe: false,
                            extraData: argumentError);
                    }
                }
                else if (ValidateCommonListArguments(args) is JsonObject listArgumentError)
                {
                    metricsError = "invalid_list_argument";
                    response = CreateToolErrorResponse(id, listArgumentError["message"]!.GetValue<string>(),
                        category: McpErrorEnvelope.CategoryInvalidArgument,
                        suggestion: "Send only non-empty string entries within the documented MCP array bounds.",
                        retrySafe: false,
                        extraData: listArgumentError);
                }
                else if (ValidateProjectFilterArguments(args) is JsonObject projectFilterError)
                {
                    metricsError = "invalid_project_filter";
                    response = CreateToolErrorResponse(id, projectFilterError["message"]!.GetValue<string>(),
                        category: McpErrorEnvelope.CategoryInvalidArgument,
                        suggestion: "Use a project name or project path from the current workspace, or correct the solution filter.",
                        retrySafe: false,
                        extraData: projectFilterError);
                }
                else
                {
                    response = await DispatchToolCallAsync(
                        toolName,
                        id,
                        args,
                        progressToken,
                        CreateUnknownToolResponseForMetrics).ConfigureAwait(false);
                }
            }
        }
        catch (OperationCanceledException) when (_currentRequestToken.Value.IsCancellationRequested)
        {
            metricsError = nameof(OperationCanceledException);
            throw;
        }
        catch (Exception ex)
        {
            // Stderr keeps a sanitized local diagnostic, while the JSON-RPC tool
            // result is reduced to the tool name + exception type. Raw exception
            // messages can echo bound parameter values (e.g. SQLite errors quote
            // the offending literal), paths, or content fragments, which would
            // otherwise leak through the MCP transcript (#1530 / #4124).
            // stderr には sanitize 済みのローカル診断だけを残し、JSON-RPC のツール結果は
            // tool 名 + 例外型に絞る。SQLite 例外などの生メッセージはバインド値、
            // 該当リテラル、パス、索引内容を含み得るため、MCP transcript へ流さない
            // (#1530 / #4124)。
            var dbDebugDump = Database.DbDebug.CaptureDump(ex);
            DeferFrameLog(() =>
            {
                WriteMcpLogLine(BuildToolErrorLog(observedToolName, ex));
                Database.DbDebug.WriteCapturedDumpToStderr(dbDebugDump);
            });
            metricsError = ex.GetType().Name;
            var classification = McpErrorEnvelope.ClassifyException(ex);
            response = CreateToolErrorResponse(true, id, BuildSanitizedToolErrorMessage(observedToolName, ex),
                category: classification.Category,
                suggestion: classification.Suggestion,
                retrySafe: classification.RetrySafe,
                extraData: BuildToolExceptionData(observedToolName, ex.GetType().Name));
        }
        finally
        {
            Database.DbDebug.ResetContext();
            if (MetricsSink.IsActive)
            {
                metricsStopwatch.Stop();
                var metricsTool = BoundToolNameForDisplay(observedToolName).Text;
                var requestId = CurrentCorrelationContext.Value?.TelemetryRequestId;
                MetricsSink.Record(new MetricsEvent(
                    Timestamp: metricsStartedAt,
                    Tool: metricsTool,
                    Source: "mcp",
                    ElapsedMs: metricsStopwatch.Elapsed.TotalMilliseconds,
                    ExitCode: metricsError == null ? 0 : 1,
                    Language: TryReadMetricStringArg(args, "language") ?? TryReadMetricStringArg(args, "lang"),
                    Error: metricsError,
                    RequestId: requestId?.Token,
                    RequestIdType: requestId?.Type,
                    RequestIdLength: requestId?.Length));
            }
        }

        // Audit observes the wire response (for result_count / error_code / isError),
        // invocation-scoped authorization identity, and any sanitized exception type, so
        // emission happens after the metrics finally block. Stop the stopwatch idempotently
        // — the metrics path may have already stopped it. TryEmitAudit is best-effort internally (#1562).
        // audit は wire response、invocation-scoped authorization identity、例外型を参照するため
        // metrics finally の後で出力する。Stopwatch.Stop は冪等。
        // TryEmitAudit 内部でベストエフォート化済み (#1562)。
        metricsStopwatch.Stop();
        var auditErrorType = metricsError == "unknown_tool" ? null : metricsError;
        TryEmitAudit(hasId, observedToolName, id, args, response, metricsStartedAt, metricsStopwatch.Elapsed.TotalMilliseconds, errorType: auditErrorType);
        _currentIndexAuditContext.Value = null;
        EmitToolInvocationTelemetry(observedToolName, args, response, metricsStartedAt, metricsStopwatch.Elapsed.TotalMilliseconds, metricsError);
        return response;
    }

    private async Task<JsonNode> DispatchToolCallAsync(
        string toolName,
        JsonNode? id,
        JsonNode? args,
        JsonNode? progressToken,
        Func<JsonObject> createUnknownToolResponse)
    {
        if (toolName is "index" or "backfill_fold")
        {
            await _sharedDbWriteGate.WaitAsync(_currentRequestToken.Value).ConfigureAwait(false);
            try
            {
                return toolName == "index"
                    ? await ExecuteIndexAsync(id, args, progressToken).ConfigureAwait(false)
                    : await ExecuteBackfillFoldAsync(id, args, progressToken).ConfigureAwait(false);
            }
            finally
            {
                _sharedDbWriteGate.Release();
            }
        }

        return toolName switch
        {
            "search" => ExecuteSearch(id, args),
            "definition" => ExecuteDefinition(id, args),
            "references" => ExecuteReferences(id, args),
            "callers" => ExecuteCallers(id, args),
            "callees" => ExecuteCallees(id, args),
            "symbols" => ExecuteSymbols(id, args),
            "files" => ExecuteFiles(id, args),
            "find_in_file" => ExecuteFindInFile(id, args),
            "excerpt" => ExecuteExcerpt(id, args),
            "read_resource" => ExecuteReadResource(id, args),
            "map" => ExecuteMap(id, args),
            "analyze_symbol" => ExecuteAnalyzeSymbol(id, args),
            "status" => ExecuteStatus(id, args),
            "outline" => ExecuteOutline(id, args),
            "batch_query" => ExecuteBatchQuery(id, args),
            "deps" => ExecuteDeps(id, args),
            "impact_analysis" => ExecuteImpactAnalysis(id, args),
            "languages" => ExecuteLanguages(id, args),
            "validate" => ExecuteValidate(id, args),
            "unused_symbols" => ExecuteUnusedSymbols(id, args),
            "symbol_hotspots" => ExecuteSymbolHotspots(id, args),
            "ping" => ExecutePing(id),
            "suggest_improvement" => await ExecuteSuggestImprovementAsync(id, args).ConfigureAwait(false),
            _ => createUnknownToolResponse(),
        };
    }

    private void EmitToolInvocationTelemetry(string toolName, JsonNode? args, JsonNode response, DateTimeOffset startedAt, double elapsedMs, string? errorType)
    {
        var context = CurrentCorrelationContext.Value;
        var (errorCode, observedErrorType) = ExtractErrorCode(response);
        var resultCount = ExtractResultCount(response);
        var (argKeys, argLengths, argKeyLengths, _) = SanitizeArgs(
            args,
            includeValues: false,
            out _,
            out _,
            out _,
            out _,
            out var argKeysTruncated,
            out var argKeyTruncationReasons,
            out var argKeysOmittedCount,
            out var argKeyNamesTruncatedCount);
        var toolDisplay = BoundToolNameForDisplay(toolName);
        var argsObject = new JsonObject();
        foreach (var pair in argLengths)
            argsObject[pair.Key] = pair.Value;

        var evt = new JsonObject
        {
            ["event"] = "mcp.tool.invocation",
            ["timestamp"] = startedAt.ToString("O", System.Globalization.CultureInfo.InvariantCulture),
            ["tool"] = toolDisplay.Text,
            ["request_id"] = context?.TelemetryRequestId?.Token,
            ["request_id_type"] = context?.TelemetryRequestId?.Type,
            ["request_id_length"] = context?.TelemetryRequestId?.Length,
            ["correlation_id"] = context?.CorrelationId,
            ["elapsed_ms"] = Math.Round(elapsedMs, 3),
            ["status"] = errorCode == 0 ? "success" : "error",
            ["error_code"] = errorCode == 0 ? null : errorCode,
            ["error_type"] = errorType ?? observedErrorType,
            ["result_count"] = resultCount,
            ["arg_keys"] = JsonSerializer.SerializeToNode(argKeys, _jsonOptions),
            ["arg_lengths"] = argsObject,
        };
        toolDisplay.AddMetadata(evt, "tool");
        AddArgKeyMetadata(evt, argKeyLengths, argKeysOmittedCount, argKeyNamesTruncatedCount);
        if (argKeysTruncated)
            evt["arg_keys_truncated"] = true;
        if (argKeyTruncationReasons.Count > 0)
            evt["arg_key_truncation_reasons"] = JsonSerializer.SerializeToNode(argKeyTruncationReasons, _jsonOptions);
        DeferFrameLog(() => WriteMcpLogLine(evt.ToJsonString(_jsonOptions)));
    }

    private JsonNode? TryReadProgressToken(JsonNode? callParams)
    {
        var token = callParams?["_meta"]?["progressToken"];
        if (token is null)
            return null;

        if (!IsSupportedProgressToken(token))
            return null;

        return TryMeasureJsonUtf8BytesWithinLimit(token, _jsonOptions, McpBoundedText.MaxProgressTokenJsonBytes, out _)
            ? McpJsonNode.Clone(token)
            : null;
    }

    private static bool IsSupportedProgressToken(JsonNode token)
    {
        var nodeCount = 0;
        return IsSupportedProgressToken(token, depth: 0, ref nodeCount);
    }

    private static bool IsSupportedProgressToken(JsonNode token, int depth, ref int nodeCount)
    {
        if (depth > McpBoundedText.MaxProgressTokenDepth)
            return false;

        nodeCount++;
        if (nodeCount > McpBoundedText.MaxProgressTokenNodeCount)
            return false;

        return token switch
        {
            JsonValue value => IsSupportedProgressTokenScalar(value),
            JsonObject obj => IsSupportedProgressTokenObject(obj, depth, ref nodeCount),
            _ => false,
        };
    }

    private static bool IsSupportedProgressTokenScalar(JsonValue value)
        => value.GetValueKind() switch
        {
            JsonValueKind.String => value.TryGetValue<string>(out var text)
                && text.Length <= McpBoundedText.MaxProgressTokenStringChars,
            JsonValueKind.Number or JsonValueKind.True or JsonValueKind.False => true,
            _ => false,
        };

    private static bool IsSupportedProgressTokenObject(JsonObject obj, int depth, ref int nodeCount)
    {
        foreach (var pair in obj)
        {
            if (pair.Key.Length > McpBoundedText.MaxProgressTokenPropertyNameChars)
                return false;
            if (pair.Value is null)
            {
                nodeCount++;
                if (nodeCount > McpBoundedText.MaxProgressTokenNodeCount)
                    return false;
                continue;
            }

            if (!IsSupportedProgressToken(pair.Value, depth + 1, ref nodeCount))
                return false;
        }

        return true;
    }

    private async Task EmitProgressNotificationAsync(JsonNode? progressToken, long progress, long? total, string? message = null)
    {
        if (progressToken is null || _currentOutOfBandFrameWriter.Value is not { } writer)
            return;

        var parameters = new JsonObject
        {
            ["progressToken"] = McpJsonNode.Clone(progressToken),
            ["progress"] = progress,
        };
        if (total.HasValue)
            parameters["total"] = total.Value;
        if (!string.IsNullOrWhiteSpace(message))
            parameters["message"] = message;

        var notification = new JsonObject
        {
            ["jsonrpc"] = "2.0",
            ["method"] = "notifications/progress",
            ["params"] = parameters,
        };
        await writer(notification.ToJsonString(_jsonOptions), _currentRequestToken.Value).ConfigureAwait(false);
    }

    private async Task EmitLogNotificationAsync(string level, string message)
    {
        if (_currentOutOfBandFrameWriter.Value is not { } writer)
            return;

        var notification = new JsonObject
        {
            ["jsonrpc"] = "2.0",
            ["method"] = "notifications/message",
            ["params"] = new JsonObject
            {
                ["level"] = level,
                ["logger"] = "cdidx",
                ["data"] = message,
            },
        };
        await writer(notification.ToJsonString(_jsonOptions), _currentRequestToken.Value).ConfigureAwait(false);
    }

    /// <summary>
    /// Emit a single audit record for the just-executed tool call. Inspects the wire
    /// response to derive the result count and error code, and uses invocation-scoped
    /// authorization state when available, so the audit trail preserves checks performed
    /// before later error paths build a response (#1562, #4606). Failures are swallowed
    /// because audit emission must never break the underlying tool call.
    /// 直前に実行したツール呼び出しを 1 レコード分監査出力する。クライアントが実際に観測する
    /// 値と一致させるため wire response から result count / error code を抽出し、後続の error
    /// path が response を生成する前の検証も残すため invocation-scoped authorization state を使う
    /// (#1562, #4606)。
    /// audit 失敗で本体ツール呼び出しを壊さないようベストエフォート化する。
    /// </summary>
    private void TryEmitAudit(bool hasId, string toolName, JsonNode? id, JsonNode? args, JsonNode response, DateTimeOffset startedAt, double elapsedMs, string? errorType)
    {
        if (_auditLog is null)
            return;

        try
        {
            var initializeState = CurrentInitializeState;
            var (errorCode, observedErrorType) = ExtractErrorCode(response);
            var resultCount = ExtractResultCount(response);
            var (argKeys, argLengths, argKeyLengths, argValuesEcho) =
                SanitizeArgs(args, _auditLog.IncludeValues,
                    out var argValuesRedacted,
                    out var argValuesTruncated,
                    out var argValueTruncationReasons,
                    out var argValuesSerializedBytes,
                    out var argKeysTruncated,
                    out var argKeyTruncationReasons,
                    out var argKeysOmittedCount,
                    out var argKeyNamesTruncatedCount);
            var toolDisplay = BoundToolNameForDisplay(toolName);
            McpRequestIdTelemetryData? requestId = hasId
                ? McpRequestIdTelemetry.Create(id)
                : null;
            var evt = new AuditLogSink.AuditEvent(
                Timestamp: startedAt,
                Tool: toolDisplay.Text,
                CallerName: initializeState.ClientName,
                CallerVersion: initializeState.ClientVersion,
                RequestId: requestId?.Token,
                ArgKeys: argKeys,
                ArgLengths: argLengths,
                ArgValues: argValuesEcho,
                ResultCount: resultCount,
                ElapsedMs: elapsedMs,
                ErrorCode: errorCode,
                ErrorType: errorType ?? observedErrorType,
                CheckedRootIdentity: _currentIndexAuditContext.Value?.CheckedRootIdentity ?? ExtractCheckedRootIdentity(response),
                ToolLength: toolDisplay.Truncated ? toolDisplay.OriginalLength : null,
                ToolTruncated: toolDisplay.Truncated,
                ArgKeyLengths: argKeyLengths,
                ArgKeysTruncated: argKeysTruncated,
                ArgKeyTruncationReasons: argKeyTruncationReasons,
                ArgKeysOmittedCount: argKeysOmittedCount,
                ArgKeyNamesTruncatedCount: argKeyNamesTruncatedCount,
                ArgValuesRedacted: argValuesRedacted,
                ArgValuesTruncated: argValuesTruncated,
                ArgValueTruncationReasons: argValueTruncationReasons,
                ArgValuesSerializedBytes: argValuesSerializedBytes,
                RequestIdType: requestId?.Type,
                RequestIdLength: requestId?.Length,
                CallerNameLength: initializeState.ClientNameDisplay?.Truncated == true ? initializeState.ClientNameDisplay.Value.OriginalLength : null,
                CallerNameTruncated: initializeState.ClientNameDisplay?.Truncated == true,
                CallerVersionLength: initializeState.ClientVersionDisplay?.Truncated == true ? initializeState.ClientVersionDisplay.Value.OriginalLength : null,
                CallerVersionTruncated: initializeState.ClientVersionDisplay?.Truncated == true);
            _auditLog.Record(evt);
        }
        catch
        {
            // Best-effort: an audit failure must not break the tool call.
            // ベストエフォート: audit 失敗で本体ツール呼び出しを壊さない。
        }
    }

    private static string? ExtractCheckedRootIdentity(JsonNode response)
    {
        var node = response["result"]?["structuredContent"]?["checked_root_identity"]
            ?? response["error"]?["data"]?["checked_root_identity"];
        return node is JsonValue value && value.TryGetValue<string>(out var identity)
            ? identity
            : null;
    }

    /// <summary>
    /// Translate the wire response into `(error_code, error_type)` for the audit record.
    /// 0 means success, positive means a tool-level error (isError=true), and negative is
    /// the verbatim JSON-RPC error code (e.g. -32602 invalid params).
    /// レスポンスを audit 用の `(error_code, error_type)` に変換する。0=成功、正値=
    /// tool エラー (isError=true)、負値=JSON-RPC エラーコード（例: -32602）。
    /// </summary>
    internal static (int Code, string? Type) ExtractErrorCode(JsonNode response)
    {
        if (response is not JsonObject obj)
            return (0, null);
        if (obj.TryGetPropertyValue("error", out var errorNode) && errorNode is JsonObject errorObj)
        {
            var code = -32603;
            if (errorObj.TryGetPropertyValue("code", out var codeNode) && codeNode is JsonValue codeValue
                && codeValue.TryGetValue<int>(out var parsed))
                code = parsed;
            return (code, "jsonrpc_error");
        }
        if (obj.TryGetPropertyValue("result", out var resultNode) && resultNode is JsonObject resultObj)
        {
            if (resultObj.TryGetPropertyValue("isError", out var isErrorNode)
                && isErrorNode is JsonValue isErrorValue
                && isErrorValue.TryGetValue<bool>(out var isError)
                && isError)
                return (1, "tool_error");
        }
        return (0, null);
    }

    /// <summary>
    /// Extract the result count from a successful tool response. Prefers
    /// `structuredContent.count`, falls back to the length of `structuredContent.results`,
    /// and returns null when neither shape is present (e.g. ping). Tool errors and JSON-RPC
    /// errors return null because there is no meaningful result-set count for those cases.
    /// 成功レスポンスから result count を抽出する。`structuredContent.count` を優先、
    /// `structuredContent.results` の長さに fallback。どちらも無い場合（例: ping）と
    /// tool/JSON-RPC エラー時は null を返す。
    /// </summary>
    internal static int? ExtractResultCount(JsonNode response)
    {
        if (response is not JsonObject obj)
            return null;
        if (obj["result"] is not JsonObject result)
            return null;
        if (result["isError"] is JsonValue isErrorValue
            && isErrorValue.TryGetValue<bool>(out var isError) && isError)
            return null;
        if (result["structuredContent"] is not JsonObject structured)
            return null;
        if (structured["count"] is JsonValue countValue && countValue.TryGetValue<int>(out var count))
            return count;
        if (structured["results"] is JsonArray results)
            return results.Count;
        return null;
    }

    /// <summary>
    /// Build the `(arg_keys, arg_lengths, arg_key_lengths, arg_values?)` audit triple. Values are echoed
    /// only when the operator has opted in via `--audit-log-include-values`; otherwise we
    /// keep keys + per-key length so AI argument shapes can be reconstructed without
    /// persisting query bodies that may contain sensitive substrings (#1562).
    /// audit 用の `(arg_keys, arg_lengths, arg_values?)` を組み立てる。値は
    /// `--audit-log-include-values` がオンの場合のみ転写し、それ以外はキーと長さだけ残す
    /// （secret 風の検索クエリを取り込まないため）。
    /// </summary>
    internal static (IReadOnlyList<string> Keys, IReadOnlyList<KeyValuePair<string, int>> Lengths, IReadOnlyList<KeyValuePair<string, int>> KeyLengths, JsonNode? ValuesEcho)
        SanitizeArgs(JsonNode? args, bool includeValues)
        => SanitizeArgs(args, includeValues, out _, out _, out _, out _, out _, out _, out _, out _);

    private static (IReadOnlyList<string> Keys, IReadOnlyList<KeyValuePair<string, int>> Lengths, IReadOnlyList<KeyValuePair<string, int>> KeyLengths, JsonNode? ValuesEcho)
        SanitizeArgs(
            JsonNode? args,
            bool includeValues,
            out bool argValuesRedacted,
            out bool argValuesTruncated,
            out IReadOnlyList<string> argValueTruncationReasons,
            out int? argValuesSerializedBytes,
            out bool argKeysTruncated,
            out IReadOnlyList<string> argKeyTruncationReasons,
            out int argKeysOmittedCount,
            out int argKeyNamesTruncatedCount)
    {
        argValuesRedacted = false;
        argValuesTruncated = false;
        argValueTruncationReasons = Array.Empty<string>();
        argValuesSerializedBytes = null;
        argKeysTruncated = false;
        argKeysOmittedCount = 0;
        argKeyNamesTruncatedCount = 0;
        var argKeyReasons = new List<string>();
        argKeyTruncationReasons = argKeyReasons;
        if (args is not JsonObject argsObj)
            return (Array.Empty<string>(), Array.Empty<KeyValuePair<string, int>>(), Array.Empty<KeyValuePair<string, int>>(), null);

        var keys = new List<string>(argsObj.Count);
        var lengths = new List<KeyValuePair<string, int>>(argsObj.Count);
        var keyLengths = new List<KeyValuePair<string, int>>();
        var usedKeys = new HashSet<string>(StringComparer.Ordinal);
        JsonObject? echoObject = includeValues ? new JsonObject() : null;
        AuditLogSink.ArgValueSanitizationState? valueState = includeValues ? new AuditLogSink.ArgValueSanitizationState() : null;
        var argValueBudgetExhausted = false;
        var argumentCount = 0;
        foreach (var (key, value) in argsObj)
        {
            if (argumentCount >= AuditLogSink.MaxAuditArgumentCount)
            {
                argKeysTruncated = true;
                argKeysOmittedCount = argsObj.Count - argumentCount;
                AddUniqueReason(argKeyReasons, "arg_key_count_limit");
                break;
            }

            var keyDisplay = McpBoundedText.ForDisplay(key, AuditLogSink.MaxAuditArgumentKeyChars);
            var displayKey = MakeUniqueArgumentDisplayKey(key, keyDisplay, usedKeys);
            keys.Add(displayKey);
            lengths.Add(new KeyValuePair<string, int>(displayKey, AuditLogSink.MeasureArgLength(value)));
            if (keyDisplay.Truncated)
            {
                keyLengths.Add(new KeyValuePair<string, int>(displayKey, keyDisplay.OriginalLength));
                argKeysTruncated = true;
                argKeyNamesTruncatedCount++;
                AddUniqueReason(argKeyReasons, "arg_key_length_limit");
            }
            if (echoObject is not null && !argValueBudgetExhausted)
            {
                try
                {
                    if (!valueState!.TryReservePropertyName(displayKey))
                    {
                        argValueBudgetExhausted = true;
                    }
                    else
                    {
                        echoObject[displayKey] = AuditLogSink.SanitizeArgValue(key, value, valueState);
                        argValuesRedacted = valueState.Redacted;
                    }
                }
                catch
                {
                    echoObject = null;
                }
            }
            argumentCount++;
        }
        if (valueState is not null)
        {
            argValuesRedacted = valueState.Redacted;
            argValuesTruncated = valueState.Truncated;
            argValueTruncationReasons = valueState.TruncationReasons;
            argValuesSerializedBytes = valueState.SerializedBytes;
        }

        return (keys, lengths, keyLengths, includeValues ? echoObject : null);
    }

    private static void AddUniqueReason(List<string> reasons, string reason)
    {
        foreach (var existing in reasons)
        {
            if (StringComparer.Ordinal.Equals(existing, reason))
                return;
        }
        reasons.Add(reason);
    }

    private static string MakeUniqueArgumentDisplayKey(string rawKey, BoundedMcpText display, ISet<string> usedKeys)
    {
        if (usedKeys.Add(display.Text))
            return display.Text;

        var hashSuffix = "#" + ShortStableHash(rawKey);
        var candidate = ComposeDisplayKeyWithSuffix(rawKey, hashSuffix);
        var disambiguator = 2;
        while (!usedKeys.Add(candidate))
        {
            candidate = ComposeDisplayKeyWithSuffix(
                rawKey,
                $"{hashSuffix}-{disambiguator.ToString(CultureInfo.InvariantCulture)}");
            disambiguator++;
        }

        return candidate;
    }

    private static string ComposeDisplayKeyWithSuffix(string rawKey, string suffix)
    {
        const int maxDisplayTextChars = McpBoundedText.MaxDiagnosticDisplayChars + 3;
        var maxPrefixChars = Math.Max(0, maxDisplayTextChars - suffix.Length - 3);
        return McpBoundedText.ForDisplay(rawKey, maxPrefixChars).Text + suffix;
    }

    private static string ShortStableHash(string value)
    {
        var bytes = SHA256.HashData(Encoding.UTF8.GetBytes(value));
        return HexEncoding.ToLowerHexString(bytes, 0, 4);
    }

    private static void AddArgKeyMetadata(
        JsonObject target,
        IReadOnlyList<KeyValuePair<string, int>> argKeyLengths,
        int argKeysOmittedCount,
        int argKeyNamesTruncatedCount)
    {
        if (argKeyLengths.Count > 0)
        {
            var lengths = new JsonObject();
            foreach (var pair in argKeyLengths)
                lengths[pair.Key] = pair.Value;
            target["arg_key_lengths"] = lengths;
            target["arg_keys_truncated"] = true;
        }
        if (argKeysOmittedCount > 0)
            target["arg_keys_omitted_count"] = argKeysOmittedCount;
        if (argKeyNamesTruncatedCount > 0)
            target["arg_key_names_truncated_count"] = argKeyNamesTruncatedCount;
    }

    private static string? SerializeRequestId(JsonNode? id)
    {
        return TrySerializeRequestId(id, out var serialized, out _) ? serialized : null;
    }

    private static string? TryReadStringArg(JsonNode? args, string key)
    {
        if (args is null)
            return null;

        try
        {
            var node = args[key];
            if (node is null)
                return null;
            if (node is JsonValue value && value.TryGetValue<string>(out var stringValue))
                return string.IsNullOrWhiteSpace(stringValue) ? null : stringValue;
        }
        catch
        {
            // Best-effort: any oddity in argument shape just suppresses the language hint.
            // ベストエフォート: 引数形状が不正でも language ヒントを抑止するだけ。
        }
        return null;
    }

    private static string? TryReadMetricStringArg(JsonNode? args, string key)
    {
        var value = TryReadStringArg(args, key);
        return value is null ? null : McpBoundedText.ForDisplay(value).Text;
    }

    internal static string BuildOversizedMessageLog(int characterCount, int byteCount) =>
        $"[cdidx-mcp] Message too large ({characterCount} chars / {byteCount} bytes), rejecting. Split the request into smaller JSON-RPC messages or shorter arguments, then retry.";

    internal static string BuildJsonParseErrorLog(string detail) =>
        $"[cdidx-mcp] JSON parse error: {DiagnosticRedactor.BoundDiagnosticText(detail, JsonFrameParser.MaxParseDiagnosticChars)}. MCP stdio expects one UTF-8 JSON-RPC object per LF-delimited line; do not send LSP Content-Length framing.";

    internal static string BuildUnhandledLoopErrorLog(string detail) =>
        $"[cdidx-mcp] Error: {detail}. This request was skipped; fix the request or inspect the server environment, then retry.";

    internal static string BuildResponseSerializationErrorLog(string detail) =>
        $"[cdidx-mcp] Error serializing response: {detail}. Returning a minimal JSON-RPC error response when possible.";

    internal static string BuildResponseWriteErrorLog(string detail) =>
        $"[cdidx-mcp] Error writing response: {detail}. The request was handled but the client connection may already be closed.";

    internal static string BuildToolErrorLog(string toolName, Exception ex) =>
        $"[cdidx-mcp] Tool error ({BoundToolNameForDisplay(toolName).Text}): {BuildSanitizedExceptionLogDetail(ex)}. Fix the tool arguments, refresh the index if needed, then retry.";

    internal static string BuildSanitizedExceptionLogDetail(Exception ex)
    {
        var exceptionType = McpBoundedText.ForDisplay(ex.GetType().Name).Text;
        if (ex is CodeIndexException codeIndexEx)
        {
            var code = McpBoundedText.ForDisplay(codeIndexEx.Code).Text;
            var category = McpBoundedText.ForDisplay(codeIndexEx.Category).Text;
            return $"{exceptionType} code={code} category={category}{BuildPathFragment(codeIndexEx)}{BuildHintFragment(codeIndexEx)}";
        }

        return exceptionType;
    }

    internal static string BuildClientResponseTooLargeLog(string member, int bytesWritten) =>
        $"[cdidx-mcp] Client response {member} exceeded the server byte limit ({bytesWritten} > {MaxClientResponseJsonBytes}); rejecting without retaining the payload.";

    private static string BuildClientResponseTooLargeMessage(int bytesWritten) =>
        $"MCP client response exceeded the server byte limit ({bytesWritten} > {MaxClientResponseJsonBytes}).";

    // Stderr log emitted when the rate limiter denies a tool call. Mirrors the JSON-RPC
    // `-32000` payload (tool + caller + retry_after_ms) so operators tailing the MCP log
    // can correlate spikes with the structured error returned on the wire (#1560).
    // レート制限で拒否されたツール呼び出しを stderr に記録する。配線上の JSON-RPC `-32000`
    // ペイロードと内容を揃え、運用側がログ追跡から状況把握できるようにする（#1560）。
    internal static string BuildRateLimitedLog(string toolName, string caller, long retryAfterMs) =>
        $"[cdidx-mcp] Rate limit exceeded: tool='{BoundToolNameForDisplay(toolName).Text}', caller='{BoundClientIdentityForDisplay(caller).Text}', retry_after_ms={retryAfterMs}. Increase {RateLimiterOptions.RpsEnvVar} / {RateLimiterOptions.BurstEnvVar} on the server, or back off and retry.";

    internal static string BuildUnknownNotificationLog(string method) =>
        $"[cdidx-mcp] Ignoring unknown notification: {method}";

    internal static bool IsSupportedMcpLogLevel(string? level)
        => level is "debug" or "info" or "notice" or "warning" or "error" or "critical" or "alert" or "emergency";

    internal static bool IsUnsafeDebugEnabled()
        => McpEnvironment.IsUnsafeDebugEnabled(DebugEnvironmentVariable);

    internal static string FormatDbPathForLog(string dbPath)
    {
        if (IsUnsafeDebugEnabled())
            return dbPath;

        try
        {
            var path = dbPath;
            if (Uri.TryCreate(dbPath, UriKind.Absolute, out var uri) && uri.IsFile)
                path = uri.LocalPath;
            var fileName = Path.GetFileName(path);
            return string.IsNullOrWhiteSpace(fileName) ? "(configured db)" : fileName;
        }
        catch
        {
            return "(configured db)";
        }
    }

    // Wire-safe error body for the tool catch-all. Mentions the tool and the
    // exception type so the client can branch (retry vs. surface to user)
    // while keeping bound values or matched content out of the response (#1530).
    // For CodeIndexException (#1580) the Code / Category / Path / Hint fields
    // are author-controlled and therefore safe to echo verbatim, so the client
    // gets the structured failure metadata it needs without re-introducing the
    // ex.Message leak vector #1530 closed.
    // ツール catch-all のワイヤー向け本文。クライアントが分岐できるよう tool 名と
    // 例外型は残し、バインド値や一致内容は含めない（#1530）。CodeIndexException (#1580)
    // の Code / Category / Path / Hint は実装側で固定したフィールドなのでそのまま転写し、
    // #1530 で封じた ex.Message 漏れを再現させずに失敗詳細をクライアントへ届ける。
    internal static string BuildSanitizedToolErrorMessage(string toolName, Exception ex)
    {
        var toolDisplay = BoundToolNameForDisplay(toolName).Text;
        if (!IsUnsafeDebugEnabled())
            return $"Tool '{toolDisplay}' failed. See cdidx server stderr for details.";
        if (ex is CodeIndexException codeIndexEx)
            return $"Error executing {toolDisplay} ({ex.GetType().Name}) [{codeIndexEx.Code}/{codeIndexEx.Category}]{BuildPathFragment(codeIndexEx)}{BuildHintFragment(codeIndexEx)}. See cdidx server stderr for details.";
        return $"Error executing {toolDisplay} ({ex.GetType().Name}). See cdidx server stderr for details.";
    }

    // Wire-safe error body for the JSON-RPC loop catch-all. Same rationale as
    // the tool catch-all (#1530, #1580).
    // JSON-RPC ループ catch-all のワイヤー向け本文。理由はツール catch-all と同じ（#1530, #1580）。
    internal static string BuildSanitizedLoopErrorMessage(Exception ex)
    {
        if (!IsUnsafeDebugEnabled())
            return "Internal MCP error. See cdidx server stderr for details.";
        if (ex is CodeIndexException codeIndexEx)
            return $"Internal error ({ex.GetType().Name}) [{codeIndexEx.Code}/{codeIndexEx.Category}]{BuildPathFragment(codeIndexEx)}{BuildHintFragment(codeIndexEx)}. See cdidx server stderr for details.";
        return $"Internal error ({ex.GetType().Name}). See cdidx server stderr for details.";
    }

    // Quote so paths/hints with spaces stay one token. Single quotes are kept
    // for human readability — this is a display contract, not a shell-parsing one.
    // 空白を含む path / hint が 2 トークンに見えないよう単引用符でラップする。
    private static string BuildPathFragment(CodeIndexException ex) =>
        string.IsNullOrEmpty(ex.Path) ? string.Empty : $" path='{ex.Path}'";

    private static string BuildHintFragment(CodeIndexException ex) =>
        string.IsNullOrEmpty(ex.Hint) ? string.Empty : $" hint='{ex.Hint}'";

}

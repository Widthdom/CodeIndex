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


    // --- JSON-RPC helpers / JSON-RPCヘルパー ---

    private enum RequestIdValidationError
    {
        None,
        InvalidType,
        TooLong,
    }

    private static bool TryGetRequestId(JsonObject request, out bool hasId, out JsonNode? id)
        => TryGetRequestId(request, out hasId, out id, out _);

    private static bool TryGetRequestId(JsonObject request, out bool hasId, out JsonNode? id, out RequestIdValidationError error)
    {
        error = RequestIdValidationError.None;
        hasId = request.TryGetPropertyValue("id", out id);
        if (!hasId)
            return true;

        if (id is null)
            return true;

        return TrySerializeRequestId(id, out _, out error);
    }

    private static bool TrySerializeRequestId(JsonNode? id, out string? serialized, out RequestIdValidationError error)
    {
        serialized = null;
        error = RequestIdValidationError.None;
        if (id is null)
            return true;

        if (id is not JsonValue value)
        {
            error = RequestIdValidationError.InvalidType;
            return false;
        }

        return TrySerializeRequestIdValue(value, out serialized, out error);
    }

    private static bool TrySerializeRequestIdValue(JsonValue value, out string? serialized, out RequestIdValidationError error)
    {
        serialized = null;
        error = RequestIdValidationError.None;
        JsonValueKind kind;
        try
        {
            kind = value.GetValueKind();
        }
        catch
        {
            error = RequestIdValidationError.InvalidType;
            return false;
        }

        switch (kind)
        {
            case JsonValueKind.String:
                try
                {
                    var requestId = value.GetValue<string>();
                    if (!IsRequestIdWithinBounds(requestId))
                    {
                        error = RequestIdValidationError.TooLong;
                        return false;
                    }

                    serialized = JsonSerializer.Serialize(requestId);
                    return true;
                }
                catch
                {
                    error = RequestIdValidationError.InvalidType;
                    return false;
                }

            case JsonValueKind.Number:
                try
                {
                    serialized = value.TryGetValue<JsonElement>(out var element) && element.ValueKind == JsonValueKind.Number
                        ? element.GetRawText()
                        : value.ToJsonString();
                }
                catch
                {
                    error = RequestIdValidationError.InvalidType;
                    return false;
                }

                if (serialized.Length == 0 || !(serialized[0] == '-' || char.IsDigit(serialized[0])))
                {
                    error = RequestIdValidationError.InvalidType;
                    serialized = null;
                    return false;
                }

                if (!IsRequestIdWithinBounds(serialized))
                {
                    error = RequestIdValidationError.TooLong;
                    serialized = null;
                    return false;
                }

                return true;

            case JsonValueKind.Null:
                return true;

            default:
                error = RequestIdValidationError.InvalidType;
                return false;
        }
    }

    private static bool IsRequestIdWithinBounds(string value)
        => value.Length <= MaxRequestIdCharacterCount
            && Encoding.UTF8.GetByteCount(value) <= MaxRequestIdByteLength;

    private static string BuildInvalidRequestIdMessage(RequestIdValidationError error)
        => error == RequestIdValidationError.TooLong
            ? "Invalid request: id exceeds the request-id length limit"
            : "Invalid request: id must be string, number, or null";

    private static string BuildInvalidRequestIdSuggestion(RequestIdValidationError error)
        => error == RequestIdValidationError.TooLong
            ? $"JSON-RPC 2.0 `id` must be no more than {MaxRequestIdCharacterCount} characters and {MaxRequestIdByteLength} UTF-8 bytes. Use a compact string or number id."
            : "JSON-RPC 2.0 `id` must be a string, integer, or null. Booleans/objects/arrays are not allowed.";

    private static JsonObject? BuildInvalidRequestIdData(RequestIdValidationError error)
        => error == RequestIdValidationError.TooLong
            ? new JsonObject
            {
                ["max_request_id_chars"] = MaxRequestIdCharacterCount,
                ["max_request_id_bytes"] = MaxRequestIdByteLength,
            }
            : null;

    private static JsonObject CreateSuccessResponse(JsonNode? id, JsonNode result)
        => CreateSuccessResponse(id is not null, id, result);

    private static JsonObject CreateSuccessResponse(bool hasId, JsonNode? id, JsonNode result)
    {
        AddResponseMeta(result);
        var response = new JsonObject
        {
            ["jsonrpc"] = "2.0",
            ["result"] = result
        };
        if (hasId)
            response["id"] = McpJsonNode.Clone(id);
        return response;
    }

    private static void AddResponseMeta(JsonNode result)
    {
        var context = CurrentCorrelationContext.Value;
        if (context is null || result is not JsonObject obj)
            return;

        var meta = obj["_meta"] as JsonObject ?? new JsonObject();
        meta["correlation_id"] = context.CorrelationId;
        if (context.WireRequestId != null)
            meta["request_id"] = context.WireRequestId;
        obj["_meta"] = meta;
    }

    private static JsonObject? AddCorrelationData(JsonObject? extraData)
    {
        var context = CurrentCorrelationContext.Value;
        if (context is null)
            return extraData;

        var data = extraData is null ? new JsonObject() : (JsonObject)extraData.DeepClone();
        data["correlation_id"] = context.CorrelationId;
        if (context.WireRequestId != null)
            data["request_id"] = context.WireRequestId;
        return data;
    }

    private static JsonObject CreateErrorResponse(JsonNode? id, int code, string message,
        string category, string suggestion, bool retrySafe, JsonObject? extraData = null)
        => CreateErrorResponse(id is not null, id, code, message, category, suggestion, retrySafe, extraData);

    private static BoundedMcpText BoundToolNameForDisplay(string toolName)
        => McpBoundedText.ForDisplay(toolName, McpBoundedText.MaxToolNameChars);

    private static void AddToolDisplayData(JsonObject target, string? toolName)
    {
        if (toolName is null)
        {
            target["tool"] = null;
            return;
        }

        var display = BoundToolNameForDisplay(toolName);
        target["tool"] = display.Text;
        display.AddMetadata(target, "tool");
    }

    internal static string BuildUnknownToolMessage(string toolName)
        => $"Unknown tool: {BoundToolNameForDisplay(toolName).Text}";

    private static JsonObject BuildUnknownToolData(string toolName)
    {
        var data = new JsonObject();
        AddToolDisplayData(data, toolName);
        return data;
    }

    private static JsonObject BuildToolExceptionData(string toolName, string exceptionType)
    {
        var data = new JsonObject
        {
            ["exception_type"] = exceptionType,
        };
        AddToolDisplayData(data, toolName);
        return data;
    }

    private static JsonObject CreateUnknownToolErrorResponse(bool hasId, JsonNode? id, string toolName)
        => CreateErrorResponse(hasId: hasId, id: id, code: -32602, message: BuildUnknownToolMessage(toolName),
            category: McpErrorEnvelope.CategoryToolUnknown,
            suggestion: "Call tools/list to enumerate the available tool names for this server. Tool name match is case-sensitive.",
            retrySafe: false,
            extraData: BuildUnknownToolData(toolName));

    // Issue #1581: every MCP error response carries a structured `data` envelope
    // (`category` / `suggestion` / `retry_safe`) so clients can branch on a stable
    // category instead of parsing the human-readable `message`. Category-specific
    // extras (e.g. rate-limited's `retry_after_ms`) merge in via `extraData`.
    // #1581: すべての MCP エラー応答に `category` / `suggestion` / `retry_safe` を含む
    // 構造化 `data` を載せ、クライアントが文字列解析せず分岐できるようにする。カテゴリ
    // 固有フィールド（rate-limited の `retry_after_ms` 等）は `extraData` で合流する。
    private static JsonObject CreateErrorResponse(bool hasId, JsonNode? id, int code, string message,
        string category, string suggestion, bool retrySafe, JsonObject? extraData = null)
    {
        var response = new JsonObject
        {
            ["jsonrpc"] = "2.0",
            ["error"] = new JsonObject
            {
                ["code"] = code,
                ["message"] = message,
                ["data"] = McpErrorEnvelope.BuildData(category, suggestion, retrySafe, AddCorrelationData(extraData)),
            }
        };
        if (hasId)
            response["id"] = McpJsonNode.Clone(id);
        return response;
    }

    private static JsonObject CreateCancelledResponse(JsonNode? id)
        => CreateErrorResponse(hasId: true, id: id, code: McpErrorEnvelope.CodeRequestCancelled,
            message: "Request cancelled",
            category: McpErrorEnvelope.CategoryRequestCancelled,
            suggestion: "The client cancelled this request before completion. Reissue the call if the work is still needed.",
            retrySafe: true);

    /// <summary>
    /// Create a tool result response (MCP format).
    /// ツール結果レスポンスを作成（MCP形式）。
    /// </summary>
    private JsonObject CreateToolResult(
        JsonNode? id,
        string text,
        JsonNode? structuredContent = null,
        string? mimeType = null,
        bool enrichStructuredContent = true)
    {
        mimeType ??= structuredContent is null ? "text/plain" : "application/json";
        var result = new JsonObject
        {
            ["content"] = new JsonArray
            {
                new JsonObject
                {
                    ["type"] = "text",
                    ["mimeType"] = mimeType,
                    ["text"] = text
                }
            }
        };
        if (structuredContent is JsonObject structuredObject)
        {
            if (enrichStructuredContent)
                EnrichToolStructuredContent(structuredObject);
            result["structuredContent"] = structuredContent;
        }
        else if (structuredContent != null)
        {
            ClearProjectFilterRootDiagnostics();
            result["structuredContent"] = structuredContent;
        }
        else
        {
            ClearProjectFilterRootDiagnostics();
        }
        var response = CreateSuccessResponse(true, id, result);
        var responseLimit = GetMaxResponseBytes();
        if (TryMeasureJsonUtf8BytesWithinLimit(response, _jsonOptions, responseLimit, out var responseBytes))
            return response;

        return CreateResponseTooLargeError(true, id, responseBytes, responseLimit, actualBytesExact: false);
    }

    private void EnrichToolStructuredContent(JsonObject structuredContent)
    {
        structuredContent.TryAdd("api_version", JsonOutputContract.ApiVersion);
        AddProjectFilterRootDiagnostics(structuredContent);
        AddConfiguredSqliteDiagnostics(structuredContent);
    }

    internal bool TrySerializeJsonNodeWithinByteLimitForTests(JsonNode node, int maxBytes, out string? serialized, out int bytesWritten)
        => TrySerializeJsonNodeWithinByteLimit(node, _jsonOptions, maxBytes, captureSerialized: true, out serialized, out bytesWritten);

    private static bool TryMeasureJsonUtf8BytesWithinLimit(JsonNode node, JsonSerializerOptions options, int maxBytes, out int bytesWritten)
        => TrySerializeJsonNodeWithinByteLimit(node, options, maxBytes, captureSerialized: false, out _, out bytesWritten);

    private static bool TrySerializeJsonNodeWithinByteLimit(JsonNode node, JsonSerializerOptions options, int maxBytes, bool captureSerialized, out string? serialized, out int bytesWritten)
    {
        if (maxBytes < 0)
            throw new ArgumentOutOfRangeException(nameof(maxBytes), maxBytes, "JSON byte limit must be non-negative.");

        serialized = null;
        using var stream = new BoundedJsonUtf8Stream(
            maxBytes,
            captureSerialized,
            bytes => new JsonResponseByteLimitExceededException(bytes));
        var writerOptions = new JsonWriterOptions
        {
            Encoder = options.Encoder,
            Indented = options.WriteIndented,
        };

        try
        {
            using var writer = new Utf8JsonWriter(stream, writerOptions);
            node.WriteTo(writer, options);
            writer.Flush();
            bytesWritten = stream.BytesWritten;
            serialized = stream.GetCapturedString();
            return true;
        }
        catch (JsonResponseByteLimitExceededException ex)
        {
            bytesWritten = ex.BytesWritten;
            return false;
        }
    }

    private sealed class JsonResponseByteLimitExceededException(int bytesWritten) : Exception
    {
        public int BytesWritten { get; } = bytesWritten;
    }

    private JsonObject CreateResponseTooLargeError(bool hasId, JsonNode? id, int responseBytes, int responseLimit, bool actualBytesExact = true)
    {
        var response = CreateErrorResponse(
            hasId: hasId,
            id: id,
            code: -32603,
            message: $"MCP response exceeded the server byte limit ({responseBytes} > {responseLimit}). Narrow the query or lower the result limit.",
            category: McpErrorEnvelope.CategoryInvalidArgument,
            suggestion: "Narrow the query, add path/language filters, lower limit, or use countOnly for a summary-first probe.",
            retrySafe: false,
            extraData: new JsonObject
            {
                ["reason"] = "response_too_large",
                ["limit_bytes"] = responseLimit,
                ["actual_bytes"] = responseBytes,
                ["actual_bytes_exact"] = actualBytesExact,
            });
        AddConfiguredSqliteDiagnostics((JsonObject)response["error"]!["data"]!);
        return response;
    }

    private static int GetMaxResponseBytes()
        => ReadPositiveIntEnvironmentLimit(
            MaxResponseBytesEnvVar,
            DefaultMaxResponseBytes,
            MaxConfiguredResponseBytes,
            "MCP response byte limit");

    private static int ReadPositiveIntEnvironmentLimit(string envVar, int defaultValue, int maximumValue, string description)
    {
        var raw = global::CodeIndex.EnvironmentAccess.GetProcessEnvironmentVariable(envVar);
        if (string.IsNullOrWhiteSpace(raw))
            return defaultValue;

        if (!int.TryParse(raw, System.Globalization.NumberStyles.None, System.Globalization.CultureInfo.InvariantCulture, out var limit)
            || limit <= 0)
        {
            var displayValue = DiagnosticRedactor.FormatEnvironmentValue(envVar, raw);
            CommandErrorWriter.WriteStderr($"[cdidx-mcp] Ignoring invalid {envVar}='{displayValue}'. Expected a positive integer for {description}. Using default {defaultValue.ToString(System.Globalization.CultureInfo.InvariantCulture)}.");
            return defaultValue;
        }

        if (limit > maximumValue)
        {
            var displayValue = DiagnosticRedactor.FormatEnvironmentValue(envVar, raw);
            CommandErrorWriter.WriteStderr($"[cdidx-mcp] Clamping {envVar}='{displayValue}' to maximum {maximumValue.ToString(System.Globalization.CultureInfo.InvariantCulture)} for {description}.");
            return maximumValue;
        }

        return limit;
    }

    /// <summary>
    /// Create a tool error response (MCP format with isError flag).
    /// Optional <paramref name="similarValues"/> attach a structured
    /// <c>data.similar_values</c> array to the result so MCP clients can offer
    /// recovery alternatives without parsing the human-readable message (#1582).
    /// ツールエラーレスポンスを作成（isError フラグ付き MCP 形式）。
    /// <paramref name="similarValues"/> を渡すと結果に構造化された
    /// <c>data.similar_values</c> 配列を添えるので、MCP クライアントは
    /// 人間向けメッセージを解析せずに代替候補を提示できる (#1582)。
    /// </summary>
    private JsonObject CreateToolErrorResponse(JsonNode? id, string message,
        string category, string suggestion, bool retrySafe, JsonObject? extraData = null,
        IReadOnlyList<string>? similarValues = null)
        => CreateToolErrorResponse(id is not null, id, message, category, suggestion, retrySafe, extraData, similarValues);

    // Backward-compatible overload for tool handlers that return argument-validation
    // failures (#1581). These were all "missing parameter / invalid argument" call sites
    // before the envelope was introduced, so the default classification is `invalid_argument`
    // / retry_safe=false. The optional `similarValues` carries the structured did-you-mean
    // candidates for unknown enum values (#1582). Sites that have richer context should
    // call the explicit overload.
    // 引数バリデーション失敗を返す既存ツールハンドラ向けの互換オーバーロード（#1581）。
    // envelope 導入前の呼び出しは全て「引数不正」系だったため既定カテゴリを `invalid_argument`
    // / retry_safe=false とする。任意の `similarValues` は未知 enum 値に対する構造化された
    // did-you-mean 候補 (#1582)。より具体的なカテゴリを持てる呼び出し元は明示オーバーロード
    // を使う。
    private JsonObject CreateToolErrorResponse(JsonNode? id, string message,
        IReadOnlyList<string>? similarValues = null)
        => CreateToolErrorResponse(id, message,
            category: McpErrorEnvelope.CategoryInvalidArgument,
            suggestion: "Tool argument validation failed. Inspect the tool's `inputSchema` via tools/list and adjust the call.",
            retrySafe: false,
            similarValues: similarValues);

    // Issue #1581: tool-result errors mirror the JSON-RPC error envelope by including
    // the same `category` / `suggestion` / `retry_safe` triple under `result.structuredContent`.
    // Existing clients that only read `content[0].text` + `isError` keep working; new clients
    // can read `structuredContent` to branch on the category.
    // #1581: ツール結果エラーにも JSON-RPC エラーと同じ `category` / `suggestion` / `retry_safe`
    // を `result.structuredContent` に載せる。既存の `content[0].text` + `isError` だけを読む
    // クライアントは互換のまま、新規クライアントは `structuredContent` でカテゴリ分岐できる。
    private JsonObject CreateToolErrorResponse(bool hasId, JsonNode? id, string message,
        string category, string suggestion, bool retrySafe, JsonObject? extraData = null,
        IReadOnlyList<string>? similarValues = null)
    {
        ClearProjectFilterRootDiagnostics();
        var structuredContent = McpErrorEnvelope.BuildData(category, suggestion, retrySafe, AddCorrelationData(extraData));
        AddConfiguredSqliteDiagnostics(structuredContent);
        var result = new JsonObject
        {
            ["content"] = new JsonArray
            {
                new JsonObject
                {
                    ["type"] = "text",
                    ["text"] = message
                }
            },
            ["isError"] = true,
            ["structuredContent"] = structuredContent,
        };
        if (similarValues != null && similarValues.Count > 0)
        {
            var similarArray = new JsonArray();
            foreach (var value in similarValues)
                similarArray.Add(JsonValue.Create(value));
            result["data"] = new JsonObject
            {
                ["similar_values"] = similarArray,
            };
        }
        return CreateSuccessResponse(hasId, id, result);
    }

    private static JsonObject CreateToolDefinition(string name, string description, JsonObject inputSchema,
        JsonObject? annotations = null)
    {
        var def = new JsonObject
        {
            ["name"] = name,
            ["description"] = AppendLanguageSupportClause(name, description),
            ["inputSchema"] = inputSchema,
            ["examples"] = BuildToolExamples(name),
        };
        if (annotations != null)
            def["annotations"] = annotations;
        return def;
    }

    private static JsonArray BuildToolExamples(string name)
    {
        var args = name switch
        {
            "search" => new JsonObject { ["query"] = "Run", ["lang"] = "csharp", ["limit"] = 5 },
            "definition" => new JsonObject { ["query"] = "App", ["exactName"] = true },
            "references" => new JsonObject { ["query"] = "Run", ["kind"] = "call" },
            "callers" => new JsonObject { ["query"] = "Run", ["rankBy"] = "weighted" },
            "callees" => new JsonObject { ["query"] = "App.Run" },
            "symbols" => new JsonObject { ["query"] = "App", ["kind"] = "class" },
            "files" => new JsonObject { ["query"] = "app.cs", ["lang"] = "csharp" },
            "excerpt" => new JsonObject { ["path"] = "src/app.cs", ["startLine"] = 1, ["endLine"] = 5 },
            "find_in_file" => new JsonObject { ["path"] = "src/app.cs", ["query"] = "Run", ["before"] = 1, ["after"] = 1 },
            "map" => new JsonObject { ["limit"] = 5, ["excludeTests"] = true },
            "analyze_symbol" => new JsonObject { ["query"] = "Run", ["includeBody"] = true },
            "impact_analysis" => new JsonObject { ["query"] = "Run", ["maxHops"] = 2, ["withPaths"] = true },
            "status" => new JsonObject(),
            "outline" => new JsonObject { ["path"] = "src/app.cs" },
            "deps" => new JsonObject { ["path"] = "src/", ["reverse"] = false, ["limit"] = 10 },
            "languages" => new JsonObject(),
            "validate" => new JsonObject { ["kind"] = "line_too_long" },
            "ping" => new JsonObject(),
            "batch_query" => new JsonObject
            {
                ["queries"] = new JsonArray
                {
                    new JsonObject { ["tool"] = "search", ["arguments"] = new JsonObject { ["query"] = "Run", ["limit"] = 3 } },
                    new JsonObject { ["tool"] = "definition", ["arguments"] = new JsonObject { ["query"] = "App", ["limit"] = 3 } },
                },
            },
            "index" => new JsonObject { ["path"] = ".", ["rebuild"] = false },
            "backfill_fold" => new JsonObject { ["dry_run"] = false, ["force"] = false },
            "symbol_hotspots" => new JsonObject { ["lang"] = "csharp", ["limit"] = 10 },
            "unused_symbols" => new JsonObject { ["lang"] = "csharp", ["limit"] = 10 },
            "suggest_improvement" => new JsonObject
            {
                ["category"] = "output_format",
                ["description"] = "The tool response should make truncation easier to detect.",
                ["evidencePaths"] = new JsonArray { "src/CodeIndex/Mcp/McpToolHandlers.cs" },
            },
            _ => new JsonObject(),
        };

        return new JsonArray
        {
            new JsonObject
            {
                ["request"] = new JsonObject
                {
                    ["method"] = "tools/call",
                    ["params"] = new JsonObject
                    {
                        ["name"] = name,
                        ["arguments"] = args,
                    },
                },
                ["response_excerpt"] = "A successful MCP tool result includes content and, when available, structuredContent.",
            },
        };
    }

    private static string AppendLanguageSupportClause(string name, string description)
    {
        var clause = name switch
        {
            "references" or "callers" or "callees" or "deps" or "impact_analysis" or "unused_symbols" or "symbol_hotspots"
                => $"Language support: Supports graph/reference extraction for: {GraphLanguageList()}. Unsupported `lang` values are reported with graph-support metadata when the tool returns graph-support fields; use `search`, `definition`, `excerpt`, or `files` for non-graph languages.",
            "definition" or "symbols" or "outline" or "analyze_symbol"
                => $"Language support: Supports symbol extraction for: {SymbolLanguageList()}. Search-only languages can still be indexed and filtered by file tools but may have no symbol rows.",
            "search"
                => "Language support: Supports indexed file/content filters for every detected language; call `languages` for the full catalog.",
            "find_in_file" or "files" or "map"
                => $"Language support: Supports indexed file/content filters for every detected language listed by `languages`: {DetectedLanguageList()}. Symbol and graph fields are available only for the languages whose capabilities are advertised by `languages`.",
            "excerpt" or "status" or "validate"
                => $"Language support: Language-agnostic over indexed files and diagnostics for every detected language listed by `languages`: {DetectedLanguageList()}. This tool does not interpret a `lang` filter.",
            "languages"
                => "Language support: This is the authoritative language catalog for MCP tools; it lists every detected language plus symbol_extraction, reference_extraction, graph_queries, and capability_gaps fields.",
            "index"
                => $"Language support: Indexes every detected language listed by `languages`: {DetectedLanguageList()}, then extracts symbols and graph references only where the catalog advertises those capabilities.",
            "batch_query"
                => "Language support: Language behavior is inherited from each nested read-only tool; consult each returned payload and the `languages` tool for capabilities.",
            "backfill_fold" or "ping" or "suggest_improvement"
                => "Language support: Language-independent tool; it does not interpret `lang` filters.",
            _ => "Language support: See the `languages` tool for detected languages and per-language symbol_extraction / reference_extraction / graph_queries capabilities.",
        };

        return $"{description} {clause}";
    }

    private static string DetectedLanguageList()
        => string.Join(", ", FileIndexer.GetDetectedLanguageNames());

    private static string SymbolLanguageList()
        => string.Join(", ", SymbolExtractor.GetSupportedLanguages()
            .OrderBy(lang => lang, StringComparer.Ordinal));

    private static string GraphLanguageList()
        => string.Join(", ", ReferenceExtractor.GetSupportedLanguages()
            .OrderBy(lang => lang, StringComparer.Ordinal));

    /// <summary>
    /// Build MCP tool annotations for a read-only query tool.
    /// 読み取り専用クエリツール用のMCPツールアノテーションを構築。
    /// </summary>
    private static JsonObject ReadOnlyAnnotations() => new()
    {
        ["readOnlyHint"] = true,
        ["destructiveHint"] = false,
        ["idempotentHint"] = true,
        ["openWorldHint"] = false
    };

    /// <summary>
    /// Build MCP tool annotations for the index (write) tool.
    /// index（書き込み）ツール用のMCPツールアノテーションを構築。
    /// Destructive because --rebuild drops the DB; not idempotent because
    /// re-indexing replaces chunks/symbols/references per file.
    /// --rebuildでDBを削除するため破壊的。再インデックスはファイルごとに
    /// チャンク・シンボル・参照を置き換えるため冪等ではない。
    /// </summary>
    private static JsonObject IndexAnnotations() => new()
    {
        ["readOnlyHint"] = false,
        ["destructiveHint"] = true,
        ["idempotentHint"] = false,
        ["openWorldHint"] = false
    };

    /// <summary>
    /// Build MCP tool annotations for the suggest_improvement tool.
    /// suggest_improvementツール用のMCPツールアノテーションを構築。
    /// Not read-only (writes suggestion to disk), not destructive,
    /// idempotent (duplicate submissions are safely deduplicated).
    /// 読み取り専用ではない（提案をディスクに書き込む）、破壊的ではない、
    /// 冪等（重複送信は安全に排除される）。
    /// </summary>
    private static JsonObject SuggestionAnnotations() => new()
    {
        ["readOnlyHint"] = false,
        ["destructiveHint"] = false,
        ["idempotentHint"] = true,
        ["openWorldHint"] = false
    };

}

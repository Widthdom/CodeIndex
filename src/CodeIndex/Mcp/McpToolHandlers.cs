using System.Diagnostics;
using System.Globalization;
using System.Text;
using System.Text.Json;
using System.Text.Json.Nodes;
using System.Text.RegularExpressions;
using CodeIndex.Cli;
using CodeIndex.Database;
using CodeIndex.Indexer;
using CodeIndex.Indexer.Extensibility;
using CodeIndex.Indexer.Hooks;
using CodeIndex.Models;

namespace CodeIndex.Mcp;

/// <summary>
/// MCP tool execution handlers (partial class split from McpServer.cs).
/// MCPツール実行ハンドラ（McpServer.csからのpartial class分割）。
/// </summary>
public partial class McpServer
{
    private const int DefaultBatchQueryResponseByteLimit = MaxLineByteLength;
    internal const int MaxBatchQueryResponseByteLimit = 10 * 1024 * 1024;
    private const int DefaultExcerptOutputByteLimit = MaxLineByteLength;
    private const string BatchQueryResponseByteLimitEnvVar = "CDIDX_MCP_BATCH_RESPONSE_MAX_BYTES";
    internal const int MaxMcpArrayFilterCount = 100;
    internal const int MaxMcpArrayFilterStringLength = 4096;

    // --- Tool implementations / ツール実装 ---

    /// <summary>
    /// Build the server instructions string for the initialize response.
    /// Uses the actual supported-language list from ReferenceExtractor and skips guidance
    /// for any tool the operator disabled through the #1561 enablement gate so scoped
    /// deployments do not advertise tools that the gate would reject.
    /// initializeレスポンス用のサーバー指示文字列を構築。
    /// ReferenceExtractorの実際の対応言語リストを使用し、#1561 の有効化ゲートで無効化された
    /// ツールについての案内は除外する（scoped デプロイで無効ツールが advertise されないように）。
    /// </summary>
    private string BuildInstructions()
    {
        bool On(string name) => _toolFilter.IsEnabled(name);
        bool All(params string[] names)
        {
            foreach (var n in names)
                if (!On(n)) return false;
            return true;
        }

        var parts = new List<string> { "cdidx is a code-index server." };

        if (On("index"))
            parts.Add("If queries fail because no index exists, run 'index' first to build it.");

        if (All("map", "search", "definition"))
            parts.Add("Start with 'map' for repo orientation, then use 'search' for text queries or 'definition' for symbol lookup.");
        else if (All("search", "definition"))
            parts.Add("Use 'search' for text queries or 'definition' for symbol lookup.");
        else if (On("search"))
            parts.Add("Use 'search' for text queries.");
        else if (On("definition"))
            parts.Add("Use 'definition' for symbol lookup.");

        if (On("analyze_symbol"))
            parts.Add("Use 'analyze_symbol' to get definition, callers, callees, and references in one call instead of chaining separate tools.");

        var graphEnabled = new List<string>();
        foreach (var name in new[] { "references", "callers", "callees" })
            if (On(name)) graphEnabled.Add(name);
        if (graphEnabled.Count > 0)
        {
            var langs = string.Join(", ", ReferenceExtractor.GetSupportedLanguages());
            var names = string.Join(", ", graphEnabled);
            var sentence = $"Graph tools ({names}) only work for supported languages ({langs});";
            sentence += On("search")
                ? " for other languages, use 'search' instead."
                : " for other languages, these tools have no answers.";
            parts.Add(sentence);
        }

        if (On("outline"))
            parts.Add("Use 'outline' to see the full symbol structure of a single file (functions, classes, properties, interfaces, enums with line numbers) without reading the file content.");

        if (On("symbols"))
            parts.Add("Filter symbols by kind using the 'kind' parameter: function, class, struct, interface, enum, property, event, delegate, namespace, import.");

        if (On("find_in_file"))
            parts.Add("Use 'find_in_file' for literal substring navigation when the target file is already known.");

        if (On("excerpt"))
            parts.Add("Use 'excerpt' to read specific line ranges from indexed files.");

        if (On("status"))
            parts.Add("Check 'status' to verify index freshness before trusting results.");

        if (On("languages"))
            parts.Add("Use 'languages' to discover all supported languages, file extensions, and which languages support call-graph queries.");

        if (On("search"))
            parts.Add("Use 'search' with 'exactSubstring: true' for case-sensitive substring matching when FTS5 returns too many results.");

        var exactNameTools = new List<string>();
        foreach (var name in new[] { "symbols", "definition", "references", "callers", "callees", "analyze_symbol" })
            if (On(name)) exactNameTools.Add(name);
        if (exactNameTools.Count > 0)
            parts.Add($"Use 'exactName: true' on {string.Join("/", exactNameTools)} for exact symbol-name equality.");

        if (All("status", "backfill_fold"))
            parts.Add("If 'status' reports fold_ready=false and Unicode exact-name matching matters, use 'backfill_fold' to upgrade folded keys without reparsing files.");

        if (On("files"))
            parts.Add("Use 'files' with 'since' to find recently modified files without scanning all results.");

        if (On("batch_query"))
            parts.Add("Use 'batch_query' to execute multiple read-only queries in a single call (max 10), dramatically reducing round-trips.");

        if (On("deps"))
            parts.Add("Use 'deps' to see file-level dependency edges — which files reference symbols from which other files.");

        if (On("unused_symbols"))
            parts.Add("Use 'unused_symbols' to find dead code — symbols defined but never referenced (only meaningful for graph-supported languages).");

        if (On("symbol_hotspots"))
            parts.Add("Use 'symbol_hotspots' to find the most-referenced symbols — central, high-impact code that changes may affect widely.");

        if (On("impact_analysis"))
            parts.Add("Use 'impact_analysis' to compute transitive callers of a symbol. Pass maxHops=0 when you only want symbol resolution without traversing callers. Caller rows are edge-kind aware: the same caller can appear once for 'call' and once for 'subscribe'. When a scoped query resolves to a single class / struct / interface but no symbol-level callers exist, it may instead return heuristic file-level dependency hints; always inspect 'impact_mode', 'heuristic', and 'file_impacts'.");

        if (On("suggest_improvement"))
            parts.Add("Use 'suggest_improvement' to report gaps or errors you notice (e.g. missing language support, poor ranking, crashes) — never include source code, only describe the issue in natural language.");

        return string.Join(" ", parts);
    }

    /// <summary>
    /// Add freshness hint fields to a zero-result payload so AI clients
    /// can self-diagnose stale or empty indexes without a separate status call.
    /// 0件レスポンスに鮮度ヒントを追加し、AIクライアントが別途statusを
    /// 呼ばなくてもインデックスの古さや空を自己診断できるようにする。
    /// </summary>
    private static void AddFreshnessHint(JsonObject payload, DbReader reader)
    {
        var freshness = reader.GetFreshnessHint();
        payload["indexed_file_count"] = freshness.FileCount;
        payload["indexed_at"] = freshness.IndexedAt.HasValue
            ? JsonSerializer.SerializeToNode(freshness.IndexedAt.Value)
            : null;
        payload["freshness_available"] = freshness.FreshnessAvailable;
        if (!freshness.FreshnessAvailable && freshness.FreshnessDegradedReason != null)
            payload["freshness_degraded_reason"] = freshness.FreshnessDegradedReason;
    }

    private static void AddSearchStabilityMetadata(JsonObject payload, DbReader reader, SearchCursor? cursor, IReadOnlyList<SearchResult> results)
    {
        var freshness = reader.GetFreshnessHint();
        payload["result_stable_at"] = freshness.IndexedAt.HasValue
            ? JsonSerializer.SerializeToNode(freshness.IndexedAt.Value)
            : null;
        payload["freshness_available"] = freshness.FreshnessAvailable;
        if (!freshness.FreshnessAvailable && freshness.FreshnessDegradedReason != null)
            payload["freshness_degraded_reason"] = freshness.FreshnessDegradedReason;

        if (results.Count > 0)
            payload["next_cursor"] = FormatSearchCursor(results[^1]);
    }

    private static string FormatSearchCursor(SearchResult result)
        => string.Create(CultureInfo.InvariantCulture, $"{result.Score:R}:{result.ChunkId}:{result.NextOffset}");

    private static bool TryParseSearchCursor(string value, out SearchCursor cursor)
    {
        cursor = default;
        var lastSeparator = value.LastIndexOf(':');
        if (lastSeparator <= 0 || lastSeparator == value.Length - 1)
            return false;

        var firstSeparator = value.LastIndexOf(':', lastSeparator - 1);
        if (firstSeparator <= 0 || firstSeparator == lastSeparator - 1)
            return false;

        if (!double.TryParse(value.AsSpan(0, firstSeparator), NumberStyles.Float, CultureInfo.InvariantCulture, out var score))
            return false;
        if (!long.TryParse(value.AsSpan(firstSeparator + 1, lastSeparator - firstSeparator - 1), NumberStyles.None, CultureInfo.InvariantCulture, out var chunkId))
            return false;
        if (!int.TryParse(value.AsSpan(lastSeparator + 1), NumberStyles.None, CultureInfo.InvariantCulture, out var offset) || offset < 0)
            return false;

        cursor = new SearchCursor(score, chunkId, offset);
        return true;
    }

    private static void AddFtsQueryDiagnostics(JsonObject payload, FtsQueryDiagnostics diagnostics)
    {
        if (!diagnostics.HasDegradation)
            return;

        payload["query_degraded_reason"] = diagnostics.QueryDegradedReason;
        var dropped = new JsonArray();
        foreach (var token in diagnostics.TokensDropped)
            dropped.Add(token);
        payload["tokens_dropped"] = dropped;
    }

    private static void AddExactZeroHint(JsonObject payload, ExactZeroHintResult? exactZeroHint)
    {
        if (exactZeroHint == null)
            return;

        var sampleNames = new JsonArray();
        foreach (var name in exactZeroHint.SampleNames)
            sampleNames.Add(name);

        payload["exact_zero_hint"] = new JsonObject
        {
            ["sample_names"] = sampleNames,
            ["suggestion"] = exactZeroHint.Suggestion,
        };
        if (exactZeroHint.RelaxedCount.HasValue)
            payload["exact_zero_hint"]!["relaxed_count"] = exactZeroHint.RelaxedCount.Value;
    }

    private static void AddRecoveryHint(JsonObject payload, string reason, string suggestedAction, string? tool = null, JsonObject? args = null)
    {
        var hint = new JsonObject
        {
            ["reason"] = reason,
            ["suggested_action"] = suggestedAction,
        };
        if (tool != null)
            hint["tool"] = tool;
        if (args != null)
            hint["args"] = args;
        payload["recovery_hint"] = hint;
    }

    private static void AddExactSubstringRecoveryHint(JsonObject payload, string query)
        => AddRecoveryHint(
            payload,
            SearchQueryAdvisor.ExactSubstringHintReason,
            SearchQueryAdvisor.McpExactSubstringSuggestedAction,
            "search",
            new JsonObject
            {
                ["query"] = query,
                ["exactSubstring"] = true,
                ["limit"] = 5,
            });

    private static void AddSymbolRecoveryHint(JsonObject payload, string query, string toolName, string? lang, string? kind, JsonNode? path)
    {
        var args = new JsonObject
        {
            ["query"] = query,
            ["limit"] = 5,
        };
        if (lang != null)
            args["lang"] = lang;
        if (kind != null)
            args["kind"] = kind;
        if (path != null)
            args["path"] = path.DeepClone();

        AddRecoveryHint(
            payload,
            "no_results",
            $"{toolName} returned no rows; check whether the symbol is indexed, whether filters are too narrow, or whether a broader symbol lookup finds a nearby name.",
            "symbols",
            args);
    }

    private static void AddNextStepSuggestion(JsonObject payload, string tool, JsonObject args)
    {
        payload["next_step_suggestion"] = new JsonObject
        {
            ["tool"] = tool,
            ["args"] = args,
        };
    }

    private static JsonObject BuildExcerptArgs(string path, int startLine, int endLine)
        => new()
        {
            ["path"] = path,
            ["startLine"] = startLine,
            ["endLine"] = endLine,
        };

    /// <summary>
    /// Clamp limit to a safe range to prevent resource exhaustion.
    /// リソース枯渇を防ぐためlimitを安全な範囲にクランプ。
    /// </summary>
    private static int ClampLimit(int limit) => Math.Clamp(limit, 1, MaxLimit);

    private static int ReadOffset(JsonNode? args)
        => Math.Clamp(args?["offset"]?.GetValue<int>() ?? 0, 0, MaxMcpPaginationOffset);

    private static string ReadResponseFormat(JsonNode? args)
        => args?["format"]?.GetValue<string>()?.Trim().ToLowerInvariant() ?? "full";

    private static string? ValidateResponseFormat(string format)
        => format is "full" or "count" or "compact"
            ? null
            : "format must be one of full, count, compact";

    private static void ApplyCompactResults<T>(
        JsonObject payload,
        IEnumerable<T> results,
        Func<T, string> pathSelector,
        Func<T, int> lineSelector,
        Func<T, int?>? columnSelector = null)
    {
        var compact = new JsonArray();
        foreach (var result in results)
        {
            var row = new JsonObject
            {
                ["file"] = pathSelector(result),
                ["line"] = lineSelector(result),
            };
            var column = columnSelector?.Invoke(result);
            if (column.HasValue)
                row["column"] = column.Value;
            compact.Add(row);
        }
        payload["results"] = compact;
        payload["format"] = "compact";
    }

    private static bool AddLimitMetadata<T>(JsonObject payload, List<T> results, int limit, int offset = 0, bool includePagination = false)
    {
        var truncated = results.Count > limit;
        if (truncated)
            results.RemoveRange(limit, results.Count - limit);

        payload["count"] = results.Count;
        payload["truncated"] = truncated;
        payload["more_available"] = truncated;
        if (includePagination)
        {
            payload["offset"] = offset;
            if (truncated)
                payload["next_offset"] = offset + results.Count;
        }
        return truncated;
    }

    /// <summary>
    /// Return true when the requested reference kind is NOT a call-graph kind (i.e. metadata
    /// `attribute` / `annotation`, compile-time `type_reference`, or structural `import`) —
    /// these are valid on the `references` tool but must be rejected on `callers` / `callees`, whose data model
    /// cannot answer those queries correctly. Metadata rows are attributed to the enclosing
    /// body-range symbol rather than the annotated target (so file-level targets drop
    /// entirely and method-level metadata appears under the enclosing class); `type_reference`
    /// rows are compile-time type-position edges (declaration types, generic constraints,
    /// `is`/`as`/`instanceof`, XML-doc `cref`), not runtime calls, so they misreport type
    /// mentions as caller/callee edges; `import` rows are structural dependency edges.
    /// `references` では有効だが `callers` / `callees` では構造的に誤答するため弾くべき kind
    /// （metadata: `attribute` / `annotation`、型位置: `type_reference`、構造 dependency: `import`）かを返す。metadata 行は
    /// 注釈対象ではなく body-range 上の外側シンボルに帰属し、`type_reference` は実行時呼び出し
    /// ではなく compile-time な型言及（宣言型、generic 制約、`is`/`as`/`instanceof`、XML-doc
    /// `cref` など）で、`import` は構造的な dependency edge なので、`callers` / `callees` は
    /// いずれの kind にも正しく答えられない。
    /// </summary>
    private static bool IsNonCallGraphReferenceKind(string? kind) =>
        kind == "attribute" || kind == "annotation" || kind == "type_reference" || kind == "import";

    /// <summary>
    /// Build the CLI / MCP error message for a non-call-graph reference kind rejected on
    /// `callers` / `callees`. The message explains why the kind is structurally wrong on
    /// the command and redirects users to `references`.
    /// `callers` / `callees` で弾いた非 call-graph kind のエラーメッセージを組み立てる。
    /// 構造的に誤答する理由を説明し、`references` に誘導する。
    /// </summary>
    private static string BuildNonCallGraphKindRejectionMessage(string command, string kind) =>
        kind == "type_reference"
            ? $"'kind: type_reference' is not supported on '{command}'. Type-position references are compile-time edges (declaration types, generic constraints, `is`/`as`/`instanceof`, XML-doc `cref`), not runtime calls, so `{command}` cannot return accurate rows for kind 'type_reference'. Use the 'references' tool with kind 'type_reference' instead."
            : kind == "import"
                ? $"'kind: import' is not supported on '{command}'. Import references are structural dependency edges, not runtime calls, so `{command}` cannot return accurate rows for kind 'import'. Use the 'references' tool with kind 'import' instead."
            : $"'kind: {kind}' is not supported on '{command}'. Metadata references are attributed to the enclosing body-range symbol, so `{command}` cannot return accurate rows for kind '{kind}'. Use the 'references' tool with kind '{kind}' for metadata enumeration.";

    private JsonNode? TryGetValidatedMaxLineWidth(JsonNode? id, JsonNode? args, out int maxLineWidth, string propertyName = "maxLineWidth")
    {
        var maxLineWidthValue = args?[propertyName]?.GetValue<int>();
        if (maxLineWidthValue.HasValue && maxLineWidthValue.Value < 0)
        {
            maxLineWidth = LineWidthFormatter.DefaultMaxLineWidth;
            return CreateToolErrorResponse(id, "maxLineWidth must be greater than or equal to 0");
        }

        if (maxLineWidthValue.HasValue && maxLineWidthValue.Value > LineWidthFormatter.MaxAllowedLineWidth)
        {
            maxLineWidth = LineWidthFormatter.DefaultMaxLineWidth;
            return CreateToolErrorResponse(id, $"maxLineWidth must be less than or equal to {LineWidthFormatter.MaxAllowedLineWidth}");
        }

        maxLineWidth = maxLineWidthValue ?? LineWidthFormatter.DefaultMaxLineWidth;
        return null;
    }

    private static List<string> ReadStringList(JsonNode? args, string propertyName)
    {
        return args?[propertyName] is JsonArray array
            ? array.Select(node => node is JsonValue value && value.TryGetValue<string>(out var text) ? text : null)
                .Where(value => !string.IsNullOrWhiteSpace(value))
                .Cast<string>()
                .ToList()
            : [];
    }

    private JsonNode? TryReadStringOrStringList(JsonNode? id, JsonNode? args, string propertyName, out List<string> values)
    {
        values = [];
        var node = args?[propertyName];
        if (node is null)
            return null;

        if (node is JsonValue singleValue && singleValue.TryGetValue<string>(out var singleText))
        {
            values.Add(singleText);
            return null;
        }

        if (node is JsonArray array)
        {
            foreach (var item in array)
            {
                if (item is not JsonValue value || !value.TryGetValue<string>(out var text))
                    return CreateToolErrorResponse(id, $"'{propertyName}' entries must be strings.");
                values.Add(text);
            }
            return null;
        }

        return CreateToolErrorResponse(id, $"'{propertyName}' must be a string or string array.");
    }

    private JsonNode? TryReadSearchGuardFilters(JsonNode? id, JsonNode? args, out List<SearchGuardFilter> filters)
    {
        filters = [];
        var collected = new List<SearchGuardFilter>();

        JsonNode? AddFilters(string propertyName, SearchGuardRole role, SearchGuardDirection direction)
        {
            if (TryReadStringOrStringList(id, args, propertyName, out var values) is JsonNode readError)
                return readError;

            foreach (var value in values)
            {
                if (string.IsNullOrWhiteSpace(value))
                    return CreateToolErrorResponse(id, $"'{propertyName}' entries must be non-empty strings.");
                if (value.Length > QueryLimits.MaxQueryLength)
                    return CreateToolErrorResponse(id, $"'{propertyName}' query too long (max {QueryLimits.MaxQueryLength} characters).");

                collected.Add(new SearchGuardFilter(role, direction, value));
            }

            return null;
        }

        if (AddFilters("requireBefore", SearchGuardRole.Require, SearchGuardDirection.Before) is JsonNode requireBeforeError)
            return requireBeforeError;
        if (AddFilters("requireAfter", SearchGuardRole.Require, SearchGuardDirection.After) is JsonNode requireAfterError)
            return requireAfterError;
        if (AddFilters("rejectBefore", SearchGuardRole.Reject, SearchGuardDirection.Before) is JsonNode rejectBeforeError)
            return rejectBeforeError;
        if (AddFilters("rejectAfter", SearchGuardRole.Reject, SearchGuardDirection.After) is JsonNode rejectAfterError)
            return rejectAfterError;

        filters = collected;
        return filters.Count > DbReader.MaxSearchGuardFilters
            ? CreateToolErrorResponse(id, $"search accepts at most {DbReader.MaxSearchGuardFilters} guard filters; got {filters.Count}.")
            : null;
    }

    private static JsonObject? ValidateCommonListArguments(JsonNode? args)
    {
        foreach (var propertyName in new[] { "path", "project", "excludePaths", "names" })
        {
            if (ValidateStringListArgument(args, propertyName) is JsonObject error)
                return error;
        }

        return null;
    }

    private static JsonObject? ValidateToolArguments(string toolName, JsonNode? args)
    {
        if (!IsKnownToolName(toolName))
            return null;

        if (args is null)
            return null;
        if (args is not JsonObject obj)
            return new JsonObject
            {
                ["message"] = "Tool arguments must be a JSON object.",
                ["tool"] = toolName,
            };

        var allowed = GetAllowedToolArguments(toolName);
        if (allowed.Count == 0)
            return obj.Count == 0 ? null : new JsonObject
            {
                ["message"] = $"Tool '{toolName}' does not accept arguments.",
                ["tool"] = toolName,
                ["unknown_argument"] = obj.First().Key,
            };

        foreach (var property in obj)
        {
            if (!allowed.Contains(property.Key))
            {
                return new JsonObject
                {
                    ["message"] = $"Unknown argument '{property.Key}' for tool '{toolName}'.",
                    ["tool"] = toolName,
                    ["unknown_argument"] = property.Key,
                };
            }

        }

        if (ValidateToolArgumentTypes(toolName, obj) is JsonObject typeError)
            return typeError;

        return null;
    }

    private static JsonObject? ValidateToolArgumentTypes(string toolName, JsonObject args)
    {
        foreach (var property in args)
        {
            if (TryGetExpectedJsonType(toolName, property.Key, out var expected)
                && !MatchesExpectedJsonType(property.Value, expected))
            {
                return new JsonObject
                {
                    ["message"] = $"Invalid type for argument '{property.Key}' on tool '{toolName}'. Expected {expected}.",
                    ["tool"] = toolName,
                    ["parameter"] = property.Key,
                    ["expected"] = expected,
                    ["actual"] = DescribeJsonType(property.Value),
                    ["jsonrpc_invalid_params"] = true,
                };
            }
        }

        return null;
    }

    private static bool TryGetExpectedJsonType(string toolName, string argumentName, out string expected)
    {
        if (argumentName is "path" or "project" or "excludePaths" or "names" or "files" or "commits" or "changedBetween")
        {
            expected = string.Empty;
            return false;
        }

        expected = argumentName switch
        {
            "limit" or "offset" or "snippetLines" or "maxLineWidth" or "before" or "after" or
                "focusLine" or "focusColumn" or "focusLength" or "startLine" or "endLine" or
                "maxHops" or "maxDepth" or "depth" or "parallelism" or "maxFileBytes" or "guardWindow" => "integer",
            "excludeTests" or "includeGenerated" or "rawQuery" or "noDedup" or "exactSubstring" or
                "exactName" or "exact" or "prefix" or "countOnly" or "includeBody" or "lsp_compatible" or
                "regex" or "withPaths" or "rebuild" or "dryRun" or "dry_run" or "force" or "optimize" => "boolean",
            "requireBefore" or "requireAfter" or "rejectBefore" or "rejectAfter" => "string_or_array",
            "query" or "lang" or "kind" or "format" or "rankBy" or "since" or "path" or "project" or
                "solution" or "symbol" or "direction" or "groupBy" or "category" or "language" or
                "description" or "context" or "toolInvocationContext" or "db" => "string",
            "queries" or "evidencePaths" or "evidence_paths" => "array",
            _ => string.Empty,
        };

        if (expected.Length == 0)
            return false;

        return true;
    }

    private static bool MatchesExpectedJsonType(JsonNode? node, string expected) => expected switch
    {
        "integer" => node is JsonValue value && value.TryGetValue<int>(out _),
        "boolean" => node is JsonValue value && value.TryGetValue<bool>(out _),
        "string" => node is JsonValue value && value.TryGetValue<string>(out _),
        "string_or_array" => node is JsonArray || node is JsonValue value && value.TryGetValue<string>(out _),
        "array" => node is JsonArray,
        _ => true,
    };

    private static string DescribeJsonType(JsonNode? node)
    {
        if (node is null)
            return "null";
        return node.GetValueKind() switch
        {
            JsonValueKind.String => "string",
            JsonValueKind.Number => "number",
            JsonValueKind.True or JsonValueKind.False => "boolean",
            JsonValueKind.Array => "array",
            JsonValueKind.Object => "object",
            JsonValueKind.Null => "null",
            _ => "unknown",
        };
    }

    private static bool IsKnownToolName(string toolName) => toolName switch
    {
        "search" or "definition" or "references" or "callers" or "callees" or "symbols" or
        "files" or "find_in_file" or "excerpt" or "map" or "analyze_symbol" or "status" or
        "outline" or "batch_query" or "deps" or "impact_analysis" or "languages" or "validate" or
        "unused_symbols" or "symbol_hotspots" or "ping" or "index" or "backfill_fold" or
        "suggest_improvement" => true,
        _ => false,
    };

    private static IReadOnlySet<string> GetAllowedToolArguments(string toolName) => toolName switch
    {
        "search" => new HashSet<string>(StringComparer.Ordinal) { "query", "limit", "lang", "snippetLines", "maxLineWidth", "rawQuery", "cursor", "path", "excludePaths", "excludeTests", "includeGenerated", "since", "noDedup", "exactSubstring", "exact", "prefix", "requireBefore", "requireAfter", "rejectBefore", "rejectAfter", "guardWindow", "countOnly", "format", "project", "solution" },
        "definition" => new HashSet<string>(StringComparer.Ordinal) { "query", "kind", "lang", "limit", "includeBody", "lsp_compatible", "path", "excludePaths", "excludeTests", "includeGenerated", "since", "exactName", "exact", "format", "project", "solution" },
        "references" => new HashSet<string>(StringComparer.Ordinal) { "query", "kind", "lang", "limit", "offset", "maxLineWidth", "lsp_compatible", "path", "excludePaths", "excludeTests", "includeGenerated", "exactName", "exact", "countOnly", "format", "project", "solution" },
        "callers" or "callees" => new HashSet<string>(StringComparer.Ordinal) { "query", "kind", "rankBy", "lang", "limit", "offset", "path", "excludePaths", "excludeTests", "includeGenerated", "exactName", "exact", "countOnly", "format", "project", "solution" },
        "symbols" => new HashSet<string>(StringComparer.Ordinal) { "query", "names", "kind", "lang", "limit", "path", "excludePaths", "excludeTests", "includeGenerated", "since", "exactName", "exact", "project", "solution" },
        "files" => new HashSet<string>(StringComparer.Ordinal) { "query", "lang", "limit", "path", "excludePaths", "excludeTests", "includeGenerated", "since" },
        "find_in_file" => new HashSet<string>(StringComparer.Ordinal) { "query", "path", "limit", "lang", "excludePaths", "excludeTests", "includeGenerated", "before", "after", "snippetLines", "focusLine", "focusColumn", "maxLineWidth", "exact", "regex" },
        "excerpt" => new HashSet<string>(StringComparer.Ordinal) { "path", "startLine", "endLine", "before", "after", "focusLine", "focusColumn", "focusLength", "maxLineWidth" },
        "map" => new HashSet<string>(StringComparer.Ordinal) { "limit", "lang", "path", "excludePaths", "excludeTests", "project", "solution" },
        "analyze_symbol" => new HashSet<string>(StringComparer.Ordinal) { "query", "lang", "limit", "includeBody", "path", "excludePaths", "excludeTests", "includeGenerated", "exactName", "exact", "maxLineWidth", "project", "solution" },
        "outline" => new HashSet<string>(StringComparer.Ordinal) { "path", "limit", "includeImports", "maxLineWidth" },
        "batch_query" => new HashSet<string>(StringComparer.Ordinal) { "queries" },
        "deps" => new HashSet<string>(StringComparer.Ordinal) { "path", "direction", "lang", "limit", "excludePaths", "excludeTests", "project", "solution" },
        "impact_analysis" => new HashSet<string>(StringComparer.Ordinal) { "symbol", "query", "lang", "maxHops", "maxDepth", "depth", "limit", "path", "excludePaths", "excludeTests", "includeGenerated", "withPaths", "countOnly", "project", "solution" },
        "validate" => new HashSet<string>(StringComparer.Ordinal) { "path", "lang", "limit", "excludePaths", "excludeTests", "project", "solution" },
        "unused_symbols" => new HashSet<string>(StringComparer.Ordinal) { "kind", "lang", "limit", "path", "excludePaths", "excludeTests", "project", "solution" },
        "symbol_hotspots" => new HashSet<string>(StringComparer.Ordinal) { "kind", "lang", "limit", "groupBy", "path", "excludePaths", "excludeTests", "project", "solution" },
        "index" => new HashSet<string>(StringComparer.Ordinal) { "path", "rebuild", "maxFileBytes" },
        "backfill_fold" => new HashSet<string>(StringComparer.Ordinal) { "dry_run", "dryRun", "force" },
        "suggest_improvement" => new HashSet<string>(StringComparer.Ordinal) { "category", "language", "description", "context", "toolInvocationContext", "evidencePaths", "evidence_paths" },
        _ => new HashSet<string>(StringComparer.Ordinal),
    };

    private static JsonObject? ValidateStringListArgument(JsonNode? args, string propertyName)
    {
        var node = args?[propertyName];
        if (node is null)
            return null;

        if (node is JsonArray array)
        {
            if (array.Count > MaxMcpArrayFilterCount)
                return new JsonObject
                {
                    ["message"] = $"{propertyName} must contain at most {MaxMcpArrayFilterCount} entries.",
                    ["invalid_count"] = array.Count - MaxMcpArrayFilterCount,
                };

            var invalidCount = 0;
            var invalidSamples = new JsonArray();
            for (var i = 0; i < array.Count; i++)
            {
                var element = array[i];
                if (element is not JsonValue value || !value.TryGetValue<string>(out var text) || string.IsNullOrWhiteSpace(text))
                {
                    invalidCount++;
                    if (invalidSamples.Count < 3)
                        invalidSamples.Add($"[{i}]");
                    continue;
                }

                if (text.Length > MaxMcpArrayFilterStringLength)
                {
                    invalidCount++;
                    if (invalidSamples.Count < 3)
                        invalidSamples.Add($"[{i}] length {text.Length}");
                }
            }

            if (invalidCount > 0 && !(propertyName == "names" && invalidCount == array.Count))
                return new JsonObject
                {
                    ["message"] = $"{propertyName} contains {invalidCount} invalid entr{(invalidCount == 1 ? "y" : "ies")}. Entries must be non-empty strings no longer than {MaxMcpArrayFilterStringLength} characters.",
                    ["invalid_count"] = invalidCount,
                    ["invalid_samples"] = invalidSamples,
                };
            return null;
        }

        if (node is JsonValue scalar && scalar.TryGetValue<string>(out var scalarText))
        {
            if (propertyName == "names")
                return null;
            if (propertyName == "path" && string.IsNullOrWhiteSpace(scalarText))
                return null;
            if (string.IsNullOrWhiteSpace(scalarText))
                return new JsonObject
                {
                    ["message"] = $"{propertyName} cannot be empty or whitespace-only.",
                    ["invalid_count"] = 1,
                };
            if (scalarText.Length > MaxMcpArrayFilterStringLength)
                return new JsonObject
                {
                    ["message"] = $"{propertyName} must be no longer than {MaxMcpArrayFilterStringLength} characters.",
                    ["invalid_count"] = 1,
                    ["invalid_samples"] = new JsonArray { $"length {scalarText.Length}" },
                };
            return null;
        }

        return new JsonObject
        {
            ["message"] = $"{propertyName} must be a string or an array of strings.",
            ["invalid_count"] = 1,
        };
    }

    private static bool TryResolveSearchExactArgument(JsonNode? args, out bool exact, out string? error)
    {
        var legacyExact = args?["exact"]?.GetValue<bool>() ?? false;
        var exactSubstring = args?["exactSubstring"]?.GetValue<bool>() ?? false;
        var exactName = args?["exactName"]?.GetValue<bool>() ?? false;

        if (CountTrue(legacyExact, exactSubstring, exactName) > 1)
        {
            exact = false;
            error = "Pass only one of 'exact', 'exactSubstring', 'exactName'.";
            return false;
        }

        if (exactName)
        {
            exact = false;
            error = "Search does not accept 'exactName'. Use 'exactSubstring' for search, or keep 'exact' for backward compatibility.";
            return false;
        }

        exact = legacyExact || exactSubstring;
        error = null;
        return true;
    }

    private static bool TryResolveNameExactArgument(JsonNode? args, string toolName, out bool exact, out string? error)
    {
        var legacyExact = args?["exact"]?.GetValue<bool>() ?? false;
        var exactSubstring = args?["exactSubstring"]?.GetValue<bool>() ?? false;
        var exactName = args?["exactName"]?.GetValue<bool>() ?? false;

        if (CountTrue(legacyExact, exactSubstring, exactName) > 1)
        {
            exact = false;
            error = "Pass only one of 'exact', 'exactSubstring', 'exactName'.";
            return false;
        }

        if (exactSubstring)
        {
            exact = false;
            error = $"Tool '{toolName}' does not accept 'exactSubstring'. Use 'exactName', or keep 'exact' for backward compatibility.";
            return false;
        }

        exact = legacyExact || exactName;
        error = null;
        return true;
    }

    private static int CountTrue(params bool[] values)
    {
        return values.Count(value => value);
    }

    private JsonArray ToJsonArray<T>(IEnumerable<T> items)
    {
        var array = new JsonArray();
        foreach (var item in items)
            array.Add(JsonSerializer.SerializeToNode(item, _jsonOptions));
        return array;
    }

    private JsonArray ToJsonArray<TSource, TResult>(IEnumerable<TSource> items, Func<TSource, TResult> selector)
    {
        var array = new JsonArray();
        foreach (var item in items)
            array.Add(JsonSerializer.SerializeToNode(selector(item), _jsonOptions));
        return array;
    }

    private static int FetchLimitForEnvelope(int limit) => limit >= int.MaxValue ? int.MaxValue : limit + 1;

    private static bool TrimToRequestedLimit<T>(List<T> results, int limit)
    {
        if (results.Count <= limit)
            return false;

        results.RemoveRange(limit, results.Count - limit);
        return true;
    }

    private static void AddResultEnvelope(JsonObject payload, int returnedCount, int? total, bool truncated)
    {
        payload["count"] = returnedCount;
        payload["truncated"] = truncated;
        payload["more_available"] = truncated;
        payload["total"] = total.HasValue ? JsonValue.Create(total.Value) : null;
    }

    private static void AddPaginatedResultEnvelope(JsonObject payload, int returnedCount, int? total, bool truncated, int offset)
    {
        AddResultEnvelope(payload, returnedCount, total, truncated);
        payload["offset"] = offset;
        if (truncated)
            payload["next_offset"] = offset + returnedCount;
    }

    private static bool ReadCountOnly(JsonNode? args) => args?["countOnly"]?.GetValue<bool>() ?? args?["count_only"]?.GetValue<bool>() ?? false;

    private JsonArray BuildTopFileHistogram<T>(IEnumerable<T> results, Func<T, string?> pathSelector)
    {
        var histogram = new JsonArray();
        foreach (var group in results
            .Select(pathSelector)
            .Where(path => !string.IsNullOrWhiteSpace(path))
            .GroupBy(path => path!, StringComparer.Ordinal)
            .OrderByDescending(group => group.Count())
            .ThenBy(group => group.Key, StringComparer.Ordinal)
            .Take(5))
        {
            histogram.Add(new JsonObject
            {
                ["path"] = group.Key,
                ["count"] = group.Count(),
            });
        }

        return histogram;
    }

    private JsonObject BuildCountOnlyPayload<T>(int count, int? total, bool truncated, IEnumerable<T> histogramSource, Func<T, string?> pathSelector)
    {
        var payload = new JsonObject
        {
            ["count_only"] = true,
            ["top_files"] = BuildTopFileHistogram(histogramSource, pathSelector),
            ["results"] = new JsonArray(),
        };
        AddResultEnvelope(payload, count, total, truncated);
        return payload;
    }

    private JsonObject ToAnalyzeSymbolJsonObject(SymbolAnalysisResult analysis)
    {
        var payload = new JsonObject
        {
            ["api_version"] = analysis.ApiVersion,
            ["query"] = analysis.Query,
            ["file"] = JsonSerializer.SerializeToNode(analysis.File, _jsonOptions),
            ["workspace_indexed_at"] = JsonSerializer.SerializeToNode(analysis.WorkspaceIndexedAt, _jsonOptions),
            ["workspace_latest_modified"] = JsonSerializer.SerializeToNode(analysis.WorkspaceLatestModified, _jsonOptions),
            ["project_root"] = analysis.ProjectRoot,
            ["git_head"] = analysis.GitHead,
            ["git_is_dirty"] = analysis.GitIsDirty,
            ["graph_language"] = analysis.GraphLanguage,
            ["graph_supported"] = analysis.GraphSupported,
            ["graph_support_reason"] = analysis.GraphSupportReason,
            ["definitions"] = ToJsonArray(analysis.Definitions),
            ["nearby_symbols"] = ToJsonArray(analysis.NearbySymbols),
            ["references"] = ToJsonArray(analysis.References),
            ["callers"] = ToJsonArray(analysis.Callers),
            ["callees"] = ToJsonArray(analysis.Callees),
            ["graph_table_available"] = analysis.GraphTableAvailable,
        };
        if (analysis.IndexedHeadCommit != null)
            payload["indexed_head_commit"] = analysis.IndexedHeadCommit;
        if (analysis.WorktreeHeadChanged.HasValue)
            payload["worktree_head_changed"] = analysis.WorktreeHeadChanged.Value;
        if (analysis.GraphDegraded.HasValue)
            payload["graph_degraded"] = analysis.GraphDegraded.Value;
        if (analysis.UnsupportedSymbolKind != null)
            payload["unsupported_symbol_kind"] = analysis.UnsupportedSymbolKind;
        if (analysis.SqlGraphContractReady.HasValue)
            payload["sql_graph_contract_ready"] = analysis.SqlGraphContractReady.Value;
        if (analysis.SqlGraphContractDegradedReason != null)
            payload["sql_graph_contract_degraded_reason"] = analysis.SqlGraphContractDegradedReason;
        if (analysis.ExactZeroHint != null)
            payload["exact_zero_hint"] = JsonSerializer.SerializeToNode(analysis.ExactZeroHint, _jsonOptions);
        if (analysis.ExactIndexAvailable.HasValue)
            payload["exact_index_available"] = analysis.ExactIndexAvailable.Value;
        if (analysis.DegradedReason != null)
            payload["degraded_reason"] = analysis.DegradedReason;
        return payload;
    }

    /// <summary>
    /// Read a path filter argument that accepts either a scalar string or an array of strings.
    /// Returns null when the value is missing or empty so downstream SQL omits the filter.
    /// スカラー文字列と文字列配列の両方を受け付けるパスフィルタを読み取る。
    /// 値が無い/空なら null を返し下流 SQL でフィルタを省略する。
    /// </summary>
    private static List<string>? ReadPathList(JsonNode? args, string propertyName)
    {
        var node = args?[propertyName];
        if (node is null)
            return null;
        if (node is JsonArray array)
        {
            var list = array.Select(n => n?.GetValue<string>())
                .Where(v => !string.IsNullOrWhiteSpace(v))
                .Cast<string>()
                .ToList();
            return list.Count > 0 ? list : null;
        }
        // Scalar string (backward compat) / スカラー文字列（後方互換）
        var value = node.GetValue<string>();
        return string.IsNullOrWhiteSpace(value) ? null : new List<string> { value };
    }

    private static List<string>? ReadScopedPathList(JsonNode? args)
    {
        var paths = ReadPathList(args, "path") ?? [];
        var projects = ReadPathList(args, "project") ?? [];
        if (projects.Count == 0)
            return paths.Count == 0 ? null : paths;

        var solution = args?["solution"]?.GetValue<string>();
        foreach (var glob in SolutionProjectResolver.ResolveProjectDirectoryGlobs(Environment.CurrentDirectory, projects, solution))
            paths.Add(glob);
        return paths.Count == 0 ? null : paths;
    }

    private static bool TryReadRequiredStringParameter(JsonNode? args, string propertyName, out string value, out string? error)
    {
        var node = args?[propertyName];
        if (node is null)
        {
            value = string.Empty;
            error = $"Missing required parameter: {propertyName}";
            return false;
        }

        value = node.GetValue<string>();
        if (string.IsNullOrWhiteSpace(value))
        {
            error = $"Parameter \"{propertyName}\" cannot be empty or whitespace-only";
            return false;
        }

        error = null;
        return true;
    }

    private static bool HasBlankPathFilter(JsonNode? args)
    {
        var node = args?["path"];
        if (node is null)
            return false;

        if (node is JsonArray array)
            return array.Count > 0 && array.All(item => string.IsNullOrWhiteSpace(item?.GetValue<string>()));

        return string.IsNullOrWhiteSpace(node.GetValue<string>());
    }

    /// <summary>
    /// Serialize a path filter list back into a JSON echo value.
    /// Null/empty → JSON null; single element → string; multiple → array.
    /// パスフィルタリストをJSONエコー値として直列化。
    /// null/空 → JSON null、1要素 → 文字列、複数 → 配列。
    /// </summary>
    private static JsonNode? PathEcho(IReadOnlyList<string>? paths)
    {
        if (paths is null || paths.Count == 0)
            return null;
        if (paths.Count == 1)
            return JsonValue.Create(paths[0]);
        var arr = new JsonArray();
        foreach (var p in paths)
            arr.Add(JsonValue.Create(p));
        return arr;
    }

    private static string BuildGraphSummary(string singular, string plural, int count, string? lang, bool? graphSupported, string? graphSupportReason = null)
    {
        if (count > 0)
            return $"Found {ConsoleUi.Counted(count, singular, plural)}.";

        if (graphSupported == false && lang != null)
            return $"No {plural} found. Call-graph queries are not indexed for '{lang}'.";

        return $"No {plural} found.";
    }

    private static (string? GraphLanguage, bool? GraphSupported, string? GraphSupportReason)
        ResolveGraphSupport(DbReader reader, bool exact, string query, string? lang, IReadOnlyList<string>? pathPatterns, IReadOnlyList<string> excludePaths, bool excludeTests)
    {
        var graphLanguage = lang ?? (exact
            ? reader.GetExactGraphSupportedDefinitionLanguage(query, lang, pathPatterns, excludePaths, excludeTests)
            : null);
        var graphSupported = graphLanguage == null ? (bool?)null : ReferenceExtractor.SupportsLanguage(graphLanguage);
        var graphSupportReason = ReferenceExtractor.BuildGraphSupportReason(graphLanguage, graphSupported);
        return (
            GraphLanguage: graphLanguage,
            GraphSupported: graphSupported,
            GraphSupportReason: graphSupportReason);
    }

    private static bool TryReadReferenceRankMode(JsonNode? args, out ReferenceRankMode rankMode, out string? error)
    {
        var value = args?["rankBy"]?.GetValue<string>();
        if (string.IsNullOrWhiteSpace(value))
        {
            rankMode = ReferenceRankMode.Weighted;
            error = null;
            return true;
        }

        if (QueryCommandRunner.TryParseReferenceRankMode(value, out rankMode))
        {
            error = null;
            return true;
        }

        error = $"rankBy must be one of weighted, count, kind; got '{value}'.";
        return false;
    }

    private JsonNode ExecuteSearch(JsonNode? id, JsonNode? args)
    {
        if (!TryReadRequiredStringParameter(args, "query", out var query, out var requiredError))
            return CreateToolErrorResponse(id, requiredError!);
        if (query.Length > QueryLimits.MaxQueryLength)
            return CreateToolErrorResponse(id, QueryLimits.FormatQueryTooLongError());

        var limit = ClampLimit(args?["limit"]?.GetValue<int>() ?? QueryCommandRunner.DefaultQueryLimit);
        var lang = QueryCommandRunner.NormalizeLangFilterValue(args?["lang"]?.GetValue<string>());
        var snippetLines = SearchSnippetFormatter.ClampSnippetLines(args?["snippetLines"]?.GetValue<int>() ?? SearchSnippetFormatter.DefaultSnippetLines);
        if (TryGetValidatedMaxLineWidth(id, args, out var maxLineWidth) is JsonNode maxLineWidthError)
            return maxLineWidthError;
        var rawQuery = args?["rawQuery"]?.GetValue<bool>() ?? false;
        SearchCursor? cursor = null;
        var cursorValue = args?["cursor"]?.GetValue<string>();
        if (!string.IsNullOrWhiteSpace(cursorValue))
        {
            if (!TryParseSearchCursor(cursorValue, out var parsedCursor))
                return CreateToolErrorResponse(id, "'cursor' must be a search pagination cursor returned as `next_cursor` by a previous search response.");
            cursor = parsedCursor;
        }
        var pathPatterns = ReadScopedPathList(args);
        var excludePaths = ReadStringList(args, "excludePaths");
        var excludeTests = args?["excludeTests"]?.GetValue<bool>() ?? false;
        var sinceStr = args?["since"]?.GetValue<string>();
        DateTime? since = null;
        if (sinceStr != null)
        {
            if (QueryCommandRunner.TryParseIso8601Since(sinceStr, out var parsedSince))
                since = parsedSince;
            else
                return CreateToolErrorResponse(id, $"Invalid 'since' timestamp: '{sinceStr}'. Use ISO 8601 format (e.g. 2024-01-01 or 2024-01-01T00:00:00Z).");
        }
        var deduplicate = !(args?["noDedup"]?.GetValue<bool>() ?? false);
        var format = ReadResponseFormat(args);
        if (ValidateResponseFormat(format) is string formatError)
            return CreateToolErrorResponse(id, formatError);
        var countOnly = ReadCountOnly(args) || format == "count";
        if (!TryResolveSearchExactArgument(args, out var exact, out var exactError))
            return CreateToolErrorResponse(id, exactError!);
        var prefix = args?["prefix"]?.GetValue<bool>() ?? false;
        if (prefix && exact)
            return CreateToolErrorResponse(id, "'prefix' cannot be combined with 'exact' / 'exactSubstring' (exact uses instr(), not FTS5 prefix phrases).");
        if (TryReadSearchGuardFilters(id, args, out var guardFilters) is JsonNode guardError)
            return guardError;
        var guardWindow = args?["guardWindow"]?.GetValue<int>() ?? DbReader.DefaultSearchGuardWindow;
        if (guardWindow < 0 || guardWindow > DbReader.MaxSearchGuardWindow)
            return CreateToolErrorResponse(id, $"'guardWindow' must be between 0 and {DbReader.MaxSearchGuardWindow}; got {guardWindow}.");
        var suggestExactSubstring = SearchQueryAdvisor.ShouldSuggestExactSubstring(query, rawQuery, exact, prefix);

        return WithDbReader(id, args, reader =>
        {
            if (countOnly)
            {
                var countResults = reader.Search(query, MaxLimit, lang, rawQuery, pathPatterns, excludePaths, excludeTests, deduplicate, since, exact, prefix, guardFilters: guardFilters, guardWindow: guardWindow);
                var truncatedCount = countResults.Count >= MaxLimit;
                var payload = BuildCountOnlyPayload(countResults.Count, truncatedCount ? null : countResults.Count, truncatedCount, countResults, result => result.Path);
                payload["query"] = query;
                payload["rawQuery"] = rawQuery;
                payload["path"] = PathEcho(pathPatterns);
                payload["excludeTests"] = excludeTests;
                AddSearchStabilityMetadata(payload, reader, cursor, []);
                if (suggestExactSubstring)
                    AddExactSubstringRecoveryHint(payload, query);
                if (countResults.Count == 0)
                    AddFtsQueryDiagnostics(payload, DbReader.AnalyzeFtsQuery(query, rawQuery, prefix, lang));
                return CreateToolResult(id, $"Counted {countResults.Count} search result(s).", payload);
            }

            var results = reader.Search(query, FetchLimitForEnvelope(limit), lang, rawQuery, pathPatterns, excludePaths, excludeTests, deduplicate, since, exact, prefix, cursor: cursor, guardFilters: guardFilters, guardWindow: guardWindow);
            var ftsDiagnostics = DbReader.AnalyzeFtsQuery(query, rawQuery, prefix, lang);
            var truncated = TrimToRequestedLimit(results, limit);
            if (results.Count == 0)
            {
                var payload = new JsonObject
                {
                    ["query"] = query,
                    ["rawQuery"] = rawQuery,
                    ["snippetLines"] = snippetLines,
                    ["maxLineWidth"] = maxLineWidth,
                    ["path"] = PathEcho(pathPatterns),
                    ["excludeTests"] = excludeTests,
                    ["results"] = new JsonArray()
                };
                AddSearchStabilityMetadata(payload, reader, cursor, results);
                AddFtsQueryDiagnostics(payload, ftsDiagnostics);
                AddResultEnvelope(payload, 0, 0, truncated: false);
                if (suggestExactSubstring)
                {
                    AddExactSubstringRecoveryHint(payload, query);
                }
                else
                {
                    AddRecoveryHint(
                        payload,
                        "no_results",
                        "search returned no rows; try removing lang/path filters, using prefix for token-prefix matches, or using exactSubstring for literal punctuation or emoji.",
                        "search",
                        new JsonObject { ["query"] = query, ["limit"] = 5 });
                }
                AddFreshnessHint(payload, reader);
                return CreateToolResult(id, "No results found.", payload);
            }

            var structured = new JsonObject
            {
                ["query"] = query,
                ["rawQuery"] = rawQuery,
                ["cursor"] = cursorValue,
                ["snippetLines"] = snippetLines,
                ["maxLineWidth"] = maxLineWidth,
                ["path"] = PathEcho(pathPatterns),
                ["excludeTests"] = excludeTests,
                ["results"] = ToJsonArray(SearchSnippetFormatter.ToCompactResults(results, query, snippetLines, exact, maxLineWidth, exposeLiteralHighlights: exact))
            };
            AddSearchStabilityMetadata(structured, reader, cursor, results);
            AddResultEnvelope(structured, results.Count, truncated ? null : results.Count, truncated);
            if (format == "compact")
                ApplyCompactResults(structured, results, result => result.Path, result => result.StartLine);
            var topResult = results[0];
            AddNextStepSuggestion(
                structured,
                "excerpt",
                BuildExcerptArgs(topResult.Path, topResult.StartLine, topResult.EndLine));
            if (suggestExactSubstring)
                AddExactSubstringRecoveryHint(structured, query);
            // Include top file paths in summary for quick AI orientation
            // AIが素早く位置把握できるよう、サマリにトップファイルパスを含める
            var topPaths = results.Select(r => r.Path).Distinct().Take(3);
            var summary = $"Found {results.Count} search result(s) in {string.Join(", ", topPaths)}.";
            return CreateToolResult(id, summary, structured);
        });
    }

    private JsonNode ExecuteSymbols(JsonNode? id, JsonNode? args)
    {
        var query = args?["query"]?.GetValue<string>();
        if (query != null && query.Length > QueryLimits.MaxQueryLength)
            return CreateToolErrorResponse(id, QueryLimits.FormatQueryTooLongError());

        // Validate the raw `names` node before normalization so we can distinguish "property absent"
        // from "property present but malformed/empty". ReadStringList alone silently drops both
        // non-array shapes and blank entries, which would let invalid input fall through as an
        // unfiltered full symbol dump.
        // 生の `names` ノードを先に検証し、「未指定」と「指定ありだが不正/空」を区別する。
        // ReadStringList は非配列や空文字列を暗黙に無視するため、不正入力が無条件の全件検索に落ちるのを防ぐ。
        var namesNode = args?["names"];
        var namesProvided = namesNode is not null;
        if (namesProvided && namesNode is not JsonArray)
            return CreateToolErrorResponse(id, "'names' must be an array of strings.");
        var names = ReadStringList(args, "names");
        foreach (var n in names)
        {
            if (n.Length > QueryLimits.MaxQueryLength)
                return CreateToolErrorResponse(id, $"names entry too long (max {QueryLimits.MaxQueryLength} characters)");
        }
        if (namesProvided && names.Count == 0)
            return CreateToolErrorResponse(id, "'names' is present but contains no usable entries (all were empty or whitespace).");
        var kind = args?["kind"]?.GetValue<string>()?.ToLowerInvariant();
        var lang = QueryCommandRunner.NormalizeLangFilterValue(args?["lang"]?.GetValue<string>());
        var limit = ClampLimit(args?["limit"]?.GetValue<int>() ?? QueryCommandRunner.DefaultQueryLimit);
        if (TryGetValidatedMaxLineWidth(id, args, out var maxLineWidth) is JsonNode maxLineWidthError)
            return maxLineWidthError;
        var pathPatterns = ReadScopedPathList(args);
        var excludePaths = ReadStringList(args, "excludePaths");
        var excludeTests = args?["excludeTests"]?.GetValue<bool>() ?? false;
        var sinceStr = args?["since"]?.GetValue<string>();
        DateTime? since = null;
        if (sinceStr != null && QueryCommandRunner.TryParseIso8601Since(sinceStr, out var parsedSince))
            since = parsedSince;
        if (!TryResolveNameExactArgument(args, "symbols", out var exact, out var exactError))
            return CreateToolErrorResponse(id, exactError!);

        // Merge query + names into a de-duplicated OR list. `|` is treated as a literal name character
        // so operator symbols (e.g. `operator |`) stay searchable; multi-name must use repeated `names[]`.
        // query と names を結合して重複排除。`|` は名前文字として扱い、`operator |` などを検索可能にする。
        var rawInputs = new List<string>();
        if (query != null)
            rawInputs.Add(query);
        rawInputs.AddRange(names);
        var hadExplicitNameInput = rawInputs.Count > 0;
        var queriesForSearch = rawInputs.Where(s => !string.IsNullOrEmpty(s)).Distinct().ToList();
        if (hadExplicitNameInput && queriesForSearch.Count == 0)
            return CreateToolErrorResponse(id, "Symbol name list is empty after normalization. Check for empty 'names' entries or bare '|' separators.");
        if (queriesForSearch.Count > QueryCommandRunner.MaxSymbolQueryNames)
            return CreateToolErrorResponse(id, $"Too many symbol names ({queriesForSearch.Count}); maximum is {QueryCommandRunner.MaxSymbolQueryNames}. Split the request into smaller batches.");
        IReadOnlyList<string>? effectiveQueries = queriesForSearch.Count == 0 ? null : queriesForSearch;

        return WithDbReader(id, args, reader =>
        {
            var results = reader.SearchSymbols(effectiveQueries, limit, kind, lang, pathPatterns, excludePaths, excludeTests, since, exact);
            var hasExactPredicate = exact && effectiveQueries is { Count: > 0 };
            var exactSignal = reader.GetSymbolsExactQuerySignal(lang, pathPatterns, excludePaths, excludeTests, since);
            var multiNameExactHint = effectiveQueries != null && effectiveQueries.Count > 1;
            var exactZeroHint = multiNameExactHint
                ? QueryCommandRunner.BuildExactZeroHint(
                    exact,
                    () => reader.AnySearchSymbols(effectiveQueries, kind, lang, pathPatterns, excludePaths, excludeTests, since, exact: false),
                    () => reader.SearchSymbols(effectiveQueries, Math.Min(limit, QueryCommandRunner.ExactZeroHintSampleLimit), kind, lang, pathPatterns, excludePaths, excludeTests, since, exact: false),
                    r => r.Name)
                : QueryCommandRunner.BuildExactZeroHint(
                    exact && effectiveQueries != null && effectiveQueries.Count > 0,
                    () => reader.CountSearchSymbols(effectiveQueries, QueryCommandRunner.ExactZeroHintProbeLimit, kind, lang, pathPatterns, excludePaths, excludeTests, since, exact: false) > 0,
                    () => reader.CountSearchSymbols(effectiveQueries, limit, kind, lang, pathPatterns, excludePaths, excludeTests, since, exact: false),
                    () => reader.SearchSymbols(effectiveQueries, Math.Min(limit, QueryCommandRunner.ExactZeroHintSampleLimit), kind, lang, pathPatterns, excludePaths, excludeTests, since, exact: false),
                    r => r.Name);
            JsonNode? namesEcho = effectiveQueries == null ? null : JsonSerializer.SerializeToNode(effectiveQueries, _jsonOptions);
            if (results.Count == 0)
            {
                var payload = new JsonObject
                {
                    ["query"] = query,
                    ["names"] = namesEcho,
                    ["kind"] = kind,
                    ["lang"] = lang,
                    ["path"] = PathEcho(pathPatterns),
                    ["excludeTests"] = excludeTests,
                    ["count"] = 0,
                    ["results"] = new JsonArray()
                };
                if (hasExactPredicate)
                    AddExactGraphSignal(payload, exactSignal);
                AddExactZeroHint(payload, exactZeroHint);
                AddFreshnessHint(payload, reader);
                return CreateToolResult(id, "No symbols found.", payload);
            }

            var structured = new JsonObject
            {
                ["query"] = query,
                ["names"] = namesEcho,
                ["kind"] = kind,
                ["lang"] = lang,
                ["path"] = PathEcho(pathPatterns),
                ["excludeTests"] = excludeTests,
                ["count"] = results.Count,
                ["results"] = ToJsonArray(results)
            };
            if (hasExactPredicate)
                AddExactGraphSignal(structured, exactSignal);
            return CreateToolResult(id, ConsoleUi.FoundSummary(results.Count, "symbol"), structured);
        });
    }

    private JsonNode ExecuteDefinition(JsonNode? id, JsonNode? args)
    {
        if (!TryReadRequiredStringParameter(args, "query", out var query, out var requiredError))
            return CreateToolErrorResponse(id, requiredError!);
        if (query.Length > QueryLimits.MaxQueryLength)
            return CreateToolErrorResponse(id, QueryLimits.FormatQueryTooLongError());
        if (IsBareVerbatimQueryToken(query))
            return CreateToolErrorResponse(id, "Add a real symbol name after the command; bare verbatim prefixes like `@` are not valid queries.");

        var kind = args?["kind"]?.GetValue<string>()?.ToLowerInvariant();
        var lang = QueryCommandRunner.NormalizeLangFilterValue(args?["lang"]?.GetValue<string>());
        var limit = ClampLimit(args?["limit"]?.GetValue<int>() ?? QueryCommandRunner.DefaultQueryLimit);
        var includeBody = args?["includeBody"]?.GetValue<bool>() ?? false;
        var lspCompatible = args?["lsp_compatible"]?.GetValue<bool>() ?? false;
        var pathPatterns = ReadScopedPathList(args);
        var excludePaths = ReadStringList(args, "excludePaths");
        var excludeTests = args?["excludeTests"]?.GetValue<bool>() ?? false;
        var sinceStr = args?["since"]?.GetValue<string>();
        DateTime? since = null;
        if (sinceStr != null && QueryCommandRunner.TryParseIso8601Since(sinceStr, out var parsedDefSince))
            since = parsedDefSince;
        if (!TryResolveNameExactArgument(args, "definition", out var exact, out var exactError))
            return CreateToolErrorResponse(id, exactError!);
        var format = ReadResponseFormat(args);
        if (ValidateResponseFormat(format) is string formatError)
            return CreateToolErrorResponse(id, formatError);

        return WithDbReader(id, args, reader =>
        {
            var results = reader.GetDefinitions(query, FetchLimitForEnvelope(limit), kind, lang, includeBody, pathPatterns, excludePaths, excludeTests, since, exact);
            var truncated = TrimToRequestedLimit(results, limit);
            if (format == "count")
            {
                var total = truncated
                    ? reader.CountDefinitionsTotal(query, kind, lang, pathPatterns, excludePaths, excludeTests, since, exact).Count
                    : results.Count;
                var countPayload = BuildCountOnlyPayload(total, total, truncated: false, results, result => result.Path);
                countPayload["query"] = query;
                countPayload["kind"] = kind;
                countPayload["lang"] = lang;
                countPayload["path"] = PathEcho(pathPatterns);
                countPayload["excludeTests"] = excludeTests;
                return CreateToolResult(id, $"Counted {ConsoleUi.Counted(total, "definition")}.", countPayload);
            }
            if (lspCompatible)
                QueryCommandRunner.AttachLspLocations(results);
            var exactSignal = reader.GetDefinitionExactQuerySignal(lang, pathPatterns, excludePaths, excludeTests, since);
            var exactZeroHint = QueryCommandRunner.BuildExactZeroHint(
                exact,
                () => reader.CountSearchSymbols(query, QueryCommandRunner.ExactZeroHintProbeLimit, kind, lang, pathPatterns, excludePaths, excludeTests, since, exact: false) > 0,
                () => reader.CountSearchSymbols(query, limit, kind, lang, pathPatterns, excludePaths, excludeTests, since, exact: false),
                () => reader.SearchSymbols(query, Math.Min(limit, QueryCommandRunner.ExactZeroHintSampleLimit), kind, lang, pathPatterns, excludePaths, excludeTests, since, exact: false),
                r => r.Name);
            var payload = new JsonObject
            {
                ["query"] = query,
                ["kind"] = kind,
                ["lang"] = lang,
                ["includeBody"] = includeBody,
                ["lspCompatible"] = lspCompatible,
                ["path"] = PathEcho(pathPatterns),
                ["excludeTests"] = excludeTests,
                ["results"] = ToJsonArray(results)
            };
            AddResultEnvelope(payload, results.Count, truncated ? null : results.Count, truncated);
            if (format == "compact")
                ApplyCompactResults(payload, results, result => result.Path, result => result.StartLine);
            if (exact)
                AddExactGraphSignal(payload, exactSignal);
            if (results.Count == 0)
            {
                AddExactZeroHint(payload, exactZeroHint);
                AddSymbolRecoveryHint(payload, query, "definition", lang, kind, PathEcho(pathPatterns));
                AddFreshnessHint(payload, reader);
            }
            return CreateToolResult(id,
                ConsoleUi.FoundSummary(results.Count, "definition"),
                payload);
        });
    }

    private JsonNode ExecuteReferences(JsonNode? id, JsonNode? args)
    {
        if (!TryReadRequiredStringParameter(args, "query", out var query, out var requiredError))
            return CreateToolErrorResponse(id, requiredError!);
        if (query.Length > QueryLimits.MaxQueryLength)
            return CreateToolErrorResponse(id, QueryLimits.FormatQueryTooLongError());
        if (IsBareVerbatimQueryToken(query))
            return CreateToolErrorResponse(id, "Add a real symbol name after the command; bare verbatim prefixes like `@` are not valid queries.");

        var kind = args?["kind"]?.GetValue<string>()?.ToLowerInvariant();
        var lang = QueryCommandRunner.NormalizeLangFilterValue(args?["lang"]?.GetValue<string>());
        var limit = ClampLimit(args?["limit"]?.GetValue<int>() ?? QueryCommandRunner.DefaultQueryLimit);
        var lspCompatible = args?["lsp_compatible"]?.GetValue<bool>() ?? false;
        var offset = ReadOffset(args);
        if (TryGetValidatedMaxLineWidth(id, args, out var maxLineWidth) is JsonNode maxLineWidthError)
            return maxLineWidthError;
        var pathPatterns = ReadScopedPathList(args);
        var excludePaths = ReadStringList(args, "excludePaths");
        var excludeTests = args?["excludeTests"]?.GetValue<bool>() ?? false;
        var format = ReadResponseFormat(args);
        if (ValidateResponseFormat(format) is string formatError)
            return CreateToolErrorResponse(id, formatError);
        var countOnly = ReadCountOnly(args) || format == "count";
        if (!TryResolveNameExactArgument(args, "references", out var exact, out var exactError))
            return CreateToolErrorResponse(id, exactError!);

        return WithDbReader(id, args, reader =>
        {
            if (countOnly)
            {
                var countOnlyTotal = reader.CountSearchReferences(query, int.MaxValue, lang, kind, pathPatterns, excludePaths, excludeTests, exact);
                var histogramResults = countOnlyTotal > 0
                    ? reader.SearchReferences(query, Math.Min(countOnlyTotal, MaxLimit), lang, kind, pathPatterns, excludePaths, excludeTests, exact, maxLineWidth)
                    : [];
                var countOnlyPayload = BuildCountOnlyPayload(countOnlyTotal, countOnlyTotal, truncated: false, histogramResults, result => result.Path);
                countOnlyPayload["query"] = query;
                countOnlyPayload["kind"] = kind;
                countOnlyPayload["lang"] = lang;
                countOnlyPayload["path"] = PathEcho(pathPatterns);
                countOnlyPayload["excludeTests"] = excludeTests;
                return CreateToolResult(id, $"Counted {ConsoleUi.Counted(countOnlyTotal, "reference")}.", countOnlyPayload);
            }

            var results = reader.SearchReferences(query, FetchLimitForEnvelope(limit), lang, kind, pathPatterns, excludePaths, excludeTests, exact, maxLineWidth, offset: offset);
            var truncated = TrimToRequestedLimit(results, limit);
            var total = truncated || offset > 0
                ? reader.CountSearchReferences(query, int.MaxValue, lang, kind, pathPatterns, excludePaths, excludeTests, exact)
                : results.Count;
            if (lspCompatible)
                QueryCommandRunner.AttachLspLocations(results);
            var graphSupport = ResolveGraphSupport(reader, exact, query, lang, pathPatterns, excludePaths, excludeTests);
            var sqlGraphSignal = QueryCommandRunner.NarrowSqlGraphContractSignalByLanguages(
                reader.GetSqlGraphContractSignal(lang, pathPatterns, excludePaths, excludeTests),
                results.Select(result => result.Lang),
                lang,
                graphSupport.GraphLanguage);
            var exactSignal = reader.GetReferencesExactQuerySignal(lang, pathPatterns, excludePaths, excludeTests, includeSqlGraphContractSignal: sqlGraphSignal.Relevant);
            var exactZeroHint = QueryCommandRunner.BuildExactZeroHint(
                exact && reader._hasReferencesTable,
                () => reader.CountSearchReferences(query, QueryCommandRunner.ExactZeroHintProbeLimit, lang, kind, pathPatterns, excludePaths, excludeTests, exact: false) > 0,
                () => reader.CountSearchReferences(query, limit, lang, kind, pathPatterns, excludePaths, excludeTests, exact: false),
                () => reader.SearchReferences(query, Math.Min(limit, QueryCommandRunner.ExactZeroHintSampleLimit), lang, kind, pathPatterns, excludePaths, excludeTests, exact: false),
                r => r.SymbolName);
            var payload = new JsonObject
            {
                ["query"] = query,
                ["kind"] = kind,
                ["lang"] = lang,
                ["lspCompatible"] = lspCompatible,
                ["maxLineWidth"] = maxLineWidth,
                ["path"] = PathEcho(pathPatterns),
                ["excludeTests"] = excludeTests,
                ["graph_language"] = graphSupport.GraphLanguage,
                ["graph_supported"] = graphSupport.GraphSupported,
                ["graph_support_reason"] = graphSupport.GraphSupportReason,
                ["results"] = ToJsonArray(results)
            };
            AddPaginatedResultEnvelope(payload, results.Count, total, truncated, offset);
            if (format == "compact")
                ApplyCompactResults(payload, results, result => result.Path, result => result.Line, result => result.Column);
            if (exact)
                AddExactGraphSignal(payload, exactSignal);
            AddSqlGraphContractSignal(payload, sqlGraphSignal);
            if (results.Count == 0)
            {
                AddExactZeroHint(payload, exactZeroHint);
                AddSymbolRecoveryHint(payload, query, "references", lang, kind, PathEcho(pathPatterns));
                AddFreshnessHint(payload, reader);
            }
            else
            {
                var topReference = results[0];
                AddNextStepSuggestion(
                    payload,
                    "excerpt",
                    BuildExcerptArgs(topReference.Path, topReference.Line, topReference.Line));
            }
            return CreateToolResult(id,
                BuildGraphSummary("reference", "references", results.Count, graphSupport.GraphLanguage, graphSupport.GraphSupported, graphSupport.GraphSupportReason),
                payload);
        });
    }

    private JsonNode ExecuteCallers(JsonNode? id, JsonNode? args)
    {
        if (!TryReadRequiredStringParameter(args, "query", out var query, out var requiredError))
            return CreateToolErrorResponse(id, requiredError!);
        if (query.Length > QueryLimits.MaxQueryLength)
            return CreateToolErrorResponse(id, QueryLimits.FormatQueryTooLongError());
        if (IsBareVerbatimQueryToken(query))
            return CreateToolErrorResponse(id, "Add a real symbol name after the command; bare verbatim prefixes like `@` are not valid queries.");

        var kind = args?["kind"]?.GetValue<string>()?.ToLowerInvariant();
        if (IsNonCallGraphReferenceKind(kind))
            return CreateToolErrorResponse(id, BuildNonCallGraphKindRejectionMessage("callers", kind!));
        var lang = QueryCommandRunner.NormalizeLangFilterValue(args?["lang"]?.GetValue<string>());
        var limit = ClampLimit(args?["limit"]?.GetValue<int>() ?? QueryCommandRunner.DefaultQueryLimit);
        var offset = ReadOffset(args);
        var pathPatterns = ReadScopedPathList(args);
        var excludePaths = ReadStringList(args, "excludePaths");
        var excludeTests = args?["excludeTests"]?.GetValue<bool>() ?? false;
        if (!TryResolveNameExactArgument(args, "callers", out var exact, out var exactError))
            return CreateToolErrorResponse(id, exactError!);
        if (!TryReadReferenceRankMode(args, out var rankMode, out var rankModeError))
            return CreateToolErrorResponse(id, rankModeError!);
        var format = ReadResponseFormat(args);
        if (ValidateResponseFormat(format) is string formatError)
            return CreateToolErrorResponse(id, formatError);
        var countOnly = ReadCountOnly(args) || format == "count";

        return WithDbReader(id, args, reader =>
        {
            if (countOnly)
            {
                var countOnlyTotal = reader.CountCallers(query, int.MaxValue, lang, kind, pathPatterns, excludePaths, excludeTests, exact);
                var histogramResults = countOnlyTotal > 0
                    ? reader.GetCallers(query, Math.Min(countOnlyTotal, MaxLimit), lang, kind, pathPatterns, excludePaths, excludeTests, exact, rankMode: rankMode)
                    : [];
                var countOnlyPayload = BuildCountOnlyPayload(countOnlyTotal, countOnlyTotal, truncated: false, histogramResults, result => result.Path);
                countOnlyPayload["query"] = query;
                countOnlyPayload["kind"] = kind;
                countOnlyPayload["lang"] = lang;
                countOnlyPayload["path"] = PathEcho(pathPatterns);
                countOnlyPayload["excludeTests"] = excludeTests;
                return CreateToolResult(id, $"Counted {ConsoleUi.Counted(countOnlyTotal, "caller")}.", countOnlyPayload);
            }

            var results = reader.GetCallers(query, FetchLimitForEnvelope(limit), lang, kind, pathPatterns, excludePaths, excludeTests, exact, rankMode: rankMode, offset: offset);
            var truncated = TrimToRequestedLimit(results, limit);
            var total = truncated || offset > 0
                ? reader.CountCallers(query, int.MaxValue, lang, kind, pathPatterns, excludePaths, excludeTests, exact)
                : results.Count;
            var graphSupport = ResolveGraphSupport(reader, exact, query, lang, pathPatterns, excludePaths, excludeTests);
            var sqlGraphSignal = QueryCommandRunner.NarrowSqlGraphContractSignalByLanguages(
                reader.GetSqlGraphContractSignal(lang, pathPatterns, excludePaths, excludeTests),
                results.Select(result => result.Lang),
                lang,
                graphSupport.GraphLanguage);
            var exactSignal = reader.GetCallersExactQuerySignal(lang, pathPatterns, excludePaths, excludeTests, includeSqlGraphContractSignal: sqlGraphSignal.Relevant);
            var exactZeroHint = QueryCommandRunner.BuildExactZeroHint(
                exact && reader._hasReferencesTable,
                () => reader.CountCallers(query, QueryCommandRunner.ExactZeroHintProbeLimit, lang, kind, pathPatterns, excludePaths, excludeTests, exact: false) > 0,
                () => reader.CountCallers(query, limit, lang, kind, pathPatterns, excludePaths, excludeTests, exact: false),
                () => reader.GetCallers(query, Math.Min(limit, QueryCommandRunner.ExactZeroHintSampleLimit), lang, kind, pathPatterns, excludePaths, excludeTests, exact: false, rankMode: rankMode),
                r => r.CalleeName);
            var payload = new JsonObject
            {
                ["query"] = query,
                ["kind"] = kind,
                ["lang"] = lang,
                ["path"] = PathEcho(pathPatterns),
                ["excludeTests"] = excludeTests,
                ["rankBy"] = QueryCommandRunner.FormatReferenceRankMode(rankMode),
                ["graph_language"] = graphSupport.GraphLanguage,
                ["graph_supported"] = graphSupport.GraphSupported,
                ["graph_support_reason"] = graphSupport.GraphSupportReason,
                ["results"] = ToJsonArray(results)
            };
            AddPaginatedResultEnvelope(payload, results.Count, total, truncated, offset);
            if (format == "compact")
                ApplyCompactResults(payload, results, result => result.Path, result => result.FirstLine);
            payload["aggregate_truncated"] = results.Any(result => result.AggregateTruncated);
            if (exact)
                AddExactGraphSignal(payload, exactSignal);
            AddSqlGraphContractSignal(payload, sqlGraphSignal);
            if (results.Count == 0)
            {
                AddExactZeroHint(payload, exactZeroHint);
                AddSymbolRecoveryHint(payload, query, "callers", lang, kind, PathEcho(pathPatterns));
                AddFreshnessHint(payload, reader);
            }
            return CreateToolResult(id,
                BuildGraphSummary("caller", "callers", results.Count, graphSupport.GraphLanguage, graphSupport.GraphSupported, graphSupport.GraphSupportReason),
                payload);
        });
    }

    private JsonNode ExecuteCallees(JsonNode? id, JsonNode? args)
    {
        if (!TryReadRequiredStringParameter(args, "query", out var query, out var requiredError))
            return CreateToolErrorResponse(id, requiredError!);
        if (query.Length > QueryLimits.MaxQueryLength)
            return CreateToolErrorResponse(id, QueryLimits.FormatQueryTooLongError());
        if (IsBareVerbatimQueryToken(query))
            return CreateToolErrorResponse(id, "Add a real symbol name after the command; bare verbatim prefixes like `@` are not valid queries.");

        var kind = args?["kind"]?.GetValue<string>()?.ToLowerInvariant();
        if (IsNonCallGraphReferenceKind(kind))
            return CreateToolErrorResponse(id, BuildNonCallGraphKindRejectionMessage("callees", kind!));
        var lang = QueryCommandRunner.NormalizeLangFilterValue(args?["lang"]?.GetValue<string>());
        var limit = ClampLimit(args?["limit"]?.GetValue<int>() ?? QueryCommandRunner.DefaultQueryLimit);
        var offset = ReadOffset(args);
        var pathPatterns = ReadScopedPathList(args);
        var excludePaths = ReadStringList(args, "excludePaths");
        var excludeTests = args?["excludeTests"]?.GetValue<bool>() ?? false;
        if (!TryResolveNameExactArgument(args, "callees", out var exact, out var exactError))
            return CreateToolErrorResponse(id, exactError!);
        if (!TryReadReferenceRankMode(args, out var rankMode, out var rankModeError))
            return CreateToolErrorResponse(id, rankModeError!);
        var format = ReadResponseFormat(args);
        if (ValidateResponseFormat(format) is string formatError)
            return CreateToolErrorResponse(id, formatError);
        var countOnly = ReadCountOnly(args) || format == "count";

        return WithDbReader(id, args, reader =>
        {
            if (countOnly)
            {
                var countOnlyTotal = reader.CountCallees(query, int.MaxValue, lang, kind, pathPatterns, excludePaths, excludeTests, exact);
                var histogramResults = countOnlyTotal > 0
                    ? reader.GetCallees(query, Math.Min(countOnlyTotal, MaxLimit), lang, kind, pathPatterns, excludePaths, excludeTests, exact, rankMode: rankMode)
                    : [];
                var countOnlyPayload = BuildCountOnlyPayload(countOnlyTotal, countOnlyTotal, truncated: false, histogramResults, result => result.Path);
                countOnlyPayload["query"] = query;
                countOnlyPayload["kind"] = kind;
                countOnlyPayload["lang"] = lang;
                countOnlyPayload["path"] = PathEcho(pathPatterns);
                countOnlyPayload["excludeTests"] = excludeTests;
                return CreateToolResult(id, $"Counted {ConsoleUi.Counted(countOnlyTotal, "callee")}.", countOnlyPayload);
            }

            var results = reader.GetCallees(query, FetchLimitForEnvelope(limit), lang, kind, pathPatterns, excludePaths, excludeTests, exact, rankMode: rankMode, offset: offset);
            var truncated = TrimToRequestedLimit(results, limit);
            var total = truncated || offset > 0
                ? reader.CountCallees(query, int.MaxValue, lang, kind, pathPatterns, excludePaths, excludeTests, exact)
                : results.Count;
            var graphSupport = ResolveGraphSupport(reader, exact, query, lang, pathPatterns, excludePaths, excludeTests);
            var sqlGraphSignal = QueryCommandRunner.NarrowSqlGraphContractSignalByLanguages(
                reader.GetSqlGraphContractSignal(lang, pathPatterns, excludePaths, excludeTests),
                results.Select(result => result.Lang),
                lang,
                graphSupport.GraphLanguage);
            var exactSignal = reader.GetCalleesExactQuerySignal(lang, pathPatterns, excludePaths, excludeTests, includeSqlGraphContractSignal: sqlGraphSignal.Relevant);
            var exactZeroHint = QueryCommandRunner.BuildExactZeroHint(
                exact && reader._hasReferencesTable,
                () => reader.CountCallees(query, QueryCommandRunner.ExactZeroHintProbeLimit, lang, kind, pathPatterns, excludePaths, excludeTests, exact: false) > 0,
                () => reader.CountCallees(query, limit, lang, kind, pathPatterns, excludePaths, excludeTests, exact: false),
                () => reader.GetCallees(query, Math.Min(limit, QueryCommandRunner.ExactZeroHintSampleLimit), lang, kind, pathPatterns, excludePaths, excludeTests, exact: false, rankMode: rankMode),
                r => r.CallerName);
            var payload = new JsonObject
            {
                ["query"] = query,
                ["kind"] = kind,
                ["lang"] = lang,
                ["path"] = PathEcho(pathPatterns),
                ["excludeTests"] = excludeTests,
                ["rankBy"] = QueryCommandRunner.FormatReferenceRankMode(rankMode),
                ["graph_language"] = graphSupport.GraphLanguage,
                ["graph_supported"] = graphSupport.GraphSupported,
                ["graph_support_reason"] = graphSupport.GraphSupportReason,
                ["results"] = ToJsonArray(results)
            };
            AddPaginatedResultEnvelope(payload, results.Count, total, truncated, offset);
            if (format == "compact")
                ApplyCompactResults(payload, results, result => result.Path, result => result.FirstLine);
            payload["aggregate_truncated"] = results.Any(result => result.AggregateTruncated);
            if (exact)
                AddExactGraphSignal(payload, exactSignal);
            AddSqlGraphContractSignal(payload, sqlGraphSignal);
            if (results.Count == 0)
            {
                AddExactZeroHint(payload, exactZeroHint);
                AddSymbolRecoveryHint(payload, query, "callees", lang, kind, PathEcho(pathPatterns));
                AddFreshnessHint(payload, reader);
            }
            return CreateToolResult(id,
                BuildGraphSummary("callee", "callees", results.Count, graphSupport.GraphLanguage, graphSupport.GraphSupported, graphSupport.GraphSupportReason),
                payload);
        });
    }

    private JsonNode ExecuteFiles(JsonNode? id, JsonNode? args)
    {
        var query = args?["query"]?.GetValue<string>();
        if (query != null && query.Length > QueryLimits.MaxQueryLength)
            return CreateToolErrorResponse(id, QueryLimits.FormatQueryTooLongError());
        var lang = QueryCommandRunner.NormalizeLangFilterValue(args?["lang"]?.GetValue<string>());
        var limit = ClampLimit(args?["limit"]?.GetValue<int>() ?? QueryCommandRunner.DefaultQueryLimit);
        var pathPatterns = ReadScopedPathList(args);
        var excludePaths = ReadStringList(args, "excludePaths");
        var excludeTests = args?["excludeTests"]?.GetValue<bool>() ?? false;
        var sinceStr = args?["since"]?.GetValue<string>();
        DateTime? since = null;
        if (sinceStr != null)
        {
            if (QueryCommandRunner.TryParseIso8601Since(sinceStr, out var parsedSince))
                since = parsedSince;
            else
                return CreateToolErrorResponse(id, $"Invalid 'since' timestamp: '{sinceStr}'. Use ISO 8601 format (e.g. 2024-01-01 or 2024-01-01T00:00:00Z).");
        }

        return WithDbReader(id, args, reader =>
        {
            var results = reader.ListFiles(query, limit, lang, pathPatterns, excludePaths, excludeTests, since);
            if (results.Count == 0)
            {
                var payload = new JsonObject
                {
                    ["query"] = query,
                    ["lang"] = lang,
                    ["path"] = PathEcho(pathPatterns),
                    ["excludeTests"] = excludeTests,
                    ["count"] = 0,
                    ["results"] = new JsonArray()
                };
                AddFreshnessHint(payload, reader);
                return CreateToolResult(id, "No files found.", payload);
            }

            var structured = new JsonObject
            {
                ["query"] = query,
                ["lang"] = lang,
                ["path"] = PathEcho(pathPatterns),
                ["excludeTests"] = excludeTests,
                ["count"] = results.Count,
                ["results"] = JsonSerializer.SerializeToNode(results, _jsonOptions)
            };
            return CreateToolResult(id, ConsoleUi.FoundSummary(results.Count, "file"), structured);
        });
    }

    private JsonNode ExecuteMap(JsonNode? id, JsonNode? args)
    {
        var lang = args?["lang"]?.GetValue<string>()?.ToLowerInvariant();
        var limit = ClampLimit(args?["limit"]?.GetValue<int>() ?? QueryCommandRunner.DefaultMapLimit);
        var pathPatterns = ReadScopedPathList(args);
        var excludePaths = ReadStringList(args, "excludePaths");
        var excludeTests = args?["excludeTests"]?.GetValue<bool>() ?? false;
        var sections = ReadStringList(args, "sections").Select(section => section.ToLowerInvariant()).ToHashSet(StringComparer.Ordinal);
        var depth = args?["depth"]?.GetValue<int>();

        return WithDbReader(id, args, reader =>
        {
            var map = reader.GetRepoMap(limit, lang, pathPatterns, excludePaths, excludeTests);
            WorkspaceMetadataEnricher.Enrich(map, _dbPath, _dbPathExplicit);
            var structured = JsonSerializer.SerializeToNode(map, _jsonOptions)!.AsObject();
            if (depth is >= 0)
            {
                var modules = structured["modules"] as JsonArray;
                if (modules != null)
                {
                    var kept = new JsonArray(modules
                        .Where(node =>
                        {
                            var module = node?["module"]?.GetValue<string>() ?? string.Empty;
                            return module.Split('/', StringSplitOptions.RemoveEmptyEntries).Length <= depth.Value;
                        })
                        .Select(node => node!.DeepClone())
                        .ToArray());
                    structured["modules"] = kept;
                }
                structured["depth"] = depth.Value;
            }
            if (sections.Count > 0)
                ApplyMapSectionFilter(structured, sections);
            structured["limit"] = limit;
            structured["lang"] = lang;
            structured["path"] = PathEcho(pathPatterns);
            structured["excludeTests"] = excludeTests;
            var hasFilter = (pathPatterns is { Count: > 0 }) || excludePaths.Count > 0 || excludeTests || lang != null;
            if (map.FileCount == 0 && hasFilter)
                AddFreshnessHint(structured, reader);
            var summary = map.FileCount > 0
                ? "Repo map returned."
                : hasFilter ? "No files found matching the given filters." : "Repo map returned.";
            return CreateToolResult(id, summary, structured);
        });
    }

    private static void ApplyMapSectionFilter(JsonObject structured, IReadOnlySet<string> sections)
    {
        var keep = new HashSet<string>(StringComparer.Ordinal)
        {
            "api_version", "fileCount", "totalLines", "totalSymbols", "totalReferences",
            "indexedAt", "latestModified", "workspaceIndexedAt", "workspaceLatestModified",
            "projectRoot", "gitHead", "gitIsDirty", "indexed_head_commit", "worktree_head_changed",
            "graphTableAvailable", "limit", "lang", "path", "excludeTests", "depth",
        };
        if (sections.Contains("languages"))
            keep.Add("languages");
        if (sections.Contains("tree") || sections.Contains("modules"))
            keep.Add("modules");
        if (sections.Contains("hotspots"))
        {
            keep.Add("topFiles");
            keep.Add("symbolRichFiles");
            keep.Add("referenceRichFiles");
            keep.Add("entrypoints");
        }
        if (sections.Contains("metrics"))
            keep.Add("largestFiles");
        foreach (var key in structured.Select(property => property.Key).Where(key => !keep.Contains(key)).ToList())
            structured.Remove(key);
        structured["sections"] = new JsonArray(sections.Select(section => JsonValue.Create(section)).ToArray<JsonNode?>());
    }

    private JsonNode ExecuteAnalyzeSymbol(JsonNode? id, JsonNode? args)
    {
        if (!TryReadRequiredStringParameter(args, "query", out var query, out var requiredError))
            return CreateToolErrorResponse(id, requiredError!);
        if (query.Length > QueryLimits.MaxQueryLength)
            return CreateToolErrorResponse(id, QueryLimits.FormatQueryTooLongError());
        if (IsBareVerbatimQueryToken(query))
            return CreateToolErrorResponse(id, "Add a real symbol name after the command; bare verbatim prefixes like `@` are not valid queries.");

        var limit = ClampLimit(args?["limit"]?.GetValue<int>() ?? QueryCommandRunner.DefaultMapLimit);
        var lang = args?["lang"]?.GetValue<string>()?.ToLowerInvariant();
        var includeBody = args?["includeBody"]?.GetValue<bool>() ?? false;
        if (TryGetValidatedMaxLineWidth(id, args, out var maxLineWidth) is JsonNode maxLineWidthError)
            return maxLineWidthError;
        var pathPatterns = ReadScopedPathList(args);
        var excludePaths = ReadStringList(args, "excludePaths");
        var excludeTests = args?["excludeTests"]?.GetValue<bool>() ?? false;
        if (!TryResolveNameExactArgument(args, "analyze_symbol", out var exact, out var exactError))
            return CreateToolErrorResponse(id, exactError!);

        return WithDbReader(id, args, reader =>
        {
            var analysis = reader.AnalyzeSymbol(query, limit, lang, includeBody, pathPatterns, excludePaths, excludeTests, exact, maxLineWidth);
            var sqlGraphSignal = QueryCommandRunner.NarrowSqlGraphContractSignal(
                reader.GetSqlGraphContractSignal(lang, pathPatterns, excludePaths, excludeTests),
                DbReader.IsSqlLanguage(lang)
                    || DbReader.IsSqlLanguage(analysis.GraphLanguage)
                    || DbReader.IsSqlLanguage(analysis.File?.Lang)
                    || DbReader.ContainsSqlLanguage(analysis.Definitions.Select(definition => definition.Lang))
                    || DbReader.ContainsSqlLanguage(analysis.References.Select(reference => reference.Lang))
                    || DbReader.ContainsSqlLanguage(analysis.Callers.Select(caller => caller.Lang))
                    || DbReader.ContainsSqlLanguage(analysis.Callees.Select(callee => callee.Lang)));
            analysis.SqlGraphContractReady = sqlGraphSignal.Relevant ? sqlGraphSignal.Ready : null;
            analysis.SqlGraphContractDegradedReason = sqlGraphSignal.Relevant ? sqlGraphSignal.DegradedReason : null;
            WorkspaceMetadataEnricher.Enrich(analysis, _dbPath, _dbPathExplicit);
            var structured = ToAnalyzeSymbolJsonObject(analysis);
            AddSqlGraphContractSignal(structured, sqlGraphSignal);
            structured.Remove("exactZeroHint");
            AddExactZeroHint(structured, analysis.ExactZeroHint);
            structured["maxLineWidth"] = maxLineWidth;
            structured["lang"] = lang;
            structured["path"] = PathEcho(pathPatterns);
            structured["excludeTests"] = excludeTests;
            return CreateToolResult(id, BuildAnalyzeSymbolSummary(analysis), structured);
        });
    }

    private static string BuildAnalyzeSymbolSummary(SymbolAnalysisResult analysis)
    {
        if (analysis.ExactZeroHint != null)
        {
            var relaxedCount = analysis.ExactZeroHint.RelaxedCount ?? analysis.ExactZeroHint.SampleNames.Count;
            return $"Symbol analysis returned. Substring would return {ConsoleUi.Counted(relaxedCount, "similarly named symbol")}.";
        }

        return "Symbol analysis returned.";
    }

    private static void AddExactGraphSignal(JsonObject payload, ExactQuerySignal signal)
    {
        payload["exact_index_available"] = signal.ExactIndexAvailable;
        if (signal.DegradedReason != null)
            payload["degraded_reason"] = signal.DegradedReason;
        // MCP uses snake_case response keys consistently; do not add camelCase aliases here.
    }

    private static void AddSqlGraphContractSignal(JsonObject payload, SqlGraphContractSignal signal)
    {
        if (!signal.Relevant)
            return;

        payload["sql_graph_contract_ready"] = signal.Ready;
        if (!signal.Ready)
        {
            payload["degraded"] = true;
            if (signal.DegradedReason != null)
            {
                payload["sql_graph_contract_degraded_reason"] = signal.DegradedReason;
            }
        }
    }

    private static void AddExactSignalAliases(JsonObject payload)
    {
        if (payload["exact_index_available"] is JsonNode snakeExact && payload["exactIndexAvailable"] is null)
            payload["exactIndexAvailable"] = snakeExact.DeepClone();
        else if (payload["exactIndexAvailable"] is JsonNode camelExact && payload["exact_index_available"] is null)
            payload["exact_index_available"] = camelExact.DeepClone();

        if (payload["degraded_reason"] is JsonNode snakeReason && payload["degradedReason"] is null)
            payload["degradedReason"] = snakeReason.DeepClone();
        else if (payload["degradedReason"] is JsonNode camelReason && payload["degraded_reason"] is null)
            payload["degraded_reason"] = camelReason.DeepClone();
    }

    private static bool IsBareVerbatimQueryToken(string value)
    {
        var trimmed = value.Trim();
        return trimmed.Length > 0 && trimmed.All(ch => ch == '@');
    }

    private static bool IsPathWithinDirectory(string parentPath, string childPath)
    {
        var parent = NormalizeDirectoryBoundaryPath(ResolveExistingDirectoryPath(parentPath));
        var child = NormalizeDirectoryBoundaryPath(ResolveExistingDirectoryPath(childPath));

        return PathCasing.IsPathEqualOrParent(parent, child);
    }

    private static string ResolveExistingDirectoryPath(string path)
    {
        var fullPath = Path.GetFullPath(path);
        if (!Directory.Exists(fullPath))
            return fullPath;

        var root = Path.GetPathRoot(fullPath);
        if (string.IsNullOrEmpty(root))
            return fullPath;

        var current = root;
        var relativePath = Path.GetRelativePath(root, fullPath);
        if (relativePath == ".")
            return fullPath;

        foreach (var segment in relativePath.Split(Path.DirectorySeparatorChar, Path.AltDirectorySeparatorChar))
        {
            if (string.IsNullOrEmpty(segment) || segment == ".")
                continue;

            current = Path.Combine(current, segment);
            try
            {
                var target = new DirectoryInfo(current).ResolveLinkTarget(returnFinalTarget: true);
                if (target != null)
                    current = target.FullName;
            }
            catch (IOException)
            {
                return Path.GetFullPath(current);
            }
            catch (UnauthorizedAccessException)
            {
                return Path.GetFullPath(current);
            }
        }

        return Path.GetFullPath(current);
    }

    private static string NormalizeDirectoryBoundaryPath(string path)
    {
        var fullPath = Path.GetFullPath(path);
        var root = Path.GetPathRoot(fullPath);
        if (!string.IsNullOrEmpty(root) && string.Equals(fullPath, root, StringComparison.Ordinal))
            return fullPath;
        return fullPath.TrimEnd(Path.DirectorySeparatorChar, Path.AltDirectorySeparatorChar);
    }

    private static Dictionary<string, string?> GetHotspotFamilyMetaSnapshot(DbContext db, Func<string, string> keyFactory)
    {
        var values = new Dictionary<string, string?>(StringComparer.Ordinal);
        foreach (var lang in FileIndexer.GetHotspotFamilyMarkerLanguages())
            values[lang] = db.GetMetaString(keyFactory(lang));
        return values;
    }

    private static Dictionary<string, string?> GetHotspotFamilyMarkerFingerprints(FileIndexer indexer)
    {
        var values = new Dictionary<string, string?>(StringComparer.Ordinal);
        foreach (var lang in FileIndexer.GetHotspotFamilyMarkerLanguages())
            values[lang] = indexer.GetProjectMarkerFingerprint(lang);
        return values;
    }

    private static void RestampHotspotFamilyTrust(
        DbWriter writer,
        IReadOnlySet<string> reusedLanguages,
        IReadOnlyDictionary<string, string?> priorVersions,
        IReadOnlyDictionary<string, string?> priorFingerprints,
        IReadOnlyDictionary<string, string?> currentFingerprints)
    {
        var currentVersion = DbContext.HotspotFamilyVersion.ToString(System.Globalization.CultureInfo.InvariantCulture);
        foreach (var lang in FileIndexer.GetHotspotFamilyMarkerLanguages())
        {
            currentFingerprints.TryGetValue(lang, out var currentFingerprint);
            priorVersions.TryGetValue(lang, out var priorVersion);
            priorFingerprints.TryGetValue(lang, out var priorFingerprint);
            if (!reusedLanguages.Contains(lang) || (priorVersion == currentVersion && priorFingerprint == currentFingerprint))
                writer.MarkHotspotFamilyReady(lang, currentFingerprint);
        }
    }

    private static Dictionary<string, bool> GetHotspotFamilyTrustMatchesCurrent(
        IReadOnlyDictionary<string, string?> priorVersions,
        IReadOnlyDictionary<string, string?> priorFingerprints,
        IReadOnlyDictionary<string, string?> currentFingerprints)
    {
        var currentVersion = DbContext.HotspotFamilyVersion.ToString(System.Globalization.CultureInfo.InvariantCulture);
        var values = new Dictionary<string, bool>(StringComparer.Ordinal);
        foreach (var lang in FileIndexer.GetHotspotFamilyMarkerLanguages())
        {
            currentFingerprints.TryGetValue(lang, out var currentFingerprint);
            priorVersions.TryGetValue(lang, out var priorVersion);
            priorFingerprints.TryGetValue(lang, out var priorFingerprint);
            values[lang] = priorVersion == currentVersion && priorFingerprint == currentFingerprint;
        }

        return values;
    }

    private static bool AllowReuseWithCurrentHotspotFamilyTrust(
        string? lang,
        IReadOnlyDictionary<string, bool> hotspotFamilyTrustMatchesCurrent)
    {
        if (!FileIndexer.SupportsHotspotFamilyMarkerLanguage(lang))
            return true;

        return lang != null
            && hotspotFamilyTrustMatchesCurrent.TryGetValue(lang, out var matchesCurrent)
            && matchesCurrent;
    }

    private static void AddHotspotFamilySignal(JsonObject payload, HotspotFamilySignal signal)
    {
        payload["hotspot_family_ready"] = signal.Ready;
        if (!signal.Ready)
        {
            payload["degraded"] = true;
            if (signal.DegradedReason != null)
            {
                payload["hotspot_family_degraded_reason"] = signal.DegradedReason;
            }
        }
    }

    private JsonNode ExecuteStatus(JsonNode? id)
    {
        return WithDbReader(id, args: null, reader =>
        {
            var status = reader.GetStatus();
            WorkspaceMetadataEnricher.Enrich(status, _dbPath, _dbPathExplicit);
            status.MacProfile = MacProfileDetector.DetectCurrent();
            status.GraphSupportedLanguages = ReferenceExtractor.GetSupportedLanguages().OrderBy(l => l).ToList();
            ExtractorPluginRegistry.LoadPatternConfigsForProjectRoot(status.ProjectRoot);
            status.Extractors = ExtractorPluginRegistry.GetStatusSnapshot();
            using var postExtractionHookRunner = PostExtractionHookRunner.DiscoverDefault();
            var postExtractionHooks = postExtractionHookRunner.Hooks;
            if (postExtractionHooks.Count > 0)
            {
                status.Hooks = postExtractionHooks
                    .Select(hook => new PostExtractionHookStatus
                    {
                        Name = hook.Name,
                        AssemblyPath = hook.AssemblyPath,
                        TypeName = hook.TypeName,
                        CallbackBudgetMs = (long)Math.Round(postExtractionHookRunner.CallbackBudget.TotalMilliseconds, MidpointRounding.AwayFromZero),
                    })
                    .ToList();
            }
            status.Version = _version;
            if (!status.FoldReady)
            {
                status.DegradedReason = DegradationReasonCodes.BuildFoldNotReadyExplanation(status.FoldReadyReason);
                status.RecommendedAction = BuildFoldBackfillCommand(_dbPath, _dbPathExplicit);
                status.AlternativeAction = BuildFoldRebuildRepairCommand(status.ProjectRoot, _dbPath, _dbPathExplicit);
            }
            var structured = JsonSerializer.SerializeToNode(status, _jsonOptions)!.AsObject();
            structured["project_root"] = status.ProjectRoot;
            structured["git_head"] = status.GitHead;
            structured["git_is_dirty"] = status.GitIsDirty;
            structured.Remove("hotspotFamilyReady");
            structured.Remove("hotspotFamilyDegradedReason");
            structured["sql_graph_contract_ready"] = status.SqlGraphContractReady;
            if (status.SqlGraphContractDegradedReason != null)
                structured["sql_graph_contract_degraded_reason"] = status.SqlGraphContractDegradedReason;
            structured["mcp_session"] = BuildMcpSessionStatus();
            structured["mcp"] = new JsonObject
            {
                ["limits"] = new JsonObject
                {
                    ["max_request_characters"] = MaxLineCharacterCount,
                    ["max_request_bytes"] = MaxLineByteLength,
                    ["max_response_bytes"] = GetMaxResponseBytes(),
                    ["max_configured_response_bytes"] = MaxConfiguredResponseBytes,
                    ["batch_response_bytes"] = GetBatchQueryResponseByteLimit(),
                    ["max_batch_response_bytes"] = MaxBatchQueryResponseByteLimit,
                    ["max_pagination_offset"] = MaxMcpPaginationOffset,
                    ["max_json_depth"] = MaxJsonDepth,
                    ["max_batch_requests"] = MaxBatchRequestCount,
                    ["keep_alive_min_interval_s"] = MinKeepAliveIntervalSeconds,
                    ["keep_alive_max_interval_s"] = MaxKeepAliveIntervalSeconds,
                    ["rate_limit_max_rps"] = RateLimiterOptions.MaxRefillTokensPerSecond,
                    ["rate_limit_max_burst"] = RateLimiterOptions.MaxBurstCapacity,
                },
                ["rate_limit"] = new JsonObject
                {
                    ["enabled"] = RateLimiter.Options.IsEnabled,
                    ["rps"] = RateLimiter.Options.RefillTokensPerSecond,
                    ["burst"] = RateLimiter.Options.BurstCapacity,
                },
            };
            return CreateToolResult(id, "Database stats returned.", structured);
        });
    }

    private JsonObject BuildMcpSessionStatus()
    {
        var roots = new JsonArray();
        foreach (var root in _clientRoots)
            roots.Add(root?.DeepClone());

        var session = new JsonObject
        {
            ["log_level"] = _mcpLogLevel,
            ["roots"] = roots,
        };
        if (_clientName is not null || _clientVersion is not null)
        {
            var clientInfo = new JsonObject();
            if (_clientName is not null)
                clientInfo["name"] = _clientName;
            if (_clientVersion is not null)
                clientInfo["version"] = _clientVersion;
            session["client_info"] = clientInfo;
        }
        if (_clientCapabilities is not null)
            session["client_capabilities"] = _clientCapabilities.DeepClone();
        return session;
    }

    private static string BuildFoldBackfillCommand(string dbPath, bool dbPathExplicit)
    {
        if (!dbPathExplicit)
            return "cdidx backfill-fold";

        return $"cdidx backfill-fold --db {QuoteCommandArgument(ResolveWritableDbPathOrPlaceholder(dbPath))}";
    }

    private static string BuildFoldRebuildRepairCommand(string? projectRoot, string dbPath, bool dbPathExplicit)
    {
        if (!dbPathExplicit)
            return "cdidx index . --rebuild";

        var resolvedDbPath = ResolveWritableDbPathOrPlaceholder(dbPath);
        var targetProject = string.IsNullOrWhiteSpace(projectRoot)
            ? "<projectPath>"
            : QuoteCommandArgument(projectRoot);
        return $"cdidx index {targetProject} --db {QuoteCommandArgument(resolvedDbPath)} --rebuild";
    }

    private static string ResolveWritableDbPathOrPlaceholder(string dbPath)
        => DbPathResolver.TryResolveWritableMutationDbPath(dbPath, out var writableDbPath)
            ? writableDbPath
            : "<writable-db-path>";

    private static string QuoteCommandArgument(string value)
    {
        if (value.Length >= 2 && value[0] == '<' && value[^1] == '>')
            return value;

        var fullPath = DbPathResolver.NormalizeDbPath(value);
        if (!fullPath.StartsWith("file:", StringComparison.OrdinalIgnoreCase))
            fullPath = Path.GetFullPath(fullPath);

        return fullPath.IndexOfAny([' ', '\t', '"']) >= 0
            ? $"\"{fullPath.Replace("\"", "\\\"", StringComparison.Ordinal)}\""
            : fullPath;
    }

    private JsonNode ExecuteOutline(JsonNode? id, JsonNode? args)
    {
        if (!TryReadRequiredStringParameter(args, "path", out var path, out var requiredError))
            return CreateToolErrorResponse(id, requiredError!);

        return WithDbReader(id, args, reader =>
        {
            var outline = reader.GetOutline(path);
            if (outline == null)
            {
                var emptyPayload = new JsonObject
                {
                    ["path"] = path,
                    ["error"] = "file not found in index"
                };
                AddFreshnessHint(emptyPayload, reader);
                return CreateToolResult(id, "File not found in index.", emptyPayload);
            }

            var structured = JsonSerializer.SerializeToNode(outline, _jsonOptions)!.AsObject();
            return CreateToolResult(id, $"Outline: {ConsoleUi.Counted(outline.SymbolCount, "symbol")} in {ConsoleUi.Counted(outline.TotalLines, "line")}.", structured);
        });
    }

    private JsonNode ExecuteExcerpt(JsonNode? id, JsonNode? args)
    {
        if (!TryReadRequiredStringParameter(args, "path", out var path, out var requiredError))
            return CreateToolErrorResponse(id, requiredError!);

        var startLine = args?["startLine"]?.GetValue<int>();
        if (startLine == null || startLine <= 0)
            return CreateToolErrorResponse(id, "Missing or invalid required parameter: startLine");

        var endLine = args?["endLine"]?.GetValue<int>() ?? startLine.Value;
        if (endLine < startLine.Value)
            return CreateToolErrorResponse(id, "endLine must be greater than or equal to startLine");

        var beforeValue = args?["before"]?.GetValue<int>();
        if (beforeValue.HasValue && beforeValue.Value < 0)
            return CreateToolErrorResponse(id, $"before must be in [0, {MaxContextLines}]");
        var before = ClampContextLines(beforeValue ?? 0);

        var afterValue = args?["after"]?.GetValue<int>();
        if (afterValue.HasValue && afterValue.Value < 0)
            return CreateToolErrorResponse(id, $"after must be in [0, {MaxContextLines}]");
        var after = ClampContextLines(afterValue ?? 0);
        var contextTruncated = beforeValue > MaxContextLines || afterValue > MaxContextLines;

        var focusLine = args?["focusLine"]?.GetValue<int>();
        var focusColumn = args?["focusColumn"]?.GetValue<int>();
        var focusLengthValue = args?["focusLength"]?.GetValue<int>();
        if (focusLengthValue.HasValue && focusLengthValue.Value <= 0)
            return CreateToolErrorResponse(id, "focusLength must be greater than or equal to 1");
        var focusLength = focusLengthValue ?? 1;
        var explicitFocusLength = args?["focusLength"] != null;
        if (TryGetValidatedMaxLineWidth(id, args, out var maxLineWidth) is JsonNode maxLineWidthError)
            return maxLineWidthError;
        if (!TryReadMaxOutputBytes(args, out var maxOutputBytes, out var maxOutputBytesError))
            return CreateToolErrorResponse(id, maxOutputBytesError!);

        if (focusLine.HasValue && focusLine.Value <= 0)
            return CreateToolErrorResponse(id, "focusLine must be greater than or equal to 1");
        if (focusColumn.HasValue && focusColumn.Value <= 0)
            return CreateToolErrorResponse(id, "focusColumn must be greater than or equal to 1");
        if (!focusColumn.HasValue && (focusLine.HasValue || explicitFocusLength))
            return CreateToolErrorResponse(id, "focusLine and focusLength require focusColumn");

        return WithDbReader(id, args, reader =>
        {
            if (focusLine.HasValue)
            {
                var file = reader.GetFileByPath(path);
                if (file != null)
                {
                    // `before` is bounded by MaxContextLines and `startLine` by `int.MaxValue`, but
                    // `endLine` is caller-supplied: int + int can still overflow when endLine is
                    // close to `int.MaxValue`. Use long intermediates so the clamp sees the real
                    // window before narrowing back to int (#1528).
                    // `before` は MaxContextLines、`startLine` は `int.MaxValue` で押さえているが、
                    // `endLine` は呼び出し側入力で `int.MaxValue` 近傍なら int 同士の加算が overflow し得る。
                    // long 中間変数で実窓を確定させてから int に戻す（#1528）。
                    var requestedStart = (int)Math.Max(1L, (long)startLine.Value - before);
                    var requestedEnd = (int)Math.Min(file.Lines, (long)endLine + after);
                    if (focusLine.Value < requestedStart || focusLine.Value > requestedEnd)
                        return CreateToolErrorResponse(id, $"focusLine ({focusLine.Value}) must be within the returned excerpt range ({requestedStart}-{requestedEnd})");
                }
            }
            if (focusColumn.HasValue)
            {
                var focusLineLength = reader.GetExcerptFocusLineLength(
                    path,
                    startLine.Value,
                    endLine,
                    before,
                    after,
                    focusLine ?? startLine.Value);
                if (focusLineLength.HasValue && focusColumn.Value > focusLineLength.Value)
                    return CreateToolErrorResponse(id, $"focusColumn ({focusColumn.Value}) must be within the focused line length ({focusLineLength.Value})");
            }

            var excerpt = reader.GetExcerpt(path, startLine.Value, endLine, before, after, maxLineWidth, focusLine ?? startLine.Value, focusColumn, focusLength);
            if (excerpt == null)
            {
                var emptyPayload = new JsonObject
                {
                    ["path"] = path,
                    ["count"] = 0
                };
                AddRecoveryHint(
                    emptyPayload,
                    "file_or_range_not_indexed",
                    "excerpt found no indexed content for the requested range; verify the path with files or outline, then retry with an indexed line range.",
                    "outline",
                    new JsonObject { ["path"] = path });
                AddFreshnessHint(emptyPayload, reader);
                return CreateToolResult(id, "No excerpt found.", emptyPayload);
            }

            var payload = JsonSerializer.SerializeToNode(excerpt, _jsonOptions)!.AsObject();
            ApplyExcerptOutputBudget(payload, maxOutputBytes);
            payload["maxOutputBytes"] = maxOutputBytes;
            payload["before"] = before;
            payload["after"] = after;
            payload["contextTruncated"] = contextTruncated;
            payload["maxLineWidth"] = maxLineWidth;
            if (focusLine.HasValue)
                payload["focusLine"] = focusLine.Value;
            if (focusColumn.HasValue)
                payload["focusColumn"] = focusColumn.Value;
            payload["focusLength"] = focusLength;
            AddNextStepSuggestion(
                payload,
                "outline",
                new JsonObject { ["path"] = excerpt.Path });
            return CreateToolResult(id, "Excerpt returned.", payload);
        });
    }

    private static bool TryReadMaxOutputBytes(JsonNode? args, out int maxOutputBytes, out string? error)
    {
        maxOutputBytes = DefaultExcerptOutputByteLimit;
        error = null;
        if (args?["maxOutputBytes"] is not JsonNode node)
            return true;
        var requested = node.GetValue<int>();
        if (requested <= 0)
        {
            error = "maxOutputBytes must be greater than or equal to 1";
            return false;
        }
        maxOutputBytes = Math.Min(requested, DefaultExcerptOutputByteLimit);
        return true;
    }

    internal static void ApplyExcerptOutputBudget(JsonObject payload, int maxOutputBytes)
    {
        var contentKey = payload.ContainsKey("content") ? "content" : "Content";
        if (payload[contentKey]?.GetValue<string>() is not string content)
            return;
        if (Encoding.UTF8.GetByteCount(content) <= maxOutputBytes)
            return;

        var builder = new StringBuilder();
        foreach (var line in content.Replace("\r\n", "\n").Split('\n'))
        {
            var candidate = builder.Length == 0 ? line : builder.ToString() + "\n" + line;
            if (Encoding.UTF8.GetByteCount(candidate) > maxOutputBytes)
                break;
            builder.Clear();
            builder.Append(candidate);
        }
        payload[contentKey] = builder.ToString();
        payload["contentTruncated"] = true;
        payload["truncated"] = true;
        payload["truncation_reason"] = "output_size_cap";
    }

    private JsonNode ExecuteFindInFile(JsonNode? id, JsonNode? args)
    {
        if (!TryReadRequiredStringParameter(args, "query", out var query, out var requiredError))
            return CreateToolErrorResponse(id, requiredError!);
        if (query.Length > QueryLimits.MaxQueryLength)
            return CreateToolErrorResponse(id, QueryLimits.FormatQueryTooLongError());

        var pathPatterns = ReadScopedPathList(args);
        if (pathPatterns == null || pathPatterns.Count == 0)
            return CreateToolErrorResponse(id, HasBlankPathFilter(args)
                ? "Parameter \"path\" cannot be empty or whitespace-only"
                : "Missing required parameter: path");

        var limit = ClampLimit(args?["limit"]?.GetValue<int>() ?? QueryCommandRunner.DefaultQueryLimit);
        var lang = args?["lang"]?.GetValue<string>()?.ToLowerInvariant();
        var excludePaths = ReadStringList(args, "excludePaths");
        var excludeTests = args?["excludeTests"]?.GetValue<bool>() ?? false;
        var beforeValue = args?["before"]?.GetValue<int>();
        if (beforeValue.HasValue && beforeValue.Value < 0)
            return CreateToolErrorResponse(id, "before must be greater than or equal to 0");
        var before = ClampContextLines(beforeValue ?? 0);

        var afterValue = args?["after"]?.GetValue<int>();
        if (afterValue.HasValue && afterValue.Value < 0)
            return CreateToolErrorResponse(id, "after must be greater than or equal to 0");
        var after = ClampContextLines(afterValue ?? 0);
        var contextTruncated = beforeValue > MaxContextLines || afterValue > MaxContextLines;
        var snippetLinesValue = args?["snippetLines"]?.GetValue<int>();
        if (snippetLinesValue.HasValue && (snippetLinesValue.Value <= 0 || snippetLinesValue.Value > SearchSnippetFormatter.MaxSnippetLines))
            return CreateToolErrorResponse(id, $"snippetLines must be in [1, {SearchSnippetFormatter.MaxSnippetLines}]");
        if (snippetLinesValue.HasValue)
        {
            var surroundingLines = snippetLinesValue.Value - 1;
            if (!beforeValue.HasValue)
                before = surroundingLines / 2;
            if (!afterValue.HasValue)
                after = surroundingLines - before;
        }
        var focusLine = args?["focusLine"]?.GetValue<int>();
        if (focusLine.HasValue && focusLine.Value <= 0)
            return CreateToolErrorResponse(id, "focusLine must be greater than or equal to 1");
        var focusColumn = args?["focusColumn"]?.GetValue<int>();
        if (focusColumn.HasValue && focusColumn.Value <= 0)
            return CreateToolErrorResponse(id, "focusColumn must be greater than or equal to 1");
        if (TryGetValidatedMaxLineWidth(id, args, out var maxLineWidth) is JsonNode maxLineWidthError)
            return maxLineWidthError;
        var exact = args?["exact"]?.GetValue<bool>() ?? false;
        var regex = args?["regex"]?.GetValue<bool>() ?? false;

        return WithDbReader(id, args, reader =>
        {
            List<FileFindResult> results;
            try
            {
                results = reader.FindInFiles(query, limit, lang, pathPatterns, excludePaths, excludeTests, before, after, exact, maxLineWidth, focusLine, focusColumn, regex);
            }
            catch (Exception ex) when (regex && (ex is ArgumentException || ex is RegexMatchTimeoutException))
            {
                return CreateToolErrorResponse(id, $"invalid regular expression: {ex.Message}");
            }
            var structured = new JsonObject
            {
                ["query"] = query,
                ["path"] = PathEcho(pathPatterns),
                ["excludeTests"] = excludeTests,
                ["before"] = before,
                ["after"] = after,
                ["contextTruncated"] = contextTruncated,
                ["maxLineWidth"] = maxLineWidth,
                ["exact"] = exact,
                ["regex"] = regex,
                ["count"] = results.Count,
                ["fileCount"] = results.Select(r => r.Path).Distinct().Count(),
                ["results"] = JsonSerializer.SerializeToNode(results, _jsonOptions),
            };
            if (snippetLinesValue.HasValue)
                structured["snippetLines"] = snippetLinesValue.Value;
            if (focusLine.HasValue)
                structured["focusLine"] = focusLine.Value;
            if (focusColumn.HasValue)
                structured["focusColumn"] = focusColumn.Value;
            if (results.Count == 0)
            {
                AddFreshnessHint(structured, reader);
                return CreateToolResult(id, "No matches found.", structured);
            }

            var fileCount = structured["fileCount"]!.GetValue<int>();
            return CreateToolResult(id, $"Found {ConsoleUi.Counted(results.Count, "in-file match", "in-file matches")} across {ConsoleUi.Counted(fileCount, "file")}.", structured);
        });
    }

    private static int ClampContextLines(int value)
    {
        return Math.Min(value, MaxContextLines);
    }

    private JsonNode ExecuteBatchQuery(JsonNode? id, JsonNode? args)
    {
        var queries = args?["queries"]?.AsArray();
        if (queries == null || queries.Count == 0)
            return CreateToolErrorResponse(id, "Missing or empty required parameter: queries");

        const int maxBatchSize = 10;
        if (queries.Count > maxBatchSize)
            return CreateToolErrorResponse(id, $"Batch too large: {queries.Count} queries (max {maxBatchSize})");

        var resultsArray = new JsonArray();
        var truncatedQueries = new JsonArray();
        var totalStopwatch = Stopwatch.StartNew();
        int successCount = 0;
        int failureCount = 0;
        int? cascadeStartedAtIndex = null;
        var truncated = false;
        var responseByteLimit = GetBatchQueryResponseByteLimit();
        var estimatedResponseBytes = EstimateBatchResponseBytes(id, "Executed 0 queries.", queries.Count, successCount, failureCount,
            GetBatchFailureScope(queries.Count, successCount, failureCount, cascadeStartedAtIndex), cascadeStartedAtIndex,
            responseByteLimit, resultsArray, truncated: false, truncatedQueries);

        bool TryAppendResult(JsonObject entry, string? toolName, JsonNode? toolArgs, int requestIndex, bool successfulSlot = false, bool failedSlot = false)
        {
            var candidateResults = CloneJsonArray(resultsArray);
            candidateResults.Add(entry.DeepClone());
            var candidateSuccessCount = successCount + (successfulSlot ? 1 : 0);
            var candidateFailureCount = failureCount + (failedSlot ? 1 : 0);
            var candidateExecutedCount = candidateSuccessCount + candidateFailureCount;
            var candidateSummary = candidateFailureCount == 0
                ? $"Executed {candidateExecutedCount} of {queries.Count} queries in 0 ms (all succeeded)."
                : $"Executed {candidateExecutedCount} of {queries.Count} queries in 0 ms ({candidateSuccessCount} succeeded, {candidateFailureCount} failed).";
            var candidateBytes = EstimateBatchResponseBytes(id, candidateSummary, queries.Count, candidateSuccessCount, candidateFailureCount,
                GetBatchFailureScope(queries.Count, candidateSuccessCount, candidateFailureCount, cascadeStartedAtIndex), cascadeStartedAtIndex,
                responseByteLimit, candidateResults, truncated: false, truncatedQueries);
            if (candidateBytes > responseByteLimit)
            {
                truncated = true;
                cascadeStartedAtIndex ??= requestIndex;
                truncatedQueries.Add(new JsonObject
                {
                    ["request_index"] = requestIndex,
                    ["tool"] = toolName,
                    ["args_summary"] = BuildArgsSummary(toolArgs),
                    ["reason"] = "response_byte_limit_exceeded",
                });
                return false;
            }

            estimatedResponseBytes = candidateBytes;
            resultsArray.Add(entry);
            return true;
        }

        void AppendSlotError(int requestIndex, string? toolName, JsonNode? toolArgs, Stopwatch slotStopwatch, string errorMessage,
            int? code = null, string? category = null, string? suggestion = null, bool? retrySafe = null)
        {
            slotStopwatch.Stop();
            var entry = new JsonObject
            {
                ["request_index"] = requestIndex,
                ["tool"] = toolName,
                ["ok"] = false,
                ["correlation_id"] = CurrentCorrelationContext.Value?.CorrelationId,
                ["args_summary"] = BuildArgsSummary(toolArgs),
                ["elapsed_ms"] = slotStopwatch.ElapsedMilliseconds,
                ["error"] = errorMessage,
            };
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
            TryAppendResult(entry, toolName, toolArgs, requestIndex, failedSlot: true);
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
        void AppendRateLimitedSlot(int requestIndex, string? toolName, JsonNode? toolArgs, Stopwatch slotStopwatch, long retryAfterMs)
        {
            slotStopwatch.Stop();
            // #1581: emit the canonical envelope (`category`, `suggestion`, `retry_safe`)
            // next to the legacy #1560 fields so clients have a single decision shape across
            // top-level and slot-level rate-limit errors.
            // #1581: 既存の #1560 フィールド（`error_category`, `retry_after_ms`）と並べて
            // canonical envelope（`category`, `suggestion`, `retry_safe`）も書き、トップレベル
            // とスロット単位のレート制限エラーで判定形状を揃える。
            var entry = new JsonObject
            {
                ["request_index"] = requestIndex,
                ["tool"] = toolName,
                ["ok"] = false,
                ["correlation_id"] = CurrentCorrelationContext.Value?.CorrelationId,
                ["args_summary"] = BuildArgsSummary(toolArgs),
                ["elapsed_ms"] = slotStopwatch.ElapsedMilliseconds,
                ["error"] = $"Rate limit exceeded for tool '{toolName}' (retry after {retryAfterMs} ms).",
                ["error_category"] = "rate_limited",
                ["retry_after_ms"] = retryAfterMs,
                ["category"] = McpErrorEnvelope.CategoryRateLimited,
                ["suggestion"] = $"Back off for at least {retryAfterMs} ms before retrying this tool.",
                ["retry_safe"] = true,
            };
            TryAppendResult(entry, toolName, toolArgs, requestIndex, failedSlot: true);
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
            var slotStopwatch = Stopwatch.StartNew();

            if (truncated)
            {
                slotStopwatch.Stop();
                cascadeStartedAtIndex ??= requestIndex;
                truncatedQueries.Add(new JsonObject
                {
                    ["request_index"] = requestIndex,
                    ["tool"] = toolName,
                    ["args_summary"] = BuildArgsSummary(toolArgs),
                    ["reason"] = "response_byte_limit_already_exceeded",
                });
                continue;
            }

            if (string.IsNullOrEmpty(toolName))
            {
                var message = queryObject is null ? "Each query must be an object with a string tool name." : "Missing tool name";
                AppendSlotError(requestIndex, toolName, toolArgs, slotStopwatch, message,
                    category: McpErrorEnvelope.CategoryMissingParameter,
                    suggestion: "Each batch_query slot must include a string `tool` field.",
                    retrySafe: false);
                continue;
            }

            if (ValidateToolArguments(toolName, toolArgs) is JsonObject argumentError)
            {
                AppendSlotError(requestIndex, toolName, toolArgs, slotStopwatch, argumentError["message"]!.GetValue<string>(),
                    category: McpErrorEnvelope.CategoryInvalidArgument,
                    suggestion: "Use exactly the argument names advertised by tools/list for this tool.",
                    retrySafe: false);
                continue;
            }

            if (ValidateCommonListArguments(toolArgs) is JsonObject listArgumentError)
            {
                AppendSlotError(requestIndex, toolName, toolArgs, slotStopwatch, listArgumentError["message"]!.GetValue<string>(),
                    category: McpErrorEnvelope.CategoryInvalidArgument,
                    suggestion: "Send only non-empty string entries within the documented MCP array bounds.",
                    retrySafe: false);
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
                AppendSlotError(requestIndex, toolName, toolArgs, slotStopwatch, $"Tool not enabled: {toolName}", code: -32601,
                    category: McpErrorEnvelope.CategoryToolDisabled,
                    suggestion: "This tool is disabled on the server. Ask the operator to enable it or remove the slot.",
                    retrySafe: false);
                continue;
            }

            // Block write operations in batch / バッチ内では書き込み操作をブロック
            if (toolName == "index" || toolName == "backfill_fold" || toolName == "suggest_improvement")
            {
                AppendSlotError(requestIndex, toolName, toolArgs, slotStopwatch, $"{toolName} is not allowed in batch_query (write operation)",
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
                AppendSlotError(requestIndex, toolName, toolArgs, slotStopwatch, "batch_query cannot be nested inside batch_query.",
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
                AppendRateLimitedSlot(requestIndex, toolName, toolArgs, slotStopwatch, slotDecision.RetryAfterMs);
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
                    "status" => ExecuteStatus(null),
                    "outline" => ExecuteOutline(null, toolArgs),
                    "deps" => ExecuteDeps(null, toolArgs),
                    "impact_analysis" => ExecuteImpactAnalysis(null, toolArgs),
                    "languages" => ExecuteLanguages(null),
                    "validate" => ExecuteValidate(null, toolArgs),
                    "unused_symbols" => ExecuteUnusedSymbols(null, toolArgs),
                    "symbol_hotspots" => ExecuteSymbolHotspots(null, toolArgs),
                    "ping" => ExecutePing(null),
                    _ => null,
                };

                if (response == null)
                {
                    AppendSlotError(requestIndex, toolName, toolArgs, slotStopwatch, $"Unknown tool: {toolName}",
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
                    AppendSlotError(requestIndex, toolName, toolArgs, slotStopwatch, errorText,
                        category: innerCategory,
                        suggestion: innerSuggestion,
                        retrySafe: innerRetrySafe);
                    continue;
                }

                slotStopwatch.Stop();
                var structured = response["result"]?["structuredContent"];
                var entry = new JsonObject
                {
                    ["request_index"] = requestIndex,
                    ["tool"] = toolName,
                    ["ok"] = true,
                    ["correlation_id"] = CurrentCorrelationContext.Value?.CorrelationId,
                    ["args_summary"] = BuildArgsSummary(toolArgs),
                    ["elapsed_ms"] = slotStopwatch.ElapsedMilliseconds,
                    ["result"] = structured?.DeepClone(),
                };
                TryAppendResult(entry, toolName, toolArgs, requestIndex, successfulSlot: true);
                successCount++;
            }
            catch (Exception ex)
            {
                // #2849: classify and sanitize slot exceptions the same way standalone
                // tools/call does, so bound values, paths, and SQL/content snippets stay
                // in stderr instead of the batch_query response.
                DeferFrameLog(() =>
                {
                    WriteMcpLogLine(BuildToolErrorLog(toolName, ex.Message));
                    Database.DbDebug.DumpToStderr(ex);
                });
                var classification = McpErrorEnvelope.ClassifyException(ex);
                AppendSlotError(requestIndex, toolName, toolArgs, slotStopwatch, BuildSanitizedToolErrorMessage(toolName, ex),
                    category: classification.Category,
                    suggestion: classification.Suggestion,
                    retrySafe: classification.RetrySafe);
            }
        }

        totalStopwatch.Stop();
        var totalElapsedMs = totalStopwatch.ElapsedMilliseconds;
        JsonObject BuildPayload() => new()
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
        while (true)
        {
            payload = BuildPayload();
            if (truncated)
            {
                payload["truncated"] = true;
                payload["truncated_queries"] = truncatedQueries.DeepClone();
            }

            summary = BuildSummary();
            estimatedResponseBytes = EstimateJsonUtf8Bytes(CreateToolResult(id, summary, payload.DeepClone()), responseByteLimit);
            if (estimatedResponseBytes <= responseByteLimit)
                break;
            if (resultsArray.Count > 0)
            {
                var removed = resultsArray[resultsArray.Count - 1];
                truncatedQueries.Insert(0, new JsonObject
                {
                    ["request_index"] = removed?["request_index"]?.DeepClone(),
                    ["tool"] = removed?["tool"]?.DeepClone(),
                    ["args_summary"] = removed?["args_summary"]?.DeepClone(),
                    ["reason"] = "final_response_byte_limit_exceeded",
                });
                resultsArray.RemoveAt(resultsArray.Count - 1);
                continue;
            }
            if (truncatedQueries.Count > 1)
            {
                truncatedQueries.RemoveAt(truncatedQueries.Count - 1);
                continue;
            }
            break;
        }

        ((JsonObject)payload["metadata"]!)["estimated_response_bytes"] = estimatedResponseBytes;
        return CreateToolResult(id, summary, payload);
    }

    private static int GetBatchQueryResponseByteLimit()
        => ReadPositiveIntEnvironmentLimit(
            BatchQueryResponseByteLimitEnvVar,
            DefaultBatchQueryResponseByteLimit,
            MaxBatchQueryResponseByteLimit,
            "MCP batch_query response byte limit");

    private int EstimateJsonUtf8Bytes(JsonNode node, int maxBytes = int.MaxValue)
    {
        _ = TryMeasureJsonUtf8BytesWithinLimit(node, _jsonOptions, maxBytes, out var bytesWritten);
        return bytesWritten;
    }

    private int EstimateBatchResponseBytes(JsonNode? id, string summary, int submittedCount, int successCount, int failureCount,
        string failureScope, int? cascadeStartedAtIndex, int responseByteLimit, JsonArray resultsArray, bool truncated, JsonArray truncatedQueries)
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

        return EstimateJsonUtf8Bytes(CreateToolResult(id, summary, payload), responseByteLimit);
    }

    private static string GetBatchFailureScope(int submittedCount, int successCount, int failureCount, int? cascadeStartedAtIndex)
    {
        if (cascadeStartedAtIndex.HasValue && cascadeStartedAtIndex.Value < submittedCount)
            return "cascading";
        return failureCount == 0 ? "none" : "isolated";
    }

    private static JsonArray CloneJsonArray(JsonArray source)
    {
        var clone = new JsonArray();
        foreach (var item in source)
            clone.Add(item?.DeepClone());
        return clone;
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
            var key = kv.Key;
            var value = kv.Value;
            string rendered = value switch
            {
                null => "null",
                JsonValue v => v.ToJsonString(),
                JsonArray arr => $"[{arr.Count}]",
                JsonObject inner => $"{{{inner.Count}}}",
                _ => value.ToJsonString(),
            };
            parts.Add($"{key}={rendered}");
        }
        var joined = string.Join(", ", parts);
        if (joined.Length > BatchArgsSummaryMaxLength)
            joined = joined.Substring(0, BatchArgsSummaryMaxLength - 1) + "…";
        return joined;
    }

    private JsonNode ExecuteDeps(JsonNode? id, JsonNode? args)
    {
        var limit = ClampLimit(args?["limit"]?.GetValue<int>() ?? QueryCommandRunner.DefaultImpactLimit);
        var lang = args?["lang"]?.GetValue<string>()?.ToLowerInvariant();
        var pathPatterns = ReadScopedPathList(args);
        var excludePaths = ReadStringList(args, "excludePaths");
        var excludeTests = args?["excludeTests"]?.GetValue<bool>() ?? false;
        var reverse = args?["reverse"]?.GetValue<bool>() ?? false;
        var cyclesOnly = args?["cycles"]?.GetValue<bool>() ?? false;
        var format = args?["format"]?.GetValue<string>()?.ToLowerInvariant() ?? "edgelist";

        return WithDbReader(id, args, reader =>
        {
            var results = reader.GetFileDependencies(limit, lang, pathPatterns, excludePaths, excludeTests, reverse);
            var baseSqlGraphSignal = reader.GetSqlGraphContractSignal(lang, pathPatterns, excludePaths, excludeTests);
            var sqlGraphSignal = results.Count == 0
                ? baseSqlGraphSignal
                : QueryCommandRunner.NarrowSqlGraphContractSignalByPaths(
                    reader,
                    baseSqlGraphSignal,
                    results.SelectMany(result => new[] { result.SourcePath, result.TargetPath }),
                    lang);
            List<List<string>> cycles = [];
            var outputEdges = cyclesOnly ? QueryCommandRunner.FilterCycleEdges(results, out cycles) : results;
            var payload = new JsonObject { ["count"] = cyclesOnly ? cycles.Count : results.Count };
            if (cyclesOnly)
                payload["cycles"] = QueryCommandRunner.BuildDependencyCyclesJson(cycles);
            else if (format == "json-graph")
                payload["graph"] = BuildJsonGraphPayload(outputEdges);
            else
                payload["edges"] = JsonSerializer.SerializeToNode(outputEdges, _jsonOptions);
            payload["format"] = format;
            AddSqlGraphContractSignal(payload, sqlGraphSignal);
            var summary = payload["count"]!.GetValue<int>() > 0
                ? cyclesOnly ? $"Found {ConsoleUi.Counted(cycles.Count, "dependency cycle")}." : $"Found {ConsoleUi.Counted(results.Count, "dependency edge")}."
                : "No file dependencies found.";
            if (results.Count == 0)
                AddFreshnessHint(payload, reader);
            return CreateToolResult(id, summary, payload);
        });
    }

    private static JsonObject BuildJsonGraphPayload(IReadOnlyList<FileDependencyResult> edges)
    {
        var nodes = edges
            .SelectMany(edge => new[] { edge.SourcePath, edge.TargetPath })
            .Distinct(StringComparer.Ordinal)
            .Select(path => new JsonObject { ["id"] = path })
            .ToArray<JsonNode?>();
        var graphEdges = edges
            .Select(edge => new JsonObject { ["source"] = edge.SourcePath, ["target"] = edge.TargetPath, ["reference_count"] = edge.ReferenceCount })
            .ToArray<JsonNode?>();
        return new JsonObject { ["nodes"] = new JsonArray(nodes), ["edges"] = new JsonArray(graphEdges) };
    }

    private JsonNode ExecuteImpactAnalysis(JsonNode? id, JsonNode? args)
    {
        if (!TryReadRequiredStringParameter(args, "query", out var query, out var requiredError))
            return CreateToolErrorResponse(id, requiredError!);
        if (IsBareVerbatimQueryToken(query))
            return CreateToolErrorResponse(id, "Add a real symbol name after the command; bare verbatim prefixes like `@` are not valid queries.");

        var maxHopsNode = args?["maxHops"];
        var deprecatedMaxDepthNode = args?["maxDepth"];
        var usedDeprecatedMaxDepth = deprecatedMaxDepthNode != null;
        var maxDepthRequested = maxHopsNode?.GetValue<int>() ?? deprecatedMaxDepthNode?.GetValue<int>() ?? 5;
        var maxDepth = Math.Clamp(maxDepthRequested, 0, MaxImpactDepth);
        var limit = ClampLimit(args?["limit"]?.GetValue<int>() ?? QueryCommandRunner.DefaultImpactLimit);
        var lang = args?["lang"]?.GetValue<string>()?.ToLowerInvariant();
        var pathPatterns = ReadScopedPathList(args);
        var excludePaths = ReadStringList(args, "excludePaths");
        var excludeTests = args?["excludeTests"]?.GetValue<bool>() ?? false;
        var withPaths = args?["withPaths"]?.GetValue<bool>() ?? false;
        var countOnly = ReadCountOnly(args);

        return WithDbReader(id, args, reader =>
        {
            var analysis = reader.AnalyzeImpact(query, maxDepth, limit, lang, pathPatterns, excludePaths, excludeTests, withPaths);
            var sqlGraphSignal = QueryCommandRunner.NarrowSqlGraphContractSignal(
                reader.GetSqlGraphContractSignal(lang, pathPatterns, excludePaths, excludeTests),
                DbReader.IsSqlLanguage(lang)
                    || DbReader.ContainsSqlLanguage(analysis.Definitions.Select(definition => definition.Lang))
                    || DbReader.ContainsSqlLanguage(analysis.Callers.Select(caller => caller.Lang))
                    || reader.AnyFilePathHasLanguage(analysis.FileImpacts.SelectMany(impact => new[] { impact.SourcePath, impact.TargetPath }), "sql"));
            var confirmedCount = analysis.Callers.Count;
            var confirmedFileCount = analysis.Callers.Select(r => r.Path).Distinct().Count();
            var hintCount = analysis.FileImpacts.Count;
            var hintFileCount = analysis.FileImpacts.Select(r => r.SourcePath).Distinct().Count();
            var hasHeuristicHints = analysis.ImpactMode == "file_dependency_hints" && hintCount > 0;
            var count = hasHeuristicHints ? hintCount : confirmedCount;
            var fileCount = hasHeuristicHints ? hintFileCount : confirmedFileCount;
            var maxActualDepth = analysis.Callers.Count > 0 ? analysis.Callers.Max(r => r.Depth) : 0;
            if (countOnly)
            {
                var topFiles = hasHeuristicHints
                    ? BuildTopFileHistogram(analysis.FileImpacts, impact => impact.SourcePath)
                    : BuildTopFileHistogram(analysis.Callers, caller => caller.Path);
                var countOnlyPayload = new JsonObject
                {
                    ["query"] = query,
                    ["resolved_name"] = analysis.ResolvedName,
                    ["count_only"] = true,
                    ["count"] = count,
                    ["file_count"] = fileCount,
                    ["confirmed_count"] = confirmedCount,
                    ["confirmed_file_count"] = confirmedFileCount,
                    ["hint_count"] = hintCount,
                    ["hint_file_count"] = hintFileCount,
                    ["max_hops"] = maxDepth,
                    ["actual_depth"] = maxActualDepth,
                    ["truncated"] = analysis.Truncated,
                    ["total"] = analysis.Truncated ? null : JsonValue.Create(count),
                    ["termination_reason"] = analysis.TerminationReason,
                    ["impact_mode"] = analysis.ImpactMode,
                    ["heuristic"] = analysis.Heuristic,
                    ["top_files"] = topFiles,
                    ["results"] = new JsonArray(),
                };
                AddImpactFailureFields(countOnlyPayload, analysis);
                AddSqlGraphContractSignal(countOnlyPayload, sqlGraphSignal);
                return CreateToolResult(id, $"Counted {ConsoleUi.Counted(count, "impact result")}.", countOnlyPayload);
            }

            var payload = new JsonObject
            {
                ["query"] = query,
                ["resolved_name"] = analysis.ResolvedName,
                ["count"] = count,
                ["file_count"] = fileCount,
                ["confirmed_count"] = confirmedCount,
                ["confirmed_file_count"] = confirmedFileCount,
                ["hint_count"] = hintCount,
                ["hint_file_count"] = hintFileCount,
                ["max_hops"] = maxDepth,
                ["max_hops_requested"] = maxDepthRequested,
                ["max_depth"] = maxDepth,
                ["max_depth_requested"] = maxDepthRequested,
                ["actual_depth"] = maxActualDepth,
                ["truncated"] = analysis.Truncated,
                ["termination_reason"] = analysis.TerminationReason,
                ["cycle_detected"] = analysis.CycleDetected,
                ["impact_mode"] = analysis.ImpactMode,
                ["heuristic"] = analysis.Heuristic,
                ["callers"] = ToJsonArray(analysis.Callers),
                ["file_impacts"] = ToJsonArray(analysis.FileImpacts),
                ["definition_count"] = analysis.DefinitionCount,
                ["definition_file_count"] = analysis.DefinitionFileCount,
                ["has_multiple_definitions"] = analysis.HasMultipleDefinitions,
                ["has_class_like_definitions"] = analysis.HasClassLikeDefinitions,
                ["has_multiple_definition_files"] = analysis.HasMultipleDefinitionFiles,
                ["definitions"] = ToJsonArray(analysis.Definitions),
                ["graph_table_available"] = analysis.GraphTableAvailable,
            };
            if (analysis.TruncatedReason != null)
                payload["truncated_reason"] = analysis.TruncatedReason;
            if (analysis.Cycles is { Count: > 0 })
                payload["cycles"] = ToJsonArray(analysis.Cycles);
            AddSqlGraphContractSignal(payload, sqlGraphSignal);
            var warnings = new JsonArray();
            string? maxDepthClampWarning = null;
            string? maxDepthDeprecationWarning = null;
            if (usedDeprecatedMaxDepth)
            {
                maxDepthDeprecationWarning = "maxDepth is deprecated for impact_analysis; use maxHops instead.";
                warnings.Add(maxDepthDeprecationWarning);
            }
            if (maxDepthRequested != maxDepth)
            {
                maxDepthClampWarning = $"maxHops was clamped from {maxDepthRequested} to {maxDepth} (server cap is [0, {MaxImpactDepth}]).";
                warnings.Add(maxDepthClampWarning);
            }
            if (warnings.Count > 0)
                payload["warnings"] = warnings;
            if (analysis.ZeroResultReason != null)
                payload["zero_result_reason"] = analysis.ZeroResultReason;
            AddImpactFailureFields(payload, analysis);
            if (analysis.Suggestion != null)
                payload["suggestion"] = analysis.Suggestion;

            // Summary tail differs by truncated_reason so retry advice is actionable: user_limit
            // is solvable by raising --limit, safety_cap is not. Issue #1533.
            // 切り捨て理由ごとに retry 助言を分岐 (user_limit は --limit 緩和で解消、safety_cap は不可) (#1533)。
            string truncatedTail;
            if (!analysis.Truncated)
                truncatedTail = "";
            else if (analysis.TruncatedReason == ImpactTruncatedReasons.SafetyCap)
                truncatedTail = " Results truncated by internal safety cap (graph likely pathological); raising limit will not help.";
            else
                truncatedTail = " Results truncated — increase limit for more.";
            var cycleTail = analysis.CycleDetected
                ? $" Cycle detected ({ConsoleUi.Counted(analysis.Cycles?.Count ?? 0, "cycle")})."
                : "";

            var summary = analysis.ImpactMode switch
            {
                "file_dependency_hints" => $"No symbol-level callers found for '{analysis.ResolvedName}'; found {ConsoleUi.Counted(hintCount, "possible file-level dependent")} across {ConsoleUi.Counted(hintFileCount, "file")}. These hints are heuristic only."
                    + truncatedTail + cycleTail,
                _ when count > 0 => $"Found {ConsoleUi.Counted(count, "transitive caller")} across {ConsoleUi.Counted(fileCount, "file")} (depth {maxActualDepth})."
                    + truncatedTail + cycleTail,
                _ => "No impact found." + cycleTail,
            };
            if (maxDepthClampWarning != null)
                summary += $" Warning: {maxDepthClampWarning}";
            if (maxDepthDeprecationWarning != null)
                summary += $" Warning: {maxDepthDeprecationWarning}";

            if (count == 0)
            {
                AddSymbolRecoveryHint(payload, query, "impact_analysis", lang, null, PathEcho(pathPatterns));
                AddFreshnessHint(payload, reader);
                var graphReason = ReferenceExtractor.BuildGraphSupportReason(lang, lang != null ? ReferenceExtractor.SupportsLanguage(lang) : null);
                if (graphReason != null)
                    payload["graph_support_reason"] = graphReason;
                if (!analysis.GraphTableAvailable)
                    payload["note"] = "symbol_references table is missing in this index (legacy or read-only DB). Zero result is degraded, not authoritative.";
            }
            else if (analysis.Heuristic)
                payload["note"] = "file_impacts are heuristic hints only; the current graph does not record resolved target file/type for each call.";
            return CreateToolResult(id, summary, payload);
        });
    }

    private static void AddImpactFailureFields(JsonObject payload, ImpactAnalysisResult analysis)
    {
        if (analysis.ImpactFailureChain is { Count: > 0 })
        {
            var chain = new JsonArray();
            foreach (var code in analysis.ImpactFailureChain)
                chain.Add(JsonValue.Create(code));
            payload["impact_failure_chain"] = chain;
        }

        if (analysis.SuggestionType != null)
            payload["suggestion_type"] = analysis.SuggestionType;
    }

    private JsonNode ExecuteValidate(JsonNode? id, JsonNode? args)
    {
        var kind = args?["kind"]?.GetValue<string>()?.ToLowerInvariant();
        var pathPatterns = ReadScopedPathList(args);

        return WithDbReader(id, args, reader =>
        {
            var issues = reader.GetIssues(kind, pathPatterns);
            var payload = new JsonObject
            {
                ["count"] = issues.Count,
                ["issues"] = JsonSerializer.SerializeToNode(issues, _jsonOptions)
            };
            var summary = issues.Count > 0
                ? $"Found {issues.Count} encoding issue(s)."
                : "No encoding issues found.";
            return CreateToolResult(id, summary, payload);
        });
    }

    private JsonNode ExecuteSymbolHotspots(JsonNode? id, JsonNode? args)
    {
        var limit = ClampLimit(args?["limit"]?.GetValue<int>() ?? QueryCommandRunner.DefaultQueryLimit);
        var kind = args?["kind"]?.GetValue<string>()?.ToLowerInvariant();
        var lang = args?["lang"]?.GetValue<string>()?.ToLowerInvariant();
        var groupBy = args?["groupBy"]?.GetValue<string>()?.ToLowerInvariant()
            ?? (string.Equals(lang, "sql", StringComparison.Ordinal) ? "statement" : "symbol");
        if (groupBy is not ("symbol" or "file" or "statement"))
            return CreateToolErrorResponse(id, $"Unsupported symbol_hotspots groupBy '{groupBy}'. Use symbol, file, or statement.");
        var pathPatterns = ReadScopedPathList(args);
        var excludePaths = ReadStringList(args, "excludePaths");
        var excludeTests = args?["excludeTests"]?.GetValue<bool>() ?? false;

        return WithDbReader(id, args, reader =>
        {
            var fileResults = groupBy == "file"
                ? reader.GetFileSymbolHotspots(limit, kind, lang, pathPatterns, excludePaths, excludeTests)
                : null;
            var results = fileResults == null
                ? reader.GetSymbolHotspots(limit, kind, lang, pathPatterns, excludePaths, excludeTests)
                : [];
            var hotspotSignal = reader.GetHotspotFamilySignal(lang);
            var baseSqlGraphSignal = reader.GetSqlGraphContractSignal(lang, pathPatterns, excludePaths, excludeTests);
            var zeroResultSqlGraphSignal = QueryCommandRunner.NarrowSqlGraphContractSignal(
                baseSqlGraphSignal,
                reader.ScopeMayIncludeSqlSymbols(kind, lang, pathPatterns, excludePaths, excludeTests));
            var resultLangs = fileResults != null
                ? fileResults.Select(result => result.Lang)
                : results.Select(result => result.Symbol.Lang);
            var visibleCount = fileResults?.Count ?? results.Count;
            var sqlGraphSignal = visibleCount == 0
                ? zeroResultSqlGraphSignal
                : QueryCommandRunner.NarrowSqlGraphContractSignalByLanguages(
                    baseSqlGraphSignal,
                    resultLangs,
                    lang);
            JsonNode? hotspotsNode;
            if (fileResults != null)
            {
                var hotspots = new JsonArray();
                foreach (var result in fileResults)
                {
                    hotspots.Add(new JsonObject
                    {
                        ["path"] = result.Path,
                        ["lang"] = result.Lang,
                        ["reference_count"] = result.ReferenceCount,
                        ["symbol_count"] = result.SymbolCount,
                    });
                }
                hotspotsNode = hotspots;
            }
            else
            {
                hotspotsNode = ToJsonArray(results, r => new
                {
                    name = r.Symbol.Name,
                    kind = r.Symbol.Kind,
                    path = r.Symbol.Path,
                    line = r.Symbol.Line,
                    reference_count = r.ReferenceCount,
                    visibility = r.Symbol.Visibility,
                    container = r.Symbol.ContainerName,
                });
            }

            var payload = new JsonObject
            {
                ["count"] = visibleCount,
                ["grouped_by"] = groupBy,
                ["hotspots"] = hotspotsNode
            };
            if (fileResults != null)
                payload["files"] = fileResults.Count;
            AddHotspotFamilySignal(payload, hotspotSignal);
            AddSqlGraphContractSignal(payload, sqlGraphSignal);
            var summary = visibleCount > 0
                ? $"Found {ConsoleUi.Counted(visibleCount, $"{groupBy} hotspot")}."
                : "No symbol hotspots found.";
            if (!hotspotSignal.Ready)
            {
                payload["note"] = "cross-file hotspot family grouping is degraded; conservative same-file fallback may hide or undercount hotspot families until the next successful reindex.";
                summary += " Warning: cross-file hotspot family grouping is degraded, so results may be conservative until the next successful reindex.";
            }
            if (visibleCount == 0)
            {
                AddRecoveryHint(
                    payload,
                    "no_results",
                    "symbol_hotspots returned no rows; verify that graph references are indexed and loosen kind/lang/path filters.",
                    "status",
                    new JsonObject());
                AddFreshnessHint(payload, reader);
            }
            return CreateToolResult(id, summary, payload);
        });
    }

    private JsonNode ExecuteUnusedSymbols(JsonNode? id, JsonNode? args)
    {
        var limit = ClampLimit(args?["limit"]?.GetValue<int>() ?? QueryCommandRunner.DefaultImpactLimit);
        var kind = args?["kind"]?.GetValue<string>()?.ToLowerInvariant();
        var lang = args?["lang"]?.GetValue<string>()?.ToLowerInvariant();
        var pathPatterns = ReadScopedPathList(args);
        var excludePaths = ReadStringList(args, "excludePaths");
        var excludeTests = args?["excludeTests"]?.GetValue<bool>() ?? false;

        // Add graph-support metadata for AI trust decisions
        // AI の信頼判断のためにグラフ対応メタデータを追加
        bool? graphSupported = lang != null ? ReferenceExtractor.SupportsLanguage(lang) : null;
        var graphSupportReason = ReferenceExtractor.BuildGraphSupportReason(lang, graphSupported);

        return WithDbReader(id, args, reader =>
        {
            var results = reader.GetUnusedSymbols(limit, kind, lang, pathPatterns, excludePaths, excludeTests);
            var baseSqlGraphSignal = reader.GetSqlGraphContractSignal(lang, pathPatterns, excludePaths, excludeTests);
            var zeroResultSqlGraphSignal = QueryCommandRunner.NarrowSqlGraphContractSignal(
                baseSqlGraphSignal,
                reader.ScopeMayIncludeSqlSymbols(kind, lang, pathPatterns, excludePaths, excludeTests));
            var sqlGraphSignal = results.Count == 0
                ? zeroResultSqlGraphSignal
                : QueryCommandRunner.NarrowSqlGraphContractSignalByLanguages(
                    baseSqlGraphSignal,
                    results.Select(result => result.Lang),
                    lang);
            var bucketCounts = QueryCommandRunner.BuildUnusedBucketCounts(results);
            var payload = new JsonObject
            {
                ["count"] = results.Count,
                ["graph_supported"] = graphSupported,
                ["graph_support_reason"] = graphSupportReason,
                ["returned_bucket_counts"] = JsonSerializer.SerializeToNode(bucketCounts, _jsonOptions),
                ["summary"] = QueryCommandRunner.BuildUnusedSummaryJson(results, _jsonOptions),
                ["bucket_taxonomy"] = QueryCommandRunner.BuildUnusedBucketTaxonomyJson(),
                ["symbols"] = JsonSerializer.SerializeToNode(results, _jsonOptions)
            };
            AddSqlGraphContractSignal(payload, sqlGraphSignal);
            var summary = results.Count > 0
                ? $"Found {ConsoleUi.Counted(results.Count, "potentially unused symbol")} across {ConsoleUi.Counted(bucketCounts.Count, "returned bucket")}. Private hits are ranked ahead of exported/config suspects, but not labeled high-confidence from indexed refs alone. Note: name-based matching — same-named symbols in different contexts may mask true unused symbols."
                : "No unused symbols found.";
            if (graphSupported == false)
                summary += $" Warning: '{lang}' does not support reference extraction. Unused results are unavailable for this language.";
            if (!reader._hasReferencesTable)
            {
                payload["graph_table_available"] = false;
                payload["degraded"] = true;
                payload["note"] = "symbol_references table is missing in this index (legacy or read-only DB). Zero result is degraded, not authoritative.";
                summary += " Warning: symbol_references table is missing in this index; zero-result unused output is degraded, not authoritative.";
            }
            if (results.Count == 0)
            {
                AddRecoveryHint(
                    payload,
                    "no_results",
                    "unused_symbols returned no rows; verify graph readiness and loosen kind/lang/path filters before treating this as authoritative.",
                    "status",
                    new JsonObject());
                AddFreshnessHint(payload, reader);
            }
            return CreateToolResult(id, summary, payload);
        });
    }

    private JsonNode ExecutePing(JsonNode? id)
    {
        var payload = new JsonObject
        {
            ["version"] = _version,
            ["timestamp"] = GetUtcNow().ToString("O"),
            ["db_path"] = _dbPath,
            ["db_exists"] = File.Exists(LongPath.EnsureWindowsPrefix(_dbPath)),
        };
        return CreateToolResult(id, $"cdidx v{_version} is ready.", payload);
    }

    private JsonNode ExecuteLanguages(JsonNode? id)
    {
        var langExtensions = FileIndexer.GetLanguageExtensions();
        var symbolLangs = SymbolExtractor.GetSupportedLanguages();
        var graphLangs = ReferenceExtractor.GetSupportedLanguages();

        // Build consolidated language info / 統合言語情報を構築
        var allLangs = new Dictionary<string, (List<string> Extensions, List<string> Aliases, bool Symbols, bool Graph)>(StringComparer.Ordinal);
        foreach (var (ext, lang) in langExtensions)
        {
            if (!allLangs.TryGetValue(lang, out var info))
            {
                info = (new List<string>(), QueryCommandRunner.GetLanguageAliases(lang).ToList(), symbolLangs.Contains(lang), graphLangs.Contains(lang));
                allLangs[lang] = info;
            }
            info.Extensions.Add(ext);
        }

        var sorted = allLangs.OrderBy(kv => kv.Key).ToList();
        var languagesArray = new JsonArray();
        foreach (var (lang, info) in sorted)
        {
            var extArray = new JsonArray();
            foreach (var ext in info.Extensions.OrderBy(e => e))
                extArray.Add(ext);

            languagesArray.Add(new JsonObject
            {
                ["lang"] = lang,
                ["extensions"] = extArray,
                ["aliases"] = new JsonArray(info.Aliases.OrderBy(alias => alias).Select(alias => JsonValue.Create(alias)).ToArray()),
                ["symbol_extraction"] = info.Symbols,
                ["graph_queries"] = info.Graph,
            });
        }

        var payload = new JsonObject { ["languages"] = languagesArray };
        var summary = $"{sorted.Count} languages supported. {symbolLangs.Count} with symbol extraction, {graphLangs.Count} with call-graph queries.";
        return CreateToolResult(id, summary, payload);
    }

    private JsonNode ExecuteIndex(JsonNode? id, JsonNode? args, JsonNode? progressToken = null)
        => ExecuteIndexAsync(id, args, progressToken).GetAwaiter().GetResult();

    private async Task RefreshClientRootsIfNeededAsync()
    {
        if (!_clientRootsStale || !HasClientCapability("roots"))
            return;

        var result = await SendClientRequestAsync("roots/list", null, _currentRequestToken.Value).ConfigureAwait(false);
        if (result?["roots"] is not JsonArray roots)
            return;

        var refreshed = new JsonArray();
        foreach (var root in roots)
        {
            var uri = TryReadStringValue(root?["uri"]) ?? TryReadStringValue(root);
            if (!string.IsNullOrWhiteSpace(uri))
                refreshed.Add(uri);
        }
        _clientRoots = refreshed;
        _clientRootsStale = false;
    }

    private bool IsPathWithinClientRoots(string path)
    {
        if (!HasClientCapability("roots"))
            return true;

        var rootPaths = _clientRoots
            .Select(root => TryReadStringValue(root))
            .Select(TryResolveRootPath)
            .Where(root => !string.IsNullOrWhiteSpace(root))
            .Cast<string>()
            .ToArray();
        if (rootPaths.Length == 0)
            return false;

        var fullPath = Path.GetFullPath(path);
        return rootPaths.Any(root => IsPathWithinDirectory(root, fullPath));
    }

    private static string? TryResolveRootPath(string? root)
    {
        if (string.IsNullOrWhiteSpace(root))
            return null;
        if (Uri.TryCreate(root, UriKind.Absolute, out var uri))
        {
            if (!string.Equals(uri.Scheme, Uri.UriSchemeFile, StringComparison.OrdinalIgnoreCase))
                return null;
            return Path.GetFullPath(Uri.UnescapeDataString(uri.LocalPath));
        }
        try
        {
            return Path.GetFullPath(root);
        }
        catch (Exception ex) when (ex is ArgumentException or NotSupportedException or PathTooLongException)
        {
            return null;
        }
    }

    private async Task<JsonNode> ExecuteIndexAsync(JsonNode? id, JsonNode? args, JsonNode? progressToken = null)
    {
        if (!TryReadRequiredStringParameter(args, "path", out var path, out var requiredError))
            return CreateToolErrorResponse(id, requiredError!);

        var rebuild = args?["rebuild"]?.GetValue<bool>() ?? false;
        long? maxFileBytes = null;
        if (args?["maxFileBytes"] is { } maxFileBytesNode)
        {
            try
            {
                maxFileBytes = maxFileBytesNode.GetValue<long>();
            }
            catch (Exception)
            {
                return CreateToolErrorResponse(id, "maxFileBytes must be a positive integer less than or equal to 2147483647");
            }
        }
        if (maxFileBytes is <= 0 or > int.MaxValue)
            return CreateToolErrorResponse(id, "maxFileBytes must be a positive integer less than or equal to 2147483647");
        var projectPath = Path.GetFullPath(path);
        var runStartedAtUtc = DateTime.UtcNow;
        var runStopwatch = Stopwatch.StartNew();

        // Prevent path traversal — only allow indexing within current working directory
        // パストラバーサル防止 — カレントディレクトリ配下のみインデックスを許可
        var cwd = Path.GetFullPath(".");
        if (!IsPathWithinDirectory(cwd, projectPath))
            return CreateToolErrorResponse(id, "Path must be within the current working directory");
        await RefreshClientRootsIfNeededAsync().ConfigureAwait(false);
        if (!IsPathWithinClientRoots(projectPath))
            return CreateToolErrorResponse(id, "Path must be within an MCP client root");

        if (!Directory.Exists(projectPath))
            return CreateToolErrorResponse(id, "Directory not found");

        if (!McpIndexRunLock.TryAcquire(_dbPath, out var indexLock, out var lockError))
            return CreateToolErrorResponse(id, lockError!);
        using var acquiredIndexLock = indexLock;

        // Reuse the per-session DbContext (issue #1494) instead of opening a fresh
        // connection on every index call. InitializeSchema below is idempotent so the
        // shared connection still picks up legacy-DB migrations on demand.
        // index 呼び出しごとに新しい接続を開かず、セッション共有 DbContext を再利用する（#1494）。
        // 後段の InitializeSchema は冪等なので共有接続でもレガシー DB の移行は正しく走る。
        var db = GetOrOpenSharedDb();
        var priorFoldVersion = db.GetMetaString("fold_key_version");
        var priorFoldFingerprint = db.GetMetaString("fold_key_fingerprint");
        var priorCSharpSymbolNameContractVersion = db.GetMetaString(DbContext.CSharpSymbolNameContractVersionMetaKey);
        var priorMetadataTargetCsharp = db.GetMetaString(DbContext.GetMetadataTargetVersionMetaKey("csharp"));
        var priorSqlGraphContractVersion = db.GetMetaString(DbContext.SqlGraphContractVersionMetaKey);
        var priorHotspotFamilyVersions = GetHotspotFamilyMetaSnapshot(db, DbContext.GetHotspotFamilyVersionMetaKey);
        var priorHotspotFamilyMarkerFingerprints = GetHotspotFamilyMetaSnapshot(db, DbContext.GetHotspotFamilyMarkerFingerprintMetaKey);
        var priorIndexedProjectRoot = db.GetMetaString(DbContext.IndexedProjectRootMetaKey);
        // Capture git HEAD so subsequent queries can detect a worktree branch / HEAD switch
        // (`git switch other-branch` inside the worktree) without a `--check` workspace scan.
        // Like the CLI full-scan path, the value is only persisted at the end of a successful
        // run (errors == 0) so a crashed / partial index keeps the previous HEAD and surfaces
        // staleness until the next clean refresh. Issues #1508 and #1512.
        // worktree 内の HEAD 切替検出のため HEAD を捕捉。CLI full-scan と同じく成功時のみ
        // 書き込み、partial 失敗は旧 HEAD を残して次回 full scan で更新する。
        var currentHeadCommit = GitHelper.TryGetHeadCommit(projectPath);

        // On --rebuild, clear readiness before DropAll so a crash during the window
        // (empty tables recreated, MarkReady not yet run) cannot leave old trust bits
        // blessing the freshly-empty tables. On non-rebuild runs, readiness is cleared
        // just before the first write below so a scan failure does not downgrade a
        // previously-healthy index.
        // --rebuild は DropAll 前に clear。通常は実書き込み直前で clear。
        if (rebuild)
        {
            db.ClearReadyFlags();
            var rebuildWriter = new DbWriter(db);
            rebuildWriter.ClearHotspotFamilyReady();
            rebuildWriter.ClearMetadataTargetReady();
            db.DropAll();
        }

        db.InitializeSchema();
        MarkSharedDbMigrated();

        var writer = new DbWriter(db);
        var indexer = new FileIndexer(projectPath, GitHelper.ResolveIgnoreCase(projectPath), GitHelper.TryGetRepositoryRoot(projectPath) ?? Path.GetFullPath(projectPath), maxFileBytes);
        var postExtractionHooks = PostExtractionHookRunner.DiscoverDefault();
        var currentHotspotFamilyMarkerFingerprints = GetHotspotFamilyMarkerFingerprints(indexer);
        var currentCSharpSymbolNameContractVersion = DbContext.CSharpSymbolNameContractVersion.ToString(System.Globalization.CultureInfo.InvariantCulture);
        var csharpSymbolNameContractMatchesCurrent = priorCSharpSymbolNameContractVersion == currentCSharpSymbolNameContractVersion;
        var currentSqlGraphContractVersion = DbContext.SqlGraphContractVersion.ToString(System.Globalization.CultureInfo.InvariantCulture);
        var sqlGraphContractMatchesCurrent = priorSqlGraphContractVersion == currentSqlGraphContractVersion;
        var hotspotFamilyTrustMatchesCurrent = GetHotspotFamilyTrustMatchesCurrent(
            priorHotspotFamilyVersions,
            priorHotspotFamilyMarkerFingerprints,
            currentHotspotFamilyMarkerFingerprints);
        var normalizedProjectPath = Path.GetFullPath(projectPath);
        var normalizedPriorIndexedProjectRoot = string.IsNullOrWhiteSpace(priorIndexedProjectRoot)
            ? null
            : Path.GetFullPath(priorIndexedProjectRoot);
        var projectRootWritten = PathsEqual(normalizedPriorIndexedProjectRoot, normalizedProjectPath);

        static bool PathsEqual(string? left, string? right)
        {
            if (left == null || right == null)
                return false;

            return CodeIndex.Cli.PathCasing.PathsEqual(left, right);
        }

        void WriteProjectRootOnce()
        {
            if (!projectRootWritten)
            {
                writer.SetMeta(DbContext.IndexedProjectRootMetaKey, normalizedProjectPath);
                projectRootWritten = true;
            }
        }

        static long SumReadableFileBytes(IEnumerable<string> paths)
        {
            long total = 0;
            foreach (var filePath in paths)
            {
                try
                {
                    var info = new FileInfo(filePath);
                    if (info.Exists)
                        total += info.Length;
                }
                catch (Exception ex) when (ex is IOException or UnauthorizedAccessException or NotSupportedException or ArgumentException)
                {
                }
            }

            return total;
        }

        // First mutation point — demote readiness just before any write.
        // 実書き込み直前で readiness をクリア。
        writer.ClearReadyFlags();
        writer.ClearHotspotFamilyReady();
        writer.ClearMetadataTargetReady();

        var hadCSharpStaticInterfaceContractsBeforePurge =
            writer.LoadCSharpStaticInterfaceContractSymbols().Count > 0;

        // Purge stale files / 古いファイルをパージ
        var purged = writer.PurgeStaleFiles(projectPath);
        if (purged > 0)
            WriteProjectRootOnce();

        // Purge references for languages no longer graph-supported / グラフ非対応になった言語の参照をパージ
        writer.PurgeUnsupportedReferences(ReferenceExtractor.GetSupportedLanguages());

        // Scan and index / スキャン・インデックス
        var requestToken = _currentRequestToken.Value;
        requestToken.ThrowIfCancellationRequested();
        var scanResult = indexer.ScanFilesDetailed(cancellationToken: requestToken);
        var files = scanResult.Files;
        EmitProgressNotification(progressToken, 0, files.Count, "Index scan complete; indexing files.");
        var csharpWorkspace = BuildMcpCSharpStaticInterfaceWorkspaceSymbols(writer, indexer, projectPath, files, requestToken);
        if (purged > 0 && hadCSharpStaticInterfaceContractsBeforePurge)
            csharpWorkspace = csharpWorkspace with { HasStaticInterfaceContracts = true };
        int processed = 0, skipped = 0, errors = 0;
        var failures = new List<IndexFileFailure>();
        var reusedHotspotFamilyLanguages = new HashSet<string>(StringComparer.Ordinal);

        foreach (var filePath in files)
        {
            var fileBatchMarked = false;
            try
            {
                requestToken.ThrowIfCancellationRequested();
                var (record, content, rawBytes, _) = indexer.BuildRecordWithRawBytes(filePath, requestToken);
                var existingId = writer.GetUnchangedFileId(
                    record.Path,
                    record.Modified,
                    record.Checksum,
                    size: record.Size,
                    language: record.Lang,
                    allowReuse: record.Lang is not ("javascript" or "typescript")
                        && (record.Lang != "csharp" || csharpSymbolNameContractMatchesCurrent)
                        && (record.Lang != "csharp" || !csharpWorkspace.HasStaticInterfaceContracts)
                        && (record.Lang != "sql" || sqlGraphContractMatchesCurrent)
                        && AllowReuseWithCurrentHotspotFamilyTrust(record.Lang, hotspotFamilyTrustMatchesCurrent));
                if (existingId != null)
                {
                    skipped++;
                    processed++;
                    if (FileIndexer.SupportsHotspotFamilyMarkerLanguage(record.Lang) && record.Lang != null)
                        reusedHotspotFamilyLanguages.Add(record.Lang);
                    continue;
                }

                writer.MarkBatchInProgress();
                fileBatchMarked = true;
                using var txn = writer.BeginTransaction();
                var fileId = writer.UpsertFile(record);
                var chunks = ChunkSplitter.Split(fileId, content);
                writer.InsertChunks(chunks);
                var symbols = SymbolExtractor.Extract(fileId, record.Lang, content, filePath, projectPath, requestToken);
                SymbolExtractor.ApplyFamilyScope(symbols, indexer.GetFamilyScopeKey(filePath, record.Lang));
                var fileContext = new FileContext(projectPath, record.Path, filePath, record.Lang);
                postExtractionHooks.OnSymbolsExtracted(fileContext, symbols);
                writer.InsertSymbols(symbols);
                var references = ReferenceExtractor.Extract(
                    fileId,
                    record.Lang,
                    content,
                    symbols,
                    record.Path,
                    record.Lang == "csharp" ? csharpWorkspace.Symbols : null,
                    requestToken);
                postExtractionHooks.OnReferencesExtracted(fileContext, references);
                writer.InsertReferences(references);
                // Keep MCP index parity with CLI index: persist file-level validation issues too.
                // MCPインデックスもCLIインデックスと同等に、ファイル検証issueを保存する。
                var issues = FileIndexer.ValidateContent(record.Path, rawBytes, content);
                writer.InsertIssues(fileId, issues);
                WriteProjectRootOnce();
                writer.ClearBatchInProgress();
                txn.Commit();
            }
            catch (FileIndexer.BinaryFileSkippedException)
            {
                try
                {
                    var relativePath = FileIndexer.NormalizePathSeparators(Path.GetRelativePath(projectPath, filePath));
                    if (writer.HasFileAtPath(relativePath))
                    {
                        using var txn = writer.BeginTransaction();
                        writer.DeleteFileByPath(relativePath);
                        WriteProjectRootOnce();
                        txn.Commit();
                    }
                }
                catch (Exception ex)
                {
                    errors++;
                    failures.Add(BuildIndexFileFailure(projectPath, filePath, ex, "delete_skipped_binary"));
                }
            }
            catch (Exception ex) when (ex is FileNotFoundException or DirectoryNotFoundException)
            {
                if (fileBatchMarked)
                    writer.ClearBatchInProgress();

                try
                {
                    var relativePath = FileIndexer.NormalizePathSeparators(Path.GetRelativePath(projectPath, filePath));
                    if (writer.HasFileAtPath(relativePath))
                    {
                        using var txn = writer.BeginTransaction();
                        writer.DeleteFileByPath(relativePath);
                        WriteProjectRootOnce();
                        txn.Commit();
                    }
                }
                catch (Exception cleanupEx)
                {
                    errors++;
                    failures.Add(BuildIndexFileFailure(projectPath, filePath, cleanupEx, "delete_missing_file"));
                }
            }
            catch (OperationCanceledException) when (requestToken.IsCancellationRequested)
            {
                if (fileBatchMarked)
                    writer.ClearBatchInProgress();
                throw;
            }
            catch (Exception ex)
            {
                if (fileBatchMarked)
                    writer.ClearBatchInProgress();
                errors++;
                failures.Add(BuildIndexFileFailure(projectPath, filePath, ex, "index_file"));
            }
            processed++;
            EmitProgressNotification(progressToken, processed, files.Count);
        }

        writer.OptimizeFts();
        // MCP index now runs ValidateContent + InsertIssues per file (bdbb2bd) on par with CLI
        // index, so stamp both graph-ready and issues-ready on clean runs — the old "graph only"
        // path is no longer accurate. Bits are only stamped when every file committed without
        // throwing, so a partial failure leaves trust degraded and `validate` still surfaces it.
        // MCP index は CLI と同等に file_issues を永続化するため、成功時は graph / issues の両方を stamp する。
        var csharpSymbolNameReadyAfter = !writer.HasAnyFilesWithLanguage("csharp");
        var csharpMetadataTargetReadyAfter = !writer.HasAnyFilesWithLanguage("csharp");
        var sqlGraphContractReadyAfter = !writer.HasAnyFilesWithLanguage("sql");
        var foldReadyAfter = false;
        string? foldReadyReason = null;
        _ = priorMetadataTargetCsharp;
        if (errors == 0)
        {
            EmitProgressNotification(progressToken, processed, files.Count, "Finalizing index metadata.");
            writer.MarkBatchInProgress();
            using var readinessTxn = writer.BeginTransaction();
            writer.MarkGraphReady();
            writer.MarkIssuesReady();
            writer.MarkSqlGraphContractReady();
            writer.MarkCSharpSymbolNameContractReady();
            csharpSymbolNameReadyAfter = true;
            if (writer.HasAnyFilesWithLanguage("csharp"))
            {
                writer.ResolveCSharpMetadataTargets();
                writer.MarkMetadataTargetReady("csharp");
                csharpMetadataTargetReadyAfter = true;
            }
            else
            {
                csharpMetadataTargetReadyAfter = true;
            }
            sqlGraphContractReadyAfter = true;
            writer.RebuildTypeScriptAugmentationReferences(projectPath);
            RestampHotspotFamilyTrust(
                writer,
                reusedHotspotFamilyLanguages,
                priorHotspotFamilyVersions,
                priorHotspotFamilyMarkerFingerprints,
                currentHotspotFamilyMarkerFingerprints);
            // FoldReady must reflect reality (#86). Like CLI full-scan, MCP index_project skips
            // unchanged files via GetUnchangedFileId, so a legacy DB's pre-#86 rows keep NULL
            // name_folded / *_folded. Stamp only when every row is backfilled; otherwise readers
            // would silently miss legacy rows on the folded-equality path. Codex #86 review.
            // MCP も incremental で skip される legacy 行が残るため、実検証を通してから stamp。
            var backfillReady = writer.AllFoldedColumnsBackfilled();
            var foldedKeysCurrent = skipped == 0 || writer.AllFoldedColumnValuesMatchCurrentFold();
            var currentFoldVersion = NameFold.Version.ToString(System.Globalization.CultureInfo.InvariantCulture);
            var currentFoldFingerprint = NameFold.Fingerprint();
            var foldVersionMatchesCurrent = priorFoldVersion == currentFoldVersion;
            var foldFingerprintMatchesCurrent = priorFoldFingerprint == currentFoldFingerprint;
            var canRestampExistingFoldTrust = foldVersionMatchesCurrent && foldFingerprintMatchesCurrent;
            if (backfillReady && foldedKeysCurrent && (skipped == 0 || canRestampExistingFoldTrust))
            {
                // MarkFoldReady re-verifies inside BEGIN IMMEDIATE; a concurrent NULL-folded
                // insert during this restamp window leaves foldReadyAfter=false and degrades
                // to the legacy "missing_fold_backfill" reason instead of silent misadvertise.
                // Issue #1535.
                // BEGIN IMMEDIATE 内で再検証する。concurrent NULL 差し込みで stamp が失敗した
                // 場合は missing_fold_backfill に降格する。Issue #1535。
                foldReadyAfter = writer.MarkFoldReady();
                if (!foldReadyAfter)
                    foldReadyReason = DegradationReasonCodes.MissingFoldBackfill;
            }
            else if (!backfillReady)
            {
                foldReadyReason = DegradationReasonCodes.MissingFoldBackfill;
            }
            else if (!foldVersionMatchesCurrent)
            {
                foldReadyReason = DegradationReasonCodes.StaleFoldKeyVersion;
            }
            else if (!foldFingerprintMatchesCurrent)
            {
                foldReadyReason = DegradationReasonCodes.StaleFoldKeyFingerprint;
            }

            writer.WriteCdidxWriterVersion(_version);

            // Successful no-op MCP full scans should repair explicit-DB roots only after
            // readiness is stamped, preserving the failure-path safety contract.
            // MCP の no-op full-scan root backfill も readiness stamp 後に限定する。
            WriteProjectRootOnce();
            writer.WriteUnknownExtensionFileMetadata(scanResult.UnknownExtensionFiles);
            writer.SetMeta(DbContext.LastIndexRunModeMetaKey, rebuild ? "rebuild" : "mcp");
            writer.SetMeta(DbContext.LastIndexRunStartedAtMetaKey, runStartedAtUtc.ToString("o", System.Globalization.CultureInfo.InvariantCulture));
            writer.SetMeta(DbContext.LastIndexRunDurationMsMetaKey, runStopwatch.ElapsedMilliseconds.ToString(System.Globalization.CultureInfo.InvariantCulture));
            writer.SetMeta(DbContext.LastIndexRunFilesScannedMetaKey, files.Count.ToString(System.Globalization.CultureInfo.InvariantCulture));
            writer.SetMeta(DbContext.LastIndexRunFilesSkippedMetaKey, skipped.ToString(System.Globalization.CultureInfo.InvariantCulture));
            writer.SetMeta(DbContext.LastIndexRunParseErrorsMetaKey, errors.ToString(System.Globalization.CultureInfo.InvariantCulture));
            writer.SetMeta(DbContext.LastIndexRunBytesReadMetaKey, SumReadableFileBytes(files).ToString(System.Globalization.CultureInfo.InvariantCulture));
            writer.SetMeta(DbContext.LastIndexRunRowsUpsertedMetaKey, processed.ToString(System.Globalization.CultureInfo.InvariantCulture));
            writer.SetMeta(DbContext.LastIndexRunRowsDeletedMetaKey, purged.ToString(System.Globalization.CultureInfo.InvariantCulture));
            // Persist the current HEAD only after the run is fully successful (errors == 0).
            // Mirrors the CLI full-scan contract (Issue #1508) so MCP-driven re-indexes also
            // refresh `worktree_head_changed`; partial / failed runs leave the prior HEAD
            // untouched and surface staleness until the next clean refresh. Issues #1508 / #1512.
            // CLI full-scan と同じく成功時のみ HEAD を記録する。partial / 失敗は旧 HEAD を残す。
            writer.SetMeta(DbContext.IndexedHeadCommitMetaKey, currentHeadCommit);
            writer.SetMeta(DbContext.IndexedHeadCommitBranchMetaKey, GitHelper.TryGetHeadBranch(projectPath));
            // #1509: also persist the always-updated HEAD/branch/timestamp triple so
            // status / consumers can detect cross-session staleness via
            // `commits_ahead_of_indexed_head`. Same best-effort contract — git unavailability
            // writes NULL stamps and stamp exceptions never fail the index itself.
            // #1509: HEAD / branch / timestamp を保存し、cross-session staleness 検出を可能にする。
            try
            {
                var headBranch = GitHelper.TryGetHeadBranch(projectPath);
                var timestamp = currentHeadCommit != null
                    ? GetUtcNow().ToString("o", System.Globalization.CultureInfo.InvariantCulture)
                    : null;
                writer.SetMeta(DbContext.IndexedHeadShaMetaKey, currentHeadCommit);
                writer.SetMeta(DbContext.IndexedHeadBranchMetaKey, headBranch);
                writer.SetMeta(DbContext.IndexedHeadTimestampMetaKey, timestamp);
            }
            catch
            {
                // Best-effort; never fail an otherwise-successful index run.
            }
            // #1546: stamp workspace path-case-sensitivity so MCP-driven indexes also
            // surface the diagnostic field through `cdidx status` / MCP status.
            // #1546: MCP 経由 index でも case-sensitivity stamp を残す。
            try
            {
                var ignoreCase = GitHelper.ResolveIgnoreCase(projectPath);
                CodeIndex.Cli.PathCasing.SeedFromWorkspace(projectPath, ignoreCase);
                writer.SetMeta(
                    DbContext.WorkspacePathCaseSensitiveMetaKey,
                    (!ignoreCase).ToString(System.Globalization.CultureInfo.InvariantCulture));
            }
            catch
            {
                // Best-effort; never fail an otherwise-successful index run.
            }
            writer.ClearBatchInProgress();
            readinessTxn.Commit();
        }
        var (totalFiles, totalChunks, totalSymbols, totalReferences) = writer.GetCounts();
        EmitProgressNotification(progressToken, files.Count, files.Count, errors == 0 ? "Indexing complete." : "Indexing completed with errors.");

        var structured = new JsonObject
        {
            ["path"] = projectPath,
            ["rebuild"] = rebuild,
            ["max_file_bytes"] = maxFileBytes,
            ["summary"] = new JsonObject
            {
                ["files"] = totalFiles,
                ["chunks"] = totalChunks,
                ["symbols"] = totalSymbols,
                ["references"] = totalReferences,
                ["scanned"] = files.Count,
                ["skipped"] = skipped,
                ["purged"] = purged,
                ["unknown_extension_file_count"] = scanResult.UnknownExtensionFiles.Count,
                ["errors"] = errors,
                ["failed_count"] = failures.Count
            },
            ["sql_graph_contract_ready"] = sqlGraphContractReadyAfter,
            ["csharp_symbol_name_ready"] = csharpSymbolNameReadyAfter,
            ["csharp_metadata_target_ready"] = csharpMetadataTargetReadyAfter,
            // #86 codex review: AI clients use this to tell whether --exact will use the
            // Unicode fold path or silently fall back to ASCII NOCASE. If false after a clean
            ["fold_ready"] = foldReadyAfter,
            ["fold_ready_reason"] = foldReadyReason
        };
        if (failures.Count > 0)
        {
            var failureArray = new JsonArray();
            foreach (var failure in failures.Take(50))
            {
                failureArray.Add(new JsonObject
                {
                    ["path"] = failure.Path,
                    ["stage"] = failure.Stage,
                    ["exception_type"] = failure.ExceptionType,
                    ["message"] = failure.Message,
                });
            }
            structured["failed_count"] = failures.Count;
            structured["failures"] = failureArray;
            if (failures.Count > 50)
                structured["failures_truncated"] = failures.Count - 50;
            GlobalToolLog.Error($"mcp_index_file_failures count={failures.Count} first_path='{failures[0].Path}' first_error='{failures[0].ExceptionType}: {failures[0].Message}'");
        }
        if (!sqlGraphContractReadyAfter)
        {
            using var signalReader = new DbReader(writer.Connection);
            AddSqlGraphContractSignal(structured, signalReader.GetSqlGraphContractSignal());
        }
        return CreateToolResult(id,
            errors == 0 && !foldReadyAfter
                ? foldReadyReason switch
                {
                    "stale_fold_key_version" => "Indexing complete. Note: --exact Unicode fold path not active because unchanged rows still carry an older fold-key version. Rewrite or purge those stale rows and rerun index, run backfill_fold, or do a full rebuild to upgrade.",
                    "stale_fold_key_fingerprint" => "Indexing complete. Note: --exact Unicode fold path not active because unchanged rows still carry folded keys generated under an older runtime fingerprint. Rewrite or purge those stale rows and rerun index, run backfill_fold, or do a full rebuild to upgrade.",
                    "missing_fold_backfill" => "Indexing complete. Note: --exact Unicode fold path not active because legacy rows without name_folded remain. Run backfill_fold to upgrade without reparsing files, or do a full rebuild.",
                    _ => "Indexing complete. Note: --exact Unicode fold path not active."
                }
                : "Indexing complete.",
            structured);
    }

    private static IndexFileFailure BuildIndexFileFailure(string projectPath, string filePath, Exception ex, string stage)
    {
        var relativePath = FileIndexer.NormalizePathSeparators(Path.GetRelativePath(projectPath, filePath));
        return new IndexFileFailure(relativePath, stage, ex.GetType().Name, ex.Message);
    }

    private sealed record IndexFileFailure(string Path, string Stage, string ExceptionType, string Message);

    private JsonNode ExecuteBackfillFold(JsonNode? id, JsonNode? args, JsonNode? progressToken = null)
    {
        if (!DbContext.TryValidateExistingCodeIndexDb(_dbPath, out var validationMessage, out var isNotFound))
        {
            var detail = isNotFound
                ? $"Database not found: {_dbPath}. Run 'cdidx index <projectPath>' first."
                : $"Database is not an existing CodeIndex DB: {_dbPath}. Run 'cdidx index <projectPath>' first.";
            if (validationMessage.StartsWith("database must be writable", StringComparison.Ordinal))
                detail = $"Database must be writable for backfill_fold: {_dbPath}.";
            return CreateToolErrorResponse(id, detail);
        }

        try
        {
            // Reuse the per-session DbContext (issue #1494). InitializeSchema is idempotent
            // and remains correct on a long-lived connection.
            // セッション共有 DbContext を再利用する（#1494）。InitializeSchema は冪等。
            var db = GetOrOpenSharedDb();
            db.InitializeSchema();
            MarkSharedDbMigrated();
            var writer = new DbWriter(db);
            var userVersionBefore = db.GetUserVersion();
            var foldReadyBefore = (userVersionBefore & DbContext.FoldReadyFlag) != 0;
            var currentFoldVersion = NameFold.Version.ToString(System.Globalization.CultureInfo.InvariantCulture);
            var currentFoldFingerprint = NameFold.Fingerprint();
            var storedFoldVersion = db.GetMetaString("fold_key_version");
            var storedFoldFingerprint = db.GetMetaString("fold_key_fingerprint");
            var foldMetadataCurrentBefore = storedFoldVersion == currentFoldVersion
                && storedFoldFingerprint == currentFoldFingerprint;
            foldReadyBefore = foldReadyBefore && foldMetadataCurrentBefore;
            var dryRun = args?["dry_run"]?.GetValue<bool>() ?? args?["dryRun"]?.GetValue<bool>() ?? false;
            var force = args?["force"]?.GetValue<bool>() ?? false;
            var rewriteAll = force
                || !foldMetadataCurrentBefore;
            var symbols = 0;
            var symbolReferences = 0;
            var totalSymbols = 0;
            var totalSymbolReferences = 0;
            var verified = false;
            var userVersionAfter = userVersionBefore;

            (totalSymbols, totalSymbolReferences) = writer.CountBackfillFoldedColumns(rewriteAll);
            if (dryRun)
            {
                symbols = totalSymbols;
                symbolReferences = totalSymbolReferences;
            }
            else
            {
                EmitProgressNotification(progressToken, 0, null, "Backfilling folded-name keys.");
                (symbols, symbolReferences) = writer.BackfillFoldedColumns(rewriteAll);
                EmitProgressNotification(progressToken, symbols + symbolReferences, totalSymbols + totalSymbolReferences, "Verifying folded-name keys.");
                // Row rewrites are intentionally committed before the final FoldReady stamp so
                // interrupted MCP backfills can resume from the remaining rows.
                // 行更新は FoldReady stamp より前に永続化し、中断後に残り行から再開できるようにする。
                using var transaction = writer.BeginTransaction();
                verified = writer.MarkFoldReady();
                if (!verified)
                    return CreateToolErrorResponse(id, "Folded-name backfill verification failed: some rows still have NULL folded values. Re-run backfill_fold.");

                transaction.Commit();
                userVersionAfter = db.GetUserVersion();
                EmitProgressNotification(progressToken, symbols + symbolReferences, symbols + symbolReferences, "Folded-name backfill complete.");
            }

            var foldMetadataCurrentAfter = dryRun
                ? foldMetadataCurrentBefore
                : true;
            var foldReadyAfter = (userVersionAfter & DbContext.FoldReadyFlag) != 0
                && foldMetadataCurrentAfter;
            var wasAlreadyComplete = foldReadyBefore && !rewriteAll && symbols == 0 && symbolReferences == 0;

            var payload = new JsonObject
            {
                ["symbols"] = symbols,
                ["symbol_references"] = symbolReferences,
                ["rewrite_all"] = rewriteAll,
                ["dry_run"] = dryRun,
                ["force"] = force,
                ["was_already_complete"] = wasAlreadyComplete,
                ["fold_ready_before"] = foldReadyBefore,
                ["fold_ready_after"] = foldReadyAfter,
                ["verified"] = verified,
                ["user_version_before"] = userVersionBefore,
                ["user_version_after"] = userVersionAfter,
                ["fold_ready"] = foldReadyAfter,
                ["fold_key_version_before"] = storedFoldVersion,
                ["fold_key_version_after"] = dryRun ? storedFoldVersion : currentFoldVersion,
                ["fold_key_fingerprint_before"] = storedFoldFingerprint,
                ["fold_key_fingerprint_after"] = dryRun ? storedFoldFingerprint : currentFoldFingerprint,
                ["progress"] = BuildBackfillProgressJson(symbols + symbolReferences, totalSymbols + totalSymbolReferences),
            };

            var summary = dryRun
                ? "Folded-name backfill preview complete."
                : rewriteAll
                ? "Folded-name keys refreshed and FoldReady stamped."
                : "Missing folded-name keys backfilled and FoldReady stamped.";
            return CreateToolResult(id, summary, payload);
        }
        catch (Exception ex)
        {
            return CreateToolErrorResponse(id, $"Failed to backfill folded-name columns: {ex.Message}");
        }
    }

    private static JsonObject BuildBackfillProgressJson(int rowsDone, int rowsTotal)
    {
        var fraction = rowsTotal <= 0 ? 1.0 : Math.Min(1.0, rowsDone / (double)rowsTotal);
        return new JsonObject
        {
            ["rows_done"] = rowsDone,
            ["rows_total"] = rowsTotal,
            ["fraction"] = fraction,
        };
    }

    /// <summary>
    /// Maximum length for suggestion description text.
    /// 提案説明テキストの最大長。
    /// </summary>
    private const int MaxDescriptionLength = 2000;

    /// <summary>
    /// Maximum length for suggestion context text.
    /// 提案コンテキストテキストの最大長。
    /// </summary>
    private const int MaxContextLength = 1000;

    private const int MaxSamplingPromptBytes = 4096;
    private const int MaxSamplingShortFieldChars = 80;
    private const int MaxSamplingDescriptionChars = 800;
    private const int MaxSamplingContextChars = 400;
    private const int MaxSamplingToolInvocationSummaryChars = 160;
    private const int MaxSamplingResponseTextChars = 8192;
    private const int MaxSamplingResponseJsonDepth = 16;

    /// <summary>
    /// Handle the suggest_improvement tool call.
    /// Records a structured suggestion to .cdidx/suggestions-*.json.
    /// Validates that no source code is included in the description or context.
    /// suggest_improvementツール呼び出しを処理する。
    /// 構造化された提案を .cdidx/suggestions-*.json に記録する。
    /// description と context にソースコードが含まれていないことを検証する。
    /// </summary>
    private JsonNode ExecuteSuggestImprovement(JsonNode? id, JsonNode? args)
        => ExecuteSuggestImprovementAsync(id, args).GetAwaiter().GetResult();

    private async Task<JsonNode> ExecuteSuggestImprovementAsync(JsonNode? id, JsonNode? args)
    {
        // 1. Validate required parameters / 必須パラメータのバリデーション
        if (!TryReadRequiredStringParameter(args, "category", out var category, out var requiredError))
            return CreateToolErrorResponse(id, requiredError!);

        if (!SuggestionRecord.ValidCategories.Contains(category))
        {
            var similar = ConsoleUi.FindClosestMatches(category, SuggestionRecord.ValidCategories);
            var message = $"Invalid category: '{category}'. Must be one of: {string.Join(", ", SuggestionRecord.ValidCategories)}";
            if (similar.Count > 0)
                message += $". Did you mean: {string.Join(", ", similar)}?";
            return CreateToolErrorResponse(id, message, similar);
        }

        if (!TryReadRequiredStringParameter(args, "description", out var description, out requiredError))
            return CreateToolErrorResponse(id, requiredError!);

        if (description.Length > MaxDescriptionLength)
            return CreateToolErrorResponse(id, $"Description too long ({description.Length} chars, max {MaxDescriptionLength})");

        // 2. Validate optional parameters / 任意パラメータのバリデーション
        var language = args?["language"]?.GetValue<string>();
        var context = args?["context"]?.GetValue<string>();
        var toolInvocationContext = args?["toolInvocationContext"]?.GetValue<string>();
        var evidencePaths = ReadEvidencePaths(args?["evidencePaths"] ?? args?["evidence_paths"], out var evidencePathsError);
        if (evidencePathsError != null)
            return CreateToolErrorResponse(id, evidencePathsError);

        if (context != null && context.Length > MaxContextLength)
            return CreateToolErrorResponse(id, $"Context too long ({context.Length} chars, max {MaxContextLength})");
        if (toolInvocationContext != null && toolInvocationContext.Length > MaxContextLength)
            return CreateToolErrorResponse(id, $"Tool invocation context too long ({toolInvocationContext.Length} chars, max {MaxContextLength})");

        // 3. Source code leak detection — reject if code is detected
        //    ソースコード漏洩検出 — コードが検出されたら拒否
        if (SourceCodeDetector.ContainsSourceCode(description))
            return CreateToolErrorResponse(id, "Description appears to contain source code. Please describe the gap in natural language without including code.");

        if (context != null && SourceCodeDetector.ContainsSourceCode(context))
            return CreateToolErrorResponse(id, "Context appears to contain source code. Please describe what you were trying to do without including code.");
        if (toolInvocationContext != null && SourceCodeDetector.ContainsSourceCode(toolInvocationContext))
            return CreateToolErrorResponse(id, "Tool invocation context appears to contain source code. Please describe the invocation without including code.");

        var sampling = await TrySampleSuggestionMetadataAsync(category, language, description, context, toolInvocationContext).ConfigureAwait(false);

        // 4. Compute dedup hash / 重複排除ハッシュを計算
        var hash = SuggestionStore.ComputeHash(category, language, description);

        // 5. Resolve .cdidx directory and create if needed
        //    .cdidx ディレクトリを解決し、必要に応じて作成
        var cdidxDir = Path.GetDirectoryName(_dbPath);
        if (string.IsNullOrEmpty(cdidxDir))
            cdidxDir = Path.GetDirectoryName(Path.GetFullPath(_dbPath));
        if (string.IsNullOrEmpty(cdidxDir))
            cdidxDir = Path.Combine(Path.GetFullPath("."), ".cdidx");
        DataDirectorySecurity.CreatePrivateDirectory(cdidxDir);
        cdidxDir = Path.GetFullPath(cdidxDir);
        if (!TryProbeCdidxDirectoryWritable(cdidxDir, out var probeError))
            return CreateToolErrorResponse(id, probeError!);

        // 6. Store locally, reserve a submission attempt under the file lock,
        //    then call GitHub outside the lock so slow remote I/O does not block
        //    other suggestion-store writers.
        //    ローカル保存と送信試行の予約だけをファイルロック内で行い、
        //    GitHub 呼び出しはロック外で実行する。遅い remote I/O が他の
        //    suggestion-store writer をブロックしないようにする。
        // Derive DB identity for scoped suggestion storage.
        // スコープ付き提案蓄積のため DB identity を導出。
        var dbName = Path.GetFileNameWithoutExtension(_dbPath);
        var store = new SuggestionStore(cdidxDir, dbName, _timeProvider);
        var record = new SuggestionRecord
        {
            Category = category,
            Language = language,
            Description = description,
            Context = context,
            Hash = hash,
            CreatedByAgent = ResolveSuggestionAgent(),
            SessionId = _sessionId,
            ClientVersion = _version,
            McpClientName = _clientName,
            McpClientVersion = _clientVersion,
            ToolInvocationContext = toolInvocationContext,
            SampledTitle = sampling?.Title,
            SampledTags = sampling?.Tags,
            EvidencePaths = evidencePaths,
        };

        // Build GitHub submission callback (null if no token configured).
        // GitHub 送信コールバックを構築（トークン未設定なら null）。
        Func<SuggestionRecord, Task<SuggestionStore.SubmitAttemptResult>>? githubCallback = null;
        var githubTokenConfigured = GitHubIssueReporter.ResolveToken() != null;
        if (githubTokenConfigured)
        {
            var version = _version;
            var cancellationToken = _currentRequestToken.Value;
            githubCallback = r => GitHubIssueReporter.TryCreateIssueDetailedAsync(r, version, cancellationToken);
        }

        var result = await store.TryAddAndSubmitAsync(record, githubCallback).ConfigureAwait(false);

        if (!result.IsNew)
        {
            var dupPayload = new JsonObject
            {
                ["status"] = "duplicate",
                ["hash"] = hash,
                ["message"] = result.AlreadySubmitted
                    ? "This suggestion has already been recorded and submitted."
                    : result.UpstreamUrl != null
                        ? "This suggestion was already recorded. GitHub submission retried successfully."
                        : "This suggestion has already been recorded.",
                ["submitted_to_github"] = result.AlreadySubmitted || result.UpstreamUrl != null,
                ["github_submission_reason"] = ResolveGitHubSubmissionReason(result, githubTokenConfigured),
                ["lifecycle_status"] = JsonNamingPolicy.SnakeCaseLower.ConvertName(result.Status.ToString()),
                ["cdidx_dir"] = cdidxDir,
            };
            if (result.SubmissionError != null)
                dupPayload["github_submission_error"] = result.SubmissionError;
            if (result.DuplicateOfHash != null)
                dupPayload["duplicate_of"] = result.DuplicateOfHash;
            if (result.DuplicateScore != null)
                dupPayload["duplicate_score"] = result.DuplicateScore.Value;
            if (result.UpstreamUrl != null)
            {
                dupPayload["upstream_url"] = result.UpstreamUrl;
                dupPayload["github_issue_url"] = result.UpstreamUrl;
            }
            return CreateToolResult(id, "Duplicate suggestion (already recorded).", dupPayload);
        }

        // 7. Return success / 成功レスポンスを返す
        var payload = new JsonObject
        {
            ["status"] = "recorded",
            ["hash"] = hash,
            ["category"] = category,
            ["language"] = language,
            ["stored_locally"] = true,
            ["submitted_to_github"] = result.UpstreamUrl != null,
            ["github_submission_reason"] = ResolveGitHubSubmissionReason(result, githubTokenConfigured),
            ["lifecycle_status"] = JsonNamingPolicy.SnakeCaseLower.ConvertName(result.Status.ToString()),
            ["cdidx_dir"] = cdidxDir,
        };
        if (result.SubmissionError != null)
            payload["github_submission_error"] = result.SubmissionError;
        if (result.UpstreamUrl != null)
        {
            payload["upstream_url"] = result.UpstreamUrl;
            payload["github_issue_url"] = result.UpstreamUrl;
        }
        if (sampling?.Title != null)
            payload["sampled_title"] = sampling.Title;
        if (sampling?.Tags is { Length: > 0 })
            payload["sampled_tags"] = new JsonArray(sampling.Tags.Select(tag => JsonValue.Create(tag)).ToArray<JsonNode?>());
        if (evidencePaths is { Length: > 0 })
            payload["evidence_paths"] = new JsonArray(evidencePaths.Select(path => JsonValue.Create(path)).ToArray<JsonNode?>());
        return CreateToolResult(id, "Suggestion recorded. Thank you for the feedback.", payload);
    }

    private static string[]? ReadEvidencePaths(JsonNode? node, out string? error)
    {
        error = null;
        if (node == null)
            return null;
        if (node is not JsonArray array)
        {
            error = "evidencePaths must be an array of path strings.";
            return null;
        }
        if (array.Count > SuggestionEvidencePaths.MaxCount)
        {
            error = $"evidencePaths has too many entries ({array.Count}, max {SuggestionEvidencePaths.MaxCount}).";
            return null;
        }

        var paths = new List<string>();
        foreach (var item in array)
        {
            string? path;
            try
            {
                path = item?.GetValue<string>();
            }
            catch (InvalidOperationException)
            {
                error = "evidencePaths must contain only path strings.";
                return null;
            }

            if (string.IsNullOrWhiteSpace(path))
                continue;
            if (!SuggestionEvidencePaths.TryNormalize(path, out var normalizedPath, out var pathError))
            {
                error = pathError;
                return null;
            }
            if (normalizedPath.Length > 0 && !paths.Contains(normalizedPath, StringComparer.Ordinal))
                paths.Add(normalizedPath);
        }

        return paths.Count == 0 ? null : paths.ToArray();
    }

    private static string ResolveGitHubSubmissionReason(SuggestionStore.AddAndSubmitResult result, bool githubTokenConfigured)
    {
        if (result.AlreadySubmitted || result.UpstreamUrl != null)
            return "submitted";
        if (!githubTokenConfigured)
            return "token_not_configured";
        if (result.SubmissionError != null)
            return StartsWithHttpStatusCode(result.SubmissionError) ? "api_error" : "network_error";
        return "repo_not_configured";
    }

    private static bool StartsWithHttpStatusCode(string value)
    {
        return value.Length >= 4
            && char.IsDigit(value[0])
            && char.IsDigit(value[1])
            && char.IsDigit(value[2])
            && value[3] == ':';
    }

    private sealed record SuggestionSamplingResult(string? Title, string[]? Tags);

    private async Task<SuggestionSamplingResult?> TrySampleSuggestionMetadataAsync(
        string category,
        string? language,
        string description,
        string? context,
        string? toolInvocationContext)
    {
        if (!IsSamplingEnabled() || !HasClientCapability("sampling"))
            return null;

        var prompt = BuildSuggestionSamplingPrompt(category, language, description, context, toolInvocationContext);

        var result = await SendClientRequestAsync("sampling/createMessage", new JsonObject
        {
            ["messages"] = new JsonArray
            {
                new JsonObject
                {
                    ["role"] = "user",
                    ["content"] = new JsonObject
                    {
                        ["type"] = "text",
                        ["text"] = prompt,
                    }
                }
            },
            ["maxTokens"] = 200,
        }, _currentRequestToken.Value).ConfigureAwait(false);

        var text = ExtractSamplingText(result);
        if (string.IsNullOrWhiteSpace(text))
            return null;
        if (text.Length > MaxSamplingResponseTextChars)
            return null;
        try
        {
            var parsed = JsonNode.Parse(text, documentOptions: new JsonDocumentOptions { MaxDepth = MaxSamplingResponseJsonDepth });
            var title = SanitizeSampledTitle(TryReadStringValue(parsed?["title"]));
            var tags = parsed?["tags"] is JsonArray tagArray
                ? tagArray.Select(TryReadStringValue)
                    .Where(t => !string.IsNullOrWhiteSpace(t))
                    .Select(SanitizeSampledTag)
                    .Where(t => t != null)
                    .Cast<string>()
                    .Distinct(StringComparer.Ordinal)
                    .Take(6)
                    .ToArray()
                : null;
            if (title == null && (tags == null || tags.Length == 0))
                return null;
            return new SuggestionSamplingResult(title, tags is { Length: > 0 } ? tags : null);
        }
        catch (JsonException)
        {
            return null;
        }
    }

    private static string BuildSuggestionSamplingPrompt(
        string category,
        string? language,
        string description,
        string? context,
        string? toolInvocationContext)
    {
        var prompt = new StringBuilder();
        var remainingBytes = MaxSamplingPromptBytes;
        AppendSamplingPromptLine(prompt, "Extract structured metadata for a cdidx improvement suggestion.", ref remainingBytes);
        AppendSamplingPromptLine(prompt, "Return only compact JSON with keys: title (one line, <=80 chars) and tags (array of 1-6 lowercase identifiers).", ref remainingBytes);
        AppendSamplingPromptLine(prompt, "Do not include source code.", ref remainingBytes);
        AppendSamplingPromptField(prompt, "category", category, MaxSamplingShortFieldChars, ref remainingBytes);
        if (!string.IsNullOrWhiteSpace(language))
            AppendSamplingPromptField(prompt, "language", language, MaxSamplingShortFieldChars, ref remainingBytes);
        AppendSamplingPromptField(prompt, "description", description, MaxSamplingDescriptionChars, ref remainingBytes);
        if (!string.IsNullOrWhiteSpace(context))
            AppendSamplingPromptField(prompt, "context", context, MaxSamplingContextChars, ref remainingBytes);
        if (!string.IsNullOrWhiteSpace(toolInvocationContext))
        {
            var summary = SummarizeToolInvocationContextForSampling(toolInvocationContext);
            AppendSamplingPromptField(prompt, "tool_invocation_context", summary, MaxSamplingToolInvocationSummaryChars, ref remainingBytes);
        }

        return prompt.ToString();
    }

    private static void AppendSamplingPromptField(StringBuilder prompt, string name, string value, int maxChars, ref int remainingBytes)
    {
        var sanitized = SanitizeSamplingPromptField(value, maxChars);
        if (sanitized.Length == 0)
            return;
        AppendSamplingPromptLine(prompt, $"{name}: {sanitized}", ref remainingBytes);
    }

    private static void AppendSamplingPromptLine(StringBuilder prompt, string line, ref int remainingBytes)
    {
        if (remainingBytes <= 0)
            return;

        var lineBytes = Encoding.UTF8.GetByteCount(line) + 1;
        if (lineBytes > remainingBytes)
        {
            const string suffix = " ... [truncated]";
            var suffixBytes = Encoding.UTF8.GetByteCount(suffix);
            var prefixBudget = remainingBytes - suffixBytes - 1;
            if (prefixBudget <= 0)
                return;
            line = TruncateUtf8(line, prefixBudget).TrimEnd() + suffix;
            lineBytes = Encoding.UTF8.GetByteCount(line) + 1;
            if (lineBytes > remainingBytes)
                return;
        }

        prompt.Append(line);
        prompt.Append('\n');
        remainingBytes -= lineBytes;
    }

    private static string SanitizeSamplingPromptField(string value, int maxChars)
    {
        var collapsed = CollapseSamplingPromptWhitespace(value);
        if (collapsed.Length <= maxChars)
            return collapsed;
        var end = Math.Min(maxChars, collapsed.Length);
        if (end > 0 && char.IsHighSurrogate(collapsed[end - 1]))
            end--;
        return collapsed[..end].TrimEnd() + " ... [truncated]";
    }

    private static string CollapseSamplingPromptWhitespace(string value)
    {
        var trimmed = value.Trim();
        var collapsed = new StringBuilder(trimmed.Length);
        var previousWhitespace = false;
        foreach (var ch in trimmed)
        {
            if (char.IsControl(ch) || char.IsWhiteSpace(ch))
            {
                if (!previousWhitespace)
                    collapsed.Append(' ');
                previousWhitespace = true;
                continue;
            }

            collapsed.Append(ch);
            previousWhitespace = false;
        }

        return collapsed.ToString().Trim();
    }

    private static string SummarizeToolInvocationContextForSampling(string value)
    {
        var trimmed = value.Trim();
        var lineCount = CountLogicalLines(trimmed);
        var byteCount = Encoding.UTF8.GetByteCount(trimmed);
        return $"provided; {trimmed.Length} chars; {byteCount} UTF-8 bytes; {lineCount} line(s); raw content withheld";
    }

    private static int CountLogicalLines(string value)
    {
        if (value.Length == 0)
            return 0;
        var lines = 1;
        foreach (var ch in value)
        {
            if (ch == '\n')
                lines++;
        }
        return lines;
    }

    private static string TruncateUtf8(string value, int maxBytes)
    {
        if (maxBytes <= 0)
            return string.Empty;
        if (Encoding.UTF8.GetByteCount(value) <= maxBytes)
            return value;

        var low = 0;
        var high = value.Length;
        while (low < high)
        {
            var mid = low + ((high - low + 1) / 2);
            var bytes = Encoding.UTF8.GetByteCount(value.AsSpan(0, mid));
            if (bytes <= maxBytes)
                low = mid;
            else
                high = mid - 1;
        }

        if (low > 0 && char.IsHighSurrogate(value[low - 1]))
            low--;
        return value[..low];
    }

    private bool HasClientCapability(string name)
        => _clientCapabilities is JsonObject obj
            && obj.TryGetPropertyValue(name, out var node)
            && node is not null;

    private static bool IsSamplingEnabled()
    {
        var raw = Environment.GetEnvironmentVariable(SamplingEnabledEnvironmentVariable);
        return raw is null || !(raw.Equals("0", StringComparison.OrdinalIgnoreCase)
            || raw.Equals("false", StringComparison.OrdinalIgnoreCase)
            || raw.Equals("off", StringComparison.OrdinalIgnoreCase));
    }

    private static string? ExtractSamplingText(JsonNode? result)
    {
        if (result is null)
            return null;
        if (TryReadStringValue(result["content"]?["text"]) is { Length: > 0 } contentText)
            return contentText;
        if (result["content"] is JsonArray contentArray)
        {
            foreach (var item in contentArray)
            {
                if (TryReadStringValue(item?["text"]) is { Length: > 0 } itemText)
                    return itemText;
            }
        }
        return TryReadStringValue(result["text"]);
    }

    private static string? SanitizeSampledTitle(string? title)
    {
        if (string.IsNullOrWhiteSpace(title))
            return null;
        title = title.Trim();
        return title.Length <= 80 ? title : title[..80];
    }

    private static string? SanitizeSampledTag(string? tag)
    {
        if (string.IsNullOrWhiteSpace(tag))
            return null;
        var normalized = new string(tag.Trim().ToLowerInvariant().Select(ch => char.IsLetterOrDigit(ch) || ch == '_' || ch == '-' ? ch : '_').ToArray()).Trim('_');
        return normalized.Length == 0 ? null : normalized.Length <= 40 ? normalized : normalized[..40];
    }

    private static bool TryProbeCdidxDirectoryWritable(string cdidxDir, out string? error)
    {
        var probePath = Path.Combine(cdidxDir, $".write_probe.{Guid.NewGuid():N}.tmp");
        try
        {
            using (new FileStream(probePath, FileMode.CreateNew, FileAccess.Write, FileShare.None))
            {
            }
            File.Delete(probePath);
            error = null;
            return true;
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
        {
            error = $"Cannot write to .cdidx directory {cdidxDir}; check directory ownership, permissions, and read-only mounts. {ex.Message}";
            return false;
        }
    }

    private static CSharpStaticInterfaceWorkspaceSymbols BuildMcpCSharpStaticInterfaceWorkspaceSymbols(
        DbWriter writer,
        FileIndexer indexer,
        string projectRoot,
        IEnumerable<string> filePaths,
        CancellationToken cancellationToken = default)
    {
        var pendingSymbols = new List<SymbolRecord>();
        var pendingPaths = new HashSet<string>(StringComparer.Ordinal);
        foreach (var filePath in filePaths)
        {
            cancellationToken.ThrowIfCancellationRequested();
            var absolutePath = Path.IsPathRooted(filePath)
                ? filePath
                : Path.Combine(projectRoot, filePath.Replace('/', Path.DirectorySeparatorChar));
            var relativePath = FileIndexer.NormalizePathSeparators(Path.GetRelativePath(projectRoot, absolutePath));
            pendingPaths.Add(relativePath);

            var detection = FileIndexer.TryDetectLanguage(absolutePath);
            if (detection.Status != FileIndexer.FileProbeStatus.Supported
                || detection.Language != "csharp")
            {
                continue;
            }

            try
            {
                var (record, content, _, _) = indexer.BuildRecordWithRawBytes(absolutePath, cancellationToken);
                if (record.Lang != "csharp")
                    continue;

                pendingSymbols.AddRange(SymbolExtractor.Extract(0, record.Lang, content, record.Path, cancellationToken: cancellationToken));
            }
            catch (OperationCanceledException) when (cancellationToken.IsCancellationRequested)
            {
                throw;
            }
            catch
            {
            }
        }

        var symbols = writer.LoadCSharpStaticInterfaceContractSymbols(pendingPaths);
        symbols.AddRange(pendingSymbols);
        var hasContracts = symbols.Any(IsMcpCSharpStaticInterfaceContractSymbol)
            || writer.HasCSharpStaticInterfaceContractSymbolsInPaths(pendingPaths);
        return new CSharpStaticInterfaceWorkspaceSymbols(symbols, hasContracts);
    }

    private static bool IsMcpCSharpStaticInterfaceContractSymbol(SymbolRecord symbol)
        => symbol.Kind is "function" or "operator" or "property"
           && symbol.ContainerKind == "interface"
           && !string.IsNullOrWhiteSpace(symbol.Signature)
           && ContainsMcpCSharpWord(symbol.Signature!, "static")
           && (ContainsMcpCSharpWord(symbol.Signature!, "abstract")
               || ContainsMcpCSharpWord(symbol.Signature!, "virtual"));

    private static bool ContainsMcpCSharpWord(string text, string word)
    {
        var index = 0;
        while (index < text.Length)
        {
            index = text.IndexOf(word, index, StringComparison.Ordinal);
            if (index < 0)
                return false;

            var before = index == 0 ? '\0' : text[index - 1];
            var afterIndex = index + word.Length;
            var after = afterIndex >= text.Length ? '\0' : text[afterIndex];
            if (!IsMcpCSharpIdentifierPart(before) && !IsMcpCSharpIdentifierPart(after))
                return true;

            index += word.Length;
        }

        return false;
    }

    private static bool IsMcpCSharpIdentifierPart(char ch)
        => char.IsLetterOrDigit(ch) || ch == '_';

    private sealed record CSharpStaticInterfaceWorkspaceSymbols(
        IReadOnlyList<SymbolRecord> Symbols,
        bool HasStaticInterfaceContracts);

    private string ResolveSuggestionAgent()
    {
        return string.IsNullOrWhiteSpace(_caller) ? "unknown" : _caller;
    }

}

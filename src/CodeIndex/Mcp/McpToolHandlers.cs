using System.Diagnostics;
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

/// <summary>
/// MCP tool execution handlers (partial class split from McpServer.cs).
/// MCPツール実行ハンドラ（McpServer.csからのpartial class分割）。
/// </summary>
public partial class McpServer
{
    private const int DefaultBatchQueryResponseByteLimit = MaxLineByteLength;
    internal const int MaxBatchQueryResponseByteLimit = 10 * 1024 * 1024;
    internal const int MaxBatchQuerySize = 10;
    private const int BatchQueryIncrementalEstimatePaddingBytes = 512;
    private const int DefaultExcerptOutputByteLimit = MaxLineByteLength;
    private const string BatchQueryResponseByteLimitEnvVar = "CDIDX_MCP_BATCH_RESPONSE_MAX_BYTES";
    internal const int MaxMcpArrayFilterCount = QueryCommandRunner.MaxQueryPathFilterCount;
    internal const int MaxMcpArrayFilterStringLength = QueryCommandRunner.MaxQueryPathFilterLength;
    private static readonly HashSet<string> BoundedEnumLikeScalarArguments = new(StringComparer.Ordinal)
    {
        "category",
        "format",
        "groupBy",
        "alias",
        "capability",
        "extension",
        "kind",
        "lang",
        "language",
        "rankBy",
        "severity",
        "snippetFocus",
        "visibility",
        "excludeVisibility",
        "followSymlinks",
    };
    internal const int MaxMcpIndexFailureMessageLength = 512;
    internal static Action<string>? McpIndexFileCommittedForTesting { get; set; }
    internal static Action<string>? McpIndexFileContentLoadForTesting { get; set; }
    internal static Action? McpIndexFtsOptimizeForTesting { get; set; }
    internal static Action? McpIndexCSharpPrepassForTesting { get; set; }
    internal static Action? McpIndexCSharpMetadataResolveForTesting { get; set; }
    internal static Action? McpIndexTypeScriptAugmentationRebuildForTesting { get; set; }
    internal static Func<string, CancellationToken, UpdateCheckResult>? StatusUpdateCheckForTesting { get; set; }
    private QueryCommandRunner.ProjectFilterRootResolution? _projectFilterRootResolutionForCurrentToolCall;

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

        var parts = new List<string>
        {
            "cdidx is a local-first code-index server. Prefer CodeIndex MCP tools before shell grep/find/cat when investigating indexed repositories; use whole-file reads only after narrowing the target. cdidx は local-first なコード検索・取得サーバーです。インデックス済みリポジトリの調査では shell の grep/find/cat を乱発する前に CodeIndex MCP tools を優先し、ファイル全体の読み取りは対象を絞ってから使ってください。",
        };

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

        var guidedFlowTools = new List<string>();
        foreach (var name in new[] { "search", "definition", "references", "callers", "callees", "outline", "map", "excerpt" })
            if (On(name)) guidedFlowTools.Add(name);
        if (guidedFlowTools.Count > 0)
        {
            parts.Add("Investigation flow: search broadly, use definition for declarations, references for usage sites, callers/callees for call graph impact, outline/map for structure, then excerpt or resources/read for focused line ranges. Prefer pagination, path/lang filters, exactName/exactSubstring, and prefix over dumping large files. 調査順序: まず広く search し、宣言は definition、利用箇所は references、呼び出し影響は callers/callees、構造把握は outline/map、その後に excerpt または resources/read で必要な行範囲だけを読んでください。大きなファイルを丸ごと読む前に pagination、path/lang filter、exactName/exactSubstring、prefix で絞り込んでください。");
        }

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

        if (!double.TryParse(value.AsSpan(0, firstSeparator), NumberStyles.Float, CultureInfo.InvariantCulture, out var score)
            || !double.IsFinite(score))
            return false;
        if (!long.TryParse(value.AsSpan(firstSeparator + 1, lastSeparator - firstSeparator - 1), NumberStyles.None, CultureInfo.InvariantCulture, out var chunkId)
            || chunkId < 0)
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
            $"{toolName} returned no rows; relax exactName/path/lang/kind filters, try symbols for nearby names, or search related identifiers/error text before assuming the symbol is absent.",
            "symbols",
            args);
    }

    private static void AddNextStepSuggestion(JsonObject payload, string tool, JsonObject args, string suggestedAction)
    {
        payload["next_step_suggestion"] = new JsonObject
        {
            ["tool"] = tool,
            ["args"] = args,
            ["suggested_action"] = suggestedAction,
        };
    }

    private static JsonObject BuildExcerptArgs(string path, int startLine, int endLine)
        => new()
        {
            ["path"] = path,
            ["startLine"] = startLine,
            ["endLine"] = endLine,
        };

    private sealed class ArgumentAdjustmentCollector
    {
        private readonly JsonArray _warnings = [];
        private readonly JsonArray _adjustments = [];

        public int Count => _adjustments.Count;

        public void AddClamped(string argument, int requested, int effective, int minimum, int maximum)
        {
            var message = $"{argument} was clamped from {requested} to {effective} (server cap is [{minimum}, {maximum}]).";
            _warnings.Add(message);
            _adjustments.Add(new JsonObject
            {
                ["argument"] = argument,
                ["action"] = "clamped",
                ["requested"] = requested,
                ["effective"] = effective,
                ["minimum"] = minimum,
                ["maximum"] = maximum,
                ["message"] = message,
            });
        }

        public void AddIgnored(string argument, int requested, string reason)
        {
            var message = $"{argument} value {requested} was ignored: {reason}";
            _warnings.Add(message);
            _adjustments.Add(new JsonObject
            {
                ["argument"] = argument,
                ["action"] = "ignored",
                ["requested"] = requested,
                ["effective"] = null,
                ["message"] = message,
            });
        }

        public void AddWarning(string message)
        {
            _warnings.Add(message);
        }

        public void ApplyTo(JsonObject payload)
        {
            if (_warnings.Count > 0)
            {
                var warnings = payload["warnings"] as JsonArray ?? [];
                foreach (var warning in _warnings)
                    warnings.Add(warning?.DeepClone());
                payload["warnings"] = warnings;
            }
            if (_adjustments.Count > 0)
                payload["argument_adjustments"] = _adjustments.DeepClone();
        }
    }

    private static int ReadLimit(JsonNode? args, int defaultLimit, ArgumentAdjustmentCollector adjustments)
    {
        var requested = ReadOptionalIntArgument(args, "limit");
        var effective = Math.Clamp(requested ?? defaultLimit, 1, MaxLimit);
        if (requested.HasValue && requested.Value != effective)
            adjustments.AddClamped("limit", requested.Value, effective, 1, MaxLimit);
        return effective;
    }

    private static int ReadOffset(JsonNode? args, ArgumentAdjustmentCollector adjustments)
    {
        var requested = ReadOptionalIntArgument(args, "offset");
        var effective = Math.Clamp(requested ?? 0, 0, MaxMcpPaginationOffset);
        if (requested.HasValue && requested.Value != effective)
            adjustments.AddClamped("offset", requested.Value, effective, 0, MaxMcpPaginationOffset);
        return effective;
    }

    private static int ReadSnippetLines(JsonNode? args, int defaultSnippetLines, ArgumentAdjustmentCollector adjustments)
    {
        var requested = ReadOptionalIntArgument(args, "snippetLines");
        var effective = SearchSnippetFormatter.ClampSnippetLines(requested ?? defaultSnippetLines);
        if (requested.HasValue && requested.Value != effective)
            adjustments.AddClamped("snippetLines", requested.Value, effective, 1, SearchSnippetFormatter.MaxSnippetLines);
        return effective;
    }

    private static int? ReadMapDepth(JsonNode? args, ArgumentAdjustmentCollector adjustments)
    {
        var requested = ReadOptionalIntArgument(args, "depth");
        if (!requested.HasValue)
            return null;
        if (requested.Value < 0)
        {
            adjustments.AddIgnored("depth", requested.Value, "depth must be greater than or equal to 0.");
            return null;
        }

        var effective = Math.Min(requested.Value, MaxMcpMapDepth);
        if (effective != requested.Value)
            adjustments.AddClamped("depth", requested.Value, effective, 0, MaxMcpMapDepth);
        return effective;
    }

    private static int? ReadOptionalIntArgument(JsonNode? args, string propertyName)
        => args?[propertyName] is JsonValue value && value.TryGetValue<int>(out var parsed)
            ? parsed
            : null;

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
        var maxLineWidthValue = ReadOptionalIntArgument(args, propertyName);
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
        var node = args?[propertyName];
        if (node is JsonArray array)
        {
            return array.Select(node => node is JsonValue value && value.TryGetValue<string>(out var text) ? text : null)
                .Where(value => !string.IsNullOrWhiteSpace(value))
                .Cast<string>()
                .ToList();
        }

        if (node is JsonValue scalar && scalar.TryGetValue<string>(out var scalarText) && !string.IsNullOrWhiteSpace(scalarText))
            return [scalarText];

        return [];
    }

    private static List<string> ReadStringOrArrayList(JsonNode? args, string propertyName)
    {
        var node = args?[propertyName];
        if (node is JsonArray array)
        {
            return array.Select(item => item is JsonValue value && value.TryGetValue<string>(out var text) ? text : null)
                .Where(value => !string.IsNullOrWhiteSpace(value))
                .Cast<string>()
                .ToList();
        }

        return node is JsonValue scalar && scalar.TryGetValue<string>(out var scalarText) && !string.IsNullOrWhiteSpace(scalarText)
            ? [scalarText]
            : [];
    }

    private static List<string> ReadStringOrCommaSeparatedList(JsonNode? args, string propertyName)
        => ReadStringOrArrayList(args, propertyName)
            .SelectMany(value => value.Split(',', StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries))
            .Where(value => !string.IsNullOrWhiteSpace(value))
            .Select(value => value.ToLowerInvariant())
            .Distinct(StringComparer.Ordinal)
            .ToList();

    private JsonNode? TryReadSearchGuardFilters(JsonNode? id, JsonNode? args, out List<SearchGuardFilter> filters)
    {
        filters = [];
        var collected = new List<SearchGuardFilter>();

        JsonNode? AddFilter(string propertyName, SearchGuardRole role, SearchGuardDirection direction, string value)
        {
            if (collected.Count >= DbReader.MaxSearchGuardFilters)
                return CreateToolErrorResponse(id, $"search accepts at most {DbReader.MaxSearchGuardFilters} guard filters; got {collected.Count + 1}.");

            if (string.IsNullOrWhiteSpace(value))
                return CreateToolErrorResponse(id, $"'{propertyName}' entries must be non-empty strings.");
            if (value.Length > QueryLimits.MaxQueryLength)
                return CreateToolErrorResponse(id, $"'{propertyName}' query too long (max {QueryLimits.MaxQueryLength} characters).");

            collected.Add(new SearchGuardFilter(role, direction, value));
            return null;
        }

        JsonNode? AddFilters(string propertyName, SearchGuardRole role, SearchGuardDirection direction)
        {
            var node = args?[propertyName];
            if (node is null)
                return null;

            if (node is JsonValue singleValue && singleValue.TryGetValue<string>(out var singleText))
                return AddFilter(propertyName, role, direction, singleText);

            if (node is JsonArray array)
            {
                foreach (var item in array)
                {
                    if (item is not JsonValue value || !value.TryGetValue<string>(out var text))
                        return CreateToolErrorResponse(id, $"'{propertyName}' entries must be strings.");
                    if (AddFilter(propertyName, role, direction, text) is JsonNode addError)
                        return addError;
                }
                return null;
            }

            return CreateToolErrorResponse(id, $"'{propertyName}' must be a string or string array.");
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
        return null;
    }

    private JsonNode? TryReadSearchGuardScope(JsonNode? id, JsonNode? args, out SearchGuardScope guardScope)
    {
        guardScope = SearchGuardScope.Window;
        var node = args?["guardScope"];
        if (node is null)
            return null;
        if (node is not JsonValue value || !value.TryGetValue<string>(out var rawScope))
            return CreateToolErrorResponse(id, "'guardScope' must be a string: window or same-line.");

        switch (rawScope.Trim().ToLowerInvariant().Replace("_", "-"))
        {
            case "window":
                guardScope = SearchGuardScope.Window;
                return null;
            case "same-line":
            case "sameline":
                guardScope = SearchGuardScope.SameLine;
                return null;
            default:
                return CreateToolErrorResponse(id, $"'guardScope' must be window or same-line; got '{rawScope}'.");
        }
    }

    private static JsonObject? ValidateCommonListArguments(JsonNode? args)
    {
        foreach (var propertyName in new[] { "path", "project", "excludePaths", "names", "sections", "capability", "scopes", "visibility", "excludeVisibility", "includeSymbolKind", "excludeSymbolKind", "commits", "changedBetween", "files" })
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
            return obj.Count == 0 ? null : AddUnknownArgumentData(
                new JsonObject
                {
                    ["message"] = $"Tool '{toolName}' does not accept arguments.",
                    ["tool"] = toolName,
                },
                toolName,
                obj.First().Key);

        foreach (var property in obj)
        {
            if (!allowed.Contains(property.Key))
            {
                return AddUnknownArgumentData(
                    new JsonObject
                    {
                        ["message"] = $"Unknown argument '{McpBoundedText.ForDisplay(property.Key).Text}' for tool '{toolName}'.",
                        ["tool"] = toolName,
                    },
                    toolName,
                    property.Key);
            }

        }

        if (ValidateToolArgumentTypes(toolName, obj) is JsonObject typeError)
            return typeError;

        if (ValidateToolArgumentRanges(toolName, obj) is JsonObject rangeError)
            return rangeError;

        if (ValidateBoundedEnumLikeScalarArguments(toolName, obj) is JsonObject scalarError)
            return scalarError;

        return null;
    }

    private static JsonObject? ValidateToolArgumentRanges(string toolName, JsonObject args)
    {
        if (args["limit"] is JsonValue limitValue
            && limitValue.TryGetValue<int>(out var limit)
            && limit <= 0)
            return CreateIntegerMinimumArgumentError(toolName, "limit", minimum: 1, actual: limit);

        if (args["offset"] is JsonValue offsetValue
            && offsetValue.TryGetValue<int>(out var offset)
            && offset < 0)
            return CreateIntegerMinimumArgumentError(toolName, "offset", minimum: 0, actual: offset);

        if (args["maxSymbolsPerFile"] is JsonValue maxSymbolsValue
            && maxSymbolsValue.TryGetValue<int>(out var maxSymbolsPerFile)
            && (maxSymbolsPerFile <= 0 || maxSymbolsPerFile > IndexCommandRunner.MaxSymbolsPerFileLimit))
            return CreateIntegerRangeArgumentError(toolName, "maxSymbolsPerFile", 1, IndexCommandRunner.MaxSymbolsPerFileLimit, maxSymbolsPerFile);

        if (args["parallelism"] is JsonValue parallelismValue
            && parallelismValue.TryGetValue<int>(out var parallelism)
            && (parallelism <= 0 || parallelism > IndexCommandRunner.MaxIndexParallelism))
            return CreateIntegerRangeArgumentError(toolName, "parallelism", 1, IndexCommandRunner.MaxIndexParallelism, parallelism);

        if (args["debounce"] is JsonValue debounceValue
            && debounceValue.TryGetValue<int>(out var debounce)
            && (debounce < 0 || debounce > IndexWatchRunner.MaxDebounceMs))
            return CreateIntegerRangeArgumentError(toolName, "debounce", 0, IndexWatchRunner.MaxDebounceMs, debounce);

        if (args["maxResponseBytes"] is JsonValue maxResponseBytesValue
            && maxResponseBytesValue.TryGetValue<int>(out var maxResponseBytes)
            && maxResponseBytes <= 0)
            return CreateIntegerMinimumArgumentError(toolName, "maxResponseBytes", minimum: 1, actual: maxResponseBytes);

        return null;
    }

    private static JsonObject CreateIntegerMinimumArgumentError(string toolName, string argumentName, int minimum, int actual) => new()
    {
        ["message"] = $"Argument '{argumentName}' on tool '{toolName}' must be greater than or equal to {minimum}; got {actual}.",
        ["tool"] = toolName,
        ["parameter"] = argumentName,
        ["minimum"] = minimum,
        ["actual"] = actual,
        ["jsonrpc_invalid_params"] = true,
    };

    private static JsonObject CreateIntegerRangeArgumentError(string toolName, string argumentName, int minimum, int maximum, int actual) => new()
    {
        ["message"] = $"Argument '{argumentName}' on tool '{toolName}' must be between {minimum} and {maximum}; got {actual}.",
        ["tool"] = toolName,
        ["parameter"] = argumentName,
        ["minimum"] = minimum,
        ["maximum"] = maximum,
        ["actual"] = actual,
        ["jsonrpc_invalid_params"] = true,
    };

    private static JsonObject? ValidateBoundedEnumLikeScalarArguments(string toolName, JsonObject args)
    {
        foreach (var property in args)
        {
            if (!BoundedEnumLikeScalarArguments.Contains(property.Key))
                continue;
            if (property.Value is not JsonValue value || !value.TryGetValue<string>(out var scalar))
                continue;
            if (scalar.Length <= McpBoundedText.MaxScalarArgumentChars)
                continue;

            var display = McpBoundedText.ForDisplay(scalar);
            var error = new JsonObject
            {
                ["message"] = $"Argument '{property.Key}' on tool '{toolName}' is too long (max {McpBoundedText.MaxScalarArgumentChars} characters): '{display.Text}'.",
                ["tool"] = toolName,
                ["parameter"] = property.Key,
                ["value"] = display.Text,
                ["max_length"] = McpBoundedText.MaxScalarArgumentChars,
                ["actual_length"] = display.OriginalLength,
            };
            display.AddMetadata(error, "value");
            return error;
        }

        return null;
    }

    private static JsonObject AddUnknownArgumentData(JsonObject error, string toolName, string argumentName)
    {
        var display = McpBoundedText.ForDisplay(argumentName);
        error["unknown_argument"] = display.Text;
        display.AddMetadata(error, "unknown_argument");
        return AddArgumentCompatibilityData(error, toolName, argumentName);
    }

    private static JsonObject AddArgumentCompatibilityData(JsonObject error, string toolName, string argumentName)
    {
        switch (toolName, argumentName)
        {
            case ("definition", "lspCompatible"):
            case ("references", "lspCompatible"):
                error["alias_of"] = "lsp_compatible";
                break;
            case ("search", "exact"):
                error["alias_of"] = "exactSubstring";
                error["deprecated"] = true;
                error["deprecation_reason"] = "Use `exactSubstring` for search exact substring matching.";
                break;
            case ("definition", "exact"):
            case ("references", "exact"):
            case ("callers", "exact"):
            case ("callees", "exact"):
            case ("symbols", "exact"):
            case ("analyze_symbol", "exact"):
                error["alias_of"] = "exactName";
                error["deprecated"] = true;
                error["deprecation_reason"] = "Use `exactName` for exact symbol-name matching.";
                break;
            case ("impact_analysis", "maxDepth"):
                error["alias_of"] = "maxHops";
                error["deprecated"] = true;
                error["deprecation_reason"] = "Use `maxHops`; `maxDepth` is retained for compatibility.";
                break;
        }

        return error;
    }

    private static JsonObject? ValidateToolArgumentTypes(string toolName, JsonObject args)
    {
        foreach (var property in args)
        {
            if (TryGetExpectedJsonType(toolName, property.Key, out var expected)
                && !MatchesExpectedJsonType(property.Value, expected))
            {
                return AddArgumentCompatibilityData(new JsonObject
                {
                    ["message"] = $"Invalid type for argument '{property.Key}' on tool '{toolName}'. Expected {expected}.",
                    ["tool"] = toolName,
                    ["parameter"] = property.Key,
                    ["expected"] = expected,
                    ["actual"] = DescribeJsonType(property.Value),
                    ["jsonrpc_invalid_params"] = true,
                }, toolName, property.Key);
            }
        }

        return null;
    }

    private static bool TryGetExpectedJsonType(string toolName, string argumentName, out string expected)
    {
        if (argumentName is "names" or "sections")
        {
            expected = "array";
            return true;
        }

        if (argumentName == "excludePaths")
        {
            expected = "string_or_array";
            return true;
        }

        if (argumentName == "path")
        {
            expected = ToolAllowsStringOrArrayPath(toolName) ? "string_or_array" : "string";
            return true;
        }

        expected = argumentName switch
        {
            "limit" or "offset" or "snippetLines" or "maxLineWidth" or "before" or "after" or
                "focusLine" or "focusColumn" or "focusLength" or "startLine" or "endLine" or
                "maxHops" or "maxDepth" or "depth" or "parallelism" or "maxFileBytes" or "maxSymbolsPerFile" or "maxReferencesPerFile" or "debounce" or
                "staleAfterSeconds" or
                "guardWindow" or "maxOutputBytes" or "maxResponseBytes" => "integer",
            "check" or "excludeTests" or "includeGenerated" or "indexedOnly" or "rawQuery" or "noDedup" or "exactSubstring" or
                "exactName" or "exact" or "prefix" or "countOnly" or "includeBody" or "lsp_compatible" or
                "lspCompatible" or
                "regex" or "withPaths" or "rebuild" or "dryRun" or "dry_run" or "force" or
                "optimize" or "reverse" or "cycles" or "config" or "logPath" or "updateCheck" or
                "rawKinds" or "orderBySize" or "rawBytes" or "byBucket" or "memoryTrace" or "watch" or
                "estimateOnly" or "listRecipes" => "boolean",
            "project" or "capability" or "scopes" or "visibility" or "excludeVisibility" or "includeSymbolKind" or "excludeSymbolKind" or
                "commits" or "changedBetween" or "files" or
                "requireBefore" or "requireAfter" or "rejectBefore" or "rejectAfter" => "string_or_array",
            "query" or "lang" or "kind" or "format" or "rankBy" or "since" or "cursor" or "guardScope" or
                "solution" or "symbol" or "groupBy" or "category" or "language" or "severity" or "explain" or "snippetFocus" or
                "bucket" or "minConfidence" or "extension" or "alias" or "description" or "context" or "toolInvocationContext" or "db" or
                "followSymlinks" or "recipe" or "auditScope" => "string",
            "minEntrypointConfidence" => "number",
            "queries" or "evidencePaths" or "evidence_paths" => "array",
            _ => string.Empty,
        };

        if (expected.Length == 0)
            return false;

        return true;
    }

    private static bool ToolAllowsStringOrArrayPath(string toolName) => toolName switch
    {
        "search" or "definition" or "references" or "callers" or "callees" or "symbols" or
        "files" or "find_in_file" or "map" or "analyze_symbol" or "deps" or "impact_analysis" or
        "validate" or "unused_symbols" or "symbol_hotspots" => true,
        _ => false,
    };

    private static bool MatchesExpectedJsonType(JsonNode? node, string expected) => expected switch
    {
        "integer" => node is JsonValue value && value.TryGetValue<int>(out _),
        "boolean" => node is JsonValue value && value.TryGetValue<bool>(out _),
        "string" => node is JsonValue value && value.TryGetValue<string>(out _),
        "string_or_array" => node is JsonArray || node is JsonValue value && value.TryGetValue<string>(out _),
        "array" => node is JsonArray,
        "number" => node is JsonValue value && value.TryGetValue<double>(out _),
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
                    ["max_count"] = MaxMcpArrayFilterCount,
                    ["actual_count"] = array.Count,
                };

            var invalidCount = 0;
            var invalidSamples = new JsonArray();
            var hasTooLongEntry = false;
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
                    hasTooLongEntry = true;
                    if (invalidSamples.Count < 3)
                        invalidSamples.Add($"[{i}] length {text.Length}");
                }
            }

            if (invalidCount > 0 && (propertyName != "names" || invalidCount != array.Count || hasTooLongEntry))
                return new JsonObject
                {
                    ["message"] = $"{propertyName} contains {invalidCount} invalid entr{(invalidCount == 1 ? "y" : "ies")}. Entries must be non-empty strings no longer than {MaxMcpArrayFilterStringLength} characters.",
                    ["invalid_count"] = invalidCount,
                    ["invalid_samples"] = invalidSamples,
                    ["max_length"] = MaxMcpArrayFilterStringLength,
                };
            return null;
        }

        if (node is JsonValue scalar && scalar.TryGetValue<string>(out var scalarText))
        {
            if (propertyName is "names" or "sections")
                return new JsonObject
                {
                    ["message"] = $"{propertyName} must be an array of strings.",
                    ["invalid_count"] = 1,
                };
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
                    ["max_length"] = MaxMcpArrayFilterStringLength,
                    ["actual_length"] = scalarText.Length,
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

    private static bool TryReadLspCompatibleArgument(JsonNode? args, out bool lspCompatible, out string? error)
    {
        var snakeNode = args?["lsp_compatible"];
        var camelNode = args?["lspCompatible"];
        var snakeProvided = snakeNode is not null;
        var camelProvided = camelNode is not null;
        var snakeValue = snakeNode?.GetValue<bool>() ?? false;
        var camelValue = camelNode?.GetValue<bool>() ?? false;

        if (snakeProvided && camelProvided && snakeValue != camelValue)
        {
            lspCompatible = false;
            error = "Pass only one of 'lsp_compatible' or 'lspCompatible', or give both aliases the same value.";
            return false;
        }

        lspCompatible = snakeProvided ? snakeValue : camelValue;
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

    private const int SearchEnvelopeMinCandidates = 200;
    private const int SearchEnvelopeOverFetchFactor = 50;
    private const int SearchEnvelopeMaxCandidates = 10_000;
    internal const int MaxMcpEnvelopeFetchLimit = MaxLimit + 1;

    private static int FetchLimitForEnvelope(int limit)
    {
        if (limit <= 0)
            return 1;

        var requested = (long)limit + 1;
        return (int)Math.Min(MaxMcpEnvelopeFetchLimit, requested);
    }

    internal static int FetchLimitForEnvelopeForTests(int limit) => FetchLimitForEnvelope(limit);

    private static int FetchLimitForSearchRecipeEnvelope(int limit)
    {
        if (limit <= 0)
            return 1;

        var requested = (long)limit + 1;
        var overFetched = requested * SearchEnvelopeOverFetchFactor;
        var candidateLimit = Math.Max(SearchEnvelopeMinCandidates, Math.Max(requested, overFetched));
        return (int)Math.Min(SearchEnvelopeMaxCandidates, candidateLimit);
    }

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
        if (truncated)
        {
            payload["pagination_hint"] = new JsonObject
            {
                ["suggested_action"] = "More rows are available; continue with the provided cursor/next_offset when you need breadth, or narrow with path/lang/kind/excludeTests/format filters before reading details.",
            };
        }
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
        var counts = new Dictionary<string, int>(StringComparer.Ordinal);
        foreach (var result in results)
        {
            var path = pathSelector(result);
            if (string.IsNullOrWhiteSpace(path))
                continue;

            counts[path] = counts.TryGetValue(path, out var count) ? count + 1 : 1;
        }

        var histogram = new JsonArray();
        foreach (var (path, count) in counts
            .OrderByDescending(kv => kv.Value)
            .ThenBy(kv => kv.Key, StringComparer.Ordinal)
            .Take(5))
        {
            histogram.Add(new JsonObject
            {
                ["path"] = path,
                ["count"] = count,
            });
        }

        return histogram;
    }

    private static bool MatchesRecipeFacetMetadata(CompactSearchResult result, SearchAuditRecipeQuery recipeQuery)
    {
        if (recipeQuery.MatchOrigins.Count > 0 &&
            !result.MatchOrigins.Any(origin => recipeQuery.MatchOrigins.Contains(origin, StringComparer.Ordinal)))
        {
            return false;
        }

        if (recipeQuery.ExcludeOrigins.Count > 0 &&
            result.MatchOrigins.Count > 0 &&
            result.MatchOrigins.All(origin => recipeQuery.ExcludeOrigins.Contains(origin, StringComparer.Ordinal)))
        {
            return false;
        }

        return recipeQuery.ResultKinds.Count == 0 ||
               result.ResultKinds.Any(kind => recipeQuery.ResultKinds.Contains(kind, StringComparer.Ordinal));
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

    private static JsonArray ToJsonStringArray(IEnumerable<string> values)
    {
        var array = new JsonArray();
        foreach (var value in values)
            array.Add(value);
        return array;
    }

    private static void AddVisibilityFilterEcho(JsonObject payload, IReadOnlyList<string> visibilityFilters, IReadOnlyList<string> excludeVisibilityFilters)
    {
        payload["visibility"] = ToJsonStringArray(visibilityFilters);
        payload["excludeVisibility"] = ToJsonStringArray(excludeVisibilityFilters);
    }

    private static JsonArray BuildCompactSymbolRows(IEnumerable<SymbolResult> results)
    {
        var rows = new JsonArray();
        foreach (var result in results)
        {
            var row = new JsonObject
            {
                ["file"] = result.Path,
                ["line"] = result.Line,
                ["kind"] = result.Kind,
                ["name"] = result.Name,
            };
            if (result.Lang != null)
                row["lang"] = result.Lang;
            if (result.Visibility != null)
                row["visibility"] = result.Visibility;
            if (result.ContainerName != null)
                row["container"] = result.ContainerName;
            rows.Add(row);
        }
        return rows;
    }

    private static JsonArray BuildCompactReferenceRows(IEnumerable<ReferenceResult> results)
    {
        var rows = new JsonArray();
        foreach (var result in results)
        {
            rows.Add(new JsonObject
            {
                ["file"] = result.Path,
                ["line"] = result.Line,
                ["column"] = result.Column,
                ["symbol"] = result.SymbolName,
                ["kind"] = result.ReferenceKind,
            });
        }
        return rows;
    }

    private static JsonArray BuildCompactCallerRows(IEnumerable<CallerResult> results)
    {
        var rows = new JsonArray();
        foreach (var result in results)
        {
            rows.Add(new JsonObject
            {
                ["file"] = result.Path,
                ["line"] = result.FirstLine,
                ["caller_kind"] = result.CallerKind,
                ["caller"] = result.CallerName,
                ["callee"] = result.CalleeName,
                ["reference_kind"] = result.ReferenceKind,
                ["reference_count"] = result.ReferenceCount,
            });
        }
        return rows;
    }

    private static JsonArray BuildCompactCalleeRows(IEnumerable<CalleeResult> results)
    {
        var rows = new JsonArray();
        foreach (var result in results)
        {
            rows.Add(new JsonObject
            {
                ["file"] = result.Path,
                ["line"] = result.FirstLine,
                ["caller_kind"] = result.CallerKind,
                ["caller"] = result.CallerName,
                ["callee"] = result.CalleeName,
                ["reference_kind"] = result.ReferenceKind,
                ["reference_count"] = result.ReferenceCount,
            });
        }
        return rows;
    }

    private JsonObject BuildUnusedSymbolsByBucket(IEnumerable<UnusedSymbolResult> results)
    {
        var grouped = results
            .GroupBy(result => result.UnusedBucket, StringComparer.Ordinal)
            .ToDictionary(group => group.Key, group => group.ToList(), StringComparer.Ordinal);
        var buckets = new JsonObject();
        foreach (var bucket in QueryCommandRunner.OrderedUnusedBuckets)
        {
            if (grouped.TryGetValue(bucket, out var rows))
                buckets[bucket] = ToJsonArray(rows);
        }
        foreach (var (bucket, rows) in grouped.OrderBy(kv => kv.Key, StringComparer.Ordinal))
        {
            if (!buckets.ContainsKey(bucket))
                buckets[bucket] = ToJsonArray(rows);
        }
        return buckets;
    }

    private JsonObject BuildAnalyzeSymbolCountPayload(SymbolAnalysisResult analysis, string? lang, JsonNode? pathEcho, bool excludeTests, int maxLineWidth)
    {
        var paths = analysis.Definitions.Select(definition => definition.Path)
            .Concat(analysis.References.Select(reference => reference.Path))
            .Concat(analysis.Callers.Select(caller => caller.Path))
            .Concat(analysis.Callees.Select(callee => callee.Path));
        var payload = new JsonObject
        {
            ["format"] = "count",
            ["count_only"] = true,
            ["query"] = analysis.Query,
            ["lang"] = lang,
            ["path"] = pathEcho?.DeepClone(),
            ["excludeTests"] = excludeTests,
            ["maxLineWidth"] = maxLineWidth,
            ["file_found"] = analysis.File != null,
            ["definition_count"] = analysis.Definitions.Count,
            ["nearby_symbol_count"] = analysis.NearbySymbols.Count,
            ["reference_count"] = analysis.References.Count,
            ["caller_count"] = analysis.Callers.Count,
            ["callee_count"] = analysis.Callees.Count,
            ["graph_language"] = analysis.GraphLanguage,
            ["graph_supported"] = analysis.GraphSupported,
            ["graph_support_reason"] = analysis.GraphSupportReason,
            ["graph_table_available"] = analysis.GraphTableAvailable,
            ["workspace_indexed_at"] = JsonSerializer.SerializeToNode(analysis.WorkspaceIndexedAt, _jsonOptions),
            ["workspace_latest_modified"] = JsonSerializer.SerializeToNode(analysis.WorkspaceLatestModified, _jsonOptions),
            ["top_files"] = BuildTopFileHistogram(paths, path => path),
            ["results"] = new JsonArray(),
        };
        if (analysis.ExactIndexAvailable.HasValue)
            payload["exact_index_available"] = analysis.ExactIndexAvailable.Value;
        if (analysis.DegradedReason != null)
            payload["degraded_reason"] = analysis.DegradedReason;
        return payload;
    }

    private JsonObject BuildAnalyzeSymbolCompactPayload(SymbolAnalysisResult analysis, string? lang, JsonNode? pathEcho, bool excludeTests, int maxLineWidth)
    {
        var payload = new JsonObject
        {
            ["api_version"] = analysis.ApiVersion,
            ["format"] = "compact",
            ["query"] = analysis.Query,
            ["lang"] = lang,
            ["path"] = pathEcho?.DeepClone(),
            ["excludeTests"] = excludeTests,
            ["maxLineWidth"] = maxLineWidth,
            ["file"] = analysis.File == null
                ? null
                : new JsonObject
                {
                    ["path"] = analysis.File.Path,
                    ["lang"] = analysis.File.Lang,
                    ["lines"] = analysis.File.Lines,
                    ["size"] = analysis.File.Size,
                },
            ["workspace_indexed_at"] = JsonSerializer.SerializeToNode(analysis.WorkspaceIndexedAt, _jsonOptions),
            ["workspace_latest_modified"] = JsonSerializer.SerializeToNode(analysis.WorkspaceLatestModified, _jsonOptions),
            ["graph_language"] = analysis.GraphLanguage,
            ["graph_supported"] = analysis.GraphSupported,
            ["graph_support_reason"] = analysis.GraphSupportReason,
            ["graph_table_available"] = analysis.GraphTableAvailable,
            ["definition_count"] = analysis.Definitions.Count,
            ["nearby_symbol_count"] = analysis.NearbySymbols.Count,
            ["reference_count"] = analysis.References.Count,
            ["caller_count"] = analysis.Callers.Count,
            ["callee_count"] = analysis.Callees.Count,
            ["definitions"] = BuildCompactSymbolRows(analysis.Definitions),
            ["nearby_symbols"] = BuildCompactSymbolRows(analysis.NearbySymbols),
            ["references"] = BuildCompactReferenceRows(analysis.References),
            ["callers"] = BuildCompactCallerRows(analysis.Callers),
            ["callees"] = BuildCompactCalleeRows(analysis.Callees),
        };
        if (analysis.ExactIndexAvailable.HasValue)
            payload["exact_index_available"] = analysis.ExactIndexAvailable.Value;
        if (analysis.DegradedReason != null)
            payload["degraded_reason"] = analysis.DegradedReason;
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

    private List<string>? ReadScopedPathList(JsonNode? args)
    {
        _projectFilterRootResolutionForCurrentToolCall = null;
        var paths = ReadPathList(args, "path") ?? [];
        var projects = ReadPathList(args, "project") ?? [];
        if (projects.Count == 0)
            return paths.Count == 0 ? null : paths;

        var solution = args?["solution"]?.GetValue<string>();
        var projectRoot = ResolveProjectFilterRoot();
        _projectFilterRootResolutionForCurrentToolCall = projectRoot;
        foreach (var glob in SolutionProjectResolver.ResolveProjectDirectoryGlobs(projectRoot.Root, projects, solution))
            paths.Add(glob);
        return paths.Count == 0 ? null : paths;
    }

    private JsonObject? ValidateProjectFilterArguments(JsonNode? args)
    {
        var projects = ReadPathList(args, "project") ?? [];
        if (projects.Count == 0)
            return null;

        var solution = args?["solution"]?.GetValue<string>();
        var projectRoot = ResolveProjectFilterRoot();
        try
        {
            _ = SolutionProjectResolver.ResolveProjectDirectoryGlobs(projectRoot.Root, projects, solution);
            return null;
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException or InvalidOperationException)
        {
            var diagnostic = FormatMcpCaughtExceptionDiagnostic(ex);
            var error = new JsonObject
            {
                ["message"] = $"Project filter could not be resolved: {diagnostic.Text}",
                ["parameter"] = "project",
                ["diagnostic"] = diagnostic.Text,
                ["project_filter_root"] = projectRoot.Root,
            };
            if (!string.IsNullOrWhiteSpace(projectRoot.FallbackReason))
                error["project_filter_root_fallback_reason"] = projectRoot.FallbackReason;
            diagnostic.AddMetadata(error, "diagnostic");
            return error;
        }
    }

    private QueryCommandRunner.ProjectFilterRootResolution ResolveProjectFilterRoot()
        => QueryCommandRunner.ResolveProjectFilterRoot(_dbPath, _dbPathExplicit);

    private void AddProjectFilterRootDiagnostics(JsonObject payload)
    {
        var projectRoot = _projectFilterRootResolutionForCurrentToolCall;
        _projectFilterRootResolutionForCurrentToolCall = null;
        if (!projectRoot.HasValue)
            return;

        payload["project_filter_root"] = projectRoot.Value.Root;
        if (!string.IsNullOrWhiteSpace(projectRoot.Value.FallbackReason))
            payload["project_filter_root_fallback_reason"] = projectRoot.Value.FallbackReason;
    }

    private void ClearProjectFilterRootDiagnostics()
        => _projectFilterRootResolutionForCurrentToolCall = null;

    private static bool TryReadSinceArgument(JsonNode? args, out DateTime? since, out string? error)
    {
        var sinceStr = args?["since"]?.GetValue<string>();
        if (sinceStr == null)
        {
            since = null;
            error = null;
            return true;
        }

        if (QueryCommandRunner.TryParseIso8601Since(sinceStr, out var parsedSince))
        {
            since = parsedSince;
            error = null;
            return true;
        }

        since = null;
        error = $"Invalid 'since' timestamp: '{sinceStr}'. Use ISO 8601 format (e.g. 2024-01-01 or 2024-01-01T00:00:00Z).";
        return false;
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

    private static bool TryReadRequiredPathParameter(JsonNode? args, string propertyName, out string value, out string? error)
    {
        if (!TryReadRequiredStringParameter(args, propertyName, out value, out error))
            return false;

        return McpPathBoundary.TryValidateWorkspaceRelativePath(value, MaxMcpArrayFilterStringLength, propertyName, out error);
    }

    private static bool TryReadRequiredIndexPathParameter(JsonNode? args, string propertyName, out string value, out string? error)
    {
        if (!TryReadRequiredStringParameter(args, propertyName, out value, out error))
            return false;

        if (value.Length > MaxMcpArrayFilterStringLength)
        {
            error = $"Parameter \"{propertyName}\" must be no longer than {MaxMcpArrayFilterStringLength} characters.";
            return false;
        }

        if (value.IndexOf("\0", StringComparison.Ordinal) >= 0)
        {
            error = $"Parameter \"{propertyName}\" must not contain NUL bytes.";
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

    private static string FormatLiteralSearchQueryLimitError()
        => $"literal search query is too long; maximum is {DbReader.MaxLiteralSearchQueryLength} characters. Split generated input into smaller queries.";

    private static string FormatSearchGuardCandidateLimitError(SearchGuardCandidateLimitException ex)
        => $"guarded search is too broad: {FormatSearchGuardCandidateLimitDetail(ex)} Narrow the search with more specific query text, lang/path filters, or a smaller cursor offset.";

    private static string FormatSearchRecipeGuardCandidateLimitError(string recipeName, string queryName, SearchGuardCandidateLimitException ex)
    {
        var recipeDisplay = McpBoundedText.ForDisplay(recipeName).Text;
        var queryDisplay = McpBoundedText.ForDisplay(queryName).Text;
        return $"guarded search is too broad for recipe '{recipeDisplay}' query '{queryDisplay}': {FormatSearchGuardCandidateLimitDetail(ex)} Narrow the search with more specific path/lang filters or guards.";
    }

    private static string FormatSearchGuardCandidateLimitDetail(SearchGuardCandidateLimitException ex)
    {
        var detail = $"inspected the maximum {ex.CandidateLimit} candidate chunks before satisfying the requested page (limit {ex.RequestedLimit}, offset {ex.RequestedOffset}).";
        if (ex.CandidatePreviewPaths.Count > 0)
            detail += $" Candidate files sampled before refusal: {string.Join(", ", ex.CandidatePreviewPaths)}.";
        if (ex.CandidatePreviewLanguages.Count > 0)
            detail += $" Candidate languages sampled before refusal: {string.Join(", ", ex.CandidatePreviewLanguages)}.";
        detail += " Use count/count-by style search without guard filters to size the broad query before retrying.";
        return detail;
    }

    private static BoundedMcpText FormatMcpCaughtExceptionDiagnostic(Exception ex)
        => McpBoundedText.ForDisplay(CommandErrorWriter.FormatSanitizedException(ex));

    private JsonNode ExecuteSearch(JsonNode? id, JsonNode? args)
    {
        var listRecipes = args?["listRecipes"]?.GetValue<bool>() ?? false;
        if (listRecipes)
            return ExecuteSearchRecipeList(id);

        var recipeNode = args?["recipe"];
        if (recipeNode is not null)
        {
            var recipeName = recipeNode.GetValue<string>();
            if (string.IsNullOrWhiteSpace(recipeName))
                return CreateToolErrorResponse(id, "'recipe' must be a non-empty search recipe name.");
            return ExecuteSearchRecipe(id, args, recipeName.Trim());
        }

        if (args?["auditScope"] is not null)
            return CreateToolErrorResponse(id, "'auditScope' is only supported with recipe execution.");

        if (!TryReadRequiredStringParameter(args, "query", out var query, out var requiredError))
            return CreateToolErrorResponse(id, requiredError!);
        if (query.Length > QueryLimits.MaxQueryLength)
            return CreateToolErrorResponse(id, QueryLimits.FormatQueryTooLongError());

        var adjustments = new ArgumentAdjustmentCollector();
        var limit = ReadLimit(args, QueryCommandRunner.DefaultQueryLimit, adjustments);
        var lang = QueryCommandRunner.NormalizeLangFilterValue(args?["lang"]?.GetValue<string>());
        var snippetLines = ReadSnippetLines(args, SearchSnippetFormatter.DefaultSnippetLines, adjustments);
        var snippetFocusText = args?["snippetFocus"]?.GetValue<string>() ?? "quality";
        if (!QueryCommandRunner.TryParseSnippetFocusMode(snippetFocusText, out var snippetFocus))
            return CreateToolErrorResponse(id, "snippetFocus must be one of quality, leftmost, proximity");
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
        if (!TryReadSinceArgument(args, out var since, out var sinceError))
            return CreateToolErrorResponse(id, sinceError!);
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
        if (TryReadSearchGuardScope(id, args, out var guardScope) is JsonNode guardScopeError)
            return guardScopeError;
        var guardWindow = ReadOptionalIntArgument(args, "guardWindow") ?? DbReader.DefaultSearchGuardWindow;
        if (guardWindow < 0 || guardWindow > DbReader.MaxSearchGuardWindow)
            return CreateToolErrorResponse(id, $"'guardWindow' must be between 0 and {DbReader.MaxSearchGuardWindow}; got {guardWindow}.");
        var suggestExactSubstring = SearchQueryAdvisor.ShouldSuggestExactSubstring(query, rawQuery, exact, prefix);

        return WithDbReader(id, args, reader =>
        {
            if (countOnly)
            {
                List<SearchResult> countResults;
                try
                {
                    countResults = reader.Search(query, MaxLimit, lang, rawQuery, pathPatterns, excludePaths, excludeTests, deduplicate, since, exact, prefix, guardFilters: guardFilters, guardWindow: guardWindow, guardScope: guardScope);
                }
                catch (SearchQueryLimitException)
                {
                    return CreateToolErrorResponse(id, FormatLiteralSearchQueryLimitError());
                }
                catch (SearchGuardCandidateLimitException ex)
                {
                    return CreateToolErrorResponse(id, FormatSearchGuardCandidateLimitError(ex));
                }
                var truncatedCount = countResults.Count >= MaxLimit;
                var payload = BuildCountOnlyPayload(countResults.Count, truncatedCount ? null : countResults.Count, truncatedCount, countResults, result => result.Path);
                payload["query"] = query;
                payload["rawQuery"] = rawQuery;
                payload["snippetFocus"] = snippetFocusText.Trim().ToLowerInvariant();
                payload["path"] = PathEcho(pathPatterns);
                payload["excludeTests"] = excludeTests;
                AddSearchStabilityMetadata(payload, reader, cursor, []);
                if (suggestExactSubstring)
                    AddExactSubstringRecoveryHint(payload, query);
                if (countResults.Count == 0)
                    AddFtsQueryDiagnostics(payload, DbReader.AnalyzeFtsQuery(query, rawQuery, prefix, lang));
                adjustments.ApplyTo(payload);
                return CreateToolResult(id, $"Counted {countResults.Count} search result(s).", payload);
            }

            List<SearchResult> results;
            try
            {
                results = reader.Search(query, FetchLimitForEnvelope(limit), lang, rawQuery, pathPatterns, excludePaths, excludeTests, deduplicate, since, exact, prefix, cursor: cursor, guardFilters: guardFilters, guardWindow: guardWindow, guardScope: guardScope);
            }
            catch (SearchQueryLimitException)
            {
                return CreateToolErrorResponse(id, FormatLiteralSearchQueryLimitError());
            }
            catch (SearchGuardCandidateLimitException ex)
            {
                return CreateToolErrorResponse(id, FormatSearchGuardCandidateLimitError(ex));
            }
            var ftsDiagnostics = DbReader.AnalyzeFtsQuery(query, rawQuery, prefix, lang);
            var truncated = TrimToRequestedLimit(results, limit);
            if (results.Count == 0)
            {
                var payload = new JsonObject
                {
                    ["query"] = query,
                    ["rawQuery"] = rawQuery,
                    ["snippetLines"] = snippetLines,
                    ["snippetFocus"] = snippetFocusText.Trim().ToLowerInvariant(),
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
                adjustments.ApplyTo(payload);
                return CreateToolResult(id, "No results found.", payload);
            }

            var queryContext = SearchSnippetFormatter.PrepareQueryContext(query);
            var compactResults = SearchSnippetFormatter
                .ToCompactResults(results, queryContext, snippetLines, exact, maxLineWidth, lang, snippetFocus, exposeLiteralHighlights: exact)
                .ToList();
            foreach (var compact in compactResults)
                SearchSnippetFormatter.ApplyOutputMetadata(compact, snippetLines, maxLineWidth, exact, rawQuery);
            var structured = new JsonObject
            {
                ["query"] = query,
                ["rawQuery"] = rawQuery,
                ["cursor"] = cursorValue,
                ["snippetLines"] = snippetLines,
                ["snippetFocus"] = snippetFocusText.Trim().ToLowerInvariant(),
                ["maxLineWidth"] = maxLineWidth,
                ["path"] = PathEcho(pathPatterns),
                ["excludeTests"] = excludeTests,
                ["results"] = ToJsonArray(compactResults)
            };
            AddSearchStabilityMetadata(structured, reader, cursor, results);
            AddResultEnvelope(structured, results.Count, truncated ? null : results.Count, truncated);
            if (format == "compact")
                ApplyCompactResults(structured, results, result => result.Path, result => result.StartLine);
            var topResult = results[0];
            AddNextStepSuggestion(
                structured,
                "excerpt",
                BuildExcerptArgs(topResult.Path, topResult.StartLine, topResult.EndLine),
                "Use excerpt on the top hit before editing; for symbol changes, follow with definition or references to confirm declarations and usage sites.");
            if (suggestExactSubstring)
                AddExactSubstringRecoveryHint(structured, query);
            adjustments.ApplyTo(structured);
            // Include top file paths in summary for quick AI orientation
            // AIが素早く位置把握できるよう、サマリにトップファイルパスを含める
            var topPaths = results.Select(r => r.Path).Distinct().Take(3);
            var summary = $"Found {results.Count} search result(s) in {string.Join(", ", topPaths)}.";
            return CreateToolResult(id, summary, structured);
        });
    }

    private JsonNode ExecuteSearchRecipeList(JsonNode? id)
    {
        var registry = SearchAuditRecipes.Load();
        var payload = new JsonObject
        {
            ["count"] = registry.Recipes.Count,
            ["recipes"] = ToSearchRecipeArray(registry.Recipes)
        };
        AddSearchRecipeSourceDiagnostics(payload, registry.Diagnostics);
        return CreateToolResult(id, $"Found {registry.Recipes.Count} search recipe(s).", payload);
    }

    private JsonNode ExecuteSearchRecipe(JsonNode? id, JsonNode? args, string recipeName)
    {
        var registry = SearchAuditRecipes.Load();
        var recipe = registry.Recipes.FirstOrDefault(r => string.Equals(r.Name, recipeName, StringComparison.OrdinalIgnoreCase));
        if (recipe is null)
        {
            var available = string.Join(", ", registry.Recipes.Select(r => r.Name));
            return CreateToolErrorResponse(id, $"unknown search recipe '{recipeName}'. Available recipes: {available}.");
        }

        var adjustments = new ArgumentAdjustmentCollector();
        var limit = ReadLimit(args, QueryCommandRunner.DefaultQueryLimit, adjustments);
        var lang = QueryCommandRunner.NormalizeLangFilterValue(args?["lang"]?.GetValue<string>());
        var snippetLines = ReadSnippetLines(args, SearchSnippetFormatter.DefaultSnippetLines, adjustments);
        if (TryGetValidatedMaxLineWidth(id, args, out var maxLineWidth) is JsonNode maxLineWidthError)
            return maxLineWidthError;
        var pathPatterns = ReadScopedPathList(args);
        var excludePaths = ReadStringList(args, "excludePaths");
        var excludeTests = args?["excludeTests"]?.GetValue<bool>() ?? false;
        if (!TryResolveMcpRecipeAuditScope(args, recipe, ref pathPatterns, excludePaths, ref excludeTests, out var auditScope, out var auditScopeError))
            return CreateToolErrorResponse(id, auditScopeError!);
        if (!TryReadSinceArgument(args, out var since, out var sinceError))
            return CreateToolErrorResponse(id, sinceError!);
        var deduplicate = !(args?["noDedup"]?.GetValue<bool>() ?? false);
        if (!TryResolveSearchExactArgument(args, out var userExact, out var exactError))
            return CreateToolErrorResponse(id, exactError!);
        var hasExactOverride = args?["exact"] is not null || args?["exactSubstring"] is not null;
        if (args?["prefix"]?.GetValue<bool>() ?? false)
            return CreateToolErrorResponse(id, "'prefix' cannot be combined with recipe execution.");
        if (args?["cursor"] is not null)
            return CreateToolErrorResponse(id, "'cursor' is not supported for recipe execution.");
        if (TryReadSearchGuardFilters(id, args, out var guardFilters) is JsonNode guardError)
            return guardError;
        if (TryReadSearchGuardScope(id, args, out var guardScope) is JsonNode guardScopeError)
            return guardScopeError;
        var guardWindow = ReadOptionalIntArgument(args, "guardWindow") ?? DbReader.DefaultSearchGuardWindow;
        if (guardWindow < 0 || guardWindow > DbReader.MaxSearchGuardWindow)
            return CreateToolErrorResponse(id, $"'guardWindow' must be between 0 and {DbReader.MaxSearchGuardWindow}; got {guardWindow}.");

        return WithDbReader(id, args, reader =>
        {
            var queryResults = new JsonArray();
            var total = 0;
            foreach (var recipeQuery in recipe.Queries)
            {
                var exact = hasExactOverride ? userExact : recipeQuery.ExactSubstring;
                List<SearchResult> results;
                try
                {
                    results = reader.Search(
                        recipeQuery.Query,
                        FetchLimitForSearchRecipeEnvelope(limit),
                        lang,
                        false,
                        pathPatterns,
                        excludePaths,
                        excludeTests,
                        deduplicate,
                        since,
                        exact,
                        false,
                        guardFilters: guardFilters,
                        guardWindow: guardWindow,
                        guardScope: guardScope);
                }
                catch (SearchQueryLimitException)
                {
                    return CreateToolErrorResponse(id, FormatLiteralSearchQueryLimitError());
                }
                catch (SearchGuardCandidateLimitException ex)
                {
                    return CreateToolErrorResponse(id, FormatSearchRecipeGuardCandidateLimitError(recipe.Name, recipeQuery.Name, ex));
                }

                var queryContext = SearchSnippetFormatter.PrepareQueryContext(recipeQuery.Query);
                var compactResults = SearchSnippetFormatter
                    .ToCompactResults(results, queryContext, snippetLines, exact, maxLineWidth, exposeLiteralHighlights: exact)
                    .Where(result => MatchesRecipeFacetMetadata(result, recipeQuery))
                    .ToList();
                var truncated = TrimToRequestedLimit(compactResults, limit);
                foreach (var compact in compactResults)
                    SearchSnippetFormatter.ApplyOutputMetadata(compact, snippetLines, maxLineWidth, exact, rawFts: false);
                total += compactResults.Count;
                queryResults.Add(new JsonObject
                {
                    ["name"] = recipeQuery.Name,
                    ["query"] = recipeQuery.Query,
                    ["description"] = recipeQuery.Description,
                    ["recommended_labels"] = ToJsonArray(recipeQuery.RecommendedLabels),
                    ["false_positive_guidance"] = recipeQuery.FalsePositiveGuidance,
                    ["exact_substring"] = exact,
                    ["match_origins"] = ToJsonArray(recipeQuery.MatchOrigins),
                    ["exclude_origins"] = ToJsonArray(recipeQuery.ExcludeOrigins),
                    ["result_kinds"] = ToJsonArray(recipeQuery.ResultKinds),
                    ["count"] = compactResults.Count,
                    ["top_files"] = BuildTopFileHistogram(compactResults, result => result.Path),
                    ["truncated"] = truncated,
                    ["results"] = ToJsonArray(compactResults)
                });
            }

            var payload = new JsonObject
            {
                ["recipe"] = ToSearchRecipeJson(recipe),
                ["query_count"] = recipe.Queries.Count,
                ["result_count"] = total,
                ["limit_per_query"] = limit,
                ["snippetLines"] = snippetLines,
                ["maxLineWidth"] = maxLineWidth,
                ["lang"] = lang,
                ["audit_scope"] = auditScope,
                ["path"] = PathEcho(pathPatterns),
                ["excludePaths"] = PathEcho(excludePaths),
                ["excludeTests"] = excludeTests,
                ["queries"] = queryResults
            };
            AddFreshnessHint(payload, reader);
            AddSearchRecipeSourceDiagnostics(payload, registry.Diagnostics);
            adjustments.ApplyTo(payload);
            var summary = total == 0
                ? $"Recipe '{recipe.Name}' returned no search results."
                : $"Recipe '{recipe.Name}' returned {total} search result(s) across {recipe.Queries.Count} query(ies).";
            return CreateToolResult(id, summary, payload);
        });
    }

    private static bool TryResolveMcpRecipeAuditScope(
        JsonNode? args,
        SearchAuditRecipe recipe,
        ref List<string>? pathPatterns,
        List<string> excludePaths,
        ref bool excludeTests,
        out string auditScope,
        out string? error)
    {
        var requestedScope = args?["auditScope"]?.GetValue<string>();
        auditScope = string.IsNullOrWhiteSpace(requestedScope)
            ? recipe.DefaultScope
            : requestedScope.Trim();
        error = null;

        if (!string.Equals(auditScope, SearchAuditRecipes.DefaultAuditScope, StringComparison.Ordinal)
            && !string.Equals(auditScope, SearchAuditRecipes.AllAuditScope, StringComparison.Ordinal))
        {
            error = "'auditScope' must be either 'source' or 'all'.";
            return false;
        }

        if (string.Equals(auditScope, SearchAuditRecipes.DefaultAuditScope, StringComparison.Ordinal))
        {
            if ((pathPatterns is null || pathPatterns.Count == 0) && recipe.DefaultPathPatterns.Count > 0)
                pathPatterns = [.. recipe.DefaultPathPatterns];
            AddDistinct(excludePaths, recipe.DefaultExcludePaths);
            excludeTests = true;
        }

        return true;
    }

    private static void AddDistinct(List<string> target, IEnumerable<string> values)
    {
        foreach (var value in values)
        {
            if (!target.Contains(value, StringComparer.Ordinal))
                target.Add(value);
        }
    }

    private JsonArray ToSearchRecipeArray(IEnumerable<SearchAuditRecipe> recipes)
        => new(recipes.Select(recipe => ToSearchRecipeJson(recipe)).ToArray<JsonNode?>());

    private JsonObject ToSearchRecipeJson(SearchAuditRecipe recipe)
        => new()
        {
            ["name"] = recipe.Name,
            ["description"] = recipe.Description,
            ["recommended_labels"] = ToJsonArray(recipe.RecommendedLabels),
            ["default_scope"] = recipe.DefaultScope,
            ["default_path_patterns"] = ToJsonArray(recipe.DefaultPathPatterns),
            ["default_exclude_paths"] = ToJsonArray(recipe.DefaultExcludePaths),
            ["queries"] = new JsonArray(recipe.Queries.Select(query => new JsonObject
            {
                ["name"] = query.Name,
                ["query"] = query.Query,
                ["description"] = query.Description,
                ["recommended_labels"] = ToJsonArray(query.RecommendedLabels),
                ["false_positive_guidance"] = query.FalsePositiveGuidance,
                ["exact_substring"] = query.ExactSubstring
            }).ToArray<JsonNode?>())
        };

    private static void AddSearchRecipeSourceDiagnostics(JsonObject payload, IReadOnlyList<string> diagnostics)
    {
        if (diagnostics.Count == 0)
            return;
        payload["recipe_source_diagnostics"] = new JsonArray(diagnostics.Select(diagnostic => JsonValue.Create(diagnostic)).ToArray<JsonNode?>());
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
        var adjustments = new ArgumentAdjustmentCollector();
        var kind = args?["kind"]?.GetValue<string>()?.ToLowerInvariant();
        var lang = QueryCommandRunner.NormalizeLangFilterValue(args?["lang"]?.GetValue<string>());
        var limit = ReadLimit(args, QueryCommandRunner.DefaultQueryLimit, adjustments);
        if (TryGetValidatedMaxLineWidth(id, args, out var maxLineWidth) is JsonNode maxLineWidthError)
            return maxLineWidthError;
        var pathPatterns = ReadScopedPathList(args);
        var excludePaths = ReadStringList(args, "excludePaths");
        var excludeTests = args?["excludeTests"]?.GetValue<bool>() ?? false;
        if (!TryReadSinceArgument(args, out var since, out var sinceError))
            return CreateToolErrorResponse(id, sinceError!);
        if (!TryResolveNameExactArgument(args, "symbols", out var exact, out var exactError))
            return CreateToolErrorResponse(id, exactError!);
        var visibilityFilters = ReadStringOrCommaSeparatedList(args, "visibility");
        var excludeVisibilityFilters = ReadStringOrCommaSeparatedList(args, "excludeVisibility");
        var format = ReadResponseFormat(args);
        if (ValidateResponseFormat(format) is string formatError)
            return CreateToolErrorResponse(id, formatError);
        var countOnly = ReadCountOnly(args) || format == "count";

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
            JsonNode? namesEcho = effectiveQueries == null ? null : JsonSerializer.SerializeToNode(effectiveQueries, _jsonOptions);
            var hasExactPredicate = exact && effectiveQueries is { Count: > 0 };
            var exactSignal = reader.GetSymbolsExactQuerySignal(lang, pathPatterns, excludePaths, excludeTests, since);
            if (countOnly)
            {
                var countSummary = reader.CountSearchSymbolsTotal(effectiveQueries, kind, lang, pathPatterns, excludePaths, excludeTests, since, exact, visibilityFilters, excludeVisibilityFilters);
                var histogramResults = countSummary.Count > 0
                    ? reader.SearchSymbols(effectiveQueries, Math.Min(countSummary.Count, MaxLimit), kind, lang, pathPatterns, excludePaths, excludeTests, since, exact, visibilityFilters, excludeVisibilityFilters)
                    : [];
                var payload = BuildCountOnlyPayload(countSummary.Count, countSummary.Count, truncated: false, histogramResults, result => result.Path);
                payload["query"] = query;
                payload["names"] = namesEcho;
                payload["kind"] = kind;
                payload["lang"] = lang;
                payload["path"] = PathEcho(pathPatterns);
                payload["excludeTests"] = excludeTests;
                AddVisibilityFilterEcho(payload, visibilityFilters, excludeVisibilityFilters);
                if (hasExactPredicate)
                    AddExactGraphSignal(payload, exactSignal);
                return CreateToolResult(id, $"Counted {ConsoleUi.Counted(countSummary.Count, "symbol")}.", payload);
            }

            var results = reader.SearchSymbols(effectiveQueries, limit, kind, lang, pathPatterns, excludePaths, excludeTests, since, exact, visibilityFilters, excludeVisibilityFilters);
            var multiNameExactHint = effectiveQueries != null && effectiveQueries.Count > 1;
            var exactZeroHint = multiNameExactHint
                ? QueryCommandRunner.BuildExactZeroHint(
                    exact,
                    () => reader.AnySearchSymbols(effectiveQueries, kind, lang, pathPatterns, excludePaths, excludeTests, since, exact: false, visibilityFilters: visibilityFilters, excludeVisibilityFilters: excludeVisibilityFilters),
                    () => reader.SearchSymbols(effectiveQueries, Math.Min(limit, QueryCommandRunner.ExactZeroHintSampleLimit), kind, lang, pathPatterns, excludePaths, excludeTests, since, exact: false, visibilityFilters: visibilityFilters, excludeVisibilityFilters: excludeVisibilityFilters),
                    r => r.Name)
                : QueryCommandRunner.BuildExactZeroHint(
                    exact && effectiveQueries != null && effectiveQueries.Count > 0,
                    () => reader.CountSearchSymbols(effectiveQueries, QueryCommandRunner.ExactZeroHintProbeLimit, kind, lang, pathPatterns, excludePaths, excludeTests, since, exact: false, visibilityFilters: visibilityFilters, excludeVisibilityFilters: excludeVisibilityFilters) > 0,
                    () => reader.CountSearchSymbols(effectiveQueries, limit, kind, lang, pathPatterns, excludePaths, excludeTests, since, exact: false, visibilityFilters: visibilityFilters, excludeVisibilityFilters: excludeVisibilityFilters),
                    () => reader.SearchSymbols(effectiveQueries, Math.Min(limit, QueryCommandRunner.ExactZeroHintSampleLimit), kind, lang, pathPatterns, excludePaths, excludeTests, since, exact: false, visibilityFilters: visibilityFilters, excludeVisibilityFilters: excludeVisibilityFilters),
                    r => r.Name);
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
                AddVisibilityFilterEcho(payload, visibilityFilters, excludeVisibilityFilters);
                if (hasExactPredicate)
                    AddExactGraphSignal(payload, exactSignal);
                AddExactZeroHint(payload, exactZeroHint);
                AddFreshnessHint(payload, reader);
                adjustments.ApplyTo(payload);
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
            AddVisibilityFilterEcho(structured, visibilityFilters, excludeVisibilityFilters);
            if (format == "compact")
            {
                structured["results"] = BuildCompactSymbolRows(results);
                structured["format"] = "compact";
            }
            if (hasExactPredicate)
                AddExactGraphSignal(structured, exactSignal);
            var topSymbol = results[0];
            AddNextStepSuggestion(
                structured,
                "definition",
                new JsonObject { ["query"] = topSymbol.Name, ["limit"] = 5, ["exactName"] = true },
                "Use definition to confirm the declaration for the best symbol candidate; then use references, callers, or callees depending on the change.");
            adjustments.ApplyTo(structured);
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

        var adjustments = new ArgumentAdjustmentCollector();
        var kind = args?["kind"]?.GetValue<string>()?.ToLowerInvariant();
        var lang = QueryCommandRunner.NormalizeLangFilterValue(args?["lang"]?.GetValue<string>());
        var limit = ReadLimit(args, QueryCommandRunner.DefaultQueryLimit, adjustments);
        var includeBody = args?["includeBody"]?.GetValue<bool>() ?? false;
        if (!TryReadLspCompatibleArgument(args, out var lspCompatible, out var lspCompatibleError))
            return CreateToolErrorResponse(id, lspCompatibleError!);
        var pathPatterns = ReadScopedPathList(args);
        var excludePaths = ReadStringList(args, "excludePaths");
        var excludeTests = args?["excludeTests"]?.GetValue<bool>() ?? false;
        if (!TryReadSinceArgument(args, out var since, out var sinceError))
            return CreateToolErrorResponse(id, sinceError!);
        if (!TryResolveNameExactArgument(args, "definition", out var exact, out var exactError))
            return CreateToolErrorResponse(id, exactError!);
        var visibilityFilters = ReadStringOrCommaSeparatedList(args, "visibility");
        var excludeVisibilityFilters = ReadStringOrCommaSeparatedList(args, "excludeVisibility");
        var format = ReadResponseFormat(args);
        if (ValidateResponseFormat(format) is string formatError)
            return CreateToolErrorResponse(id, formatError);

        return WithDbReader(id, args, reader =>
        {
            var results = reader.GetDefinitions(query, FetchLimitForEnvelope(limit), kind, lang, includeBody, pathPatterns, excludePaths, excludeTests, since, exact, visibilityFilters, excludeVisibilityFilters);
            var truncated = TrimToRequestedLimit(results, limit);
            if (format == "count")
            {
                var total = truncated
                    ? reader.CountDefinitionsTotal(query, kind, lang, pathPatterns, excludePaths, excludeTests, since, exact, visibilityFilters, excludeVisibilityFilters).Count
                    : results.Count;
                var countPayload = BuildCountOnlyPayload(total, total, truncated: false, results, result => result.Path);
                countPayload["query"] = query;
                countPayload["kind"] = kind;
                countPayload["lang"] = lang;
                countPayload["path"] = PathEcho(pathPatterns);
                countPayload["excludeTests"] = excludeTests;
                AddVisibilityFilterEcho(countPayload, visibilityFilters, excludeVisibilityFilters);
                adjustments.ApplyTo(countPayload);
                return CreateToolResult(id, $"Counted {ConsoleUi.Counted(total, "definition")}.", countPayload);
            }
            if (lspCompatible)
                QueryCommandRunner.AttachLspLocations(results);
            ApplyExcerptRecoveryDbPath(results);
            var exactSignal = reader.GetDefinitionExactQuerySignal(lang, pathPatterns, excludePaths, excludeTests, since);
            var exactZeroHint = QueryCommandRunner.BuildExactZeroHint(
                exact,
                () => reader.CountSearchSymbols(query, QueryCommandRunner.ExactZeroHintProbeLimit, kind, lang, pathPatterns, excludePaths, excludeTests, since, exact: false, visibilityFilters: visibilityFilters, excludeVisibilityFilters: excludeVisibilityFilters) > 0,
                () => reader.CountSearchSymbols(query, limit, kind, lang, pathPatterns, excludePaths, excludeTests, since, exact: false, visibilityFilters: visibilityFilters, excludeVisibilityFilters: excludeVisibilityFilters),
                () => reader.SearchSymbols(query, Math.Min(limit, QueryCommandRunner.ExactZeroHintSampleLimit), kind, lang, pathPatterns, excludePaths, excludeTests, since, exact: false, visibilityFilters: visibilityFilters, excludeVisibilityFilters: excludeVisibilityFilters),
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
            AddVisibilityFilterEcho(payload, visibilityFilters, excludeVisibilityFilters);
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
            else
            {
                AddNextStepSuggestion(
                    payload,
                    "references",
                    new JsonObject { ["query"] = results[0].Name, ["limit"] = 5, ["exactName"] = true },
                    "Use references to inspect usage sites before changing this definition; then use excerpt for the relevant definition or reference ranges.");
            }
            adjustments.ApplyTo(payload);
            return CreateToolResult(id,
                ConsoleUi.FoundSummary(results.Count, "definition"),
                payload);
        });
    }

    private void ApplyExcerptRecoveryDbPath(IEnumerable<DefinitionResult> results)
    {
        foreach (var result in results)
            ExcerptRecoveryCommandFormatter.ApplyDbPath(result.BodyContentRecovery, result.Path, _dbPath);
    }

    private void ApplyExcerptRecoveryDbPath(IEnumerable<ReferenceResult> results)
    {
        foreach (var result in results)
            ExcerptRecoveryCommandFormatter.ApplyDbPath(result.BodyContentRecovery, result.Path, _dbPath);
    }

    private void ApplyExcerptRecoveryDbPath(IEnumerable<CallerResult> results)
    {
        foreach (var result in results)
            ExcerptRecoveryCommandFormatter.ApplyDbPath(result.BodyContentRecovery, result.Path, _dbPath);
    }

    private void ApplyExcerptRecoveryDbPath(IEnumerable<CalleeResult> results)
    {
        foreach (var result in results)
            ExcerptRecoveryCommandFormatter.ApplyDbPath(result.BodyContentRecovery, result.Path, _dbPath);
    }

    private JsonNode ExecuteReferences(JsonNode? id, JsonNode? args)
    {
        if (!TryReadRequiredStringParameter(args, "query", out var query, out var requiredError))
            return CreateToolErrorResponse(id, requiredError!);
        if (query.Length > QueryLimits.MaxQueryLength)
            return CreateToolErrorResponse(id, QueryLimits.FormatQueryTooLongError());
        if (IsBareVerbatimQueryToken(query))
            return CreateToolErrorResponse(id, "Add a real symbol name after the command; bare verbatim prefixes like `@` are not valid queries.");

        var adjustments = new ArgumentAdjustmentCollector();
        var kind = args?["kind"]?.GetValue<string>()?.ToLowerInvariant();
        var lang = QueryCommandRunner.NormalizeLangFilterValue(args?["lang"]?.GetValue<string>());
        var limit = ReadLimit(args, QueryCommandRunner.DefaultQueryLimit, adjustments);
        if (!TryReadLspCompatibleArgument(args, out var lspCompatible, out var lspCompatibleError))
            return CreateToolErrorResponse(id, lspCompatibleError!);
        var offset = ReadOffset(args, adjustments);
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
                var countOnlyTotal = reader.CountSearchReferencesTotal(query, lang, kind, pathPatterns, excludePaths, excludeTests, exact).Count;
                var histogramResults = countOnlyTotal > 0
                    ? reader.SearchReferences(query, Math.Min(countOnlyTotal, MaxLimit), lang, kind, pathPatterns, excludePaths, excludeTests, exact, maxLineWidth)
                    : [];
                var countOnlyPayload = BuildCountOnlyPayload(countOnlyTotal, countOnlyTotal, truncated: false, histogramResults, result => result.Path);
                countOnlyPayload["query"] = query;
                countOnlyPayload["kind"] = kind;
                countOnlyPayload["lang"] = lang;
                countOnlyPayload["path"] = PathEcho(pathPatterns);
                countOnlyPayload["excludeTests"] = excludeTests;
                adjustments.ApplyTo(countOnlyPayload);
                return CreateToolResult(id, $"Counted {ConsoleUi.Counted(countOnlyTotal, "reference")}.", countOnlyPayload);
            }

            var results = reader.SearchReferences(query, FetchLimitForEnvelope(limit), lang, kind, pathPatterns, excludePaths, excludeTests, exact, maxLineWidth, offset: offset);
            var truncated = TrimToRequestedLimit(results, limit);
            var total = truncated || offset > 0
                ? reader.CountSearchReferencesTotal(query, lang, kind, pathPatterns, excludePaths, excludeTests, exact).Count
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
                    BuildExcerptArgs(topReference.Path, topReference.Line, topReference.Line),
                    "Use excerpt on representative usage sites before editing; use callers or callees when you need call graph impact.");
            }
            adjustments.ApplyTo(payload);
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

        var adjustments = new ArgumentAdjustmentCollector();
        var kind = args?["kind"]?.GetValue<string>()?.ToLowerInvariant();
        if (IsNonCallGraphReferenceKind(kind))
            return CreateToolErrorResponse(id, BuildNonCallGraphKindRejectionMessage("callers", kind!));
        var lang = QueryCommandRunner.NormalizeLangFilterValue(args?["lang"]?.GetValue<string>());
        var limit = ReadLimit(args, QueryCommandRunner.DefaultQueryLimit, adjustments);
        var offset = ReadOffset(args, adjustments);
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
        var rawKinds = args?["rawKinds"]?.GetValue<bool>() ?? false;

        return WithDbReader(id, args, reader =>
        {
            if (countOnly)
            {
                var countOnlyTotal = reader.CountCallersTotal(query, lang, kind, pathPatterns, excludePaths, excludeTests, exact, rawKinds).Count;
                var histogramResults = countOnlyTotal > 0
                    ? reader.GetCallers(query, Math.Min(countOnlyTotal, MaxLimit), lang, kind, pathPatterns, excludePaths, excludeTests, exact, rawKinds, rankMode: rankMode)
                    : [];
                var countOnlyPayload = BuildCountOnlyPayload(countOnlyTotal, countOnlyTotal, truncated: false, histogramResults, result => result.Path);
                countOnlyPayload["query"] = query;
                countOnlyPayload["kind"] = kind;
                countOnlyPayload["rawKinds"] = rawKinds;
                countOnlyPayload["lang"] = lang;
                countOnlyPayload["path"] = PathEcho(pathPatterns);
                countOnlyPayload["excludeTests"] = excludeTests;
                adjustments.ApplyTo(countOnlyPayload);
                return CreateToolResult(id, $"Counted {ConsoleUi.Counted(countOnlyTotal, "caller")}.", countOnlyPayload);
            }

            var results = reader.GetCallers(query, FetchLimitForEnvelope(limit), lang, kind, pathPatterns, excludePaths, excludeTests, exact, rawKinds, rankMode: rankMode, offset: offset);
            var truncated = TrimToRequestedLimit(results, limit);
            var total = truncated || offset > 0
                ? reader.CountCallersTotal(query, lang, kind, pathPatterns, excludePaths, excludeTests, exact, rawKinds).Count
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
                () => reader.CountCallers(query, QueryCommandRunner.ExactZeroHintProbeLimit, lang, kind, pathPatterns, excludePaths, excludeTests, exact: false, rawKinds: rawKinds) > 0,
                () => reader.CountCallers(query, limit, lang, kind, pathPatterns, excludePaths, excludeTests, exact: false, rawKinds: rawKinds),
                () => reader.GetCallers(query, Math.Min(limit, QueryCommandRunner.ExactZeroHintSampleLimit), lang, kind, pathPatterns, excludePaths, excludeTests, exact: false, rawKinds: rawKinds, rankMode: rankMode),
                r => r.CalleeName);
            var payload = new JsonObject
            {
                ["query"] = query,
                ["kind"] = kind,
                ["rawKinds"] = rawKinds,
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
            else
            {
                var topCaller = results[0];
                AddNextStepSuggestion(
                    payload,
                    "excerpt",
                    BuildExcerptArgs(topCaller.Path, topCaller.FirstLine, topCaller.FirstLine),
                    "Use excerpt on a caller row to understand the concrete call site before widening impact analysis or editing.");
            }
            adjustments.ApplyTo(payload);
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

        var adjustments = new ArgumentAdjustmentCollector();
        var kind = args?["kind"]?.GetValue<string>()?.ToLowerInvariant();
        if (IsNonCallGraphReferenceKind(kind))
            return CreateToolErrorResponse(id, BuildNonCallGraphKindRejectionMessage("callees", kind!));
        var lang = QueryCommandRunner.NormalizeLangFilterValue(args?["lang"]?.GetValue<string>());
        var limit = ReadLimit(args, QueryCommandRunner.DefaultQueryLimit, adjustments);
        var offset = ReadOffset(args, adjustments);
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
        var rawKinds = args?["rawKinds"]?.GetValue<bool>() ?? false;

        return WithDbReader(id, args, reader =>
        {
            if (countOnly)
            {
                var countOnlyTotal = reader.CountCalleesTotal(query, lang, kind, pathPatterns, excludePaths, excludeTests, exact, rawKinds).Count;
                var histogramResults = countOnlyTotal > 0
                    ? reader.GetCallees(query, Math.Min(countOnlyTotal, MaxLimit), lang, kind, pathPatterns, excludePaths, excludeTests, exact, rawKinds, rankMode: rankMode)
                    : [];
                var countOnlyPayload = BuildCountOnlyPayload(countOnlyTotal, countOnlyTotal, truncated: false, histogramResults, result => result.Path);
                countOnlyPayload["query"] = query;
                countOnlyPayload["kind"] = kind;
                countOnlyPayload["rawKinds"] = rawKinds;
                countOnlyPayload["lang"] = lang;
                countOnlyPayload["path"] = PathEcho(pathPatterns);
                countOnlyPayload["excludeTests"] = excludeTests;
                adjustments.ApplyTo(countOnlyPayload);
                return CreateToolResult(id, $"Counted {ConsoleUi.Counted(countOnlyTotal, "callee")}.", countOnlyPayload);
            }

            var results = reader.GetCallees(query, FetchLimitForEnvelope(limit), lang, kind, pathPatterns, excludePaths, excludeTests, exact, rawKinds, rankMode: rankMode, offset: offset);
            var truncated = TrimToRequestedLimit(results, limit);
            var total = truncated || offset > 0
                ? reader.CountCalleesTotal(query, lang, kind, pathPatterns, excludePaths, excludeTests, exact, rawKinds).Count
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
                () => reader.CountCallees(query, QueryCommandRunner.ExactZeroHintProbeLimit, lang, kind, pathPatterns, excludePaths, excludeTests, exact: false, rawKinds: rawKinds) > 0,
                () => reader.CountCallees(query, limit, lang, kind, pathPatterns, excludePaths, excludeTests, exact: false, rawKinds: rawKinds),
                () => reader.GetCallees(query, Math.Min(limit, QueryCommandRunner.ExactZeroHintSampleLimit), lang, kind, pathPatterns, excludePaths, excludeTests, exact: false, rawKinds: rawKinds, rankMode: rankMode),
                r => r.CallerName);
            var payload = new JsonObject
            {
                ["query"] = query,
                ["kind"] = kind,
                ["rawKinds"] = rawKinds,
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
            else
            {
                var topCallee = results[0];
                AddNextStepSuggestion(
                    payload,
                    "excerpt",
                    BuildExcerptArgs(topCallee.Path, topCallee.FirstLine, topCallee.FirstLine),
                    "Use excerpt on a callee row to inspect the concrete dependency before changing the caller or callee.");
            }
            adjustments.ApplyTo(payload);
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
        var adjustments = new ArgumentAdjustmentCollector();
        var lang = QueryCommandRunner.NormalizeLangFilterValue(args?["lang"]?.GetValue<string>());
        var limit = ReadLimit(args, QueryCommandRunner.DefaultQueryLimit, adjustments);
        var pathPatterns = ReadScopedPathList(args);
        var excludePaths = ReadStringList(args, "excludePaths");
        var excludeTests = args?["excludeTests"]?.GetValue<bool>() ?? false;
        if (!TryReadSinceArgument(args, out var since, out var sinceError))
            return CreateToolErrorResponse(id, sinceError!);
        var orderBySize = args?["orderBySize"]?.GetValue<bool>() ?? false;
        var rawBytes = args?["rawBytes"]?.GetValue<bool>() ?? false;

        return WithDbReader(id, args, reader =>
        {
            var results = reader.ListFiles(query, limit, lang, pathPatterns, excludePaths, excludeTests, since, orderBySize || rawBytes);
            if (results.Count == 0)
            {
                var payload = new JsonObject
                {
                    ["query"] = query,
                    ["lang"] = lang,
                    ["path"] = PathEcho(pathPatterns),
                    ["excludeTests"] = excludeTests,
                    ["orderBySize"] = orderBySize,
                    ["rawBytes"] = rawBytes,
                    ["count"] = 0,
                    ["results"] = new JsonArray()
                };
                if (rawBytes)
                {
                    payload["raw_bytes_payload_supported"] = false;
                    payload["raw_bytes_note"] = "MCP returns indexed file size metadata; raw file bytes are not returned.";
                }
                AddFreshnessHint(payload, reader);
                adjustments.ApplyTo(payload);
                return CreateToolResult(id, "No files found.", payload);
            }

            var structured = new JsonObject
            {
                ["query"] = query,
                ["lang"] = lang,
                ["path"] = PathEcho(pathPatterns),
                ["excludeTests"] = excludeTests,
                ["orderBySize"] = orderBySize,
                ["rawBytes"] = rawBytes,
                ["count"] = results.Count,
                ["results"] = JsonSerializer.SerializeToNode(results, _jsonOptions)
            };
            if (rawBytes)
            {
                structured["raw_bytes_payload_supported"] = false;
                structured["raw_bytes_note"] = "MCP returns indexed file size metadata; raw file bytes are not returned.";
            }
            adjustments.ApplyTo(structured);
            return CreateToolResult(id, ConsoleUi.FoundSummary(results.Count, "file"), structured);
        });
    }

    private JsonNode ExecuteMap(JsonNode? id, JsonNode? args)
    {
        var adjustments = new ArgumentAdjustmentCollector();
        var lang = args?["lang"]?.GetValue<string>()?.ToLowerInvariant();
        var limit = ReadLimit(args, QueryCommandRunner.DefaultMapLimit, adjustments);
        var pathPatterns = ReadScopedPathList(args);
        var excludePaths = ReadStringList(args, "excludePaths");
        var excludeTests = args?["excludeTests"]?.GetValue<bool>() ?? false;
        var sections = ReadStringList(args, "sections").Select(section => section.ToLowerInvariant()).ToHashSet(StringComparer.Ordinal);
        var depth = ReadMapDepth(args, adjustments);
        var minEntrypointConfidence = args?["minEntrypointConfidence"]?.GetValue<double>() ?? 0;
        if (minEntrypointConfidence is < 0 or > 1)
            return CreateToolErrorResponse(id, "minEntrypointConfidence must be between 0.0 and 1.0");

        return WithDbReader(id, args, reader =>
        {
            var map = reader.GetRepoMap(limit, lang, pathPatterns, excludePaths, excludeTests, minEntrypointConfidence);
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
            structured["minEntrypointConfidence"] = minEntrypointConfidence;
            var hasFilter = (pathPatterns is { Count: > 0 }) || excludePaths.Count > 0 || excludeTests || lang != null;
            if (map.FileCount == 0 && hasFilter)
                AddFreshnessHint(structured, reader);
            adjustments.ApplyTo(structured);
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
            "graphTableAvailable", "limit", "lang", "path", "excludeTests", "depth", "minEntrypointConfidence",
        };
        foreach (var section in sections)
            AddMapSectionStructuredProperties(keep, section);
        foreach (var key in structured.Select(property => property.Key).Where(key => !keep.Contains(key)).ToList())
            structured.Remove(key);
        structured["sections"] = new JsonArray(sections.Select(section => JsonValue.Create(section)).ToArray<JsonNode?>());
        structured["sectionProperties"] = BuildMapSectionStructuredProperties(sections);
    }

    private static readonly IReadOnlyDictionary<string, string[]> MapSectionStructuredProperties = new Dictionary<string, string[]>(StringComparer.Ordinal)
    {
        ["languages"] = ["languages"],
        ["tree"] = ["modules"],
        ["modules"] = ["modules"],
        ["hotspots"] = ["topFiles", "symbolRichFiles", "referenceRichFiles", "entrypoints"],
        ["metrics"] = ["largestFiles"],
    };

    private static void AddMapSectionStructuredProperties(HashSet<string> keep, string section)
    {
        if (!MapSectionStructuredProperties.TryGetValue(section, out var properties))
            return;

        foreach (var property in properties)
            keep.Add(property);
    }

    private static JsonObject BuildMapSectionStructuredProperties(IReadOnlySet<string> sections)
    {
        var payload = new JsonObject();
        foreach (var section in sections)
        {
            if (!MapSectionStructuredProperties.TryGetValue(section, out var properties))
                continue;

            payload[section] = new JsonArray(properties.Select(property => JsonValue.Create(property)).ToArray<JsonNode?>());
        }

        return payload;
    }

    private JsonNode ExecuteAnalyzeSymbol(JsonNode? id, JsonNode? args)
    {
        if (!TryReadRequiredStringParameter(args, "query", out var query, out var requiredError))
            return CreateToolErrorResponse(id, requiredError!);
        if (query.Length > QueryLimits.MaxQueryLength)
            return CreateToolErrorResponse(id, QueryLimits.FormatQueryTooLongError());
        if (IsBareVerbatimQueryToken(query))
            return CreateToolErrorResponse(id, "Add a real symbol name after the command; bare verbatim prefixes like `@` are not valid queries.");

        var adjustments = new ArgumentAdjustmentCollector();
        var limit = ReadLimit(args, QueryCommandRunner.DefaultMapLimit, adjustments);
        var lang = args?["lang"]?.GetValue<string>()?.ToLowerInvariant();
        var includeBody = args?["includeBody"]?.GetValue<bool>() ?? false;
        if (TryGetValidatedMaxLineWidth(id, args, out var maxLineWidth) is JsonNode maxLineWidthError)
            return maxLineWidthError;
        var pathPatterns = ReadScopedPathList(args);
        var excludePaths = ReadStringList(args, "excludePaths");
        var excludeTests = args?["excludeTests"]?.GetValue<bool>() ?? false;
        if (!TryResolveNameExactArgument(args, "analyze_symbol", out var exact, out var exactError))
            return CreateToolErrorResponse(id, exactError!);
        var format = ReadResponseFormat(args);
        if (ValidateResponseFormat(format) is string formatError)
            return CreateToolErrorResponse(id, formatError);
        var countOnly = ReadCountOnly(args) || format == "count";

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
            ApplyExcerptRecoveryDbPath(analysis.Definitions);
            ApplyExcerptRecoveryDbPath(analysis.References);
            ApplyExcerptRecoveryDbPath(analysis.Callers);
            ApplyExcerptRecoveryDbPath(analysis.Callees);
            var pathEcho = PathEcho(pathPatterns);
            var structured = countOnly
                ? BuildAnalyzeSymbolCountPayload(analysis, lang, pathEcho, excludeTests, maxLineWidth)
                : format == "compact"
                    ? BuildAnalyzeSymbolCompactPayload(analysis, lang, pathEcho, excludeTests, maxLineWidth)
                    : ToAnalyzeSymbolJsonObject(analysis);
            AddSqlGraphContractSignal(structured, sqlGraphSignal);
            structured.Remove("exactZeroHint");
            AddExactZeroHint(structured, analysis.ExactZeroHint);
            structured["maxLineWidth"] = maxLineWidth;
            structured["lang"] = lang;
            structured["path"] = pathEcho;
            structured["excludeTests"] = excludeTests;
            adjustments.ApplyTo(structured);
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

    private static Dictionary<string, string?> GetHotspotFamilyMetaSnapshot(DbContext db, Func<string, string> keyFactory)
    {
        var values = new Dictionary<string, string?>(StringComparer.Ordinal);
        foreach (var lang in FileIndexer.GetHotspotFamilyMarkerLanguages())
            values[lang] = db.GetMetaString(keyFactory(lang));
        return values;
    }

    private static Dictionary<string, FileIndexer.ProjectMarkerFingerprintResult> GetHotspotFamilyMarkerFingerprints(
        FileIndexer indexer,
        CancellationToken cancellationToken)
    {
        var values = new Dictionary<string, FileIndexer.ProjectMarkerFingerprintResult>(StringComparer.Ordinal);
        foreach (var lang in FileIndexer.GetHotspotFamilyMarkerLanguages())
            values[lang] = indexer.GetProjectMarkerFingerprintResult(lang, cancellationToken);
        return values;
    }

    private static void RestampHotspotFamilyTrust(
        DbWriter writer,
        IReadOnlySet<string> reusedLanguages,
        IReadOnlyDictionary<string, string?> priorVersions,
        IReadOnlyDictionary<string, string?> priorFingerprints,
        IReadOnlyDictionary<string, FileIndexer.ProjectMarkerFingerprintResult> currentFingerprints)
    {
        var currentVersion = DbContext.HotspotFamilyVersion.ToString(System.Globalization.CultureInfo.InvariantCulture);
        foreach (var lang in FileIndexer.GetHotspotFamilyMarkerLanguages())
        {
            if (!currentFingerprints.TryGetValue(lang, out var currentFingerprint))
                continue;

            if (!currentFingerprint.IsComplete)
            {
                writer.MarkHotspotFamilyMarkerFingerprintIncomplete(lang, currentFingerprint.Fingerprint);
                continue;
            }

            priorVersions.TryGetValue(lang, out var priorVersion);
            priorFingerprints.TryGetValue(lang, out var priorFingerprint);
            if (!reusedLanguages.Contains(lang) || (priorVersion == currentVersion && priorFingerprint == currentFingerprint.Fingerprint))
                writer.MarkHotspotFamilyReady(lang, currentFingerprint.Fingerprint);
        }
    }

    private static Dictionary<string, bool> GetHotspotFamilyTrustMatchesCurrent(
        IReadOnlyDictionary<string, string?> priorVersions,
        IReadOnlyDictionary<string, string?> priorFingerprints,
        IReadOnlyDictionary<string, FileIndexer.ProjectMarkerFingerprintResult> currentFingerprints)
    {
        var currentVersion = DbContext.HotspotFamilyVersion.ToString(System.Globalization.CultureInfo.InvariantCulture);
        var values = new Dictionary<string, bool>(StringComparer.Ordinal);
        foreach (var lang in FileIndexer.GetHotspotFamilyMarkerLanguages())
        {
            currentFingerprints.TryGetValue(lang, out var currentFingerprint);
            priorVersions.TryGetValue(lang, out var priorVersion);
            priorFingerprints.TryGetValue(lang, out var priorFingerprint);
            values[lang] = currentFingerprint.IsComplete
                && priorVersion == currentVersion
                && priorFingerprint == currentFingerprint.Fingerprint;
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

    private static long? TryGetUnchangedFileIdFromStat(
        DbWriter writer,
        string absolutePath,
        string relativePath,
        string? language,
        bool allowReuse,
        out long? size)
    {
        size = null;
        if (!allowReuse || language == null)
            return null;

        try
        {
            var info = new FileInfo(absolutePath);
            if (!info.Exists)
                return null;

            size = info.Length;
            return writer.GetUnchangedFileId(
                relativePath,
                info.LastWriteTimeUtc,
                checksum: null,
                size: info.Length,
                language: language);
        }
        catch (IOException)
        {
            return null;
        }
        catch (UnauthorizedAccessException)
        {
            return null;
        }
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

    private JsonNode ExecuteStatus(JsonNode? id, JsonNode? args)
    {
        var checkWorkspace = args?["check"]?.GetValue<bool>() ?? false;
        var staleAfterSeconds = ReadOptionalIntArgument(args, "staleAfterSeconds") ?? (int)TimeSpan.FromDays(1).TotalSeconds;
        if (staleAfterSeconds <= 0)
            return CreateToolErrorResponse(id, "staleAfterSeconds must be greater than or equal to 1");
        var explain = args?["explain"]?.GetValue<string>()?.Trim().ToLowerInvariant();
        if (explain is not (null or "freshness" or "readiness" or "all"))
            return CreateToolErrorResponse(id, "explain must be one of freshness, readiness, all");
        var format = ReadResponseFormat(args);
        if (format is not ("full" or "compact"))
            return CreateToolErrorResponse(id, "format must be one of full, compact");
        if (!TryReadStatusScopes(args, out var statusScopes, out var scopeError))
            return CreateToolErrorResponse(id, scopeError!);
        var includeConfig = args?["config"]?.GetValue<bool>() ?? false;
        var includeLogPath = args?["logPath"]?.GetValue<bool>() ?? false;
        var runUpdateCheck = args?["updateCheck"]?.GetValue<bool>() ?? false;

        return WithDbReader(id, args, reader =>
        {
            var requestToken = _currentRequestToken.Value;
            var status = reader.GetStatus();
            WorkspaceMetadataEnricher.Enrich(status, _dbPath, _dbPathExplicit, requestToken);
            var macProfile = MacProfileDetector.DetectCurrentWithDiagnostics();
            status.MacProfile = macProfile.Profile;
            if (macProfile.Diagnostics.Count > 0)
                status.MacProfileDiagnostics = macProfile.Diagnostics.ToList();
            if (checkWorkspace)
            {
                status.WorkspaceCheck = IndexFreshnessChecker.Check(reader, status.ProjectRoot, requestToken);
                status.IndexMatchesWorkspace = status.WorkspaceCheck.Checked
                    ? status.WorkspaceCheck.MatchesWorkspace
                    : null;
                status.StaleAfterSeconds = staleAfterSeconds;
                if (status.IndexedAt.HasValue)
                    status.IndexAgeSeconds = Math.Max(0, (long)Math.Round((GetUtcNow() - status.IndexedAt.Value).TotalSeconds, MidpointRounding.AwayFromZero));
            }
            ExtractorPluginRegistry.LoadPatternConfigsForProjectRoot(status.ProjectRoot);
            status.GraphSupportedLanguages = ReferenceExtractor.GetSupportedLanguages().OrderBy(l => l).ToList();
            status.Extractors = ExtractorPluginRegistry.GetStatusSnapshot();
            var postExtractionHookSnapshot = PostExtractionHookRunner.DiscoverDefaultMetadata();
            var postExtractionHooks = postExtractionHookSnapshot.Hooks;
            if (postExtractionHookSnapshot.Diagnostics.Count > 0)
                status.HookDiagnostics = postExtractionHookSnapshot.Diagnostics.ToList();
            var trustOverrides = ExtractorPluginRegistry.GetAcceptedTrustOverrides(status.ProjectRoot)
                .Concat(postExtractionHookSnapshot.TrustOverrides)
                .ToList();
            if (trustOverrides.Count > 0)
                status.TrustOverrides = trustOverrides;
            if (postExtractionHooks.Count > 0)
            {
                status.Hooks = postExtractionHooks
                    .Select(hook => new PostExtractionHookStatus
                    {
                        Name = hook.Name,
                        AssemblyPath = hook.AssemblyPath,
                        TypeName = hook.TypeName,
                        CallbackBudgetMs = (long)Math.Round(postExtractionHookSnapshot.CallbackBudget.TotalMilliseconds, MidpointRounding.AwayFromZero),
                    })
                    .ToList();
            }
            status.Version = _version;
            requestToken.ThrowIfCancellationRequested();
            status.UpdateCheck = runUpdateCheck
                ? (StatusUpdateCheckForTesting ?? UpdateChecker.Check)(_version, requestToken)
                : null;
            if (!status.FoldReady)
            {
                status.DegradedReason = DegradationReasonCodes.BuildFoldNotReadyExplanation(status.FoldReadyReason);
                status.RecommendedAction = BuildFoldBackfillCommand(_dbPath, _dbPathExplicit);
                status.AlternativeAction = BuildFoldRebuildRepairCommand(status.ProjectRoot, _dbPath, _dbPathExplicit);
            }
            var checkFailures = checkWorkspace
                ? BuildMcpStatusCheckFailures(status, statusScopes)
                : [];
            if (checkWorkspace)
                status.FailedChecks = checkFailures.Select(failure => failure.Name).ToList();

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
            var rateLimitDiagnostics = RateLimiter.SnapshotDiagnostics();
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
                    ["batch_query_response_bytes"] = GetBatchQueryResponseByteLimit(),
                    ["batch_query_max_response_bytes"] = MaxBatchQueryResponseByteLimit,
                    ["batch_query_max_queries"] = MaxBatchQuerySize,
                    ["max_pagination_offset"] = MaxMcpPaginationOffset,
                    ["max_json_depth"] = MaxJsonDepth,
                    ["max_batch_requests"] = MaxBatchRequestCount,
                    ["json_rpc_batch_max_requests"] = MaxBatchRequestCount,
                    ["keep_alive_min_interval_s"] = MinKeepAliveIntervalSeconds,
                    ["keep_alive_max_interval_s"] = MaxKeepAliveIntervalSeconds,
                    ["rate_limit_max_rps"] = RateLimiterOptions.MaxRefillTokensPerSecond,
                    ["rate_limit_max_burst"] = RateLimiterOptions.MaxBurstCapacity,
                    ["rate_limit_max_buckets"] = RateLimiterOptions.DefaultMaxBucketCount,
                },
                ["rate_limit"] = new JsonObject
                {
                    ["enabled"] = RateLimiter.Options.IsEnabled,
                    ["rps"] = RateLimiter.Options.RefillTokensPerSecond,
                    ["burst"] = RateLimiter.Options.BurstCapacity,
                    ["bucket_count"] = rateLimitDiagnostics.BucketCount,
                    ["bucket_limit"] = rateLimitDiagnostics.MaxBucketCount,
                    ["bucket_limit_rejection_count"] = rateLimitDiagnostics.BucketLimitRejectionCount,
                    ["bucket_idle_ttl_seconds"] = rateLimitDiagnostics.BucketIdleTtlSeconds,
                    ["next_prune_in_ms"] = rateLimitDiagnostics.NextPruneInMs,
                    ["last_prune_age_ms"] = rateLimitDiagnostics.LastPruneAgeMs.HasValue ? JsonValue.Create(rateLimitDiagnostics.LastPruneAgeMs.Value) : null,
                    ["last_pruned_bucket_count"] = rateLimitDiagnostics.LastPrunedBucketCount,
                },
                ["request_timeouts"] = BuildRequestTimeoutDiagnosticsStatus(),
            };
            var effectiveConfig = includeConfig
                ? BuildMcpStatusEffectiveConfig(status, staleAfterSeconds, checkWorkspace, runUpdateCheck)
                : null;
            var logPath = includeLogPath ? GlobalToolLog.ResolveLogDirectoryForStatus() : null;
            var explainPayload = explain is null
                ? null
                : BuildMcpStatusExplain(status, checkFailures, explain);
            if (effectiveConfig is not null)
                structured["effective_config"] = effectiveConfig.DeepClone();
            if (logPath is not null)
                structured["log_path"] = logPath;
            if (explainPayload is not null)
                structured["explain"] = explainPayload.DeepClone();
            if (format == "compact")
            {
                structured = BuildMcpCompactStatusPayload(status, checkFailures);
                if (effectiveConfig is not null)
                    structured["effective_config"] = effectiveConfig;
                if (logPath is not null)
                    structured["log_path"] = logPath;
                if (explainPayload is not null)
                    structured["explain"] = explainPayload;
            }
            return CreateToolResult(id, "Database stats returned.", structured);
        });
    }

    private sealed record McpStatusCheckFailure(string Name, bool IsStale, string Diagnostic);

    private static bool TryReadStatusScopes(JsonNode? args, out HashSet<string>? scopes, out string? error)
    {
        scopes = null;
        error = null;
        if (args?["scopes"] is null)
            return true;

        var values = ReadStringOrArrayList(args, "scopes")
            .Select(scope => scope.Trim().ToLowerInvariant())
            .ToList();
        if (args["scopes"] is JsonArray array && values.Count != array.Count)
        {
            error = "scopes entries must be non-empty strings.";
            return false;
        }
        if (values.Count == 0)
        {
            error = "scopes cannot be empty or whitespace-only.";
            return false;
        }

        scopes = new HashSet<string>(StringComparer.Ordinal);
        foreach (var value in values)
        {
            if (!IsKnownMcpStatusScope(value))
            {
                error = $"Invalid status scope '{value}'. Use one of: workspace, graph, issues, sql, hotspot, csharp, fold, newer.";
                return false;
            }
            scopes.Add(value);
        }
        return true;
    }

    private static bool IsKnownMcpStatusScope(string scope) =>
        scope is "workspace" or "graph" or "issues" or "sql" or "hotspot" or "csharp" or "fold" or "newer";

    private static IReadOnlyList<McpStatusCheckFailure> BuildMcpStatusCheckFailures(StatusResult status, IReadOnlySet<string>? scopes)
    {
        var failures = new List<McpStatusCheckFailure>();
        var checkAll = scopes is not { Count: > 0 };
        bool Includes(string scope) => checkAll || scopes!.Contains(scope);

        if (Includes("workspace"))
        {
            if (status.WorkspaceCheck?.Checked != true)
            {
                failures.Add(new McpStatusCheckFailure("workspace_unavailable", true, "[stale] workspace_check unavailable"));
            }
            else if (!status.WorkspaceCheck.MatchesWorkspace)
            {
                var check = status.WorkspaceCheck;
                failures.Add(new McpStatusCheckFailure(
                    "workspace_stale",
                    true,
                    $"[stale] workspace_check reason={check.Reason} changed={check.ChangedFileCount} missing={check.MissingFileCount} unindexed={check.UnindexedFileCount}"));
            }
        }

        if (Includes("graph") && !status.GraphTableAvailable)
            failures.Add(new McpStatusCheckFailure("graph_table_available", false, "[degraded] graph_table_available=false"));
        if (Includes("issues") && !status.IssuesTableAvailable)
            failures.Add(new McpStatusCheckFailure("issues_table_available", false, "[degraded] issues_table_available=false"));
        if (Includes("issues") && status.IssuesTableAvailable && !status.FileIssuesDataCurrent)
            failures.Add(new McpStatusCheckFailure("file_issues_data_current", false, "[degraded] file_issues_data_current=false"));
        if (Includes("workspace") && status.MigrationInProgress)
            failures.Add(new McpStatusCheckFailure("migration_in_progress", false, "[degraded] migration_in_progress=true"));
        if (Includes("sql") && !status.SqlGraphContractReady)
            failures.Add(new McpStatusCheckFailure("sql_graph_contract_ready", false, $"[degraded] sql_graph_contract_ready=false reason={status.SqlGraphContractDegradedReason ?? "unknown"}"));
        if (Includes("hotspot") && !status.HotspotFamilyReady)
            failures.Add(new McpStatusCheckFailure("hotspot_family_ready", false, $"[degraded] hotspot_family_ready=false reason={status.HotspotFamilyDegradedReason ?? "unknown"}"));
        if (Includes("csharp") && !status.CSharpSymbolNameReady)
            failures.Add(new McpStatusCheckFailure("csharp_symbol_name_ready", false, "[degraded] csharp_symbol_name_ready=false"));
        if (Includes("csharp") && !status.CSharpMetadataTargetReady)
            failures.Add(new McpStatusCheckFailure("csharp_metadata_target_ready", false, $"[degraded] csharp_metadata_target_ready=false reason={status.CSharpMetadataTargetDegradedReason ?? "unknown"}"));
        if (Includes("fold") && !status.FoldReady)
            failures.Add(new McpStatusCheckFailure("fold_ready", false, $"[degraded] fold_ready=false reason={status.FoldReadyReason ?? "unknown"}"));
        if (Includes("newer") && status.IndexNewerThanReader)
            failures.Add(new McpStatusCheckFailure("index_newer_than_reader", false, $"[degraded] index_newer_than_reader=true reason={status.IndexNewerThanReaderReason ?? "unknown"}"));

        return failures;
    }

    private JsonObject BuildMcpStatusEffectiveConfig(StatusResult status, int staleAfterSeconds, bool checkWorkspace, bool runUpdateCheck) => new()
    {
        ["db_path"] = _dbPath,
        ["db_explicit"] = _dbPathExplicit,
        ["project_root"] = status.ProjectRoot,
        ["data_dir"] = status.DataDir,
        ["data_dir_source"] = status.DataDirSource,
        ["global_tool_log_dir"] = GlobalToolLog.ResolveLogDirectoryForStatus(),
        ["stale_after_seconds"] = staleAfterSeconds,
        ["check"] = checkWorkspace,
        ["update_check_requested"] = runUpdateCheck,
        ["version"] = status.Version,
    };

    private JsonObject BuildMcpStatusExplain(StatusResult status, IReadOnlyList<McpStatusCheckFailure> failures, string explain)
    {
        var payload = new JsonObject();
        if (explain is "freshness" or "all")
        {
            payload["freshness"] = new JsonObject
            {
                ["index_matches_workspace"] = status.IndexMatchesWorkspace.HasValue ? JsonValue.Create(status.IndexMatchesWorkspace.Value) : null,
                ["stale_after_seconds"] = status.StaleAfterSeconds.HasValue ? JsonValue.Create(status.StaleAfterSeconds.Value) : null,
                ["index_age_seconds"] = status.IndexAgeSeconds.HasValue ? JsonValue.Create(status.IndexAgeSeconds.Value) : null,
                ["workspace_check"] = status.WorkspaceCheck is null ? null : JsonSerializer.SerializeToNode(status.WorkspaceCheck, _jsonOptions),
            };
        }
        if (explain is "readiness" or "all")
        {
            payload["readiness"] = BuildMcpStatusReadiness(status);
            payload["failed_check_details"] = BuildMcpStatusFailureArray(failures);
        }
        return payload;
    }

    private static JsonObject BuildMcpStatusReadiness(StatusResult status) => new()
    {
        ["graph_table_available"] = status.GraphTableAvailable,
        ["issues_table_available"] = status.IssuesTableAvailable,
        ["file_issues_data_current"] = status.FileIssuesDataCurrent,
        ["sql_graph_contract_ready"] = status.SqlGraphContractReady,
        ["hotspot_family_ready"] = status.HotspotFamilyReady,
        ["csharp_symbol_name_ready"] = status.CSharpSymbolNameReady,
        ["csharp_metadata_target_ready"] = status.CSharpMetadataTargetReady,
        ["fold_ready"] = status.FoldReady,
        ["index_newer_than_reader"] = status.IndexNewerThanReader,
        ["migration_in_progress"] = status.MigrationInProgress,
    };

    private static JsonArray BuildMcpStatusFailureArray(IReadOnlyList<McpStatusCheckFailure> failures)
    {
        var array = new JsonArray();
        foreach (var failure in failures)
        {
            array.Add(new JsonObject
            {
                ["name"] = failure.Name,
                ["is_stale"] = failure.IsStale,
                ["diagnostic"] = failure.Diagnostic,
            });
        }
        return array;
    }

    private static JsonObject BuildMcpCompactStatusPayload(StatusResult status, IReadOnlyList<McpStatusCheckFailure> failures)
    {
        var payload = new JsonObject
        {
            ["format"] = "compact",
            ["summary"] = status.Summary,
            ["version"] = status.Version,
            ["project_root"] = status.ProjectRoot,
            ["files"] = status.Files,
            ["chunks"] = status.Chunks,
            ["symbols"] = status.Symbols,
            ["references"] = status.References,
            ["language_count"] = status.Languages.Count,
            ["top_languages"] = new JsonArray(status.Languages
                .OrderByDescending(kv => kv.Value)
                .ThenBy(kv => kv.Key, StringComparer.Ordinal)
                .Take(5)
                .Select(kv => new JsonObject { ["lang"] = kv.Key, ["files"] = kv.Value })
                .ToArray<JsonNode?>()),
            ["git_head"] = status.GitHead,
            ["git_is_dirty"] = status.GitIsDirty.HasValue ? JsonValue.Create(status.GitIsDirty.Value) : null,
            ["index_matches_workspace"] = status.IndexMatchesWorkspace.HasValue ? JsonValue.Create(status.IndexMatchesWorkspace.Value) : null,
            ["stale_after_seconds"] = status.StaleAfterSeconds.HasValue ? JsonValue.Create(status.StaleAfterSeconds.Value) : null,
            ["index_age_seconds"] = status.IndexAgeSeconds.HasValue ? JsonValue.Create(status.IndexAgeSeconds.Value) : null,
            ["failed_checks"] = new JsonArray(failures.Select(failure => JsonValue.Create(failure.Name)).ToArray()),
            ["failed_check_details"] = BuildMcpStatusFailureArray(failures),
            ["readiness"] = BuildMcpStatusReadiness(status),
        };
        if (status.WorkspaceCheck is not null)
            payload["workspace_check"] = JsonSerializer.SerializeToNode(status.WorkspaceCheck);
        if (status.TrustOverrides is { Count: > 0 })
            payload["trust_overrides"] = JsonSerializer.SerializeToNode(status.TrustOverrides);
        return payload;
    }

    private JsonObject BuildMcpSessionStatus()
    {
        var roots = new JsonArray();
        foreach (var root in _clientRootDiagnostics)
            roots.Add(root?.DeepClone());

        var session = new JsonObject
        {
            ["log_level"] = _mcpLogLevel,
            ["roots"] = roots,
        };
        if (_clientRootsTruncated)
        {
            session["roots_truncated"] = true;
            session["root_count"] = _clientRootCount;
            session["root_limit"] = MaxClientRootCount;
            session["root_uri_length_limit"] = MaxClientRootUriChars;
        }
        if (_clientName is not null || _clientVersion is not null)
        {
            var clientInfo = new JsonObject();
            if (_clientNameDisplay is not null)
            {
                clientInfo["name"] = _clientName;
                _clientNameDisplay.Value.AddMetadata(clientInfo, "name");
            }
            if (_clientVersionDisplay is not null)
            {
                clientInfo["version"] = _clientVersion;
                _clientVersionDisplay.Value.AddMetadata(clientInfo, "version");
            }
            session["client_info"] = clientInfo;
        }
        if (_clientCapabilities is not null)
        {
            session["client_capabilities_summary"] = BuildClientCapabilitiesSummary(_clientCapabilities);
            session["client_capabilities"] = _clientCapabilities.DeepClone();
        }
        if (_clientCapabilitiesTruncationReason is not null)
        {
            session["client_capabilities_truncated"] = true;
            session["client_capabilities_truncation_reason"] = _clientCapabilitiesTruncationReason;
            if (_clientCapabilitiesSerializedBytes is { } serializedBytes)
                session["client_capabilities_serialized_bytes"] = serializedBytes;
            session["client_capabilities_byte_limit"] = MaxClientCapabilitiesJsonBytes;
            session["client_capabilities_depth_limit"] = MaxClientCapabilitiesDepth;
            if (!session.ContainsKey("client_capabilities_summary"))
                session["client_capabilities_summary"] = BuildClientCapabilitiesSummary(_clientCapabilities);
        }
        if (_auditLog is not null)
            session["audit_log"] = BuildAuditLogStatus(_auditLog.SnapshotDiagnostics());
        return session;
    }

    private JsonObject BuildClientCapabilitiesSummary(JsonNode? capabilities)
    {
        var summary = new JsonObject
        {
            ["roots"] = _clientSupportsRoots,
            ["sampling"] = _clientSupportsSampling,
            ["truncated"] = _clientCapabilitiesTruncationReason is not null,
            ["truncation_reason"] = _clientCapabilitiesTruncationReason,
        };
        if (_clientCapabilitiesSerializedBytes is { } serializedBytes)
            summary["serialized_bytes"] = serializedBytes;
        if (capabilities is JsonObject obj)
        {
            summary["top_level_count"] = obj.Count;
            summary["top_level_keys"] = new JsonArray(obj
                .Select(kv => JsonValue.Create(McpBoundedText.ForDisplay(kv.Key, 64).Text))
                .Take(20)
                .ToArray<JsonNode?>());
            summary["top_level_keys_truncated"] = obj.Count > 20;
            if (obj["experimental"] is JsonObject experimental)
            {
                summary["experimental_count"] = experimental.Count;
                summary["experimental_keys"] = new JsonArray(experimental
                    .Select(kv => JsonValue.Create(McpBoundedText.ForDisplay(kv.Key, 64).Text))
                    .Take(20)
                    .ToArray<JsonNode?>());
                summary["experimental_keys_truncated"] = experimental.Count > 20;
            }
        }
        return summary;
    }

    private static bool IsAuditLogDegraded(AuditLogSink.AuditLogDiagnostics? diagnostics)
        => diagnostics is not null
            && (diagnostics.DroppedRecordCount > 0 || diagnostics.RotationDegraded);

    private static JsonObject BuildAuditLogStatus(AuditLogSink.AuditLogDiagnostics diagnostics)
    {
        var payload = new JsonObject
        {
            ["enabled"] = true,
            ["path"] = diagnostics.Path,
            ["include_values"] = diagnostics.IncludeValues,
            ["max_bytes"] = diagnostics.MaxBytes,
            ["bytes_written"] = diagnostics.BytesWritten,
            ["disposed"] = diagnostics.Disposed,
            ["queue_capacity"] = diagnostics.QueueCapacity,
            ["queue_depth"] = diagnostics.QueueDepth,
            ["dropped_record_count"] = diagnostics.DroppedRecordCount,
            ["queue_full_drop_count"] = diagnostics.QueueFullDropCount,
            ["serialization_failure_count"] = diagnostics.SerializationFailureCount,
            ["write_failure_count"] = diagnostics.WriteFailureCount,
            ["rotation_failure_count"] = diagnostics.RotationFailureCount,
            ["rotation_cleanup_failure_count"] = diagnostics.RotationCleanupFailureCount,
            ["rotation_degraded"] = diagnostics.RotationDegraded,
        };
        if (!string.IsNullOrWhiteSpace(diagnostics.LastDropReason))
            payload["last_drop_reason"] = diagnostics.LastDropReason;
        if (!string.IsNullOrWhiteSpace(diagnostics.LastRotationFailure))
            payload["last_rotation_failure"] = diagnostics.LastRotationFailure;
        return payload;
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
        if (!TryReadRequiredPathParameter(args, "path", out var path, out var requiredError))
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
            AddNextStepSuggestion(
                structured,
                "excerpt",
                new JsonObject { ["path"] = path, ["startLine"] = 1, ["endLine"] = Math.Min(outline.TotalLines, 80) },
                "Use excerpt for only the relevant outline range instead of reading the whole file.");
            return CreateToolResult(id, $"Outline: {ConsoleUi.Counted(outline.SymbolCount, "symbol")} in {ConsoleUi.Counted(outline.TotalLines, "line")}.", structured);
        });
    }

    private JsonNode ExecuteExcerpt(JsonNode? id, JsonNode? args)
    {
        if (!TryReadRequiredPathParameter(args, "path", out var path, out var requiredError))
            return CreateToolErrorResponse(id, requiredError!);

        var startLine = ReadOptionalIntArgument(args, "startLine");
        if (startLine == null || startLine <= 0)
            return CreateToolErrorResponse(id, "Missing or invalid required parameter: startLine");

        var endLine = ReadOptionalIntArgument(args, "endLine") ?? startLine.Value;
        if (endLine < startLine.Value)
            return CreateToolErrorResponse(id, "endLine must be greater than or equal to startLine");

        var beforeValue = ReadOptionalIntArgument(args, "before");
        if (beforeValue.HasValue && beforeValue.Value < 0)
            return CreateToolErrorResponse(id, $"before must be in [0, {MaxContextLines}]");
        var before = ClampContextLines(beforeValue ?? 0);

        var afterValue = ReadOptionalIntArgument(args, "after");
        if (afterValue.HasValue && afterValue.Value < 0)
            return CreateToolErrorResponse(id, $"after must be in [0, {MaxContextLines}]");
        var after = ClampContextLines(afterValue ?? 0);
        var contextTruncated = beforeValue > MaxContextLines || afterValue > MaxContextLines;

        var focusLine = ReadOptionalIntArgument(args, "focusLine");
        var focusColumn = ReadOptionalIntArgument(args, "focusColumn");
        var focusLengthValue = ReadOptionalIntArgument(args, "focusLength");
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

            ExcerptRecoveryCommandFormatter.ApplyDbPath(excerpt, _dbPath);
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
                new JsonObject { ["path"] = excerpt.Path },
                "Use outline to navigate neighboring symbols before requesting more ranges from the same file.");
            return CreateToolResult(id, "Excerpt returned.", payload);
        });
    }

    private static bool TryReadMaxOutputBytes(JsonNode? args, out int maxOutputBytes, out string? error)
    {
        maxOutputBytes = DefaultExcerptOutputByteLimit;
        error = null;
        if (args?["maxOutputBytes"] is not JsonNode node)
            return true;
        if (node is not JsonValue value || !value.TryGetValue<int>(out var requested))
        {
            error = "maxOutputBytes must be an integer";
            return false;
        }
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
        var retainedLineCount = 0;
        var firstRetainedLine = true;
        foreach (var line in content.Replace("\r\n", "\n").Split('\n'))
        {
            var candidate = firstRetainedLine ? line : builder.ToString() + "\n" + line;
            if (Encoding.UTF8.GetByteCount(candidate) > maxOutputBytes)
                break;
            builder.Clear();
            builder.Append(candidate);
            retainedLineCount++;
            firstRetainedLine = false;
        }
        payload[contentKey] = builder.ToString();
        TrimExcerptCoordinatePayload(payload, retainedLineCount);
        payload["contentTruncated"] = true;
        payload["truncated"] = true;
        payload["truncation_reason"] = "output_size_cap";
    }

    private static void TrimExcerptCoordinatePayload(JsonObject payload, int retainedLineCount)
    {
        var spansKey = FirstPayloadKey(payload, "contentLineSpans", "content_line_spans", "ContentLineSpans");
        var retainedSpans = new List<ExcerptPayloadSpan>();
        var hasSpanMapping = false;
        if (spansKey is not null && payload[spansKey] is JsonArray spans)
        {
            hasSpanMapping = true;
            var trimmedSpans = new JsonArray();
            foreach (var spanNode in spans)
            {
                if (spanNode is not JsonObject span)
                    continue;
                var contentLine = GetPayloadInt(span, "contentLine", "content_line", "ContentLine");
                if (!contentLine.HasValue || contentLine.Value > retainedLineCount)
                    continue;

                trimmedSpans.Add(span.DeepClone());
                var sourceLine = GetPayloadInt(span, "sourceLine", "source_line", "SourceLine");
                var sourceStartColumn = GetPayloadInt(span, "sourceStartColumn", "source_start_column", "SourceStartColumn");
                var sourceEndColumn = GetPayloadInt(span, "sourceEndColumn", "source_end_column", "SourceEndColumn");
                if (sourceLine.HasValue && sourceStartColumn.HasValue && sourceEndColumn.HasValue)
                    retainedSpans.Add(new ExcerptPayloadSpan(sourceLine.Value, sourceStartColumn.Value, sourceEndColumn.Value));
            }

            payload[spansKey] = trimmedSpans;
        }

        var tokensKey = FirstPayloadKey(payload, "semanticTokens", "semantic_tokens", "SemanticTokens");
        if (tokensKey is null || payload[tokensKey] is not JsonArray tokens)
            return;
        if (!hasSpanMapping)
        {
            if (retainedLineCount == 0)
                payload[tokensKey] = new JsonArray();
            return;
        }

        var trimmedTokens = new JsonArray();
        if (retainedLineCount > 0 && retainedSpans.Count > 0)
        {
            foreach (var tokenNode in tokens)
            {
                if (tokenNode is not JsonObject token)
                    continue;
                var startLine = GetPayloadInt(token, "startLine", "start_line", "StartLine");
                var endLine = GetPayloadInt(token, "endLine", "end_line", "EndLine");
                var startColumn = GetPayloadInt(token, "startColumn", "start_column", "StartColumn");
                var endColumn = GetPayloadInt(token, "endColumn", "end_column", "EndColumn");
                if (!startLine.HasValue || !endLine.HasValue || !startColumn.HasValue || !endColumn.HasValue)
                    continue;
                if (retainedSpans.Any(span =>
                    startLine.Value == span.SourceLine &&
                    endLine.Value == span.SourceLine &&
                    startColumn.Value >= span.SourceStartColumn &&
                    endColumn.Value <= span.SourceEndColumn))
                {
                    trimmedTokens.Add(token.DeepClone());
                }
            }
        }

        payload[tokensKey] = trimmedTokens;
    }

    private static string? FirstPayloadKey(JsonObject payload, params string[] keys)
        => keys.FirstOrDefault(payload.ContainsKey);

    private static int? GetPayloadInt(JsonObject obj, params string[] keys)
    {
        foreach (var key in keys)
        {
            if (obj[key] is JsonNode node)
                return node.GetValue<int>();
        }

        return null;
    }

    private readonly record struct ExcerptPayloadSpan(int SourceLine, int SourceStartColumn, int SourceEndColumn);

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

        var adjustments = new ArgumentAdjustmentCollector();
        var limit = ReadLimit(args, QueryCommandRunner.DefaultQueryLimit, adjustments);
        var lang = args?["lang"]?.GetValue<string>()?.ToLowerInvariant();
        var excludePaths = ReadStringList(args, "excludePaths");
        var excludeTests = args?["excludeTests"]?.GetValue<bool>() ?? false;
        var beforeValue = ReadOptionalIntArgument(args, "before");
        if (beforeValue.HasValue && beforeValue.Value < 0)
            return CreateToolErrorResponse(id, "before must be greater than or equal to 0");
        var before = ClampContextLines(beforeValue ?? 0);

        var afterValue = ReadOptionalIntArgument(args, "after");
        if (afterValue.HasValue && afterValue.Value < 0)
            return CreateToolErrorResponse(id, "after must be greater than or equal to 0");
        var after = ClampContextLines(afterValue ?? 0);
        var contextTruncated = beforeValue > MaxContextLines || afterValue > MaxContextLines;
        var snippetLinesValue = ReadOptionalIntArgument(args, "snippetLines");
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
                results = reader.FindInFiles(query, limit, lang, pathPatterns, excludePaths, excludeTests, before, after, exact, maxLineWidth, focusLine, focusColumn, regex).Results;
            }
            catch (RegexMatchTimeoutException ex) when (regex)
            {
                return CreateToolErrorResponse(
                    id,
                    RegexTimeoutPolicy.FormatFindTimeout(ex),
                    category: RegexTimeoutPolicy.RegexTimeoutCategory,
                    suggestion: RegexTimeoutPolicy.McpFindTimeoutSuggestion,
                    retrySafe: true,
                    extraData: new JsonObject
                    {
                        ["error_code"] = CommandErrorCodes.RegexMatchTimeout,
                        ["timeout_ms"] = ex.MatchTimeout.TotalMilliseconds,
                    });
            }
            catch (ArgumentException) when (regex)
            {
                return CreateToolErrorResponse(id, "invalid regular expression. Check regex syntax and retry.");
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
                adjustments.ApplyTo(structured);
                return CreateToolResult(id, "No matches found.", structured);
            }

            var fileCount = structured["fileCount"]!.GetValue<int>();
            adjustments.ApplyTo(structured);
            return CreateToolResult(id, $"Found {ConsoleUi.Counted(results.Count, "in-file match", "in-file matches")} across {ConsoleUi.Counted(fileCount, "file")}.", structured);
        });
    }

    private static int ClampContextLines(int value)
    {
        return Math.Min(value, MaxContextLines);
    }

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

    private JsonNode ExecuteDeps(JsonNode? id, JsonNode? args)
    {
        var adjustments = new ArgumentAdjustmentCollector();
        var limit = ReadLimit(args, QueryCommandRunner.DefaultImpactLimit, adjustments);
        var lang = args?["lang"]?.GetValue<string>()?.ToLowerInvariant();
        var pathPatterns = ReadScopedPathList(args);
        var excludePaths = ReadStringList(args, "excludePaths");
        var excludeTests = args?["excludeTests"]?.GetValue<bool>() ?? false;
        var includeGenerated = args?["includeGenerated"]?.GetValue<bool>() ?? false;
        var reverse = args?["reverse"]?.GetValue<bool>() ?? false;
        var cyclesOnly = args?["cycles"]?.GetValue<bool>() ?? false;
        var format = args?["format"]?.GetValue<string>()?.ToLowerInvariant() ?? "edgelist";

        return WithDbReader(id, args, reader =>
        {
            var results = reader.GetFileDependencies(limit, lang, pathPatterns, excludePaths, excludeTests, reverse);
            var cycleCandidates = cyclesOnly
                ? reader.GetFileDependencies(QueryCommandRunner.GetDependencyCycleGraphLimit(limit), lang, pathPatterns, excludePaths, excludeTests, reverse)
                : results;
            var baseSqlGraphSignal = reader.GetSqlGraphContractSignal(lang, pathPatterns, excludePaths, excludeTests);
            List<List<string>> cycles = [];
            var outputEdges = cyclesOnly ? QueryCommandRunner.FilterCycleEdges(cycleCandidates, out cycles).Take(limit).ToList() : results;
            if (cyclesOnly)
                cycles = cycles.Take(limit).ToList();
            var sqlGraphSignalPaths = cyclesOnly
                ? cycles.Count > 0
                    ? cycles.SelectMany(static cycle => cycle)
                    : cycleCandidates.SelectMany(static result => new[] { result.SourcePath, result.TargetPath })
                : results.SelectMany(static result => new[] { result.SourcePath, result.TargetPath });
            var sqlGraphSignal = results.Count == 0
                ? baseSqlGraphSignal
                : QueryCommandRunner.NarrowSqlGraphContractSignalByPaths(
                    reader,
                    baseSqlGraphSignal,
                    sqlGraphSignalPaths,
                    lang);
            var payload = new JsonObject { ["count"] = cyclesOnly ? cycles.Count : results.Count };
            if (cyclesOnly)
                payload["cycles"] = QueryCommandRunner.BuildDependencyCyclesJson(cycles);
            else if (format == "json-graph")
                payload["graph"] = BuildJsonGraphPayload(outputEdges);
            else
                payload["edges"] = JsonSerializer.SerializeToNode(outputEdges, _jsonOptions);
            payload["format"] = format;
            payload["includeGenerated"] = includeGenerated;
            payload["generated_code_filter_supported"] = true;
            payload["generated_code_scope"] = "source_and_target_files";
            AddSqlGraphContractSignal(payload, sqlGraphSignal);
            var summary = payload["count"]!.GetValue<int>() > 0
                ? cyclesOnly ? $"Found {ConsoleUi.Counted(cycles.Count, "dependency cycle")}." : $"Found {ConsoleUi.Counted(results.Count, "dependency edge")}."
                : "No file dependencies found.";
            if (results.Count == 0)
                AddFreshnessHint(payload, reader);
            adjustments.ApplyTo(payload);
            return CreateToolResult(id, summary, payload);
        });
    }

    private static JsonObject BuildJsonGraphPayload(IReadOnlyList<FileDependencyResult> edges)
    {
        var nodes = new JsonArray();
        var seenNodes = new HashSet<string>(StringComparer.Ordinal);
        var graphEdges = new JsonArray();
        foreach (var edge in edges)
        {
            if (seenNodes.Add(edge.SourcePath))
                nodes.Add(new JsonObject { ["id"] = edge.SourcePath });
            if (seenNodes.Add(edge.TargetPath))
                nodes.Add(new JsonObject { ["id"] = edge.TargetPath });

            graphEdges.Add(new JsonObject
            {
                ["source"] = edge.SourcePath,
                ["target"] = edge.TargetPath,
                ["reference_count"] = edge.ReferenceCount,
            });
        }

        return new JsonObject { ["nodes"] = nodes, ["edges"] = graphEdges };
    }

    private JsonNode ExecuteImpactAnalysis(JsonNode? id, JsonNode? args)
    {
        if (!TryReadRequiredStringParameter(args, "query", out var query, out var requiredError))
            return CreateToolErrorResponse(id, requiredError!);
        if (query.Length > QueryLimits.MaxQueryLength)
            return CreateToolErrorResponse(id, QueryLimits.FormatQueryTooLongError());
        if (IsBareVerbatimQueryToken(query))
            return CreateToolErrorResponse(id, "Add a real symbol name after the command; bare verbatim prefixes like `@` are not valid queries.");

        var maxHopsNode = args?["maxHops"];
        var deprecatedMaxDepthNode = args?["maxDepth"];
        var usedDeprecatedMaxDepth = deprecatedMaxDepthNode != null;
        var adjustments = new ArgumentAdjustmentCollector();
        var maxDepthRequested = ReadOptionalIntArgument(args, "maxHops") ?? ReadOptionalIntArgument(args, "maxDepth") ?? 5;
        var maxDepth = Math.Clamp(maxDepthRequested, 0, MaxImpactDepth);
        string? maxDepthClampWarning = null;
        string? maxDepthDeprecationWarning = null;
        if (usedDeprecatedMaxDepth)
        {
            maxDepthDeprecationWarning = "maxDepth is deprecated for impact_analysis; use maxHops instead.";
            adjustments.AddWarning(maxDepthDeprecationWarning);
        }
        if (maxDepthRequested != maxDepth)
        {
            maxDepthClampWarning = $"maxHops was clamped from {maxDepthRequested} to {maxDepth} (server cap is [0, {MaxImpactDepth}]).";
            adjustments.AddClamped("maxHops", maxDepthRequested, maxDepth, 0, MaxImpactDepth);
        }
        var limit = ReadLimit(args, QueryCommandRunner.DefaultImpactLimit, adjustments);
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
                adjustments.ApplyTo(countOnlyPayload);
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
            adjustments.ApplyTo(payload);
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
        var adjustments = new ArgumentAdjustmentCollector();
        var kind = args?["kind"]?.GetValue<string>()?.ToLowerInvariant();
        var severity = args?["severity"]?.GetValue<string>()?.Trim().ToLowerInvariant();
        if (severity is not (null or "error" or FileIssue.SeverityWarning or FileIssue.SeverityInfo))
            return CreateToolErrorResponse(id, "severity must be one of error, warning, info");
        var limit = ReadLimit(args, QueryCommandRunner.DefaultQueryLimit, adjustments);
        var format = ReadResponseFormat(args);
        if (ValidateResponseFormat(format) is string formatError)
            return CreateToolErrorResponse(id, formatError);
        var countOnly = ReadCountOnly(args) || format == "count";
        var pathPatterns = ReadScopedPathList(args);

        return WithDbReader(id, args, reader =>
        {
            var issues = reader.GetIssues(
                kind,
                pathPatterns,
                limit: countOnly ? null : FetchLimitForEnvelope(limit),
                severity: severity);
            var truncated = !countOnly && TrimToRequestedLimit(issues, limit);
            var pathFilterArray = new JsonArray();
            if (pathPatterns is not null)
            {
                foreach (var path in pathPatterns)
                    pathFilterArray.Add(path);
            }
            var payload = new JsonObject
            {
                ["count"] = issues.Count,
                ["truncated"] = truncated,
                ["more_available"] = truncated,
                ["filters"] = new JsonObject
                {
                    ["kind"] = kind,
                    ["severity"] = severity,
                    ["path"] = pathFilterArray,
                },
                ["top_files"] = BuildTopFileHistogram(issues, issue => issue.Path),
            };
            if (countOnly)
            {
                payload["format"] = "count";
            }
            else if (format == "compact")
            {
                payload["format"] = "compact";
                payload["issues"] = BuildCompactValidateIssues(issues);
            }
            else
            {
                payload["issues"] = JsonSerializer.SerializeToNode(issues, _jsonOptions);
            }
            var summary = issues.Count > 0
                ? $"Found {issues.Count} encoding issue(s)."
                : "No encoding issues found.";
            adjustments.ApplyTo(payload);
            return CreateToolResult(id, summary, payload);
        });
    }

    private static JsonArray BuildCompactValidateIssues(IEnumerable<FileIssue> issues)
    {
        var compact = new JsonArray();
        foreach (var issue in issues)
        {
            compact.Add(new JsonObject
            {
                ["path"] = issue.Path,
                ["line"] = issue.Line,
                ["kind"] = issue.Kind,
                ["severity"] = issue.Severity,
                ["origin"] = issue.Origin,
                ["message"] = issue.Message,
            });
        }
        return compact;
    }

    private JsonNode ExecuteSymbolHotspots(JsonNode? id, JsonNode? args)
    {
        var adjustments = new ArgumentAdjustmentCollector();
        var limit = ReadLimit(args, QueryCommandRunner.DefaultQueryLimit, adjustments);
        var kind = args?["kind"]?.GetValue<string>()?.ToLowerInvariant();
        var lang = args?["lang"]?.GetValue<string>()?.ToLowerInvariant();
        var groupBy = args?["groupBy"]?.GetValue<string>()?.ToLowerInvariant()
            ?? (string.Equals(lang, "sql", StringComparison.Ordinal) ? "statement" : "symbol");
        if (groupBy is not ("symbol" or "file" or "statement"))
        {
            var groupByDisplay = McpBoundedText.ForDisplay(groupBy);
            var extra = new JsonObject
            {
                ["parameter"] = "groupBy",
                ["value"] = groupByDisplay.Text,
            };
            groupByDisplay.AddMetadata(extra, "value");
            return CreateToolErrorResponse(
                id,
                $"Unsupported symbol_hotspots groupBy '{groupByDisplay.Text}'. Use symbol, file, or statement.",
                category: McpErrorEnvelope.CategoryInvalidArgument,
                suggestion: "Use symbol, file, or statement for symbol_hotspots groupBy.",
                retrySafe: false,
                extraData: extra);
        }
        var pathPatterns = ReadScopedPathList(args);
        var excludePaths = ReadStringList(args, "excludePaths");
        var excludeTests = args?["excludeTests"]?.GetValue<bool>() ?? false;
        var visibilityFilters = ReadStringOrCommaSeparatedList(args, "visibility");
        var excludeVisibilityFilters = ReadStringOrCommaSeparatedList(args, "excludeVisibility");

        return WithDbReader(id, args, reader =>
        {
            var fileResults = groupBy == "file"
                ? reader.GetFileSymbolHotspots(limit, kind, lang, pathPatterns, excludePaths, excludeTests, visibilityFilters, excludeVisibilityFilters)
                : null;
            var results = fileResults == null
                ? reader.GetSymbolHotspots(limit, kind, lang, pathPatterns, excludePaths, excludeTests, visibilityFilters, excludeVisibilityFilters)
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
            AddVisibilityFilterEcho(payload, visibilityFilters, excludeVisibilityFilters);
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
            adjustments.ApplyTo(payload);
            return CreateToolResult(id, summary, payload);
        });
    }

    private JsonNode ExecuteUnusedSymbols(JsonNode? id, JsonNode? args)
    {
        var adjustments = new ArgumentAdjustmentCollector();
        var limit = ReadLimit(args, QueryCommandRunner.DefaultImpactLimit, adjustments);
        var kind = args?["kind"]?.GetValue<string>()?.ToLowerInvariant();
        var lang = args?["lang"]?.GetValue<string>()?.ToLowerInvariant();
        var bucket = args?["bucket"]?.GetValue<string>()?.ToLowerInvariant();
        var minConfidence = args?["minConfidence"]?.GetValue<string>()?.ToLowerInvariant();
        var pathPatterns = ReadScopedPathList(args);
        var excludePaths = ReadStringList(args, "excludePaths");
        var excludeTests = args?["excludeTests"]?.GetValue<bool>() ?? false;
        var visibilityFilters = ReadStringOrCommaSeparatedList(args, "visibility");
        var excludeVisibilityFilters = ReadStringOrCommaSeparatedList(args, "excludeVisibility");
        var byBucket = args?["byBucket"]?.GetValue<bool>() ?? false;
        if (bucket != null && !QueryCommandRunner.IsKnownUnusedBucket(bucket))
            return CreateToolErrorResponse(id, $"Invalid bucket '{bucket}'. Use one of: {string.Join(", ", QueryCommandRunner.OrderedUnusedBuckets)}.");
        if (minConfidence != null && !QueryCommandRunner.IsKnownUnusedConfidence(minConfidence))
            return CreateToolErrorResponse(id, $"Invalid minConfidence '{minConfidence}'. Use one of: medium, low.");

        // Add graph-support metadata for AI trust decisions
        // AI の信頼判断のためにグラフ対応メタデータを追加
        bool? graphSupported = lang != null ? ReferenceExtractor.SupportsLanguage(lang) : null;
        var graphSupportReason = ReferenceExtractor.BuildGraphSupportReason(lang, graphSupported);

        return WithDbReader(id, args, reader =>
        {
            var results = reader.GetUnusedSymbols(
                limit,
                kind,
                lang,
                pathPatterns,
                excludePaths,
                excludeTests,
                visibilityFilters,
                excludeVisibilityFilters,
                bucketFilter: bucket,
                minConfidence: minConfidence);
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
            AddVisibilityFilterEcho(payload, visibilityFilters, excludeVisibilityFilters);
            payload["byBucket"] = byBucket;
            if (byBucket)
                payload["symbols_by_bucket"] = BuildUnusedSymbolsByBucket(results);
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
            adjustments.ApplyTo(payload);
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

    private JsonNode ExecuteLanguages(JsonNode? id, JsonNode? args)
    {
        var langExtensions = FileIndexer.GetLanguageExtensions();
        var symbolLangs = SymbolExtractor.GetSupportedLanguages();
        var referenceLangs = ReferenceExtractor.GetSupportedLanguages();
        var indexedOnly = args?["indexedOnly"]?.GetValue<bool>() ?? false;
        var capabilities = ReadStringOrArrayList(args, "capability")
            .Select(value => value.Trim().ToLowerInvariant())
            .ToList();
        var extensionFilter = args?["extension"]?.GetValue<string>()?.Trim();
        var normalizedExtension = string.IsNullOrWhiteSpace(extensionFilter)
            ? null
            : extensionFilter.StartsWith(".", StringComparison.Ordinal) ? extensionFilter : "." + extensionFilter;
        var aliasFilter = QueryCommandRunner.NormalizeLangFilterValue(args?["alias"]?.GetValue<string>());

        if (args?["capability"] is JsonArray capabilityArray && capabilities.Count != capabilityArray.Count)
            return CreateToolErrorResponse(id, "capability entries must be non-empty strings.");

        foreach (var capability in capabilities)
        {
            if (!IsKnownLanguageCapability(capability))
                return CreateToolErrorResponse(id, $"Invalid language capability '{capability}'. Use one of: symbols, graph, references.");
        }

        // Build consolidated language info / 統合言語情報を構築
        var allLangs = new Dictionary<string, (List<string> Extensions, List<string> Aliases, bool Symbols, bool References, bool Graph, List<string> CapabilityGaps)>(StringComparer.Ordinal);
        foreach (var (ext, lang) in langExtensions)
        {
            if (!allLangs.TryGetValue(lang, out var info))
            {
                var hasSymbols = symbolLangs.Contains(lang);
                var hasReferences = referenceLangs.Contains(lang);
                info = (
                    new List<string>(),
                    QueryCommandRunner.GetLanguageAliases(lang).ToList(),
                    hasSymbols,
                    hasReferences,
                    hasReferences,
                    BuildLanguageCapabilityGaps(hasSymbols, hasReferences, hasReferences));
                allLangs[lang] = info;
            }
            info.Extensions.Add(ext);
        }

        JsonNode BuildResponse(HashSet<string>? indexedLanguages)
        {
            var sorted = allLangs
                .Where(kv => !indexedOnly || indexedLanguages?.Contains(kv.Key) == true)
                .Where(kv => capabilities.All(capability => LanguageMatchesCapability(kv.Value.Symbols, kv.Value.References, kv.Value.Graph, capability)))
                .Where(kv => normalizedExtension is null || kv.Value.Extensions.Contains(normalizedExtension, StringComparer.OrdinalIgnoreCase))
                .Where(kv => aliasFilter is null
                    || string.Equals(kv.Key, aliasFilter, StringComparison.OrdinalIgnoreCase)
                    || kv.Value.Aliases.Contains(aliasFilter, StringComparer.OrdinalIgnoreCase))
                .OrderBy(kv => kv.Key, StringComparer.Ordinal)
                .ToList();

            var languagesArray = new JsonArray();
            foreach (var (lang, info) in sorted)
            {
                var extArray = new JsonArray();
                foreach (var ext in info.Extensions.OrderBy(e => e, StringComparer.Ordinal))
                    extArray.Add(ext);

                languagesArray.Add(new JsonObject
                {
                    ["lang"] = lang,
                    ["extensions"] = extArray,
                    ["aliases"] = new JsonArray(info.Aliases.OrderBy(alias => alias, StringComparer.Ordinal).Select(alias => JsonValue.Create(alias)).ToArray()),
                    ["symbol_extraction"] = info.Symbols,
                    ["reference_extraction"] = info.References,
                    ["graph_queries"] = info.Graph,
                    ["capability_gaps"] = new JsonArray(info.CapabilityGaps.Select(gap => JsonValue.Create(gap)).ToArray()),
                });
            }

            var payload = new JsonObject
            {
                ["languages"] = languagesArray,
                ["filters"] = new JsonObject
                {
                    ["indexedOnly"] = indexedOnly,
                    ["capability"] = new JsonArray(capabilities.Select(capability => JsonValue.Create(capability)).ToArray()),
                    ["extension"] = normalizedExtension,
                    ["alias"] = aliasFilter,
                },
            };
            if (normalizedExtension is not null)
            {
                payload["extension_lookup"] = new JsonObject
                {
                    ["extension"] = normalizedExtension,
                    ["matched"] = sorted.Count,
                    ["languages"] = new JsonArray(sorted.Select(kv => JsonValue.Create(kv.Key)).ToArray()),
                };
            }
            if (aliasFilter is not null)
            {
                payload["alias_lookup"] = new JsonObject
                {
                    ["alias"] = aliasFilter,
                    ["matched"] = sorted.Count,
                    ["languages"] = new JsonArray(sorted.Select(kv => JsonValue.Create(kv.Key)).ToArray()),
                };
            }

            var summary = $"{sorted.Count} languages supported. {symbolLangs.Count} with symbol extraction, {referenceLangs.Count} with reference extraction, {referenceLangs.Count} with call-graph queries.";
            return CreateToolResult(id, summary, payload);
        }

        if (!indexedOnly)
            return BuildResponse(null);

        return WithDbReader(id, args, reader => BuildResponse(new HashSet<string>(reader.GetStatus().Languages.Keys, StringComparer.Ordinal)));
    }

    private static bool IsKnownLanguageCapability(string capability) =>
        capability is "symbols" or "graph" or "references";

    private static bool LanguageMatchesCapability(bool symbols, bool references, bool graph, string capability) =>
        capability switch
        {
            "symbols" => symbols,
            "references" => references,
            "graph" => graph,
            _ => false,
        };

    private static List<string> BuildLanguageCapabilityGaps(bool symbols, bool references, bool graph)
    {
        var gaps = new List<string>();
        if (!symbols)
            gaps.Add("missing-symbols");
        if (!references)
            gaps.Add("missing-references");
        if (!graph)
            gaps.Add("missing-graph");
        return gaps;
    }

    private sealed record McpIndexUnsupportedMode(string Name, string Reason, bool BlocksIndexing);

    private static FileIssue BuildMcpSymbolCountExceededIssue(string path, int symbolCount, int maxSymbolsPerFile) =>
        new()
        {
            Path = path,
            Kind = "symbol_count_exceeded",
            Line = 0,
            Message = $"Symbol extraction produced {symbolCount:N0} symbols, exceeding the maxSymbolsPerFile limit of {maxSymbolsPerFile:N0}; file content, symbols, and references were not indexed. Exclude the generated/pathological file or raise maxSymbolsPerFile if this is expected.",
        };

    private static FileIssue BuildMcpReferenceCountExceededIssue(string path, int referenceCount, int maxReferencesPerFile) =>
        new()
        {
            Path = path,
            Kind = "reference_count_exceeded",
            Line = 0,
            Message = $"Reference extraction produced {referenceCount:N0} references, exceeding the maxReferencesPerFile limit of {maxReferencesPerFile:N0}; references were not indexed for this file. Exclude the generated/pathological file or raise maxReferencesPerFile if this is expected.",
        };

    private static bool TryReadMcpIndexSymlinkPolicy(JsonNode? args, out FileIndexer.SymlinkPolicy symlinkPolicy, out string? error)
    {
        symlinkPolicy = FileIndexer.SymlinkPolicy.None;
        error = null;
        var value = args?["followSymlinks"]?.GetValue<string>()?.Trim().ToLowerInvariant();
        if (string.IsNullOrEmpty(value) || value == "none")
            return true;
        if (value == "internal")
        {
            symlinkPolicy = FileIndexer.SymlinkPolicy.Internal;
            return true;
        }
        if (value == "all")
        {
            symlinkPolicy = FileIndexer.SymlinkPolicy.All;
            return true;
        }

        error = "followSymlinks must be one of none, internal, all";
        return false;
    }

    private static string FormatMcpIndexSymlinkPolicy(FileIndexer.SymlinkPolicy symlinkPolicy)
        => symlinkPolicy switch
        {
            FileIndexer.SymlinkPolicy.Internal => "internal",
            FileIndexer.SymlinkPolicy.All => "all",
            _ => "none",
        };

    private static List<McpIndexUnsupportedMode> BuildMcpIndexUnsupportedModes(JsonNode? args, int? requestedParallelism, int? requestedDebounce)
    {
        var modes = new List<McpIndexUnsupportedMode>();
        if (requestedParallelism.HasValue && requestedParallelism.Value != 1)
            modes.Add(new McpIndexUnsupportedMode("parallelism", "MCP index currently runs serially; requested parallelism is reported but effective_parallelism remains 1.", false));
        if (ReadStringOrArrayList(args, "commits").Count > 0)
            modes.Add(new McpIndexUnsupportedMode("commits", "Commit-scoped MCP indexing is not implemented; use CLI `cdidx index --commits ...` for this scope.", true));
        if (ReadStringOrArrayList(args, "changedBetween").Count > 0)
            modes.Add(new McpIndexUnsupportedMode("changedBetween", "Changed-between MCP indexing is not implemented; use CLI `cdidx index --changed-between ...` for this scope.", true));
        if (ReadStringOrArrayList(args, "files").Count > 0)
            modes.Add(new McpIndexUnsupportedMode("files", "File-scoped MCP indexing is not implemented; use CLI `cdidx index --files ...` for this scope.", true));

        var watchRequested = args?["watch"]?.GetValue<bool>() ?? false;
        if (watchRequested)
        {
            modes.Add(new McpIndexUnsupportedMode("watch", "Long-running watch mode is intentionally disabled for MCP tool calls; use the CLI watch command instead.", true));
        }
        else if (requestedDebounce.HasValue)
        {
            modes.Add(new McpIndexUnsupportedMode("debounce", "debounce only applies to watch mode, which is disabled for MCP tool calls.", false));
        }
        return modes;
    }

    private static JsonArray BuildMcpIndexUnsupportedModesJson(IEnumerable<McpIndexUnsupportedMode> unsupportedModes)
    {
        var array = new JsonArray();
        foreach (var mode in unsupportedModes)
        {
            array.Add(new JsonObject
            {
                ["name"] = mode.Name,
                ["reason"] = mode.Reason,
                ["blocks_indexing"] = mode.BlocksIndexing,
            });
        }
        return array;
    }

    private static bool HasBlockingMcpIndexUnsupportedMode(IEnumerable<McpIndexUnsupportedMode> unsupportedModes)
        => unsupportedModes.Any(mode => mode.BlocksIndexing);

    private static JsonObject CaptureMcpIndexMemorySample(string stage, Stopwatch stopwatch)
    {
        var snapshot = ProcessMemorySnapshot.Capture();
        return new JsonObject
        {
            ["stage"] = stage,
            ["elapsed_ms"] = stopwatch.ElapsedMilliseconds,
            ["managed_bytes"] = snapshot.HeapBytes,
            ["working_set_bytes"] = snapshot.WorkingSetBytes,
            ["private_bytes"] = snapshot.PrivateBytes,
        };
    }

    private static JsonObject BuildMcpIndexOptionsPayload(
        bool dryRun,
        bool rebuild,
        long? maxFileBytes,
        int maxSymbolsPerFile,
        int maxReferencesPerFile,
        FileIndexer.SymlinkPolicy symlinkPolicy,
        IReadOnlyList<string> includeSymbolKinds,
        IReadOnlyList<string> excludeSymbolKinds,
        bool memoryTrace,
        int? requestedParallelism,
        int? requestedDebounce,
        JsonNode? args)
        => new()
        {
            ["dryRun"] = dryRun,
            ["rebuild"] = rebuild,
            ["maxFileBytes"] = maxFileBytes.HasValue ? JsonValue.Create(maxFileBytes.Value) : null,
            ["maxSymbolsPerFile"] = maxSymbolsPerFile,
            ["maxReferencesPerFile"] = maxReferencesPerFile,
            ["followSymlinks"] = FormatMcpIndexSymlinkPolicy(symlinkPolicy),
            ["includeSymbolKind"] = ToJsonStringArray(includeSymbolKinds),
            ["excludeSymbolKind"] = ToJsonStringArray(excludeSymbolKinds),
            ["memoryTrace"] = memoryTrace,
            ["parallelism_requested"] = requestedParallelism.HasValue ? JsonValue.Create(requestedParallelism.Value) : null,
            ["effective_parallelism"] = 1,
            ["watch_requested"] = args?["watch"]?.GetValue<bool>() ?? false,
            ["debounce"] = requestedDebounce.HasValue ? JsonValue.Create(requestedDebounce.Value) : null,
        };

    private async Task RefreshClientRootsIfNeededAsync()
    {
        if (!_clientRootsStale || !HasClientCapability("roots"))
            return;

        var result = await SendClientRequestAsync("roots/list", null, _currentRequestToken.Value).ConfigureAwait(false);
        if (result?["roots"] is not JsonArray roots)
            return;

        ResetClientRoots();
        foreach (var root in roots)
        {
            var uri = TryReadStringValue(root?["uri"]) ?? TryReadStringValue(root);
            if (!string.IsNullOrWhiteSpace(uri))
                CaptureClientRoot(uri);
        }
        _clientRootsStale = false;
    }

    private bool IsPathWithinClientRoots(string path)
    {
        if (!HasClientCapability("roots"))
            return true;

        var rootPaths = _clientRoots
            .Select(root => TryReadStringValue(root))
            .Select(McpPathBoundary.TryResolveRootPath)
            .Where(root => !string.IsNullOrWhiteSpace(root))
            .Cast<string>()
            .ToArray();
        if (rootPaths.Length == 0)
            return false;

        var fullPath = Path.GetFullPath(path);
        return rootPaths.Any(root => McpPathBoundary.IsPathWithinDirectory(root, fullPath));
    }

    private async Task<JsonNode> ExecuteIndexAsync(JsonNode? id, JsonNode? args, JsonNode? progressToken = null)
    {
        if (!TryReadRequiredIndexPathParameter(args, "path", out var path, out var requiredError))
            return CreateToolErrorResponse(id, requiredError!);

        var rebuild = args?["rebuild"]?.GetValue<bool>() ?? false;
        var dryRun = args?["dryRun"]?.GetValue<bool>() ?? args?["dry_run"]?.GetValue<bool>() ?? false;
        var memoryTrace = args?["memoryTrace"]?.GetValue<bool>() ?? false;
        var requestedParallelism = ReadOptionalIntArgument(args, "parallelism");
        var requestedDebounce = ReadOptionalIntArgument(args, "debounce");
        var maxSymbolsPerFile = ReadOptionalIntArgument(args, "maxSymbolsPerFile") ?? IndexCommandRunner.DefaultMaxSymbolsPerFile;
        if (maxSymbolsPerFile <= 0 || maxSymbolsPerFile > IndexCommandRunner.MaxSymbolsPerFileLimit)
            return CreateToolErrorResponse(id, $"maxSymbolsPerFile must be between 1 and {IndexCommandRunner.MaxSymbolsPerFileLimit}");
        var maxReferencesPerFile = ReadOptionalIntArgument(args, "maxReferencesPerFile") ?? IndexCommandRunner.DefaultMaxReferencesPerFile;
        if (maxReferencesPerFile <= 0 || maxReferencesPerFile > IndexCommandRunner.MaxReferencesPerFileLimit)
            return CreateToolErrorResponse(id, $"maxReferencesPerFile must be between 1 and {IndexCommandRunner.MaxReferencesPerFileLimit}");
        if (!TryReadMcpIndexSymlinkPolicy(args, out var symlinkPolicy, out var symlinkPolicyError))
            return CreateToolErrorResponse(id, symlinkPolicyError!);
        var includeSymbolKinds = ReadStringOrCommaSeparatedList(args, "includeSymbolKind");
        var excludeSymbolKinds = ReadStringOrCommaSeparatedList(args, "excludeSymbolKind");
        var symbolKindFilter = SymbolKindFilter.Create(includeSymbolKinds, excludeSymbolKinds, parseError: null);
        if (symbolKindFilter.ParseError != null)
            return CreateToolErrorResponse(id, symbolKindFilter.ParseError);
        var unsupportedModes = BuildMcpIndexUnsupportedModes(args, requestedParallelism, requestedDebounce);
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
        var runStartedAtUtc = GetUtcNow();
        var runStopwatch = Stopwatch.StartNew();
        var memorySamples = memoryTrace
            ? new JsonArray { CaptureMcpIndexMemorySample("start", runStopwatch) }
            : null;
        var optionsPayload = BuildMcpIndexOptionsPayload(
            dryRun,
            rebuild,
            maxFileBytes,
            maxSymbolsPerFile,
            maxReferencesPerFile,
            symlinkPolicy,
            includeSymbolKinds,
            excludeSymbolKinds,
            memoryTrace,
            requestedParallelism,
            requestedDebounce,
            args);

        // Prevent path traversal — only allow indexing within current working directory
        // パストラバーサル防止 — カレントディレクトリ配下のみインデックスを許可
        var cwd = Path.GetFullPath(".");
        if (!McpPathBoundary.IsPathWithinDirectory(cwd, projectPath))
            return CreateToolErrorResponse(id, "Path must be within the current working directory");
        await RefreshClientRootsIfNeededAsync().ConfigureAwait(false);
        if (!IsPathWithinClientRoots(projectPath))
            return CreateToolErrorResponse(id, "Path must be within an MCP client root");

        if (!Directory.Exists(projectPath))
            return CreateToolErrorResponse(id, "Directory not found");

        var unsupportedModesJson = BuildMcpIndexUnsupportedModesJson(unsupportedModes);
        if (dryRun)
        {
            var ignoreCase = GitHelper.ResolveIgnoreCase(projectPath, _currentRequestToken.Value);
            var dryRunIndexer = new FileIndexer(
                projectPath,
                ignoreCase,
                GitHelper.TryGetRepositoryRoot(projectPath, _currentRequestToken.Value) ?? Path.GetFullPath(projectPath),
                maxFileBytes,
                directoryIgnoreCaseProbe: null,
                symlinkPolicy: symlinkPolicy,
                generatedCodePatterns: IndexCommandRunner.ReadGeneratedCodePatternsFromEnvironment());
            var scan = dryRunIndexer.ScanFilesDetailed(cancellationToken: _currentRequestToken.Value);
            if (memorySamples != null)
                memorySamples.Add(CaptureMcpIndexMemorySample("scan", runStopwatch));
            var dryRunFatalScanErrors = scan.Errors.Where(error => error.IsFatal).ToList();
            var dryRunPayload = new JsonObject
            {
                ["path"] = projectPath,
                ["dry_run"] = true,
                ["would_rebuild"] = rebuild,
                ["max_file_bytes"] = maxFileBytes,
                ["index_options"] = optionsPayload,
                ["unsupported_modes"] = unsupportedModesJson,
                ["summary"] = new JsonObject
                {
                    ["files_scanned"] = scan.Files.Count,
                    ["scan_errors"] = scan.Errors.Count,
                    ["fatal_scan_errors"] = dryRunFatalScanErrors.Count,
                    ["unknown_extension_file_count"] = scan.UnknownExtensionFiles.Count,
                    ["would_mutate_database"] = false,
                },
                ["duration_ms"] = runStopwatch.ElapsedMilliseconds,
                ["started_at"] = runStartedAtUtc.ToString("o", System.Globalization.CultureInfo.InvariantCulture),
                ["completed_at"] = GetUtcNow().ToString("o", System.Globalization.CultureInfo.InvariantCulture),
            };
            if (memorySamples != null)
            {
                memorySamples.Add(CaptureMcpIndexMemorySample("finalize", runStopwatch));
                dryRunPayload["memory_trace"] = memorySamples;
            }
            return CreateToolResult(id, "Index dry run complete.", dryRunPayload);
        }

        if (HasBlockingMcpIndexUnsupportedMode(unsupportedModes))
        {
            var unsupportedData = new JsonObject
            {
                ["unsupported_modes"] = unsupportedModesJson,
                ["index_options"] = optionsPayload,
                ["index_started"] = false,
            };
            return CreateToolErrorResponse(
                id,
                "MCP index does not support the requested scoped or watch indexing mode; no indexing started.",
                category: McpErrorEnvelope.CategoryInvalidArgument,
                suggestion: "Use dryRun:true to inspect the plan, remove unsupported scope/watch arguments, or run the equivalent cdidx index command in the CLI.",
                retrySafe: false,
                extraData: unsupportedData);
        }

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
        var priorSymbolKindFilterSignature = db.GetMetaString(IndexCommandRunner.SymbolKindFilterMetaKey);
        var requestToken = _currentRequestToken.Value;
        requestToken.ThrowIfCancellationRequested();
        // Capture git HEAD so subsequent queries can detect a worktree branch / HEAD switch
        // (`git switch other-branch` inside the worktree) without a `--check` workspace scan.
        // Like the CLI full-scan path, the value is only persisted at the end of a successful
        // run (errors == 0) so a crashed / partial index keeps the previous HEAD and surfaces
        // staleness until the next clean refresh. Issues #1508 and #1512.
        // worktree 内の HEAD 切替検出のため HEAD を捕捉。CLI full-scan と同じく成功時のみ
        // 書き込み、partial 失敗は旧 HEAD を残して次回 full scan で更新する。
        var currentHeadCommit = GitHelper.TryGetHeadCommit(projectPath, requestToken);

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
        var indexer = new FileIndexer(
            projectPath,
            GitHelper.ResolveIgnoreCase(projectPath, requestToken),
            GitHelper.TryGetRepositoryRoot(projectPath, requestToken) ?? Path.GetFullPath(projectPath),
            maxFileBytes,
            directoryIgnoreCaseProbe: null,
            symlinkPolicy: symlinkPolicy,
            generatedCodePatterns: IndexCommandRunner.ReadGeneratedCodePatternsFromEnvironment());
        using var postExtractionHooks = PostExtractionHookRunner.DiscoverDefault(maxFileBytes);
        var currentHotspotFamilyMarkerFingerprints = GetHotspotFamilyMarkerFingerprints(indexer, requestToken);
        var currentCSharpSymbolNameContractVersion = DbContext.CSharpSymbolNameContractVersion.ToString(System.Globalization.CultureInfo.InvariantCulture);
        var csharpSymbolNameContractMatchesCurrent = priorCSharpSymbolNameContractVersion == currentCSharpSymbolNameContractVersion;
        var currentMetadataTargetVersion = DbContext.MetadataTargetVersion.ToString(System.Globalization.CultureInfo.InvariantCulture);
        var csharpMetadataTargetsNeedRefresh = priorMetadataTargetCsharp != currentMetadataTargetVersion;
        var currentSqlGraphContractVersion = DbContext.SqlGraphContractVersion.ToString(System.Globalization.CultureInfo.InvariantCulture);
        var sqlGraphContractMatchesCurrent = priorSqlGraphContractVersion == currentSqlGraphContractVersion;
        var hotspotFamilyTrustMatchesCurrent = GetHotspotFamilyTrustMatchesCurrent(
            priorHotspotFamilyVersions,
            priorHotspotFamilyMarkerFingerprints,
            currentHotspotFamilyMarkerFingerprints);
        var symbolKindFilterMatchesPrior = string.Equals(
            priorSymbolKindFilterSignature,
            symbolKindFilter.Signature,
            StringComparison.Ordinal);
        var symbolKindFilterMetaMarkedIncomplete = symbolKindFilterMatchesPrior;
        var normalizedProjectPath = Path.GetFullPath(projectPath);
        var normalizedPriorIndexedProjectRoot = string.IsNullOrWhiteSpace(priorIndexedProjectRoot)
            ? null
            : Path.GetFullPath(priorIndexedProjectRoot);
        var projectRootWritten = PathsEqual(normalizedPriorIndexedProjectRoot, normalizedProjectPath);
        var typeScriptAugmentationVersionMatchesCurrent = writer.TypeScriptAugmentationVersionMatchesCurrent();
        var typeScriptAugmentationNeedsRefresh = !projectRootWritten
            || !typeScriptAugmentationVersionMatchesCurrent;
        var typeScriptAugmentationReadyCleared = !typeScriptAugmentationVersionMatchesCurrent;
        var ftsMutated = false;

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

        void MarkSymbolKindFilterMetaIncompleteOnce()
        {
            if (symbolKindFilterMetaMarkedIncomplete)
                return;
            writer.SetMeta(IndexCommandRunner.SymbolKindFilterMetaKey, null);
            symbolKindFilterMetaMarkedIncomplete = true;
        }

        void RequireTypeScriptAugmentationRefresh()
        {
            if (!typeScriptAugmentationReadyCleared)
            {
                writer.ClearTypeScriptAugmentationReady();
                typeScriptAugmentationReadyCleared = true;
            }

            typeScriptAugmentationNeedsRefresh = true;
        }

        static (long BytesRead, long SkippedFileCount) SumReadableFileBytes(
            IEnumerable<string> paths,
            string projectRoot,
            List<string> diagnostics,
            List<McpIndexDiagnostic> structuredDiagnostics,
            IReadOnlyDictionary<string, long>? knownFileSizes = null)
        {
            long total = 0;
            long skipped = 0;
            foreach (var filePath in paths)
            {
                if (knownFileSizes != null && knownFileSizes.TryGetValue(filePath, out var knownSize))
                {
                    total += knownSize;
                    continue;
                }

                try
                {
                    var info = new FileInfo(filePath);
                    if (info.Exists)
                        total += info.Length;
                }
                catch (Exception ex) when (ex is IOException or UnauthorizedAccessException or NotSupportedException or ArgumentException)
                {
                    skipped++;
                    diagnostics.Add(IndexCommandRunner.FormatIndexRunDiagnostic(
                        "file_size_bytes_skipped",
                        FormatDiagnosticPath(projectRoot, filePath),
                        ex));
                    structuredDiagnostics.Add(BuildMcpIndexExceptionDiagnostic(
                        "file_size_bytes_skipped",
                        "skipped_file_sizing",
                        "measure_file_size",
                        projectRoot,
                        filePath,
                        ex));
                }
            }

            return (total, skipped);
        }

        static string FormatDiagnosticPath(string projectRoot, string path)
        {
            try
            {
                var relative = FileIndexer.NormalizePathSeparators(Path.GetRelativePath(projectRoot, path));
                return relative == "."
                    || relative.StartsWith("../", StringComparison.Ordinal)
                    || Path.IsPathRooted(relative)
                    ? path
                    : relative;
            }
            catch (Exception ex) when (ex is IOException or UnauthorizedAccessException or NotSupportedException or ArgumentException)
            {
                return path;
            }
        }

        var indexRunDiagnostics = new List<string>();
        var mcpIndexDiagnostics = new List<McpIndexDiagnostic>();

        // First mutation point — demote readiness just before any write.
        // 実書き込み直前で readiness をクリア。
        writer.ClearReadyFlags();
        writer.ClearHotspotFamilyReady();
        writer.ClearMetadataTargetReady();

        var hadCSharpStaticInterfaceContractsBeforePurge =
            writer.LoadCSharpStaticInterfaceContractSymbols().Count > 0;

        // Purge stale files / 古いファイルをパージ
        var purged = writer.PurgeStaleFiles(projectPath, beforeCommit: RequireTypeScriptAugmentationRefresh);
        if (purged > 0)
        {
            csharpMetadataTargetsNeedRefresh = true;
            ftsMutated = true;
            WriteProjectRootOnce();
        }

        // Purge references for languages no longer graph-supported / グラフ非対応になった言語の参照をパージ
        writer.PurgeUnsupportedReferences(ReferenceExtractor.GetSupportedLanguages());

        // Scan and index / スキャン・インデックス
        var scanResult = indexer.ScanFilesDetailed(cancellationToken: requestToken);
        if (memorySamples != null)
            memorySamples.Add(CaptureMcpIndexMemorySample("scan", runStopwatch));
        var files = scanResult.Files;
        var fileTargets = files.Select(filePath => CSharpStaticInterfacePrepass.FileTarget.Create(
            projectPath,
            filePath,
            FileIndexer.GetReusableDetectedLanguage(filePath, scanResult.FileLanguages))).ToArray();
        var knownReadableFileSizes = new Dictionary<string, long>(StringComparer.Ordinal);
        await EmitProgressNotificationAsync(progressToken, 0, files.Count, "Index scan complete; indexing files.").ConfigureAwait(false);
        var csharpPrepassTargets = fileTargets
            .Where(static target => target.Language == "csharp")
            .ToArray();
        bool CanReuseCSharpPrepassTargetWithoutRead(CSharpStaticInterfacePrepass.FileTarget target)
        {
            if (!symbolKindFilterMatchesPrior || !csharpSymbolNameContractMatchesCurrent)
                return false;
            if (target.Language != "csharp")
                return false;

            var existingId = TryGetUnchangedFileIdFromStat(
                writer,
                target.FilePath,
                target.IndexPath,
                target.Language,
                allowReuse: true,
                out _);
            if (existingId == null)
                return false;

            return !writer.HasReusableFileBlockingIssueForFile(
                existingId.Value,
                maxSymbolsPerFile,
                maxReferencesPerFile,
                indexer.IsGeneratedCodeExtractionSuppressed(target.IndexPath));
        }

        CSharpStaticInterfaceWorkspaceSymbols csharpWorkspace;
        if (csharpPrepassTargets.Length == 0)
        {
            csharpWorkspace = new CSharpStaticInterfaceWorkspaceSymbols([], false);
        }
        else
        {
            McpIndexCSharpPrepassForTesting?.Invoke();
            csharpWorkspace = CSharpStaticInterfacePrepass.BuildWorkspaceSymbols(
                writer,
                indexer,
                csharpPrepassTargets,
                canReuseExistingSymbolsWithoutRead: CanReuseCSharpPrepassTargetWithoutRead,
                cancellationToken: requestToken);
        }
        if (purged > 0 && hadCSharpStaticInterfaceContractsBeforePurge)
            csharpWorkspace = csharpWorkspace with { HasStaticInterfaceContracts = true };
        var fatalScanErrors = scanResult.Errors
            .Where(error => error.IsFatal)
            .ToList();
        int processed = 0, skipped = 0, errors = fatalScanErrors.Count;
        var failures = fatalScanErrors
            .Select(BuildScanFailure)
            .ToList();
        var reusedHotspotFamilyLanguages = new HashSet<string>(StringComparer.Ordinal);
        var symbolsDroppedByKindFilter = 0;

        foreach (var target in fileTargets)
        {
            var filePath = target.FilePath;
            var fileBatchMarked = false;
            try
            {
                requestToken.ThrowIfCancellationRequested();
                var allowStatReuse = symbolKindFilterMatchesPrior
                    && (target.Language != "csharp" || csharpSymbolNameContractMatchesCurrent)
                    && (target.Language != "csharp" || !csharpWorkspace.HasStaticInterfaceContracts)
                    && (target.Language != "sql" || sqlGraphContractMatchesCurrent)
                    && AllowReuseWithCurrentHotspotFamilyTrust(target.Language, hotspotFamilyTrustMatchesCurrent);
                var statMatchedId = TryGetUnchangedFileIdFromStat(
                    writer,
                    filePath,
                    target.IndexPath,
                    target.Language,
                    allowStatReuse,
                    out var statSize);
                if (statMatchedId != null
                    && writer.HasReusableFileBlockingIssueForFile(
                        statMatchedId.Value,
                        maxSymbolsPerFile,
                        maxReferencesPerFile,
                        indexer.IsGeneratedCodeExtractionSuppressed(target.IndexPath)))
                {
                    statMatchedId = null;
                }
                if (statMatchedId != null)
                {
                    skipped++;
                    processed++;
                    if (statSize.HasValue)
                        knownReadableFileSizes[filePath] = statSize.Value;
                    if (FileIndexer.SupportsHotspotFamilyMarkerLanguage(target.Language) && target.Language != null)
                        reusedHotspotFamilyLanguages.Add(target.Language);
                    await EmitProgressNotificationAsync(progressToken, processed, files.Count).ConfigureAwait(false);
                    continue;
                }

                McpIndexFileContentLoadForTesting?.Invoke(target.IndexPath);
                var loaded = indexer.BuildLoadedRecordWithRawBytes(
                    filePath,
                    target.RelativePath,
                    target.Language,
                    requestToken);
                var record = loaded.Record;
                knownReadableFileSizes[filePath] = record.Size;
                var content = loaded.Content;
                var rawBytes = loaded.RawBytes;
                var generatedSuppressionIssue = indexer.BuildGeneratedCodeExtractionSkippedIssue(record.Path);
                var existingId = writer.GetUnchangedFileId(
                    record.Path,
                    record.Modified,
                    record.Checksum,
                    size: record.Size,
                    lines: record.Lines,
                    language: record.Lang,
                    generated: record.Generated,
                    allowReuse: symbolKindFilterMatchesPrior
                        && (record.Lang != "csharp" || csharpSymbolNameContractMatchesCurrent)
                        && (record.Lang != "csharp" || !csharpWorkspace.HasStaticInterfaceContracts)
                        && (record.Lang != "sql" || sqlGraphContractMatchesCurrent)
                        && AllowReuseWithCurrentHotspotFamilyTrust(record.Lang, hotspotFamilyTrustMatchesCurrent));
                if (existingId != null
                    && writer.HasReusableFileBlockingIssueForFile(
                        existingId.Value,
                        maxSymbolsPerFile,
                        maxReferencesPerFile,
                        generatedSuppressionIssue != null))
                {
                    existingId = null;
                }
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
                MarkSymbolKindFilterMetaIncompleteOnce();
                if (record.Lang == "csharp")
                    csharpMetadataTargetsNeedRefresh = true;
                var recordRequiresTypeScriptAugmentationRefresh = record.Lang == "typescript";
                using var txn = writer.BeginTransaction(requestToken, "mcp index file");
                if (recordRequiresTypeScriptAugmentationRefresh)
                    RequireTypeScriptAugmentationRefresh();
                var fileId = writer.UpsertFile(record);
                var chunks = ChunkSplitter.SplitNormalized(fileId, content, loaded.HasOversizeLine, record.Lines);
                if (generatedSuppressionIssue != null)
                {
                    writer.InsertChunks(chunks, requestToken);
                    writer.InsertSymbols([], requestToken);
                    writer.InsertReferences([], requestToken);
                    var issues = IndexCommandRunner.AppendIssueIfMissing(
                        FileIndexer.ValidateContent(record.Path, rawBytes, content, record.Lang, loaded.Inspection, loaded.HasOversizeLine),
                        generatedSuppressionIssue);
                    writer.InsertIssues(fileId, issues);
                    WriteProjectRootOnce();
                    writer.ClearBatchInProgress();
                    txn.Commit();
                    ftsMutated = true;
                    processed++;
                    await EmitProgressNotificationAsync(progressToken, processed, files.Count).ConfigureAwait(false);
                    McpIndexFileCommittedForTesting?.Invoke(record.Path);
                    continue;
                }
                List<SymbolRecord> symbols;
                FileIssue? symbolRegexTimeoutIssue;
                using (var regexTimeouts = BoundedRegex.CaptureTimeouts(record.Lang, "symbol_extraction"))
                {
                    symbols = SymbolExtractor.ExtractNormalized(
                        fileId,
                        record.Lang,
                        content,
                        loaded.HasOversizeLine,
                        filePath,
                        projectPath,
                        requestToken);
                    symbolRegexTimeoutIssue = IndexCommandRunner.BuildRegexTimeoutIssue(record.Path, regexTimeouts);
                }
                SymbolExtractor.ApplyFamilyScope(symbols, indexer.GetFamilyScopeKey(filePath, record.Lang));
                var fileContext = new FileContext(projectPath, record.Path, filePath, record.Lang);
                postExtractionHooks.OnSymbolsExtracted(fileContext, symbols);
                symbolsDroppedByKindFilter += symbolKindFilter.Apply(symbols);
                if (symbols.Count > maxSymbolsPerFile)
                {
                    var issue = BuildMcpSymbolCountExceededIssue(record.Path, symbols.Count, maxSymbolsPerFile);
                    IReadOnlyList<FileIssue> capIssues = symbolRegexTimeoutIssue == null
                        ? [issue]
                        : IndexCommandRunner.AppendIssue([symbolRegexTimeoutIssue], issue);
                    writer.InsertSymbols([], requestToken);
                    writer.InsertReferences([], requestToken);
                    writer.InsertIssues(fileId, capIssues);
                }
                else
                {
                    writer.InsertChunks(chunks, requestToken);
                    writer.InsertSymbols(symbols, requestToken);
                    List<ReferenceRecord> references;
                    FileIssue? regexTimeoutIssue;
                    using (var regexTimeouts = BoundedRegex.CaptureTimeouts(record.Lang, "reference_extraction"))
                    {
                        references = ReferenceExtractor.ExtractNormalized(
                            fileId,
                            record.Lang,
                            content,
                            loaded.HasOversizeLine,
                            symbols,
                            record.Path,
                            record.Lang == "csharp" ? csharpWorkspace.Symbols : null,
                            requestToken,
                            maxReferenceCount: maxReferencesPerFile + 1);
                        regexTimeoutIssue = IndexCommandRunner.BuildRegexTimeoutIssue(record.Path, regexTimeouts);
                    }
                    postExtractionHooks.OnReferencesExtracted(fileContext, references);
                    FileIssue? referenceCapIssue = null;
                    if (references.Count > maxReferencesPerFile)
                    {
                        referenceCapIssue = BuildMcpReferenceCountExceededIssue(record.Path, references.Count, maxReferencesPerFile);
                        references = [];
                    }
                    writer.InsertReferences(references, requestToken);
                    // Keep MCP index parity with CLI index: persist file-level validation issues too.
                    // MCPインデックスもCLIインデックスと同等に、ファイル検証issueを保存する。
                    IReadOnlyList<FileIssue> issues = FileIndexer.ValidateContent(record.Path, rawBytes, content, record.Lang, loaded.Inspection, loaded.HasOversizeLine);
                    if (symbolRegexTimeoutIssue != null)
                        issues = IndexCommandRunner.AppendIssue(issues, symbolRegexTimeoutIssue);
                    if (regexTimeoutIssue != null)
                        issues = IndexCommandRunner.AppendIssue(issues, regexTimeoutIssue);
                    if (referenceCapIssue != null)
                        issues = IndexCommandRunner.AppendIssue(issues, referenceCapIssue);
                    writer.InsertIssues(fileId, issues);
                }
                WriteProjectRootOnce();
                writer.ClearBatchInProgress();
                txn.Commit();
                ftsMutated = true;
                McpIndexFileCommittedForTesting?.Invoke(record.Path);
            }
            catch (FileIndexer.BinaryFileSkippedException ex)
            {
                try
                {
                    var skippedRecord = indexer.BuildSkippedFileRecord(filePath, target.RelativePath, target.Language);
                    knownReadableFileSizes[filePath] = skippedRecord.Size;
                    if (skippedRecord.Lang == "csharp")
                        csharpMetadataTargetsNeedRefresh = true;
                    var skippedRecordRequiresTypeScriptAugmentationRefresh = skippedRecord.Lang == "typescript";
                    using var txn = writer.BeginTransaction(requestToken, "mcp index skipped binary");
                    if (skippedRecordRequiresTypeScriptAugmentationRefresh)
                        RequireTypeScriptAugmentationRefresh();
                    var fileId = writer.UpsertFile(skippedRecord);
                    writer.InsertChunks([], requestToken);
                    writer.InsertSymbols([], requestToken);
                    writer.InsertReferences([], requestToken);
                    writer.InsertIssues(fileId, [IndexCommandRunner.BuildNullByteIssue(ex)]);
                    WriteProjectRootOnce();
                    txn.Commit();
                    ftsMutated = true;
                }
                catch (Exception cleanupEx)
                {
                    errors++;
                    failures.Add(BuildIndexFileFailure(projectPath, filePath, cleanupEx, "record_skipped_binary"));
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
                        using var txn = writer.BeginTransaction(requestToken, "mcp index delete missing file");
                        writer.DeleteFileByPath(relativePath);
                        csharpMetadataTargetsNeedRefresh = true;
                        RequireTypeScriptAugmentationRefresh();
                        WriteProjectRootOnce();
                        txn.Commit();
                        ftsMutated = true;
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
            await EmitProgressNotificationAsync(progressToken, processed, files.Count).ConfigureAwait(false);
        }

        if (ftsMutated)
        {
            McpIndexFtsOptimizeForTesting?.Invoke();
            writer.OptimizeFts();
        }
        // MCP index now runs ValidateContent + InsertIssues per file (bdbb2bd) on par with CLI
        // index, so stamp both graph-ready and issues-ready on clean runs — the old "graph only"
        // path is no longer accurate. Bits are only stamped when every file committed without
        // throwing, so a partial failure leaves trust degraded and `validate` still surfaces it.
        // MCP index は CLI と同等に file_issues を永続化するため、成功時は graph / issues の両方を stamp する。
        var hasCSharpFilesAfter = writer.HasAnyFilesWithLanguage("csharp");
        var hasSqlFilesAfter = writer.HasAnyFilesWithLanguage("sql");
        var csharpSymbolNameReadyAfter = !hasCSharpFilesAfter;
        var csharpMetadataTargetReadyAfter = !hasCSharpFilesAfter;
        var sqlGraphContractReadyAfter = !hasSqlFilesAfter;
        var foldReadyAfter = false;
        string? foldReadyReason = null;
        if (!scanResult.HadErrors && errors == 0)
        {
            await EmitProgressNotificationAsync(progressToken, processed, files.Count, "Finalizing index metadata.").ConfigureAwait(false);
            writer.MarkBatchInProgress();
            using var readinessTxn = writer.BeginTransaction(requestToken, "mcp index readiness");
            writer.MarkGraphReady();
            writer.MarkIssuesReady();
            writer.MarkSqlGraphContractReady();
            writer.MarkCSharpSymbolNameContractReady();
            csharpSymbolNameReadyAfter = true;
            if (hasCSharpFilesAfter)
            {
                if (csharpMetadataTargetsNeedRefresh)
                {
                    McpIndexCSharpMetadataResolveForTesting?.Invoke();
                    writer.ResolveCSharpMetadataTargets();
                }
                writer.MarkMetadataTargetReady("csharp");
                csharpMetadataTargetReadyAfter = true;
            }
            else
            {
                csharpMetadataTargetReadyAfter = true;
            }
            sqlGraphContractReadyAfter = true;
            if (typeScriptAugmentationNeedsRefresh)
            {
                McpIndexTypeScriptAugmentationRebuildForTesting?.Invoke();
                writer.RebuildTypeScriptAugmentationReferences(projectPath);
            }
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
            writer.SetMeta(IndexCommandRunner.SymbolKindFilterMetaKey, symbolKindFilter.Signature);

            // Successful no-op MCP full scans should repair explicit-DB roots only after
            // readiness is stamped, preserving the failure-path safety contract.
            // MCP の no-op full-scan root backfill も readiness stamp 後に限定する。
            WriteProjectRootOnce();
            writer.WriteUnknownExtensionFileMetadata(scanResult.UnknownExtensionFiles);
            var bytesRead = SumReadableFileBytes(files, projectPath, indexRunDiagnostics, mcpIndexDiagnostics, knownReadableFileSizes);
            writer.SetMeta(DbContext.LastIndexRunModeMetaKey, rebuild ? "rebuild" : "mcp");
            writer.SetMeta(DbContext.LastIndexRunStartedAtMetaKey, runStartedAtUtc.ToString("o", System.Globalization.CultureInfo.InvariantCulture));
            writer.SetMeta(DbContext.LastIndexRunDurationMsMetaKey, runStopwatch.ElapsedMilliseconds.ToString(System.Globalization.CultureInfo.InvariantCulture));
            writer.SetMeta(DbContext.LastIndexRunFilesScannedMetaKey, files.Count.ToString(System.Globalization.CultureInfo.InvariantCulture));
            writer.SetMeta(DbContext.LastIndexRunFilesSkippedMetaKey, skipped.ToString(System.Globalization.CultureInfo.InvariantCulture));
            writer.SetMeta(DbContext.LastIndexRunParseErrorsMetaKey, errors.ToString(System.Globalization.CultureInfo.InvariantCulture));
            writer.SetMeta(DbContext.LastIndexRunBytesReadMetaKey, bytesRead.BytesRead.ToString(System.Globalization.CultureInfo.InvariantCulture));
            writer.SetMeta(DbContext.LastIndexRunBytesReadSkippedFileCountMetaKey, bytesRead.SkippedFileCount.ToString(System.Globalization.CultureInfo.InvariantCulture));
            writer.SetMeta(DbContext.LastIndexRunBytesReadIncompleteMetaKey, (bytesRead.SkippedFileCount > 0).ToString(System.Globalization.CultureInfo.InvariantCulture));
            writer.SetMeta(DbContext.LastIndexRunRowsUpsertedMetaKey, processed.ToString(System.Globalization.CultureInfo.InvariantCulture));
            writer.SetMeta(DbContext.LastIndexRunRowsDeletedMetaKey, purged.ToString(System.Globalization.CultureInfo.InvariantCulture));
            writer.ClearLastFailedIndexRunMetadata();
            // Persist the current HEAD only after the run is fully successful (errors == 0).
            // Mirrors the CLI full-scan contract (Issue #1508) so MCP-driven re-indexes also
            // refresh `worktree_head_changed`; partial / failed runs leave the prior HEAD
            // untouched and surface staleness until the next clean refresh. Issues #1508 / #1512.
            // CLI full-scan と同じく成功時のみ HEAD を記録する。partial / 失敗は旧 HEAD を残す。
            writer.SetMeta(DbContext.IndexedHeadCommitMetaKey, currentHeadCommit);
            writer.SetMeta(DbContext.IndexedHeadCommitBranchMetaKey, GitHelper.TryGetHeadBranch(projectPath, requestToken));
            // #1509: also persist the always-updated HEAD/branch/timestamp triple so
            // status / consumers can detect cross-session staleness via
            // `commits_ahead_of_indexed_head`. Same best-effort contract — git unavailability
            // writes NULL stamps and stamp exceptions never fail the index itself.
            // #1509: HEAD / branch / timestamp を保存し、cross-session staleness 検出を可能にする。
            try
            {
                var headBranch = GitHelper.TryGetHeadBranch(projectPath, requestToken);
                var timestamp = currentHeadCommit != null
                    ? GetUtcNow().ToString("o", System.Globalization.CultureInfo.InvariantCulture)
                    : null;
                writer.SetMeta(DbContext.IndexedHeadShaMetaKey, currentHeadCommit);
                writer.SetMeta(DbContext.IndexedHeadBranchMetaKey, headBranch);
                writer.SetMeta(DbContext.IndexedHeadTimestampMetaKey, timestamp);
            }
            catch (OperationCanceledException) when (requestToken.IsCancellationRequested)
            {
                throw;
            }
            catch (Exception ex)
            {
                // Best-effort; never fail an otherwise-successful index run.
                indexRunDiagnostics.Add(IndexCommandRunner.FormatIndexRunDiagnostic("indexed_head_metadata_write_failed", ex));
            }
            // #1546: stamp workspace path-case-sensitivity so MCP-driven indexes also
            // surface the diagnostic field through `cdidx status` / MCP status.
            // #1546: MCP 経由 index でも case-sensitivity stamp を残す。
            try
            {
                var ignoreCase = GitHelper.ResolveIgnoreCase(projectPath, requestToken);
                CodeIndex.Cli.PathCasing.SeedFromWorkspace(projectPath, ignoreCase);
                writer.SetMeta(
                    DbContext.WorkspacePathCaseSensitiveMetaKey,
                    (!ignoreCase).ToString(System.Globalization.CultureInfo.InvariantCulture));
            }
            catch (OperationCanceledException) when (requestToken.IsCancellationRequested)
            {
                throw;
            }
            catch (Exception ex)
            {
                // Best-effort; never fail an otherwise-successful index run.
                indexRunDiagnostics.Add(IndexCommandRunner.FormatIndexRunDiagnostic("path_case_sensitivity_metadata_write_failed", ex));
            }
            IndexCommandRunner.StampLastIndexRunDiagnostics(writer, indexRunDiagnostics);
            writer.ClearBatchInProgress();
            readinessTxn.Commit();
        }
        if (!scanResult.HadErrors && errors == 0)
        {
            var plannerMaintenanceFailure = db.RunPlannerStatisticsMaintenance(forceAnalyze: false);
            if (plannerMaintenanceFailure != null)
                IndexCommandRunner.TryStampPlannerStatisticsMaintenanceDiagnostic(writer, indexRunDiagnostics, plannerMaintenanceFailure);
        }
        var (totalFiles, totalChunks, totalSymbols, totalReferences) = writer.GetCounts();
        await EmitProgressNotificationAsync(progressToken, files.Count, files.Count, errors == 0 ? "Indexing complete." : "Indexing completed with errors.").ConfigureAwait(false);
        if (memorySamples != null)
            memorySamples.Add(CaptureMcpIndexMemorySample("finalize", runStopwatch));

        var structured = new JsonObject
        {
            ["path"] = projectPath,
            ["rebuild"] = rebuild,
            ["dry_run"] = false,
            ["max_file_bytes"] = maxFileBytes,
            ["index_options"] = optionsPayload,
            ["unsupported_modes"] = unsupportedModesJson,
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
                ["failed_count"] = failures.Count,
                ["symbols_dropped_by_kind_filter"] = symbolsDroppedByKindFilter
            },
            ["symbol_kind_filter"] = new JsonObject
            {
                ["include"] = ToJsonStringArray(symbolKindFilter.Include),
                ["exclude"] = ToJsonStringArray(symbolKindFilter.Exclude),
                ["active"] = symbolKindFilter.IsActive,
            },
            ["duration_ms"] = runStopwatch.ElapsedMilliseconds,
            ["started_at"] = runStartedAtUtc.ToString("o", System.Globalization.CultureInfo.InvariantCulture),
            ["completed_at"] = GetUtcNow().ToString("o", System.Globalization.CultureInfo.InvariantCulture),
            ["sql_graph_contract_ready"] = sqlGraphContractReadyAfter,
            ["csharp_symbol_name_ready"] = csharpSymbolNameReadyAfter,
            ["csharp_metadata_target_ready"] = csharpMetadataTargetReadyAfter,
            // #86 codex review: AI clients use this to tell whether --exact will use the
            // Unicode fold path or silently fall back to ASCII NOCASE. If false after a clean
            ["fold_ready"] = foldReadyAfter,
            ["fold_ready_reason"] = foldReadyReason
        };
        if (memorySamples != null)
            structured["memory_trace"] = memorySamples;
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
                    ["message_truncated"] = failure.MessageTruncated,
                });
            }
            structured["failed_count"] = failures.Count;
            structured["failures"] = failureArray;
            if (failures.Count > 50)
                structured["failures_truncated"] = failures.Count - 50;
            GlobalToolLog.Error(
                $"mcp_index_file_failures count={failures.Count} first_path={QuoteMcpIndexFailureLogValue(failures[0].Path)} first_error={QuoteMcpIndexFailureLogValue($"{failures[0].ExceptionType}: {failures[0].Message}")}");
        }
        AddMcpIndexDiagnostics(structured, failures, mcpIndexDiagnostics);
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
        var message = BuildSanitizedIndexFileFailureMessage(stage, ex.GetType().Name, out var messageTruncated);
        return new IndexFileFailure(relativePath, stage, ex.GetType().Name, message, messageTruncated);
    }

    private static IndexFileFailure BuildScanFailure(FileIndexer.ScanError error)
    {
        var message = SanitizeAndCapMcpIndexFailureMessage(error.Message, out var messageTruncated);
        return new IndexFileFailure(
            FileIndexer.NormalizePathSeparators(error.Path),
            "scan",
            nameof(FileIndexer.ScanError),
            message,
            messageTruncated);
    }

    private static McpIndexDiagnostic BuildMcpIndexExceptionDiagnostic(
        string code,
        string category,
        string stage,
        string projectRoot,
        string filePath,
        Exception ex)
    {
        var path = SanitizeMcpIndexDiagnosticPath(projectRoot, filePath);
        var exceptionType = SanitizeMcpIndexFailureToken(ex.GetType().Name, "Exception");
        var message = SanitizeAndCapMcpIndexFailureMessage(
            DiagnosticRedactor.FormatExceptionStackLine(ex.Message),
            out var messageTruncated);
        return new McpIndexDiagnostic(code, category, path, stage, exceptionType, message, messageTruncated);
    }

    internal static JsonObject BuildMcpIndexExceptionDiagnosticForTesting(
        string code,
        string category,
        string stage,
        string projectRoot,
        string filePath,
        Exception ex)
        => BuildMcpIndexDiagnosticJson(BuildMcpIndexExceptionDiagnostic(
            code,
            category,
            stage,
            projectRoot,
            filePath,
            ex));

    private static string SanitizeMcpIndexDiagnosticPath(string projectRoot, string path)
    {
        try
        {
            var relative = FileIndexer.NormalizePathSeparators(Path.GetRelativePath(projectRoot, path));
            if (!string.IsNullOrWhiteSpace(relative)
                && relative != "."
                && !relative.StartsWith("../", StringComparison.Ordinal)
                && !Path.IsPathRooted(relative))
            {
                return McpBoundedText.ForDisplay(relative, 256).Text;
            }
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException or NotSupportedException or ArgumentException)
        {
        }

        return "<redacted>";
    }

    private static void AddMcpIndexDiagnostics(
        JsonObject structured,
        IReadOnlyList<IndexFileFailure> failures,
        IReadOnlyList<McpIndexDiagnostic> diagnostics)
    {
        var total = failures.Count + diagnostics.Count;
        if (total == 0)
            return;

        var categories = new Dictionary<string, int>(StringComparer.Ordinal);
        var items = new JsonArray();
        var emitted = 0;
        foreach (var failure in failures)
        {
            var diagnostic = new McpIndexDiagnostic(
                "recoverable_index_error",
                "recoverable_index_error",
                failure.Path,
                failure.Stage,
                failure.ExceptionType,
                failure.Message,
                failure.MessageTruncated);
            AddMcpIndexDiagnosticCategory(categories, diagnostic.Category);
            if (emitted < 50)
            {
                items.Add(BuildMcpIndexDiagnosticJson(diagnostic));
                emitted++;
            }
        }

        foreach (var diagnostic in diagnostics)
        {
            AddMcpIndexDiagnosticCategory(categories, diagnostic.Category);
            if (emitted < 50)
            {
                items.Add(BuildMcpIndexDiagnosticJson(diagnostic));
                emitted++;
            }
        }

        var categoryJson = new JsonObject();
        foreach (var entry in categories.OrderBy(entry => entry.Key, StringComparer.Ordinal))
            categoryJson[entry.Key] = entry.Value;

        structured["diagnostics"] = new JsonObject
        {
            ["total_count"] = total,
            ["sample_count"] = emitted,
            ["truncated"] = total > emitted,
            ["categories"] = categoryJson,
            ["items"] = items,
        };
    }

    private static void AddMcpIndexDiagnosticCategory(Dictionary<string, int> categories, string category)
        => categories[category] = categories.TryGetValue(category, out var count) ? count + 1 : 1;

    private static JsonObject BuildMcpIndexDiagnosticJson(McpIndexDiagnostic diagnostic)
        => new()
        {
            ["code"] = diagnostic.Code,
            ["category"] = diagnostic.Category,
            ["path"] = diagnostic.Path,
            ["stage"] = diagnostic.Stage,
            ["exception_type"] = diagnostic.ExceptionType,
            ["message"] = diagnostic.Message,
            ["message_truncated"] = diagnostic.MessageTruncated,
        };

    internal static string BuildSanitizedIndexFileFailureMessageForTesting(string stage, string exceptionType, out bool messageTruncated) =>
        BuildSanitizedIndexFileFailureMessage(stage, exceptionType, out messageTruncated);

    internal static string SanitizeMcpIndexFailureMessageForTesting(string message, out bool messageTruncated) =>
        SanitizeAndCapMcpIndexFailureMessage(message, out messageTruncated);

    private static string BuildSanitizedIndexFileFailureMessage(string stage, string exceptionType, out bool messageTruncated)
    {
        var safeStage = SanitizeMcpIndexFailureToken(stage, "unknown_stage");
        var safeExceptionType = SanitizeMcpIndexFailureToken(exceptionType, "Exception");
        return SanitizeAndCapMcpIndexFailureMessage(
            $"File indexing failed during {safeStage} ({safeExceptionType}). See cdidx server stderr for details.",
            out messageTruncated);
    }

    private static string SanitizeMcpIndexFailureToken(string? value, string fallback)
    {
        if (string.IsNullOrWhiteSpace(value))
            return fallback;

        var builder = new StringBuilder(value.Length);
        foreach (var ch in value)
        {
            if (char.IsAsciiLetterOrDigit(ch) || ch is '_' or '-' or '.')
                builder.Append(ch);
        }

        return builder.Length == 0 ? fallback : builder.ToString();
    }

    private static string SanitizeAndCapMcpIndexFailureMessage(string? message, out bool messageTruncated)
    {
        var collapsed = CollapseMcpIndexFailureWhitespace(string.IsNullOrWhiteSpace(message)
            ? "File indexing failed. See cdidx server stderr for details."
            : message);
        const string suffix = "...(truncated)";
        if (collapsed.Length <= MaxMcpIndexFailureMessageLength)
        {
            messageTruncated = false;
            return collapsed;
        }

        messageTruncated = true;
        return collapsed[..(MaxMcpIndexFailureMessageLength - suffix.Length)] + suffix;
    }

    private static string CollapseMcpIndexFailureWhitespace(string value)
    {
        var builder = new StringBuilder(value.Length);
        var lastWasSpace = false;
        foreach (var ch in value)
        {
            if (ch is '\r' or '\n' or '\t' || char.IsControl(ch))
            {
                if (!lastWasSpace)
                {
                    builder.Append(' ');
                    lastWasSpace = true;
                }
                continue;
            }

            builder.Append(ch);
            lastWasSpace = ch == ' ';
        }

        return builder.ToString().Trim();
    }

    private static string QuoteMcpIndexFailureLogValue(string value)
    {
        var message = SanitizeAndCapMcpIndexFailureMessage(value, out _);
        return JsonSerializer.Serialize(message);
    }

    private sealed record IndexFileFailure(string Path, string Stage, string ExceptionType, string Message, bool MessageTruncated);
    private sealed record McpIndexDiagnostic(string Code, string Category, string Path, string Stage, string ExceptionType, string Message, bool MessageTruncated);

    private async Task<JsonNode> ExecuteBackfillFoldAsync(JsonNode? id, JsonNode? args, JsonNode? progressToken = null)
    {
        var requestToken = _currentRequestToken.Value;
        if (!DbContext.TryValidateExistingCodeIndexDb(_dbPath, out var validationMessage, out var isNotFound, requestToken))
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
                await EmitProgressNotificationAsync(progressToken, 0, null, "Backfilling folded-name keys.").ConfigureAwait(false);
                (symbols, symbolReferences) = writer.BackfillFoldedColumns(rewriteAll);
                await EmitProgressNotificationAsync(progressToken, symbols + symbolReferences, totalSymbols + totalSymbolReferences, "Verifying folded-name keys.").ConfigureAwait(false);
                // Row rewrites are intentionally committed before the final FoldReady stamp so
                // interrupted MCP backfills can resume from the remaining rows.
                // 行更新は FoldReady stamp より前に永続化し、中断後に残り行から再開できるようにする。
                using var transaction = writer.BeginTransaction();
                verified = writer.MarkFoldReady();
                if (!verified)
                    return CreateToolErrorResponse(id, "Folded-name backfill verification failed: some rows still have NULL folded values. Re-run backfill_fold.");

                transaction.Commit();
                userVersionAfter = db.GetUserVersion();
                await EmitProgressNotificationAsync(progressToken, symbols + symbolReferences, symbols + symbolReferences, "Folded-name backfill complete.").ConfigureAwait(false);
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
            DeferFrameLog(() =>
            {
                WriteMcpLogLine(BuildToolErrorLog("backfill_fold", ex));
                Database.DbDebug.DumpToStderr(ex);
            });
            var classification = McpErrorEnvelope.ClassifyException(ex);
            return CreateToolErrorResponse(id, BuildSanitizedToolErrorMessage("backfill_fold", ex),
                category: classification.Category,
                suggestion: classification.Suggestion,
                retrySafe: classification.RetrySafe,
                extraData: new JsonObject
                {
                    ["tool"] = "backfill_fold",
                    ["exception_type"] = ex.GetType().Name,
                });
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
        var descriptionDetection = SourceCodeDetector.Detect(description);
        if (descriptionDetection.ContainsSourceCode)
            return CreateSourceCodeDetectedErrorResponse(
                id,
                "description",
                descriptionDetection,
                "Description appears to contain source code. Please describe the gap in natural language without including code.");

        if (context != null)
        {
            var contextDetection = SourceCodeDetector.Detect(context);
            if (contextDetection.ContainsSourceCode)
                return CreateSourceCodeDetectedErrorResponse(
                    id,
                    "context",
                    contextDetection,
                    "Context appears to contain source code. Please describe what you were trying to do without including code.");
        }

        if (toolInvocationContext != null)
        {
            var invocationDetection = SourceCodeDetector.Detect(toolInvocationContext);
            if (invocationDetection.ContainsSourceCode)
                return CreateSourceCodeDetectedErrorResponse(
                    id,
                    "toolInvocationContext",
                    invocationDetection,
                    "Tool invocation context appears to contain source code. Please describe the invocation without including code.");
        }

        var samplingDecision = ResolveSuggestionSamplingDecision();
        var samplingAttempt = await TrySampleSuggestionMetadataAsync(
            category,
            language,
            RedactSuggestionSamplingInput(description),
            context == null ? null : RedactSuggestionSamplingInput(context),
            toolInvocationContext == null ? null : RedactSuggestionSamplingInput(toolInvocationContext),
            samplingDecision).ConfigureAwait(false);
        var sampling = RedactSuggestionSamplingResult(samplingAttempt.Result);

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
        Func<SuggestionRecord, CancellationToken, Task<SuggestionStore.SubmitAttemptResult>>? githubCallback = null;
        var githubTokenConfigured = GitHubIssueReporter.ResolveToken() != null;
        var cancellationToken = _currentRequestToken.Value;
        if (githubTokenConfigured)
        {
            var version = _version;
            githubCallback = (r, token) => GitHubIssueReporter.TryCreateIssueDetailedAsync(r, version, token);
        }

        var result = await store.TryAddAndSubmitAsync(record, githubCallback, cancellationToken).ConfigureAwait(false);
        var storedHash = result.StoredHash ?? hash;

        if (!result.IsNew)
        {
            var dupPayload = new JsonObject
            {
                ["status"] = "duplicate",
                ["hash"] = storedHash,
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
            AddSuggestionSamplingDiagnostics(dupPayload, samplingDecision, sampling, samplingAttempt.Diagnostic);
            return CreateToolResult(id, "Duplicate suggestion (already recorded).", dupPayload);
        }

        // 7. Return success / 成功レスポンスを返す
        var payload = new JsonObject
        {
            ["status"] = "recorded",
            ["hash"] = storedHash,
            ["category"] = category,
            ["language"] = language,
            ["stored_locally"] = true,
            ["submitted_to_github"] = result.UpstreamUrl != null,
            ["github_submission_reason"] = ResolveGitHubSubmissionReason(result, githubTokenConfigured),
            ["lifecycle_status"] = JsonNamingPolicy.SnakeCaseLower.ConvertName(result.Status.ToString()),
            ["cdidx_dir"] = cdidxDir,
        };
        AddSuggestionSamplingDiagnostics(payload, samplingDecision, sampling, samplingAttempt.Diagnostic);
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

    private JsonObject CreateSourceCodeDetectedErrorResponse(
        JsonNode? id,
        string field,
        SourceCodeDetectionResult detection,
        string message)
    {
        var extraData = new JsonObject
        {
            ["source_code_rejection"] = new JsonObject
            {
                ["field"] = field,
                ["reason_code"] = detection.ReasonCode ?? "unknown",
                ["reason_code_counts"] = CreateSourceCodeReasonCounts(detection),
            },
        };
        return CreateToolErrorResponse(
            id,
            message,
            category: McpErrorEnvelope.CategoryInvalidArgument,
            suggestion: "Describe the gap in natural language without including code.",
            retrySafe: false,
            extraData: extraData);
    }

    private static JsonObject CreateSourceCodeReasonCounts(SourceCodeDetectionResult detection)
    {
        var counts = new JsonObject();
        if (detection.ReasonCounts is null)
            return counts;

        foreach (var reason in detection.ReasonCounts)
        {
            if (reason.Value > 0)
                counts[reason.Key] = reason.Value;
        }

        return counts;
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

    private sealed record SuggestionSamplingAttempt(SuggestionSamplingResult? Result, string? Diagnostic);

    private readonly record struct SuggestionSamplingDecision(
        bool ShouldRequestClient,
        string Status,
        string? Diagnostic);

    private static string RedactSuggestionSamplingInput(string value)
        => SuggestionStore.RedactSensitiveText(value, out _);

    private static SuggestionSamplingResult? RedactSuggestionSamplingResult(SuggestionSamplingResult? sampling)
    {
        if (sampling == null)
            return null;

        var title = SanitizeSampledTitle(RedactNullableSamplingValue(sampling.Title));
        var tags = sampling.Tags?
            .Select(RedactNullableSamplingValue)
            .Where(t => !string.IsNullOrWhiteSpace(t))
            .Select(SanitizeSampledTag)
            .Where(t => t != null)
            .Cast<string>()
            .Distinct(StringComparer.Ordinal)
            .Take(6)
            .ToArray();

        return title == null && (tags == null || tags.Length == 0)
            ? null
            : new SuggestionSamplingResult(title, tags is { Length: > 0 } ? tags : null);
    }

    private static string? RedactNullableSamplingValue(string? value)
        => value == null ? null : SuggestionStore.RedactSensitiveText(value, out _);

    private async Task<SuggestionSamplingAttempt> TrySampleSuggestionMetadataAsync(
        string category,
        string? language,
        string description,
        string? context,
        string? toolInvocationContext,
        SuggestionSamplingDecision samplingDecision)
    {
        if (!samplingDecision.ShouldRequestClient)
            return new SuggestionSamplingAttempt(null, null);

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
            return new SuggestionSamplingAttempt(null, null);
        if (text.Length > MaxSamplingResponseTextChars)
            return new SuggestionSamplingAttempt(
                null,
                BuildSamplingRejectionDiagnostic(
                    $"Sampling response rejected: text length {text.Length.ToString(CultureInfo.InvariantCulture)} exceeds {MaxSamplingResponseTextChars.ToString(CultureInfo.InvariantCulture)} characters."));
        try
        {
            var parsed = JsonNode.Parse(text, documentOptions: new JsonDocumentOptions { MaxDepth = MaxSamplingResponseJsonDepth });
            if (parsed is not JsonObject obj)
                return new SuggestionSamplingAttempt(null, BuildSamplingSchemaRejectionDiagnostic());

            var titleNode = obj["title"];
            var titleText = TryReadStringValue(titleNode);
            if (titleNode is not null && titleText is null)
                return new SuggestionSamplingAttempt(null, BuildSamplingSchemaRejectionDiagnostic());
            var title = SanitizeSampledTitle(RedactNullableSamplingValue(titleText));

            var tagsNode = obj["tags"];
            string[]? tags = null;
            if (tagsNode is JsonArray tagArray)
            {
                var tagList = new List<string>();
                foreach (var tagNode in tagArray)
                {
                    var tagText = TryReadStringValue(tagNode);
                    if (tagText is null)
                        return new SuggestionSamplingAttempt(null, BuildSamplingSchemaRejectionDiagnostic());
                    if (string.IsNullOrWhiteSpace(tagText))
                        continue;
                    var tag = SanitizeSampledTag(RedactNullableSamplingValue(tagText));
                    if (tag is not null && !tagList.Contains(tag, StringComparer.Ordinal))
                        tagList.Add(tag);
                    if (tagList.Count >= 6)
                        break;
                }
                tags = tagList.Count > 0 ? tagList.ToArray() : null;
            }
            else if (tagsNode is not null)
            {
                return new SuggestionSamplingAttempt(null, BuildSamplingSchemaRejectionDiagnostic());
            }

            if (title == null && (tags == null || tags.Length == 0))
                return new SuggestionSamplingAttempt(null, BuildSamplingSchemaRejectionDiagnostic());
            return new SuggestionSamplingAttempt(new SuggestionSamplingResult(title, tags is { Length: > 0 } ? tags : null), null);
        }
        catch (JsonException ex)
        {
            return new SuggestionSamplingAttempt(
                null,
                BuildSamplingRejectionDiagnostic(
                    $"Sampling response JSON rejected: {JsonFrameParser.FormatExceptionDetail(ex)}."));
        }
    }

    private static string BuildSamplingSchemaRejectionDiagnostic()
        => BuildSamplingRejectionDiagnostic(
            "Sampling response schema rejected: expected compact JSON with optional title string and tags array containing strings.");

    private static string BuildSamplingRejectionDiagnostic(string diagnostic)
        => DiagnosticRedactor.BoundDiagnosticText(diagnostic, 240);

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
        => name switch
        {
            "roots" => _clientSupportsRoots,
            "sampling" => _clientSupportsSampling,
            _ => _clientCapabilities is JsonObject obj
                && obj.TryGetPropertyValue(name, out var node)
                && node is not null,
        };

    private SuggestionSamplingDecision ResolveSuggestionSamplingDecision()
    {
        var sampling = McpEnvironment.ReadOptInSwitch(SamplingEnabledEnvironmentVariable);
        if (sampling.State == McpEnvironmentSwitchState.Unset)
        {
            return new SuggestionSamplingDecision(
                false,
                "disabled",
                $"{SamplingEnabledEnvironmentVariable} is unset; suggestion metadata sampling requires explicit opt-in with true, 1, yes, or on.");
        }

        if (sampling.IsEnabled)
        {
            return HasClientCapability("sampling")
                ? new SuggestionSamplingDecision(true, "enabled", null)
                : new SuggestionSamplingDecision(
                    false,
                    "client_capability_missing",
                    "Client did not advertise MCP sampling capability; suggestion metadata sampling skipped.");
        }

        if (sampling.IsDisabled)
        {
            return new SuggestionSamplingDecision(
                false,
                "disabled",
                $"{SamplingEnabledEnvironmentVariable} is set to an opt-out value; suggestion metadata sampling disabled.");
        }

        return new SuggestionSamplingDecision(
            false,
            "disabled",
            $"{SamplingEnabledEnvironmentVariable} contains an unrecognized value; suggestion metadata sampling disabled. Use true, 1, yes, or on to enable.");
    }

    private static void AddSuggestionSamplingDiagnostics(
        JsonObject payload,
        SuggestionSamplingDecision samplingDecision,
        SuggestionSamplingResult? sampling,
        string? samplingRejectionDiagnostic)
    {
        payload["sampling_status"] = sampling != null
            ? "sampled"
            : samplingRejectionDiagnostic != null
                ? "sampling_rejected"
                : samplingDecision.Status;
        if (samplingRejectionDiagnostic != null)
            payload["sampling_diagnostic"] = samplingRejectionDiagnostic;
        else if (samplingDecision.Diagnostic != null)
            payload["sampling_diagnostic"] = samplingDecision.Diagnostic;
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
        var createdProbe = false;
        try
        {
            FileWriteProbe.WriteEmptyFile(probePath);
            createdProbe = true;
            error = null;
            return true;
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
        {
            error = $"Cannot write to .cdidx directory {cdidxDir}; check directory ownership, permissions, and read-only mounts.";
            return false;
        }
        finally
        {
            if (createdProbe)
                TryDeleteCdidxDirectoryWritableProbe(probePath);
        }
    }

    private static void TryDeleteCdidxDirectoryWritableProbe(string probePath)
    {
        try
        {
            if (!File.Exists(probePath))
                return;

            if (DeleteCdidxDirectoryWritableProbeForTesting != null)
                DeleteCdidxDirectoryWritableProbeForTesting(probePath);
            else
                File.Delete(probePath);
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
        {
            CommandErrorWriter.WriteStderr($"Warning: failed to delete .cdidx writable probe {ConsoleUi.FormatBoundedValue(probePath)} ({CommandErrorWriter.FormatSanitizedException(ex)}).");
        }
    }

    internal static Action<string>? DeleteCdidxDirectoryWritableProbeForTesting { get; set; }

    private string ResolveSuggestionAgent()
    {
        return string.IsNullOrWhiteSpace(_caller) ? "unknown" : _caller;
    }

}

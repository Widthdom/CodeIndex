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
    internal static Action? McpIndexPostExtractionHookDiscoveryForTesting { get; set; }
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
            "check" or "excludeTests" or "includeGenerated" or "indexedOnly" or "rawQuery" or "noDedup" or "exactSubstring" or "tokenBoundary" or
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
        var tokenBoundary = args?["tokenBoundary"]?.GetValue<bool>() ?? false;
        var exactName = args?["exactName"]?.GetValue<bool>() ?? false;

        if (CountTrue(legacyExact, exactSubstring, tokenBoundary, exactName) > 1)
        {
            exact = false;
            error = "Pass only one of 'exact', 'exactSubstring', 'tokenBoundary', 'exactName'.";
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
            QueryCommandRunner.AnnotateValidateIssues(issues);
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
                ["summary"] = QueryCommandRunner.BuildValidateIssueSummary(issues),
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
                ["category"] = issue.Category,
                ["actionable"] = issue.Actionable,
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
        var requestedGroupBy = args?["groupBy"]?.GetValue<string>()?.ToLowerInvariant();
        if (!QueryCommandRunner.TryResolveHotspotsGroupBy(requestedGroupBy, lang, groupByName: false, out var groupBy, out var groupByError))
        {
            var groupByDisplay = McpBoundedText.ForDisplay(requestedGroupBy ?? string.Empty);
            var extra = new JsonObject
            {
                ["parameter"] = "groupBy",
                ["value"] = groupByDisplay.Text,
            };
            groupByDisplay.AddMetadata(extra, "value");
            var message = groupByError.StartsWith("Error: ", StringComparison.Ordinal)
                ? groupByError["Error: ".Length..]
                : groupByError;
            message = message
                .Replace("hotspots --group-by", "symbol_hotspots groupBy", StringComparison.Ordinal)
                .Replace("--lang sql", "lang=sql", StringComparison.Ordinal)
                .Replace("--group-by symbol", "groupBy=symbol", StringComparison.Ordinal)
                .Replace("--group-by file", "groupBy=file", StringComparison.Ordinal);
            return CreateToolErrorResponse(
                id,
                message,
                category: McpErrorEnvelope.CategoryInvalidArgument,
                suggestion: "Use groupBy=symbol or groupBy=file for non-SQL scopes, or set lang=sql with groupBy=statement.",
                retrySafe: false,
                extraData: extra);
        }
        if (groupBy == QueryCommandRunner.HotspotsGroupedByNameKind)
        {
            var groupByDisplay = McpBoundedText.ForDisplay(requestedGroupBy ?? groupBy);
            var extra = new JsonObject
            {
                ["parameter"] = "groupBy",
                ["value"] = groupByDisplay.Text,
            };
            groupByDisplay.AddMetadata(extra, "value");
            return CreateToolErrorResponse(
                id,
                $"Unsupported symbol_hotspots groupBy '{groupByDisplay.Text}'. Use groupBy=symbol or groupBy=file for non-SQL scopes, or set lang=sql with groupBy=statement.",
                category: McpErrorEnvelope.CategoryInvalidArgument,
                suggestion: "Use groupBy=symbol or groupBy=file for non-SQL scopes, or set lang=sql with groupBy=statement.",
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
                    reference_score = r.ReferenceScore,
                    ranking_score = r.RankingScore,
                    generic_name_penalty = r.GenericNamePenalty,
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
            QueryCommandRunner.AddHotspotsGroupingContractJsonFields(payload, groupBy, queryOptions: null, jsonOptions: _jsonOptions, countOnly: false);
            payload["query_context"] = BuildSymbolHotspotsQueryContext(
                limit,
                kind,
                lang,
                pathPatterns,
                excludePaths,
                excludeTests,
                groupBy,
                QueryCommandRunner.GetHotspotsGroupingUnit(groupBy),
                QueryCommandRunner.GetHotspotsCountKind(groupBy, countOnly: false),
                QueryCommandRunner.GetHotspotsLimitAppliesTo(groupBy, countOnly: false));
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

    private JsonObject BuildSymbolHotspotsQueryContext(
        int limit,
        string? kind,
        string? lang,
        IReadOnlyList<string>? pathPatterns,
        IReadOnlyList<string>? excludePaths,
        bool excludeTests,
        string groupBy,
        string groupingUnit,
        string countKind,
        string limitAppliesTo)
    {
        var queryContext = new JsonObject
        {
            ["limit"] = limit,
        };
        QueryCommandRunner.AddHotspotsGroupingQueryContextFields(queryContext, groupBy, groupingUnit, countKind, limitAppliesTo);
        if (kind != null)
            queryContext["kind"] = kind;
        if (lang != null)
            queryContext["lang"] = lang;
        if (pathPatterns is { Count: > 0 })
            queryContext["path"] = JsonSerializer.SerializeToNode(pathPatterns, _jsonOptions);
        if (excludePaths is { Count: > 0 })
            queryContext["exclude_path"] = JsonSerializer.SerializeToNode(excludePaths, _jsonOptions);
        if (excludeTests)
            queryContext["exclude_tests"] = true;
        return queryContext;
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
            var contractDomainCounts = QueryCommandRunner.BuildUnusedContractDomainCounts(results);
            var payload = new JsonObject
            {
                ["count"] = results.Count,
                ["graph_supported"] = graphSupported,
                ["graph_support_reason"] = graphSupportReason,
                ["returned_bucket_counts"] = JsonSerializer.SerializeToNode(bucketCounts, _jsonOptions),
                ["returned_contract_domain_counts"] = JsonSerializer.SerializeToNode(contractDomainCounts, _jsonOptions),
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
                ? $"Found {ConsoleUi.Counted(results.Count, "potentially unused symbol")} across {ConsoleUi.Counted(bucketCounts.Count, "returned bucket")} and {ConsoleUi.Counted(contractDomainCounts.Count, "contract domain")}. Private hits are ranked ahead of exported/config suspects, but not labeled high-confidence from indexed refs alone. Note: name-based matching — same-named symbols in different contexts may mask true unused symbols."
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
        var allLangs = new Dictionary<string, (List<string> Extensions, List<string> Aliases, bool Symbols, bool References, bool Graph, List<string> CapabilityGaps, List<LanguageUnsupportedGuidance> UnsupportedGuidance)>(StringComparer.Ordinal);
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
                    LanguageCapabilitySupport.BuildGaps(hasSymbols, hasReferences, hasReferences),
                    LanguageCapabilitySupport.BuildUnsupportedGuidance(lang, hasSymbols, hasReferences, hasReferences));
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

                var guidanceArray = new JsonArray();
                foreach (var guidance in info.UnsupportedGuidance)
                {
                    guidanceArray.Add(new JsonObject
                    {
                        ["capability"] = guidance.Capability,
                        ["message"] = guidance.Message,
                        ["recommended_commands"] = new JsonArray(guidance.RecommendedCommands.Select(command => JsonValue.Create(command)).ToArray()),
                    });
                }

                languagesArray.Add(new JsonObject
                {
                    ["lang"] = lang,
                    ["extensions"] = extArray,
                    ["aliases"] = new JsonArray(info.Aliases.OrderBy(alias => alias, StringComparer.Ordinal).Select(alias => JsonValue.Create(alias)).ToArray()),
                    ["symbol_extraction"] = info.Symbols,
                    ["reference_extraction"] = info.References,
                    ["graph_queries"] = info.Graph,
                    ["capability_gaps"] = new JsonArray(info.CapabilityGaps.Select(gap => JsonValue.Create(gap)).ToArray()),
                    ["unsupported_guidance"] = guidanceArray,
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


}

using System.Diagnostics;
using System.Text.Json;
using System.Text.Json.Nodes;
using CodeIndex.Cli;
using CodeIndex.Database;
using CodeIndex.Indexer;
using CodeIndex.Models;

namespace CodeIndex.Mcp;

/// <summary>
/// MCP tool execution handlers (partial class split from McpServer.cs).
/// MCPツール実行ハンドラ（McpServer.csからのpartial class分割）。
/// </summary>
public partial class McpServer
{
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
            parts.Add("Use 'impact_analysis' to compute transitive callers of a symbol. Pass maxDepth=0 when you only want symbol resolution without traversing callers. When a scoped query resolves to a single class / struct / interface but no symbol-level callers exist, it may instead return heuristic file-level dependency hints; always inspect 'impact_mode', 'heuristic', and 'file_impacts'.");

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

    /// <summary>
    /// Clamp limit to a safe range to prevent resource exhaustion.
    /// リソース枯渇を防ぐためlimitを安全な範囲にクランプ。
    /// </summary>
    private static int ClampLimit(int limit) => Math.Clamp(limit, 1, MaxLimit);

    /// <summary>
    /// Return true when the requested reference kind is NOT a call-graph kind (i.e. metadata
    /// `attribute` / `annotation` or compile-time `type_reference`) — these are valid on the
    /// `references` tool but must be rejected on `callers` / `callees`, whose data model
    /// cannot answer those queries correctly. Metadata rows are attributed to the enclosing
    /// body-range symbol rather than the annotated target (so file-level targets drop
    /// entirely and method-level metadata appears under the enclosing class); `type_reference`
    /// rows are compile-time type-position edges (declaration types, generic constraints,
    /// `is`/`as`/`instanceof`, XML-doc `cref`), not runtime calls, so they misreport type
    /// mentions as caller/callee edges.
    /// `references` では有効だが `callers` / `callees` では構造的に誤答するため弾くべき kind
    /// （metadata: `attribute` / `annotation`、型位置: `type_reference`）かを返す。metadata 行は
    /// 注釈対象ではなく body-range 上の外側シンボルに帰属し、`type_reference` は実行時呼び出し
    /// ではなく compile-time な型言及（宣言型、generic 制約、`is`/`as`/`instanceof`、XML-doc
    /// `cref` など）なので、`callers` / `callees` はいずれの kind にも正しく答えられない。
    /// </summary>
    private static bool IsNonCallGraphReferenceKind(string? kind) =>
        kind == "attribute" || kind == "annotation" || kind == "type_reference";

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
            ? array.Select(node => node?.GetValue<string>())
                .Where(value => !string.IsNullOrWhiteSpace(value))
                .Cast<string>()
                .ToList()
            : [];
    }

    private static bool TryResolveSearchExactArgument(JsonNode? args, out bool exact, out string? error)
    {
        if (args?["exactName"]?.GetValue<bool>() ?? false)
        {
            exact = false;
            error = "Search does not accept 'exactName'. Use 'exactSubstring' for search, or keep 'exact' for backward compatibility.";
            return false;
        }

        exact = (args?["exact"]?.GetValue<bool>() ?? false) || (args?["exactSubstring"]?.GetValue<bool>() ?? false);
        error = null;
        return true;
    }

    private static bool TryResolveNameExactArgument(JsonNode? args, string toolName, out bool exact, out string? error)
    {
        if (args?["exactSubstring"]?.GetValue<bool>() ?? false)
        {
            exact = false;
            error = $"Tool '{toolName}' does not accept 'exactSubstring'. Use 'exactName', or keep 'exact' for backward compatibility.";
            return false;
        }

        exact = (args?["exact"]?.GetValue<bool>() ?? false) || (args?["exactName"]?.GetValue<bool>() ?? false);
        error = null;
        return true;
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

    private static string BuildGraphSummary(string label, int count, string? lang, bool? graphSupported, string? graphSupportReason = null)
    {
        if (count > 0)
            return $"Found {count} {label}.";

        if (graphSupported == false && lang != null)
            return $"No {label} found. Call-graph queries are not indexed for '{lang}'.";

        return $"No {label} found.";
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

    private JsonNode ExecuteSearch(JsonNode? id, JsonNode? args)
    {
        var query = args?["query"]?.GetValue<string>();
        if (string.IsNullOrWhiteSpace(query))
            return CreateToolErrorResponse(id, "Missing required parameter: query");
        if (query.Length > MaxQueryLength)
            return CreateToolErrorResponse(id, $"Query too long (max {MaxQueryLength} characters)");

        var limit = ClampLimit(args?["limit"]?.GetValue<int>() ?? 20);
        var lang = QueryCommandRunner.NormalizeLangFilterValue(args?["lang"]?.GetValue<string>());
        var snippetLines = SearchSnippetFormatter.ClampSnippetLines(args?["snippetLines"]?.GetValue<int>() ?? SearchSnippetFormatter.DefaultSnippetLines);
        if (TryGetValidatedMaxLineWidth(id, args, out var maxLineWidth) is JsonNode maxLineWidthError)
            return maxLineWidthError;
        var rawQuery = args?["rawQuery"]?.GetValue<bool>() ?? false;
        var pathPatterns = ReadPathList(args, "path");
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
        if (!TryResolveSearchExactArgument(args, out var exact, out var exactError))
            return CreateToolErrorResponse(id, exactError!);
        var prefix = args?["prefix"]?.GetValue<bool>() ?? false;
        if (prefix && exact)
            return CreateToolErrorResponse(id, "'prefix' cannot be combined with 'exact' / 'exactSubstring' (exact uses instr(), not FTS5 prefix phrases).");

        return WithDbReader(id, reader =>
        {
            var results = reader.Search(query, limit, lang, rawQuery, pathPatterns, excludePaths, excludeTests, deduplicate, since, exact, prefix);
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
                    ["count"] = 0,
                    ["results"] = new JsonArray()
                };
                AddFreshnessHint(payload, reader);
                return CreateToolResult(id, "No results found.", payload);
            }

            var structured = new JsonObject
            {
                ["query"] = query,
                ["rawQuery"] = rawQuery,
                ["snippetLines"] = snippetLines,
                ["maxLineWidth"] = maxLineWidth,
                ["path"] = PathEcho(pathPatterns),
                ["excludeTests"] = excludeTests,
                ["count"] = results.Count,
                ["results"] = JsonSerializer.SerializeToNode(results.Select(result => SearchSnippetFormatter.ToCompactResult(result, query, snippetLines, exact, maxLineWidth)), _jsonOptions)
            };
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
        if (query != null && query.Length > MaxQueryLength)
            return CreateToolErrorResponse(id, $"Query too long (max {MaxQueryLength} characters)");

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
            if (n.Length > MaxQueryLength)
                return CreateToolErrorResponse(id, $"names entry too long (max {MaxQueryLength} characters)");
        }
        if (namesProvided && names.Count == 0)
            return CreateToolErrorResponse(id, "'names' is present but contains no usable entries (all were empty or whitespace).");
        var kind = args?["kind"]?.GetValue<string>()?.ToLowerInvariant();
        var lang = QueryCommandRunner.NormalizeLangFilterValue(args?["lang"]?.GetValue<string>());
        var limit = ClampLimit(args?["limit"]?.GetValue<int>() ?? 20);
        if (TryGetValidatedMaxLineWidth(id, args, out var maxLineWidth) is JsonNode maxLineWidthError)
            return maxLineWidthError;
        var pathPatterns = ReadPathList(args, "path");
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

        return WithDbReader(id, reader =>
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
                ["results"] = JsonSerializer.SerializeToNode(results, _jsonOptions)
            };
            if (hasExactPredicate)
                AddExactGraphSignal(structured, exactSignal);
            return CreateToolResult(id, $"Found {results.Count} symbol(s).", structured);
        });
    }

    private JsonNode ExecuteDefinition(JsonNode? id, JsonNode? args)
    {
        var query = args?["query"]?.GetValue<string>();
        if (string.IsNullOrWhiteSpace(query))
            return CreateToolErrorResponse(id, "Missing required parameter: query");
        if (query.Length > MaxQueryLength)
            return CreateToolErrorResponse(id, $"Query too long (max {MaxQueryLength} characters)");
        if (IsBareVerbatimQueryToken(query))
            return CreateToolErrorResponse(id, "Add a real symbol name after the command; bare verbatim prefixes like `@` are not valid queries.");

        var kind = args?["kind"]?.GetValue<string>()?.ToLowerInvariant();
        var lang = QueryCommandRunner.NormalizeLangFilterValue(args?["lang"]?.GetValue<string>());
        var limit = ClampLimit(args?["limit"]?.GetValue<int>() ?? 20);
        var includeBody = args?["includeBody"]?.GetValue<bool>() ?? false;
        var pathPatterns = ReadPathList(args, "path");
        var excludePaths = ReadStringList(args, "excludePaths");
        var excludeTests = args?["excludeTests"]?.GetValue<bool>() ?? false;
        var sinceStr = args?["since"]?.GetValue<string>();
        DateTime? since = null;
        if (sinceStr != null && QueryCommandRunner.TryParseIso8601Since(sinceStr, out var parsedDefSince))
            since = parsedDefSince;
        if (!TryResolveNameExactArgument(args, "definition", out var exact, out var exactError))
            return CreateToolErrorResponse(id, exactError!);

        return WithDbReader(id, reader =>
        {
            var results = reader.GetDefinitions(query, limit, kind, lang, includeBody, pathPatterns, excludePaths, excludeTests, since, exact);
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
                ["path"] = PathEcho(pathPatterns),
                ["excludeTests"] = excludeTests,
                ["count"] = results.Count,
                ["results"] = JsonSerializer.SerializeToNode(results, _jsonOptions)
            };
            if (exact)
                AddExactGraphSignal(payload, exactSignal);
            if (results.Count == 0)
            {
                AddExactZeroHint(payload, exactZeroHint);
                AddFreshnessHint(payload, reader);
            }
            return CreateToolResult(id,
                results.Count == 0 ? "No definitions found." : $"Found {results.Count} definition(s).",
                payload);
        });
    }

    private JsonNode ExecuteReferences(JsonNode? id, JsonNode? args)
    {
        var query = args?["query"]?.GetValue<string>();
        if (string.IsNullOrWhiteSpace(query))
            return CreateToolErrorResponse(id, "Missing required parameter: query");
        if (query.Length > MaxQueryLength)
            return CreateToolErrorResponse(id, $"Query too long (max {MaxQueryLength} characters)");
        if (IsBareVerbatimQueryToken(query))
            return CreateToolErrorResponse(id, "Add a real symbol name after the command; bare verbatim prefixes like `@` are not valid queries.");

        var kind = args?["kind"]?.GetValue<string>()?.ToLowerInvariant();
        var lang = QueryCommandRunner.NormalizeLangFilterValue(args?["lang"]?.GetValue<string>());
        var limit = ClampLimit(args?["limit"]?.GetValue<int>() ?? 20);
        if (TryGetValidatedMaxLineWidth(id, args, out var maxLineWidth) is JsonNode maxLineWidthError)
            return maxLineWidthError;
        var pathPatterns = ReadPathList(args, "path");
        var excludePaths = ReadStringList(args, "excludePaths");
        var excludeTests = args?["excludeTests"]?.GetValue<bool>() ?? false;
        if (!TryResolveNameExactArgument(args, "references", out var exact, out var exactError))
            return CreateToolErrorResponse(id, exactError!);

        return WithDbReader(id, reader =>
        {
            var results = reader.SearchReferences(query, limit, lang, kind, pathPatterns, excludePaths, excludeTests, exact, maxLineWidth);
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
                ["maxLineWidth"] = maxLineWidth,
                ["path"] = PathEcho(pathPatterns),
                ["excludeTests"] = excludeTests,
                ["graphLanguage"] = graphSupport.GraphLanguage,
                ["graphSupported"] = graphSupport.GraphSupported,
                ["graphSupportReason"] = graphSupport.GraphSupportReason,
                ["count"] = results.Count,
                ["results"] = JsonSerializer.SerializeToNode(results, _jsonOptions)
            };
            if (exact)
                AddExactGraphSignal(payload, exactSignal);
            AddSqlGraphContractSignal(payload, sqlGraphSignal);
            if (results.Count == 0)
            {
                AddExactZeroHint(payload, exactZeroHint);
                AddFreshnessHint(payload, reader);
            }
            return CreateToolResult(id,
                BuildGraphSummary("references", results.Count, graphSupport.GraphLanguage, graphSupport.GraphSupported, graphSupport.GraphSupportReason),
                payload);
        });
    }

    private JsonNode ExecuteCallers(JsonNode? id, JsonNode? args)
    {
        var query = args?["query"]?.GetValue<string>();
        if (string.IsNullOrWhiteSpace(query))
            return CreateToolErrorResponse(id, "Missing required parameter: query");
        if (query.Length > MaxQueryLength)
            return CreateToolErrorResponse(id, $"Query too long (max {MaxQueryLength} characters)");
        if (IsBareVerbatimQueryToken(query))
            return CreateToolErrorResponse(id, "Add a real symbol name after the command; bare verbatim prefixes like `@` are not valid queries.");

        var kind = args?["kind"]?.GetValue<string>()?.ToLowerInvariant();
        if (IsNonCallGraphReferenceKind(kind))
            return CreateToolErrorResponse(id, BuildNonCallGraphKindRejectionMessage("callers", kind!));
        var lang = QueryCommandRunner.NormalizeLangFilterValue(args?["lang"]?.GetValue<string>());
        var limit = ClampLimit(args?["limit"]?.GetValue<int>() ?? 20);
        var pathPatterns = ReadPathList(args, "path");
        var excludePaths = ReadStringList(args, "excludePaths");
        var excludeTests = args?["excludeTests"]?.GetValue<bool>() ?? false;
        if (!TryResolveNameExactArgument(args, "callers", out var exact, out var exactError))
            return CreateToolErrorResponse(id, exactError!);

        return WithDbReader(id, reader =>
        {
            var results = reader.GetCallers(query, limit, lang, kind, pathPatterns, excludePaths, excludeTests, exact);
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
                () => reader.GetCallers(query, Math.Min(limit, QueryCommandRunner.ExactZeroHintSampleLimit), lang, kind, pathPatterns, excludePaths, excludeTests, exact: false),
                r => r.CalleeName);
            var payload = new JsonObject
            {
                ["query"] = query,
                ["kind"] = kind,
                ["lang"] = lang,
                ["path"] = PathEcho(pathPatterns),
                ["excludeTests"] = excludeTests,
                ["graphLanguage"] = graphSupport.GraphLanguage,
                ["graphSupported"] = graphSupport.GraphSupported,
                ["graphSupportReason"] = graphSupport.GraphSupportReason,
                ["count"] = results.Count,
                ["results"] = JsonSerializer.SerializeToNode(results, _jsonOptions)
            };
            if (exact)
                AddExactGraphSignal(payload, exactSignal);
            AddSqlGraphContractSignal(payload, sqlGraphSignal);
            if (results.Count == 0)
            {
                AddExactZeroHint(payload, exactZeroHint);
                AddFreshnessHint(payload, reader);
            }
            return CreateToolResult(id,
                BuildGraphSummary("callers", results.Count, graphSupport.GraphLanguage, graphSupport.GraphSupported, graphSupport.GraphSupportReason),
                payload);
        });
    }

    private JsonNode ExecuteCallees(JsonNode? id, JsonNode? args)
    {
        var query = args?["query"]?.GetValue<string>();
        if (string.IsNullOrWhiteSpace(query))
            return CreateToolErrorResponse(id, "Missing required parameter: query");
        if (query.Length > MaxQueryLength)
            return CreateToolErrorResponse(id, $"Query too long (max {MaxQueryLength} characters)");
        if (IsBareVerbatimQueryToken(query))
            return CreateToolErrorResponse(id, "Add a real symbol name after the command; bare verbatim prefixes like `@` are not valid queries.");

        var kind = args?["kind"]?.GetValue<string>()?.ToLowerInvariant();
        if (IsNonCallGraphReferenceKind(kind))
            return CreateToolErrorResponse(id, BuildNonCallGraphKindRejectionMessage("callees", kind!));
        var lang = QueryCommandRunner.NormalizeLangFilterValue(args?["lang"]?.GetValue<string>());
        var limit = ClampLimit(args?["limit"]?.GetValue<int>() ?? 20);
        var pathPatterns = ReadPathList(args, "path");
        var excludePaths = ReadStringList(args, "excludePaths");
        var excludeTests = args?["excludeTests"]?.GetValue<bool>() ?? false;
        if (!TryResolveNameExactArgument(args, "callees", out var exact, out var exactError))
            return CreateToolErrorResponse(id, exactError!);

        return WithDbReader(id, reader =>
        {
            var results = reader.GetCallees(query, limit, lang, kind, pathPatterns, excludePaths, excludeTests, exact);
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
                () => reader.GetCallees(query, Math.Min(limit, QueryCommandRunner.ExactZeroHintSampleLimit), lang, kind, pathPatterns, excludePaths, excludeTests, exact: false),
                r => r.CallerName);
            var payload = new JsonObject
            {
                ["query"] = query,
                ["kind"] = kind,
                ["lang"] = lang,
                ["path"] = PathEcho(pathPatterns),
                ["excludeTests"] = excludeTests,
                ["graphLanguage"] = graphSupport.GraphLanguage,
                ["graphSupported"] = graphSupport.GraphSupported,
                ["graphSupportReason"] = graphSupport.GraphSupportReason,
                ["count"] = results.Count,
                ["results"] = JsonSerializer.SerializeToNode(results, _jsonOptions)
            };
            if (exact)
                AddExactGraphSignal(payload, exactSignal);
            AddSqlGraphContractSignal(payload, sqlGraphSignal);
            if (results.Count == 0)
            {
                AddExactZeroHint(payload, exactZeroHint);
                AddFreshnessHint(payload, reader);
            }
            return CreateToolResult(id,
                BuildGraphSummary("callees", results.Count, graphSupport.GraphLanguage, graphSupport.GraphSupported, graphSupport.GraphSupportReason),
                payload);
        });
    }

    private JsonNode ExecuteFiles(JsonNode? id, JsonNode? args)
    {
        var query = args?["query"]?.GetValue<string>();
        if (query != null && query.Length > MaxQueryLength)
            return CreateToolErrorResponse(id, $"Query too long (max {MaxQueryLength} characters)");
        var lang = QueryCommandRunner.NormalizeLangFilterValue(args?["lang"]?.GetValue<string>());
        var limit = ClampLimit(args?["limit"]?.GetValue<int>() ?? 20);
        var pathPatterns = ReadPathList(args, "path");
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

        return WithDbReader(id, reader =>
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
            return CreateToolResult(id, $"Found {results.Count} file(s).", structured);
        });
    }

    private JsonNode ExecuteMap(JsonNode? id, JsonNode? args)
    {
        var lang = args?["lang"]?.GetValue<string>()?.ToLowerInvariant();
        var limit = ClampLimit(args?["limit"]?.GetValue<int>() ?? 10);
        var pathPatterns = ReadPathList(args, "path");
        var excludePaths = ReadStringList(args, "excludePaths");
        var excludeTests = args?["excludeTests"]?.GetValue<bool>() ?? false;

        return WithDbReader(id, reader =>
        {
            var map = reader.GetRepoMap(limit, lang, pathPatterns, excludePaths, excludeTests);
            WorkspaceMetadataEnricher.Enrich(map, _dbPath, _dbPathExplicit);
            var structured = JsonSerializer.SerializeToNode(map, _jsonOptions)!.AsObject();
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

    private JsonNode ExecuteAnalyzeSymbol(JsonNode? id, JsonNode? args)
    {
        var query = args?["query"]?.GetValue<string>();
        if (string.IsNullOrWhiteSpace(query))
            return CreateToolErrorResponse(id, "Missing required parameter: query");
        if (query.Length > MaxQueryLength)
            return CreateToolErrorResponse(id, $"Query too long (max {MaxQueryLength} characters)");
        if (IsBareVerbatimQueryToken(query))
            return CreateToolErrorResponse(id, "Add a real symbol name after the command; bare verbatim prefixes like `@` are not valid queries.");

        var limit = ClampLimit(args?["limit"]?.GetValue<int>() ?? 10);
        var lang = args?["lang"]?.GetValue<string>()?.ToLowerInvariant();
        var includeBody = args?["includeBody"]?.GetValue<bool>() ?? false;
        if (TryGetValidatedMaxLineWidth(id, args, out var maxLineWidth) is JsonNode maxLineWidthError)
            return maxLineWidthError;
        var pathPatterns = ReadPathList(args, "path");
        var excludePaths = ReadStringList(args, "excludePaths");
        var excludeTests = args?["excludeTests"]?.GetValue<bool>() ?? false;
        if (!TryResolveNameExactArgument(args, "analyze_symbol", out var exact, out var exactError))
            return CreateToolErrorResponse(id, exactError!);

        return WithDbReader(id, reader =>
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
            var structured = JsonSerializer.SerializeToNode(analysis, _jsonOptions)!.AsObject();
            AddExactSignalAliases(structured);
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
            return $"Symbol analysis returned. Substring would return {analysis.ExactZeroHint.RelaxedCount} similarly named symbol(s).";

        return "Symbol analysis returned.";
    }

    private static void AddExactGraphSignal(JsonObject payload, ExactQuerySignal signal)
    {
        payload["exact_index_available"] = signal.ExactIndexAvailable;
        if (signal.DegradedReason != null)
            payload["degraded_reason"] = signal.DegradedReason;
        AddExactSignalAliases(payload);
    }

    private static void AddSqlGraphContractSignal(JsonObject payload, SqlGraphContractSignal signal)
    {
        if (!signal.Relevant)
            return;

        payload["sql_graph_contract_ready"] = signal.Ready;
        payload["sqlGraphContractReady"] = signal.Ready;
        if (!signal.Ready)
        {
            payload["degraded"] = true;
            if (signal.DegradedReason != null)
            {
                payload["sql_graph_contract_degraded_reason"] = signal.DegradedReason;
                payload["sqlGraphContractDegradedReason"] = signal.DegradedReason;
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
        payload["hotspotFamilyReady"] = signal.Ready;
        if (!signal.Ready)
        {
            payload["degraded"] = true;
            if (signal.DegradedReason != null)
            {
                payload["hotspot_family_degraded_reason"] = signal.DegradedReason;
                payload["hotspotFamilyDegradedReason"] = signal.DegradedReason;
            }
        }
    }

    private JsonNode ExecuteStatus(JsonNode? id)
    {
        return WithDbReader(id, reader =>
        {
            var status = reader.GetStatus();
            WorkspaceMetadataEnricher.Enrich(status, _dbPath, _dbPathExplicit);
            status.GraphSupportedLanguages = ReferenceExtractor.GetSupportedLanguages().OrderBy(l => l).ToList();
            status.Version = _version;
            var structured = JsonSerializer.SerializeToNode(status, _jsonOptions)!.AsObject();
            structured["hotspotFamilyReady"] = status.HotspotFamilyReady;
            if (status.HotspotFamilyDegradedReason != null)
                structured["hotspotFamilyDegradedReason"] = status.HotspotFamilyDegradedReason;
            structured["sqlGraphContractReady"] = status.SqlGraphContractReady;
            if (status.SqlGraphContractDegradedReason != null)
                structured["sqlGraphContractDegradedReason"] = status.SqlGraphContractDegradedReason;
            return CreateToolResult(id, "Database stats returned.", structured);
        });
    }

    private JsonNode ExecuteOutline(JsonNode? id, JsonNode? args)
    {
        var path = args?["path"]?.GetValue<string>();
        if (string.IsNullOrWhiteSpace(path))
            return CreateToolErrorResponse(id, "Missing required parameter: path");

        return WithDbReader(id, reader =>
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
            return CreateToolResult(id, $"Outline: {outline.SymbolCount} symbol(s) in {outline.TotalLines} lines.", structured);
        });
    }

    private JsonNode ExecuteExcerpt(JsonNode? id, JsonNode? args)
    {
        var path = args?["path"]?.GetValue<string>();
        if (string.IsNullOrWhiteSpace(path))
            return CreateToolErrorResponse(id, "Missing required parameter: path");

        var startLine = args?["startLine"]?.GetValue<int>();
        if (startLine == null || startLine <= 0)
            return CreateToolErrorResponse(id, "Missing or invalid required parameter: startLine");

        var endLine = args?["endLine"]?.GetValue<int>() ?? startLine.Value;
        if (endLine < startLine.Value)
            return CreateToolErrorResponse(id, "endLine must be greater than or equal to startLine");

        var beforeValue = args?["before"]?.GetValue<int>();
        if (beforeValue.HasValue && (beforeValue.Value < 0 || beforeValue.Value > MaxContextLines))
            return CreateToolErrorResponse(id, $"before must be in [0, {MaxContextLines}]");
        var before = beforeValue ?? 0;

        var afterValue = args?["after"]?.GetValue<int>();
        if (afterValue.HasValue && (afterValue.Value < 0 || afterValue.Value > MaxContextLines))
            return CreateToolErrorResponse(id, $"after must be in [0, {MaxContextLines}]");
        var after = afterValue ?? 0;

        var focusLine = args?["focusLine"]?.GetValue<int>();
        var focusColumn = args?["focusColumn"]?.GetValue<int>();
        var focusLengthValue = args?["focusLength"]?.GetValue<int>();
        if (focusLengthValue.HasValue && focusLengthValue.Value <= 0)
            return CreateToolErrorResponse(id, "focusLength must be greater than or equal to 1");
        var focusLength = focusLengthValue ?? 1;
        var explicitFocusLength = args?["focusLength"] != null;
        if (TryGetValidatedMaxLineWidth(id, args, out var maxLineWidth) is JsonNode maxLineWidthError)
            return maxLineWidthError;

        if (focusLine.HasValue && focusLine.Value <= 0)
            return CreateToolErrorResponse(id, "focusLine must be greater than or equal to 1");
        if (focusColumn.HasValue && focusColumn.Value <= 0)
            return CreateToolErrorResponse(id, "focusColumn must be greater than or equal to 1");
        if (!focusColumn.HasValue && (focusLine.HasValue || explicitFocusLength))
            return CreateToolErrorResponse(id, "focusLine and focusLength require focusColumn");

        return WithDbReader(id, reader =>
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
                AddFreshnessHint(emptyPayload, reader);
                return CreateToolResult(id, "No excerpt found.", emptyPayload);
            }

            var payload = JsonSerializer.SerializeToNode(excerpt, _jsonOptions)!.AsObject();
            payload["maxLineWidth"] = maxLineWidth;
            if (focusLine.HasValue)
                payload["focusLine"] = focusLine.Value;
            if (focusColumn.HasValue)
                payload["focusColumn"] = focusColumn.Value;
            payload["focusLength"] = focusLength;
            return CreateToolResult(id, "Excerpt returned.", payload);
        });
    }

    private JsonNode ExecuteFindInFile(JsonNode? id, JsonNode? args)
    {
        var query = args?["query"]?.GetValue<string>();
        if (string.IsNullOrWhiteSpace(query))
            return CreateToolErrorResponse(id, "Missing required parameter: query");
        if (query.Length > MaxQueryLength)
            return CreateToolErrorResponse(id, $"Query too long (max {MaxQueryLength} characters)");

        var pathPatterns = ReadPathList(args, "path");
        if (pathPatterns == null || pathPatterns.Count == 0)
            return CreateToolErrorResponse(id, "Missing required parameter: path");

        var limit = ClampLimit(args?["limit"]?.GetValue<int>() ?? 20);
        var lang = args?["lang"]?.GetValue<string>()?.ToLowerInvariant();
        var excludePaths = ReadStringList(args, "excludePaths");
        var excludeTests = args?["excludeTests"]?.GetValue<bool>() ?? false;
        var beforeValue = args?["before"]?.GetValue<int>();
        if (beforeValue.HasValue && beforeValue.Value < 0)
            return CreateToolErrorResponse(id, "before must be greater than or equal to 0");
        var before = beforeValue ?? 0;

        var afterValue = args?["after"]?.GetValue<int>();
        if (afterValue.HasValue && afterValue.Value < 0)
            return CreateToolErrorResponse(id, "after must be greater than or equal to 0");
        var after = afterValue ?? 0;
        if (TryGetValidatedMaxLineWidth(id, args, out var maxLineWidth) is JsonNode maxLineWidthError)
            return maxLineWidthError;
        var exact = args?["exact"]?.GetValue<bool>() ?? false;

        return WithDbReader(id, reader =>
        {
            var results = reader.FindInFiles(query, limit, lang, pathPatterns, excludePaths, excludeTests, before, after, exact, maxLineWidth);
            var structured = new JsonObject
            {
                ["query"] = query,
                ["path"] = PathEcho(pathPatterns),
                ["excludeTests"] = excludeTests,
                ["before"] = before,
                ["after"] = after,
                ["maxLineWidth"] = maxLineWidth,
                ["exact"] = exact,
                ["count"] = results.Count,
                ["fileCount"] = results.Select(r => r.Path).Distinct().Count(),
                ["results"] = JsonSerializer.SerializeToNode(results, _jsonOptions),
            };
            if (results.Count == 0)
            {
                AddFreshnessHint(structured, reader);
                return CreateToolResult(id, "No matches found.", structured);
            }

            var fileCount = structured["fileCount"]!.GetValue<int>();
            return CreateToolResult(id, $"Found {results.Count} in-file match(es) across {fileCount} file(s).", structured);
        });
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
        var totalStopwatch = Stopwatch.StartNew();
        int successCount = 0;
        int failureCount = 0;

        void AppendSlotError(string? toolName, JsonNode? toolArgs, Stopwatch slotStopwatch, string errorMessage, int? code = null)
        {
            slotStopwatch.Stop();
            failureCount++;
            var entry = new JsonObject
            {
                ["tool"] = toolName,
                ["args_summary"] = BuildArgsSummary(toolArgs),
                ["elapsed_ms"] = slotStopwatch.ElapsedMilliseconds,
                ["error"] = errorMessage,
            };
            if (code.HasValue)
                entry["code"] = code.Value;
            resultsArray.Add(entry);
        }

        foreach (var q in queries)
        {
            var toolName = q?["tool"]?.GetValue<string>();
            var toolArgs = q?["arguments"];
            var slotStopwatch = Stopwatch.StartNew();

            if (string.IsNullOrEmpty(toolName))
            {
                AppendSlotError(toolName, toolArgs, slotStopwatch, "Missing tool name");
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
                AppendSlotError(toolName, toolArgs, slotStopwatch, $"Tool not enabled: {toolName}", code: -32601);
                continue;
            }

            // Block write operations in batch / バッチ内では書き込み操作をブロック
            if (toolName == "index" || toolName == "backfill_fold" || toolName == "suggest_improvement")
            {
                AppendSlotError(toolName, toolArgs, slotStopwatch, $"{toolName} is not allowed in batch_query (write operation)");
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
                    AppendSlotError(toolName, toolArgs, slotStopwatch, $"Unknown tool: {toolName}");
                    continue;
                }

                // Check for tool-level errors (validation failures return isError=true)
                // ツールレベルのエラーを確認（バリデーション失敗は isError=true を返す）
                var isError = response["result"]?["isError"]?.GetValue<bool>() ?? false;
                if (isError)
                {
                    var errorText = response["result"]?["content"]?[0]?["text"]?.GetValue<string>() ?? "Unknown error";
                    AppendSlotError(toolName, toolArgs, slotStopwatch, errorText);
                    continue;
                }

                slotStopwatch.Stop();
                var structured = response["result"]?["structuredContent"];
                successCount++;
                resultsArray.Add(new JsonObject
                {
                    ["tool"] = toolName,
                    ["args_summary"] = BuildArgsSummary(toolArgs),
                    ["elapsed_ms"] = slotStopwatch.ElapsedMilliseconds,
                    ["result"] = structured?.DeepClone(),
                });
            }
            catch (Exception ex)
            {
                AppendSlotError(toolName, toolArgs, slotStopwatch, ex.Message);
            }
        }

        totalStopwatch.Stop();
        var totalElapsedMs = totalStopwatch.ElapsedMilliseconds;
        var payload = new JsonObject
        {
            ["count"] = resultsArray.Count,
            ["metadata"] = new JsonObject
            {
                ["total_elapsed_ms"] = totalElapsedMs,
                ["success_count"] = successCount,
                ["failure_count"] = failureCount,
            },
            ["results"] = resultsArray,
        };
        var summary = failureCount == 0
            ? $"Executed {resultsArray.Count} queries in {totalElapsedMs} ms (all succeeded)."
            : $"Executed {resultsArray.Count} queries in {totalElapsedMs} ms ({successCount} succeeded, {failureCount} failed).";
        return CreateToolResult(id, summary, payload);
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
        var limit = ClampLimit(args?["limit"]?.GetValue<int>() ?? 50);
        var lang = args?["lang"]?.GetValue<string>()?.ToLowerInvariant();
        var pathPatterns = ReadPathList(args, "path");
        var excludePaths = ReadStringList(args, "excludePaths");
        var excludeTests = args?["excludeTests"]?.GetValue<bool>() ?? false;
        var reverse = args?["reverse"]?.GetValue<bool>() ?? false;

        return WithDbReader(id, reader =>
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
            var payload = new JsonObject
            {
                ["count"] = results.Count,
                ["edges"] = JsonSerializer.SerializeToNode(results, _jsonOptions)
            };
            AddSqlGraphContractSignal(payload, sqlGraphSignal);
            var summary = results.Count > 0
                ? $"Found {results.Count} dependency edge(s)."
                : "No file dependencies found.";
            if (results.Count == 0)
                AddFreshnessHint(payload, reader);
            return CreateToolResult(id, summary, payload);
        });
    }

    private JsonNode ExecuteImpactAnalysis(JsonNode? id, JsonNode? args)
    {
        var query = args?["query"]?.GetValue<string>();
        if (string.IsNullOrWhiteSpace(query))
            return CreateToolErrorResponse(id, "Missing required parameter: query");
        if (IsBareVerbatimQueryToken(query))
            return CreateToolErrorResponse(id, "Add a real symbol name after the command; bare verbatim prefixes like `@` are not valid queries.");

        var maxDepthRequested = args?["maxDepth"]?.GetValue<int>() ?? 5;
        var maxDepth = Math.Clamp(maxDepthRequested, 0, MaxImpactDepth);
        var limit = ClampLimit(args?["limit"]?.GetValue<int>() ?? 50);
        var lang = args?["lang"]?.GetValue<string>()?.ToLowerInvariant();
        var pathPatterns = ReadPathList(args, "path");
        var excludePaths = ReadStringList(args, "excludePaths");
        var excludeTests = args?["excludeTests"]?.GetValue<bool>() ?? false;
        var withPaths = args?["withPaths"]?.GetValue<bool>() ?? false;

        return WithDbReader(id, reader =>
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
                ["max_depth"] = maxDepth,
                ["max_depth_requested"] = maxDepthRequested,
                ["actual_depth"] = maxActualDepth,
                ["truncated"] = analysis.Truncated,
                ["impact_mode"] = analysis.ImpactMode,
                ["heuristic"] = analysis.Heuristic,
                ["callers"] = JsonSerializer.SerializeToNode(analysis.Callers, _jsonOptions),
                ["file_impacts"] = JsonSerializer.SerializeToNode(analysis.FileImpacts, _jsonOptions),
                ["definition_count"] = analysis.DefinitionCount,
                ["definition_file_count"] = analysis.DefinitionFileCount,
                ["has_multiple_definitions"] = analysis.HasMultipleDefinitions,
                ["has_class_like_definitions"] = analysis.HasClassLikeDefinitions,
                ["has_multiple_definition_files"] = analysis.HasMultipleDefinitionFiles,
                ["definitions"] = JsonSerializer.SerializeToNode(analysis.Definitions, _jsonOptions),
                ["graph_table_available"] = analysis.GraphTableAvailable,
            };
            if (analysis.TruncatedReason != null)
                payload["truncated_reason"] = analysis.TruncatedReason;
            AddSqlGraphContractSignal(payload, sqlGraphSignal);
            string? maxDepthClampWarning = null;
            if (maxDepthRequested != maxDepth)
            {
                maxDepthClampWarning = $"maxDepth was clamped from {maxDepthRequested} to {maxDepth} (server cap is [0, {MaxImpactDepth}]).";
                payload["warnings"] = new JsonArray { maxDepthClampWarning };
            }
            if (analysis.ZeroResultReason != null)
                payload["zero_result_reason"] = analysis.ZeroResultReason;
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

            var summary = analysis.ImpactMode switch
            {
                "file_dependency_hints" => $"No symbol-level callers found for '{analysis.ResolvedName}'; found {hintCount} possible file-level dependent(s) across {hintFileCount} files. These hints are heuristic only."
                    + truncatedTail,
                _ when count > 0 => $"Found {count} transitive caller(s) across {fileCount} files (depth {maxActualDepth})."
                    + truncatedTail,
                _ => "No impact found.",
            };
            if (maxDepthClampWarning != null)
                summary += $" Warning: {maxDepthClampWarning}";

            if (count == 0)
            {
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

    private JsonNode ExecuteValidate(JsonNode? id, JsonNode? args)
    {
        var kind = args?["kind"]?.GetValue<string>()?.ToLowerInvariant();
        var pathPatterns = ReadPathList(args, "path");

        return WithDbReader(id, reader =>
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
        var limit = ClampLimit(args?["limit"]?.GetValue<int>() ?? 20);
        var kind = args?["kind"]?.GetValue<string>()?.ToLowerInvariant();
        var lang = args?["lang"]?.GetValue<string>()?.ToLowerInvariant();
        var pathPatterns = ReadPathList(args, "path");
        var excludePaths = ReadStringList(args, "excludePaths");
        var excludeTests = args?["excludeTests"]?.GetValue<bool>() ?? false;

        return WithDbReader(id, reader =>
        {
            var results = reader.GetSymbolHotspots(limit, kind, lang, pathPatterns, excludePaths, excludeTests);
            var hotspotSignal = reader.GetHotspotFamilySignal(lang);
            var baseSqlGraphSignal = reader.GetSqlGraphContractSignal(lang, pathPatterns, excludePaths, excludeTests);
            var zeroResultSqlGraphSignal = QueryCommandRunner.NarrowSqlGraphContractSignal(
                baseSqlGraphSignal,
                reader.ScopeMayIncludeSqlSymbols(kind, lang, pathPatterns, excludePaths, excludeTests));
            var sqlGraphSignal = results.Count == 0
                ? zeroResultSqlGraphSignal
                : QueryCommandRunner.NarrowSqlGraphContractSignalByLanguages(
                    baseSqlGraphSignal,
                    results.Select(result => result.Symbol.Lang),
                    lang);
            var items = results.Select(r => new
            {
                name = r.Symbol.Name,
                kind = r.Symbol.Kind,
                path = r.Symbol.Path,
                line = r.Symbol.Line,
                reference_count = r.ReferenceCount,
                visibility = r.Symbol.Visibility,
                container = r.Symbol.ContainerName,
            });
            var payload = new JsonObject
            {
                ["count"] = results.Count,
                ["hotspots"] = JsonSerializer.SerializeToNode(items, _jsonOptions)
            };
            AddHotspotFamilySignal(payload, hotspotSignal);
            AddSqlGraphContractSignal(payload, sqlGraphSignal);
            var summary = results.Count > 0
                ? $"Found {results.Count} symbol hotspot(s)."
                : "No symbol hotspots found.";
            if (!hotspotSignal.Ready)
            {
                payload["note"] = "cross-file hotspot family grouping is degraded; conservative same-file fallback may hide or undercount hotspot families until the next successful reindex.";
                summary += " Warning: cross-file hotspot family grouping is degraded, so results may be conservative until the next successful reindex.";
            }
            if (results.Count == 0)
                AddFreshnessHint(payload, reader);
            return CreateToolResult(id, summary, payload);
        });
    }

    private JsonNode ExecuteUnusedSymbols(JsonNode? id, JsonNode? args)
    {
        var limit = ClampLimit(args?["limit"]?.GetValue<int>() ?? 50);
        var kind = args?["kind"]?.GetValue<string>()?.ToLowerInvariant();
        var lang = args?["lang"]?.GetValue<string>()?.ToLowerInvariant();
        var pathPatterns = ReadPathList(args, "path");
        var excludePaths = ReadStringList(args, "excludePaths");
        var excludeTests = args?["excludeTests"]?.GetValue<bool>() ?? false;

        // Add graph-support metadata for AI trust decisions
        // AI の信頼判断のためにグラフ対応メタデータを追加
            bool? graphSupported = lang != null ? ReferenceExtractor.SupportsLanguage(lang) : null;
            var graphSupportReason = ReferenceExtractor.BuildGraphSupportReason(lang, graphSupported);

        return WithDbReader(id, reader =>
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
            var bucketCounts = results
                .GroupBy(result => result.UnusedBucket, StringComparer.Ordinal)
                .OrderBy(group => Array.IndexOf(new[] { "likely_unused_private", "maybe_unused_nonpublic", "public_or_exported_no_refs", "reflection_or_config_suspect" }, group.Key))
                .ToDictionary(group => group.Key, group => group.Count(), StringComparer.Ordinal);
            var payload = new JsonObject
            {
                ["count"] = results.Count,
                ["graph_supported"] = graphSupported,
                ["graph_support_reason"] = graphSupportReason,
                ["returned_bucket_counts"] = JsonSerializer.SerializeToNode(bucketCounts, _jsonOptions),
                ["symbols"] = JsonSerializer.SerializeToNode(results, _jsonOptions)
            };
            AddSqlGraphContractSignal(payload, sqlGraphSignal);
            var summary = results.Count > 0
                ? $"Found {results.Count} potentially unused symbol(s) across {bucketCounts.Count} returned bucket(s). Private hits are ranked ahead of exported/config suspects, but not labeled high-confidence from indexed refs alone. Note: name-based matching — same-named symbols in different contexts may mask true unused symbols."
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
                AddFreshnessHint(payload, reader);
            return CreateToolResult(id, summary, payload);
        });
    }

    private JsonNode ExecutePing(JsonNode? id)
    {
        var payload = new JsonObject
        {
            ["version"] = _version,
            ["timestamp"] = DateTime.UtcNow.ToString("O"),
            ["db_path"] = _dbPath,
            ["db_exists"] = File.Exists(_dbPath),
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

    private JsonNode ExecuteIndex(JsonNode? id, JsonNode? args)
    {
        var path = args?["path"]?.GetValue<string>();
        if (string.IsNullOrWhiteSpace(path))
            return CreateToolErrorResponse(id, "Missing required parameter: path");

        var rebuild = args?["rebuild"]?.GetValue<bool>() ?? false;
        var projectPath = Path.GetFullPath(path);

        // Prevent path traversal — only allow indexing within current working directory
        // パストラバーサル防止 — カレントディレクトリ配下のみインデックスを許可
        var cwd = Path.GetFullPath(".");
        if (!projectPath.StartsWith(cwd + Path.DirectorySeparatorChar) && projectPath != cwd)
            return CreateToolErrorResponse(id, "Path must be within the current working directory");

        if (!Directory.Exists(projectPath))
            return CreateToolErrorResponse(id, "Directory not found");

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
            var rebuildWriter = new DbWriter(db.Connection);
            rebuildWriter.ClearHotspotFamilyReady();
            rebuildWriter.ClearMetadataTargetReady();
            db.DropAll();
        }

        db.InitializeSchema();
        MarkSharedDbMigrated();

        var writer = new DbWriter(db.Connection);
        var indexer = new FileIndexer(projectPath, GitHelper.ResolveIgnoreCase(projectPath), GitHelper.TryGetRepositoryRoot(projectPath) ?? Path.GetFullPath(projectPath));
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

        // First mutation point — demote readiness just before any write.
        // 実書き込み直前で readiness をクリア。
        writer.ClearReadyFlags();
        writer.ClearHotspotFamilyReady();
        writer.ClearMetadataTargetReady();

        // Purge stale files / 古いファイルをパージ
        var purged = writer.PurgeStaleFiles(projectPath);
        if (purged > 0)
            WriteProjectRootOnce();

        // Purge references for languages no longer graph-supported / グラフ非対応になった言語の参照をパージ
        writer.PurgeUnsupportedReferences(ReferenceExtractor.GetSupportedLanguages());

        // Scan and index / スキャン・インデックス
        var files = indexer.ScanFiles();
        int processed = 0, skipped = 0, errors = 0;
        var reusedHotspotFamilyLanguages = new HashSet<string>(StringComparer.Ordinal);

        foreach (var filePath in files)
        {
            try
            {
                var (record, content, rawBytes, _) = indexer.BuildRecordWithRawBytes(filePath);
                var existingId = writer.GetUnchangedFileId(
                    record.Path,
                    record.Modified,
                    record.Checksum,
                    allowReuse: (record.Lang != "csharp" || csharpSymbolNameContractMatchesCurrent)
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

                using var txn = writer.BeginTransaction();
                var fileId = writer.UpsertFile(record);
                var chunks = ChunkSplitter.Split(fileId, content);
                writer.InsertChunks(chunks);
                var symbols = SymbolExtractor.Extract(fileId, record.Lang, content, record.Path);
                SymbolExtractor.ApplyFamilyScope(symbols, indexer.GetFamilyScopeKey(filePath, record.Lang));
                writer.InsertSymbols(symbols);
                var references = ReferenceExtractor.Extract(fileId, record.Lang, content, symbols, record.Path);
                writer.InsertReferences(references);
                // Keep MCP index parity with CLI index: persist file-level validation issues too.
                // MCPインデックスもCLIインデックスと同等に、ファイル検証issueを保存する。
                var issues = FileIndexer.ValidateContent(record.Path, rawBytes, content);
                writer.InsertIssues(fileId, issues);
                WriteProjectRootOnce();
                txn.Commit();
            }
            catch
            {
                errors++;
            }
            processed++;
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
            var currentFoldVersion = NameFold.Version.ToString(System.Globalization.CultureInfo.InvariantCulture);
            var currentFoldFingerprint = NameFold.Fingerprint();
            var foldVersionMatchesCurrent = priorFoldVersion == currentFoldVersion;
            var foldFingerprintMatchesCurrent = priorFoldFingerprint == currentFoldFingerprint;
            var canRestampExistingFoldTrust = foldVersionMatchesCurrent && foldFingerprintMatchesCurrent;
            if (backfillReady && (skipped == 0 || canRestampExistingFoldTrust))
            {
                // MarkFoldReady re-verifies inside BEGIN IMMEDIATE; a concurrent NULL-folded
                // insert during this restamp window leaves foldReadyAfter=false and degrades
                // to the legacy "missing_fold_backfill" reason instead of silent misadvertise.
                // Issue #1535.
                // BEGIN IMMEDIATE 内で再検証する。concurrent NULL 差し込みで stamp が失敗した
                // 場合は missing_fold_backfill に降格する。Issue #1535。
                foldReadyAfter = writer.MarkFoldReady();
                if (!foldReadyAfter)
                    foldReadyReason = "missing_fold_backfill";
            }
            else if (!backfillReady)
            {
                foldReadyReason = "missing_fold_backfill";
            }
            else if (!foldVersionMatchesCurrent)
            {
                foldReadyReason = "stale_fold_key_version";
            }
            else if (!foldFingerprintMatchesCurrent)
            {
                foldReadyReason = "stale_fold_key_fingerprint";
            }

            writer.WriteCdidxWriterVersion(_version);

            // Successful no-op MCP full scans should repair explicit-DB roots only after
            // readiness is stamped, preserving the failure-path safety contract.
            // MCP の no-op full-scan root backfill も readiness stamp 後に限定する。
            WriteProjectRootOnce();
            // Persist the current HEAD only after the run is fully successful (errors == 0).
            // Mirrors the CLI full-scan contract (Issue #1508) so MCP-driven re-indexes also
            // refresh `worktree_head_changed`; partial / failed runs leave the prior HEAD
            // untouched and surface staleness until the next clean refresh. Issues #1508 / #1512.
            // CLI full-scan と同じく成功時のみ HEAD を記録する。partial / 失敗は旧 HEAD を残す。
            writer.SetMeta(DbContext.IndexedHeadCommitMetaKey, currentHeadCommit);
            // #1509: also persist the always-updated HEAD/branch/timestamp triple so
            // status / consumers can detect cross-session staleness via
            // `commits_ahead_of_indexed_head`. Same best-effort contract — git unavailability
            // writes NULL stamps and stamp exceptions never fail the index itself.
            // #1509: HEAD / branch / timestamp を保存し、cross-session staleness 検出を可能にする。
            try
            {
                var headBranch = GitHelper.TryGetHeadBranch(projectPath);
                var timestamp = currentHeadCommit != null
                    ? DateTime.UtcNow.ToString("o", System.Globalization.CultureInfo.InvariantCulture)
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
        }
        var (totalFiles, totalChunks, totalSymbols, totalReferences) = writer.GetCounts();

        var structured = new JsonObject
        {
            ["path"] = projectPath,
            ["rebuild"] = rebuild,
            ["summary"] = new JsonObject
            {
                ["files"] = totalFiles,
                ["chunks"] = totalChunks,
                ["symbols"] = totalSymbols,
                ["references"] = totalReferences,
                ["scanned"] = files.Count,
                ["skipped"] = skipped,
                ["purged"] = purged,
                ["errors"] = errors
            },
            ["sql_graph_contract_ready"] = sqlGraphContractReadyAfter,
            ["sqlGraphContractReady"] = sqlGraphContractReadyAfter,
            ["csharp_symbol_name_ready"] = csharpSymbolNameReadyAfter,
            ["csharp_metadata_target_ready"] = csharpMetadataTargetReadyAfter,
            // #86 codex review: AI clients use this to tell whether --exact will use the
            // Unicode fold path or silently fall back to ASCII NOCASE. If false after a clean
            ["fold_ready"] = foldReadyAfter,
            ["fold_ready_reason"] = foldReadyReason
        };
        if (!sqlGraphContractReadyAfter)
        {
            var signalReader = new DbReader(writer.Connection);
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

    private JsonNode ExecuteBackfillFold(JsonNode? id)
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
            var writer = new DbWriter(db.Connection);
            var userVersionBefore = db.GetUserVersion();
            var currentFoldVersion = NameFold.Version.ToString(System.Globalization.CultureInfo.InvariantCulture);
            var currentFoldFingerprint = NameFold.Fingerprint();
            var storedFoldVersion = db.GetMetaString("fold_key_version");
            var storedFoldFingerprint = db.GetMetaString("fold_key_fingerprint");
            var rewriteAll = storedFoldVersion != currentFoldVersion
                || storedFoldFingerprint != currentFoldFingerprint;
            var (symbols, symbolReferences) = writer.BackfillFoldedColumns(rewriteAll);
            // MarkFoldReady wraps its own re-verification in BEGIN IMMEDIATE, so a concurrent
            // writer cannot insert NULL-folded rows between the verify and the stamp. Issue #1535.
            // MarkFoldReady は BEGIN IMMEDIATE 内で再検証するため、concurrent writer による
            // NULL 行差し込みで fold_ready が嘘になるのを防ぐ。Issue #1535。
            var verified = writer.MarkFoldReady();
            if (!verified)
                return CreateToolErrorResponse(id, "Folded-name backfill verification failed: some rows still have NULL folded values. Re-run backfill_fold.");

            var userVersionAfter = db.GetUserVersion();

            var payload = new JsonObject
            {
                ["symbols"] = symbols,
                ["symbol_references"] = symbolReferences,
                ["rewrite_all"] = rewriteAll,
                ["verified"] = verified,
                ["user_version_before"] = userVersionBefore,
                ["user_version_after"] = userVersionAfter,
                ["fold_ready"] = true,
            };

            var summary = rewriteAll
                ? "Folded-name keys refreshed and FoldReady stamped."
                : "Missing folded-name keys backfilled and FoldReady stamped.";
            return CreateToolResult(id, summary, payload);
        }
        catch (Exception ex)
        {
            return CreateToolErrorResponse(id, $"Failed to backfill folded-name columns: {ex.Message}");
        }
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

    /// <summary>
    /// Handle the suggest_improvement tool call.
    /// Records a structured suggestion to .cdidx/suggestions.json.
    /// Validates that no source code is included in the description or context.
    /// suggest_improvementツール呼び出しを処理する。
    /// 構造化された提案を .cdidx/suggestions.json に記録する。
    /// description と context にソースコードが含まれていないことを検証する。
    /// </summary>
    private JsonNode ExecuteSuggestImprovement(JsonNode? id, JsonNode? args)
    {
        // 1. Validate required parameters / 必須パラメータのバリデーション
        var category = args?["category"]?.GetValue<string>();
        if (string.IsNullOrWhiteSpace(category))
            return CreateToolErrorResponse(id, "Missing required parameter: category");

        if (!SuggestionRecord.ValidCategories.Contains(category))
            return CreateToolErrorResponse(id, $"Invalid category: '{category}'. Must be one of: {string.Join(", ", SuggestionRecord.ValidCategories)}");

        var description = args?["description"]?.GetValue<string>();
        if (string.IsNullOrWhiteSpace(description))
            return CreateToolErrorResponse(id, "Missing required parameter: description");

        if (description.Length > MaxDescriptionLength)
            return CreateToolErrorResponse(id, $"Description too long ({description.Length} chars, max {MaxDescriptionLength})");

        // 2. Validate optional parameters / 任意パラメータのバリデーション
        var language = args?["language"]?.GetValue<string>();
        var context = args?["context"]?.GetValue<string>();

        if (context != null && context.Length > MaxContextLength)
            return CreateToolErrorResponse(id, $"Context too long ({context.Length} chars, max {MaxContextLength})");

        // 3. Source code leak detection — reject if code is detected
        //    ソースコード漏洩検出 — コードが検出されたら拒否
        if (SourceCodeDetector.ContainsSourceCode(description))
            return CreateToolErrorResponse(id, "Description appears to contain source code. Please describe the gap in natural language without including code.");

        if (context != null && SourceCodeDetector.ContainsSourceCode(context))
            return CreateToolErrorResponse(id, "Context appears to contain source code. Please describe what you were trying to do without including code.");

        // 4. Compute dedup hash / 重複排除ハッシュを計算
        var hash = SuggestionStore.ComputeHash(category, language, description);

        // 5. Resolve .cdidx directory and create if needed
        //    .cdidx ディレクトリを解決し、必要に応じて作成
        var cdidxDir = Path.GetDirectoryName(_dbPath);
        if (string.IsNullOrEmpty(cdidxDir))
            cdidxDir = Path.Combine(Path.GetFullPath("."), ".cdidx");
        Directory.CreateDirectory(cdidxDir);

        // 6. Store locally and attempt GitHub submission atomically.
        //    TryAddAndSubmit runs the entire sequence under a single file lock:
        //    read → dedup check → write → GitHub submit → mark submitted.
        //    This prevents concurrent callers from both creating duplicate GitHub issues.
        //    ローカル保存と GitHub 送信をアトミックに実行する。
        //    TryAddAndSubmit は全シーケンスを1つのファイルロック内で実行:
        //    読み込み → 重複チェック → 書き込み → GitHub 送信 → 送信済みマーク。
        //    並行呼び出しで重複 GitHub Issue が作られることを防ぐ。
        // Derive DB identity for scoped suggestion storage.
        // スコープ付き提案蓄積のため DB identity を導出。
        var dbName = Path.GetFileNameWithoutExtension(_dbPath);
        var store = new SuggestionStore(cdidxDir, dbName);
        var record = new SuggestionRecord
        {
            Category = category,
            Language = language,
            Description = description,
            Context = context,
            Hash = hash,
            CreatedAt = DateTime.UtcNow,
        };

        // Build GitHub submission callback (null if no token configured).
        // GitHub 送信コールバックを構築（トークン未設定なら null）。
        Func<SuggestionRecord, string?>? githubCallback = null;
        if (GitHubIssueReporter.ResolveToken() != null)
        {
            var version = _version;
            githubCallback = r => GitHubIssueReporter.TryCreateIssueAsync(r, version).GetAwaiter().GetResult();
        }

        var result = store.TryAddAndSubmit(record, githubCallback);

        if (!result.IsNew)
        {
            var dupPayload = new JsonObject
            {
                ["status"] = "duplicate",
                ["hash"] = hash,
                ["message"] = result.AlreadySubmitted
                    ? "This suggestion has already been recorded and submitted."
                    : result.GitHubIssueUrl != null
                        ? "This suggestion was already recorded. GitHub submission retried successfully."
                        : "This suggestion has already been recorded.",
                ["submitted_to_github"] = result.AlreadySubmitted || result.GitHubIssueUrl != null,
            };
            if (result.GitHubIssueUrl != null)
                dupPayload["github_issue_url"] = result.GitHubIssueUrl;
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
            ["submitted_to_github"] = result.GitHubIssueUrl != null,
        };
        if (result.GitHubIssueUrl != null)
            payload["github_issue_url"] = result.GitHubIssueUrl;
        return CreateToolResult(id, "Suggestion recorded. Thank you for the feedback.", payload);
    }

}

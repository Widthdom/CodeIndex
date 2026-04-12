using System.Text.Json;
using System.Text.Json.Nodes;
using System.Reflection;
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
    /// Uses the actual supported-language list from ReferenceExtractor.
    /// initializeレスポンス用のサーバー指示文字列を構築。
    /// ReferenceExtractorの実際の対応言語リストを使用。
    /// </summary>
    private static string BuildInstructions()
    {
        var langs = string.Join(", ", ReferenceExtractor.GetSupportedLanguages());
        return "cdidx is a code-index server. "
            + "If queries fail because no index exists, run 'index' first to build it. "
            + "Start with 'map' for repo orientation, then use 'search' for text queries or 'definition' for symbol lookup. "
            + "Use 'analyze_symbol' to get definition, callers, callees, and references in one call instead of chaining separate tools. "
            + $"Graph tools (references, callers, callees) only work for supported languages ({langs}); "
            + "for other languages, use 'search' instead. "
            + "Use 'outline' to see the full symbol structure of a single file (functions, classes, properties, interfaces, enums with line numbers) without reading the file content. "
            + "Filter symbols by kind using the 'kind' parameter: function, class, struct, interface, enum, property, event, delegate, namespace, import. "
            + "Use 'excerpt' to read specific line ranges from indexed files. "
            + "Check 'status' to verify index freshness before trusting results. "
            + "Use 'languages' to discover all supported languages, file extensions, and which languages support call-graph queries. "
            + "Use 'search' with 'exact: true' for case-sensitive substring matching when FTS5 returns too many results. "
            + "Use 'files' with 'since' to find recently modified files without scanning all results. "
            + "Use 'batch_query' to execute multiple read-only queries in a single call (max 10), dramatically reducing round-trips. "
            + "Use 'deps' to see file-level dependency edges — which files reference symbols from which other files. "
            + "Use 'unused_symbols' to find dead code — symbols defined but never referenced (only meaningful for graph-supported languages). "
            + "Use 'symbol_hotspots' to find the most-referenced symbols — central, high-impact code that changes may affect widely. "
            + "Use 'impact_analysis' to compute transitive callers of a symbol — the ripple effect of changing it. Returns callers at each BFS depth level. "
            + "Use 'suggest_improvement' to report gaps or errors you notice (e.g. missing language support, poor ranking, crashes) — never include source code, only describe the issue in natural language.";
    }

    /// <summary>
    /// Add freshness hint fields to a zero-result payload so AI clients
    /// can self-diagnose stale or empty indexes without a separate status call.
    /// 0件レスポンスに鮮度ヒントを追加し、AIクライアントが別途statusを
    /// 呼ばなくてもインデックスの古さや空を自己診断できるようにする。
    /// </summary>
    private static void AddFreshnessHint(JsonObject payload, DbReader reader)
    {
        var (fileCount, indexedAt) = reader.GetFreshnessHint();
        payload["indexed_file_count"] = fileCount;
        if (indexedAt.HasValue)
            payload["indexed_at"] = JsonSerializer.SerializeToNode(indexedAt.Value);
    }

    /// <summary>
    /// Clamp limit to a safe range to prevent resource exhaustion.
    /// リソース枯渇を防ぐためlimitを安全な範囲にクランプ。
    /// </summary>
    private static int ClampLimit(int limit) => Math.Clamp(limit, 1, MaxLimit);

    private static List<string> ReadStringList(JsonNode? args, string propertyName)
    {
        return args?[propertyName] is JsonArray array
            ? array.Select(node => node?.GetValue<string>())
                .Where(value => !string.IsNullOrWhiteSpace(value))
                .Cast<string>()
                .ToList()
            : [];
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

    private static string BuildGraphSummary(string label, int count, string? lang, bool? graphSupported)
    {
        if (count > 0)
            return $"Found {count} {label}.";

        if (graphSupported == false && lang != null)
            return $"No {label} found. Call-graph queries are not indexed for '{lang}'.";

        return $"No {label} found.";
    }

    /// <summary>
    /// Best-effort invocation for optional readiness markers.
    /// Works with older builds (method absent) and newer split-readiness builds.
    /// 任意のreadinessマーカーをベストエフォートで呼び出す。
    /// 旧ビルド（メソッド未定義）と新split-readinessビルドの両方で動作。
    /// </summary>
    private static void TryInvokeDbWriterMarker(DbWriter writer, string methodName)
    {
        var method = typeof(DbWriter).GetMethod(methodName, BindingFlags.Instance | BindingFlags.Public);
        if (method is null || method.GetParameters().Length != 0)
            return;
        method.Invoke(writer, null);
    }

    private JsonNode ExecuteSearch(JsonNode? id, JsonNode? args)
    {
        var query = args?["query"]?.GetValue<string>();
        if (string.IsNullOrWhiteSpace(query))
            return CreateToolErrorResponse(id, "Missing required parameter: query");
        if (query.Length > MaxQueryLength)
            return CreateToolErrorResponse(id, $"Query too long (max {MaxQueryLength} characters)");

        var limit = ClampLimit(args?["limit"]?.GetValue<int>() ?? 20);
        var lang = args?["lang"]?.GetValue<string>();
        var snippetLines = SearchSnippetFormatter.ClampSnippetLines(args?["snippetLines"]?.GetValue<int>() ?? SearchSnippetFormatter.DefaultSnippetLines);
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
        var exact = args?["exact"]?.GetValue<bool>() ?? false;

        return WithDbReader(id, reader =>
        {
            var results = reader.Search(query, limit, lang, rawQuery, pathPatterns, excludePaths, excludeTests, deduplicate, since, exact);
            if (results.Count == 0)
            {
                var payload = new JsonObject
                {
                    ["query"] = query,
                    ["rawQuery"] = rawQuery,
                    ["snippetLines"] = snippetLines,
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
                ["path"] = PathEcho(pathPatterns),
                ["excludeTests"] = excludeTests,
                ["count"] = results.Count,
                ["results"] = JsonSerializer.SerializeToNode(results.Select(result => SearchSnippetFormatter.ToCompactResult(result, query, snippetLines, exact)), _jsonOptions)
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
        var kind = args?["kind"]?.GetValue<string>();
        var lang = args?["lang"]?.GetValue<string>();
        var limit = ClampLimit(args?["limit"]?.GetValue<int>() ?? 20);
        var pathPatterns = ReadPathList(args, "path");
        var excludePaths = ReadStringList(args, "excludePaths");
        var excludeTests = args?["excludeTests"]?.GetValue<bool>() ?? false;
        var sinceStr = args?["since"]?.GetValue<string>();
        DateTime? since = null;
        if (sinceStr != null && QueryCommandRunner.TryParseIso8601Since(sinceStr, out var parsedSince))
            since = parsedSince;

        return WithDbReader(id, reader =>
        {
            var results = reader.SearchSymbols(query, limit, kind, lang, pathPatterns, excludePaths, excludeTests, since);
            if (results.Count == 0)
            {
                var payload = new JsonObject
                {
                    ["query"] = query,
                    ["kind"] = kind,
                    ["lang"] = lang,
                    ["path"] = PathEcho(pathPatterns),
                    ["excludeTests"] = excludeTests,
                    ["count"] = 0,
                    ["results"] = new JsonArray()
                };
                AddFreshnessHint(payload, reader);
                return CreateToolResult(id, "No symbols found.", payload);
            }

            var structured = new JsonObject
            {
                ["query"] = query,
                ["kind"] = kind,
                ["lang"] = lang,
                ["path"] = PathEcho(pathPatterns),
                ["excludeTests"] = excludeTests,
                ["count"] = results.Count,
                ["results"] = JsonSerializer.SerializeToNode(results, _jsonOptions)
            };
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

        var kind = args?["kind"]?.GetValue<string>();
        var lang = args?["lang"]?.GetValue<string>();
        var limit = ClampLimit(args?["limit"]?.GetValue<int>() ?? 20);
        var includeBody = args?["includeBody"]?.GetValue<bool>() ?? false;
        var pathPatterns = ReadPathList(args, "path");
        var excludePaths = ReadStringList(args, "excludePaths");
        var excludeTests = args?["excludeTests"]?.GetValue<bool>() ?? false;
        var sinceStr = args?["since"]?.GetValue<string>();
        DateTime? since = null;
        if (sinceStr != null && QueryCommandRunner.TryParseIso8601Since(sinceStr, out var parsedDefSince))
            since = parsedDefSince;

        return WithDbReader(id, reader =>
        {
            var results = reader.GetDefinitions(query, limit, kind, lang, includeBody, pathPatterns, excludePaths, excludeTests, since);
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
            if (results.Count == 0)
                AddFreshnessHint(payload, reader);
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

        var kind = args?["kind"]?.GetValue<string>();
        var lang = args?["lang"]?.GetValue<string>();
        var limit = ClampLimit(args?["limit"]?.GetValue<int>() ?? 20);
        var pathPatterns = ReadPathList(args, "path");
        var excludePaths = ReadStringList(args, "excludePaths");
        var excludeTests = args?["excludeTests"]?.GetValue<bool>() ?? false;

        return WithDbReader(id, reader =>
        {
            var results = reader.SearchReferences(query, limit, lang, kind, pathPatterns, excludePaths, excludeTests);
            bool? graphSupported = lang == null ? null : ReferenceExtractor.SupportsLanguage(lang);
            var payload = new JsonObject
            {
                ["query"] = query,
                ["kind"] = kind,
                ["lang"] = lang,
                ["path"] = PathEcho(pathPatterns),
                ["excludeTests"] = excludeTests,
                ["graphLanguage"] = lang,
                ["graphSupported"] = graphSupported,
                ["graphSupportReason"] = ReferenceExtractor.BuildGraphSupportReason(lang, graphSupported),
                ["count"] = results.Count,
                ["results"] = JsonSerializer.SerializeToNode(results, _jsonOptions)
            };
            if (results.Count == 0)
                AddFreshnessHint(payload, reader);
            return CreateToolResult(id,
                BuildGraphSummary("references", results.Count, lang, graphSupported),
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

        var kind = args?["kind"]?.GetValue<string>();
        var lang = args?["lang"]?.GetValue<string>();
        var limit = ClampLimit(args?["limit"]?.GetValue<int>() ?? 20);
        var pathPatterns = ReadPathList(args, "path");
        var excludePaths = ReadStringList(args, "excludePaths");
        var excludeTests = args?["excludeTests"]?.GetValue<bool>() ?? false;

        return WithDbReader(id, reader =>
        {
            var results = reader.GetCallers(query, limit, lang, kind, pathPatterns, excludePaths, excludeTests);
            bool? graphSupported = lang == null ? null : ReferenceExtractor.SupportsLanguage(lang);
            var payload = new JsonObject
            {
                ["query"] = query,
                ["kind"] = kind,
                ["lang"] = lang,
                ["path"] = PathEcho(pathPatterns),
                ["excludeTests"] = excludeTests,
                ["graphLanguage"] = lang,
                ["graphSupported"] = graphSupported,
                ["graphSupportReason"] = ReferenceExtractor.BuildGraphSupportReason(lang, graphSupported),
                ["count"] = results.Count,
                ["results"] = JsonSerializer.SerializeToNode(results, _jsonOptions)
            };
            if (results.Count == 0)
                AddFreshnessHint(payload, reader);
            return CreateToolResult(id,
                BuildGraphSummary("callers", results.Count, lang, graphSupported),
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

        var kind = args?["kind"]?.GetValue<string>();
        var lang = args?["lang"]?.GetValue<string>();
        var limit = ClampLimit(args?["limit"]?.GetValue<int>() ?? 20);
        var pathPatterns = ReadPathList(args, "path");
        var excludePaths = ReadStringList(args, "excludePaths");
        var excludeTests = args?["excludeTests"]?.GetValue<bool>() ?? false;

        return WithDbReader(id, reader =>
        {
            var results = reader.GetCallees(query, limit, lang, kind, pathPatterns, excludePaths, excludeTests);
            bool? graphSupported = lang == null ? null : ReferenceExtractor.SupportsLanguage(lang);
            var payload = new JsonObject
            {
                ["query"] = query,
                ["kind"] = kind,
                ["lang"] = lang,
                ["path"] = PathEcho(pathPatterns),
                ["excludeTests"] = excludeTests,
                ["graphLanguage"] = lang,
                ["graphSupported"] = graphSupported,
                ["graphSupportReason"] = ReferenceExtractor.BuildGraphSupportReason(lang, graphSupported),
                ["count"] = results.Count,
                ["results"] = JsonSerializer.SerializeToNode(results, _jsonOptions)
            };
            if (results.Count == 0)
                AddFreshnessHint(payload, reader);
            return CreateToolResult(id,
                BuildGraphSummary("callees", results.Count, lang, graphSupported),
                payload);
        });
    }

    private JsonNode ExecuteFiles(JsonNode? id, JsonNode? args)
    {
        var query = args?["query"]?.GetValue<string>();
        if (query != null && query.Length > MaxQueryLength)
            return CreateToolErrorResponse(id, $"Query too long (max {MaxQueryLength} characters)");
        var lang = args?["lang"]?.GetValue<string>();
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
        var lang = args?["lang"]?.GetValue<string>();
        var limit = ClampLimit(args?["limit"]?.GetValue<int>() ?? 10);
        var pathPatterns = ReadPathList(args, "path");
        var excludePaths = ReadStringList(args, "excludePaths");
        var excludeTests = args?["excludeTests"]?.GetValue<bool>() ?? false;

        return WithDbReader(id, reader =>
        {
            var map = reader.GetRepoMap(limit, lang, pathPatterns, excludePaths, excludeTests);
            WorkspaceMetadataEnricher.Enrich(map, _dbPath);
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

        var limit = ClampLimit(args?["limit"]?.GetValue<int>() ?? 10);
        var lang = args?["lang"]?.GetValue<string>();
        var includeBody = args?["includeBody"]?.GetValue<bool>() ?? false;
        var pathPatterns = ReadPathList(args, "path");
        var excludePaths = ReadStringList(args, "excludePaths");
        var excludeTests = args?["excludeTests"]?.GetValue<bool>() ?? false;

        return WithDbReader(id, reader =>
        {
            var analysis = reader.AnalyzeSymbol(query, limit, lang, includeBody, pathPatterns, excludePaths, excludeTests);
            WorkspaceMetadataEnricher.Enrich(analysis, _dbPath);
            var structured = JsonSerializer.SerializeToNode(analysis, _jsonOptions)!.AsObject();
            structured["lang"] = lang;
            structured["path"] = PathEcho(pathPatterns);
            structured["excludeTests"] = excludeTests;
            return CreateToolResult(id, "Symbol analysis returned.", structured);
        });
    }

    private JsonNode ExecuteStatus(JsonNode? id)
    {
        return WithDbReader(id, reader =>
        {
            var status = reader.GetStatus();
            WorkspaceMetadataEnricher.Enrich(status, _dbPath);
            status.GraphSupportedLanguages = ReferenceExtractor.GetSupportedLanguages().OrderBy(l => l).ToList();
            status.Version = _version;
            var structured = JsonSerializer.SerializeToNode(status, _jsonOptions)!.AsObject();
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

        var before = Math.Max(0, args?["before"]?.GetValue<int>() ?? 0);
        var after = Math.Max(0, args?["after"]?.GetValue<int>() ?? 0);

        return WithDbReader(id, reader =>
        {
            var excerpt = reader.GetExcerpt(path, startLine.Value, endLine, before, after);
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
            return CreateToolResult(id, "Excerpt returned.", payload);
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
        foreach (var q in queries)
        {
            var toolName = q?["tool"]?.GetValue<string>();
            var toolArgs = q?["arguments"];

            if (string.IsNullOrEmpty(toolName))
            {
                resultsArray.Add(new JsonObject { ["tool"] = toolName, ["error"] = "Missing tool name" });
                continue;
            }

            // Block write operations in batch / バッチ内では書き込み操作をブロック
            if (toolName == "index" || toolName == "suggest_improvement")
            {
                resultsArray.Add(new JsonObject { ["tool"] = toolName, ["error"] = $"{toolName} is not allowed in batch_query (write operation)" });
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
                    resultsArray.Add(new JsonObject { ["tool"] = toolName, ["error"] = $"Unknown tool: {toolName}" });
                    continue;
                }

                // Check for tool-level errors (validation failures return isError=true)
                // ツールレベルのエラーを確認（バリデーション失敗は isError=true を返す）
                var isError = response["result"]?["isError"]?.GetValue<bool>() ?? false;
                if (isError)
                {
                    var errorText = response["result"]?["content"]?[0]?["text"]?.GetValue<string>() ?? "Unknown error";
                    resultsArray.Add(new JsonObject { ["tool"] = toolName, ["error"] = errorText });
                    continue;
                }

                var structured = response["result"]?["structuredContent"];
                resultsArray.Add(new JsonObject
                {
                    ["tool"] = toolName,
                    ["result"] = structured?.DeepClone()
                });
            }
            catch (Exception ex)
            {
                resultsArray.Add(new JsonObject { ["tool"] = toolName, ["error"] = ex.Message });
            }
        }

        var payload = new JsonObject
        {
            ["count"] = resultsArray.Count,
            ["results"] = resultsArray,
        };
        return CreateToolResult(id, $"Executed {resultsArray.Count} queries.", payload);
    }

    private JsonNode ExecuteDeps(JsonNode? id, JsonNode? args)
    {
        var limit = ClampLimit(args?["limit"]?.GetValue<int>() ?? 50);
        var lang = args?["lang"]?.GetValue<string>();
        var pathPatterns = ReadPathList(args, "path");
        var excludePaths = ReadStringList(args, "excludePaths");
        var excludeTests = args?["excludeTests"]?.GetValue<bool>() ?? false;
        var reverse = args?["reverse"]?.GetValue<bool>() ?? false;

        return WithDbReader(id, reader =>
        {
            var results = reader.GetFileDependencies(limit, lang, pathPatterns, excludePaths, excludeTests, reverse);
            var payload = new JsonObject
            {
                ["count"] = results.Count,
                ["edges"] = JsonSerializer.SerializeToNode(results, _jsonOptions)
            };
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

        var maxDepth = Math.Clamp(args?["maxDepth"]?.GetValue<int>() ?? 5, 1, 10);
        var limit = ClampLimit(args?["limit"]?.GetValue<int>() ?? 50);
        var lang = args?["lang"]?.GetValue<string>();
        var pathPatterns = ReadPathList(args, "path");
        var excludePaths = ReadStringList(args, "excludePaths");
        var excludeTests = args?["excludeTests"]?.GetValue<bool>() ?? false;

        return WithDbReader(id, reader =>
        {
            var (results, truncated) = reader.GetTransitiveCallers(query, maxDepth, limit, lang, pathPatterns, excludePaths, excludeTests);
            var fileCount = results.Select(r => r.Path).Distinct().Count();
            var maxActualDepth = results.Count > 0 ? results.Max(r => r.Depth) : 0;
            var payload = new JsonObject
            {
                ["query"] = query,
                ["count"] = results.Count,
                ["file_count"] = fileCount,
                ["max_depth"] = maxDepth,
                ["actual_depth"] = maxActualDepth,
                ["truncated"] = truncated,
                ["callers"] = JsonSerializer.SerializeToNode(results, _jsonOptions)
            };
            var summary = results.Count > 0
                ? $"Found {results.Count} transitive caller(s) across {fileCount} files (depth {maxActualDepth})."
                  + (truncated ? " Results truncated — increase limit for more." : "")
                : "No transitive callers found.";
            if (results.Count == 0)
            {
                AddFreshnessHint(payload, reader);
                var graphReason = ReferenceExtractor.BuildGraphSupportReason(lang, lang != null ? ReferenceExtractor.SupportsLanguage(lang) : null);
                if (graphReason != null)
                    payload["graph_support_reason"] = graphReason;
            }
            return CreateToolResult(id, summary, payload);
        });
    }

    private JsonNode ExecuteValidate(JsonNode? id, JsonNode? args)
    {
        var kind = args?["kind"]?.GetValue<string>();
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
        var kind = args?["kind"]?.GetValue<string>();
        var lang = args?["lang"]?.GetValue<string>();
        var pathPatterns = ReadPathList(args, "path");
        var excludePaths = ReadStringList(args, "excludePaths");
        var excludeTests = args?["excludeTests"]?.GetValue<bool>() ?? false;

        return WithDbReader(id, reader =>
        {
            var results = reader.GetSymbolHotspots(limit, kind, lang, pathPatterns, excludePaths, excludeTests);
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
            var summary = results.Count > 0
                ? $"Found {results.Count} symbol hotspot(s)."
                : "No symbol hotspots found.";
            if (results.Count == 0)
                AddFreshnessHint(payload, reader);
            return CreateToolResult(id, summary, payload);
        });
    }

    private JsonNode ExecuteUnusedSymbols(JsonNode? id, JsonNode? args)
    {
        var limit = ClampLimit(args?["limit"]?.GetValue<int>() ?? 50);
        var kind = args?["kind"]?.GetValue<string>();
        var lang = args?["lang"]?.GetValue<string>();
        var pathPatterns = ReadPathList(args, "path");
        var excludePaths = ReadStringList(args, "excludePaths");
        var excludeTests = args?["excludeTests"]?.GetValue<bool>() ?? false;

        // Add graph-support metadata for AI trust decisions
        // AI の信頼判断のためにグラフ対応メタデータを追加
        bool? graphSupported = lang != null ? ReferenceExtractor.SupportsLanguage(lang) : null;

        return WithDbReader(id, reader =>
        {
            var results = reader.GetUnusedSymbols(limit, kind, lang, pathPatterns, excludePaths, excludeTests);
            var payload = new JsonObject
            {
                ["count"] = results.Count,
                ["graph_supported"] = graphSupported,
                ["symbols"] = JsonSerializer.SerializeToNode(results, _jsonOptions)
            };
            var summary = results.Count > 0
                ? $"Found {results.Count} potentially unused symbol(s). Note: name-based matching — same-named symbols in different contexts may mask true unused symbols."
                : "No unused symbols found.";
            if (graphSupported == false)
                summary += $" Warning: '{lang}' does not support reference extraction. Results may be unreliable.";
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
        var allLangs = new Dictionary<string, (List<string> Extensions, bool Symbols, bool Graph)>(StringComparer.Ordinal);
        foreach (var (ext, lang) in langExtensions)
        {
            if (!allLangs.TryGetValue(lang, out var info))
            {
                info = (new List<string>(), symbolLangs.Contains(lang), graphLangs.Contains(lang));
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

        // Determine DB path — use the provided _dbPath or default
        // DBパスを決定 — 指定された_dbPathまたはデフォルトを使用
        using var db = new DbContext(_dbPath);

        if (rebuild)
            db.DropAll();

        db.InitializeSchema();

        var writer = new DbWriter(db.Connection);
        var indexer = new FileIndexer(projectPath);

        // Purge stale files / 古いファイルをパージ
        var purged = writer.PurgeStaleFiles(projectPath);

        // Purge references for languages no longer graph-supported / グラフ非対応になった言語の参照をパージ
        writer.PurgeUnsupportedReferences(ReferenceExtractor.GetSupportedLanguages());

        // Scan and index / スキャン・インデックス
        var files = indexer.ScanFiles();
        int processed = 0, skipped = 0, errors = 0;

        foreach (var filePath in files)
        {
            try
            {
                var (record, content, rawBytes, _) = indexer.BuildRecordWithRawBytes(filePath);
                var existingId = writer.GetUnchangedFileId(record.Path, record.Modified, record.Checksum);
                if (existingId != null)
                {
                    skipped++;
                    processed++;
                    continue;
                }

                using var txn = writer.BeginTransaction();
                var fileId = writer.UpsertFile(record);
                var chunks = ChunkSplitter.Split(fileId, content);
                writer.InsertChunks(chunks);
                var symbols = SymbolExtractor.Extract(fileId, record.Lang, content);
                writer.InsertSymbols(symbols);
                var references = ReferenceExtractor.Extract(fileId, record.Lang, content, symbols);
                writer.InsertReferences(references);
                // Keep MCP index parity with CLI index: persist file-level validation issues too.
                // MCPインデックスもCLIインデックスと同等に、ファイル検証issueを保存する。
                var issues = FileIndexer.ValidateContent(record.Path, rawBytes, content);
                writer.InsertIssues(fileId, issues);
                txn.Commit();
            }
            catch
            {
                errors++;
            }
            processed++;
        }

        if (errors == 0)
        {
            // Forward-compatible with split readiness flags introduced in #75 lineage.
            // #75系で導入される分離readinessフラグにも前方互換で対応する。
            TryInvokeDbWriterMarker(writer, "MarkGraphReady");
            TryInvokeDbWriterMarker(writer, "MarkIssuesReady");
        }

        writer.OptimizeFts();
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
            }
        };
        return CreateToolResult(id, "Indexing complete.", structured);
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

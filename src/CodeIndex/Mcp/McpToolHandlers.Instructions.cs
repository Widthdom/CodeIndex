using System.Globalization;
using System.Text.Json;
using System.Text.Json.Nodes;
using CodeIndex.Database;
using CodeIndex.Indexer;
using CodeIndex.Models;

namespace CodeIndex.Mcp;

public partial class McpServer
{
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

        parts.Add("For an exact known repository-relative path, expand the cdidx://file-path/{path} template from resources/templates/list and pass that URI to resources/read. 正確なリポジトリ相対 path が分かっている場合は、resources/templates/list の cdidx://file-path/{path} template を展開し、その URI を resources/read に渡してください。");
        parts.Add("For browsing, call resources/list with optional path, lang, includeGenerated, and maxBytes; continue with result.nextCursor and unchanged filters. Generated files are excluded by default. browse する場合は resources/list に任意の path、lang、includeGenerated、maxBytes を渡し、同じ filter のまま result.nextCursor で継続してください。generated file は既定で除外されます。");
        parts.Add("For resources/read, use startLine/endLine and maxBytes, then continue truncated reads with result._meta.nextCursor. resources/read では startLine/endLine と maxBytes を使い、切り詰められた場合は result._meta.nextCursor で継続してください。");

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

    private static void AddSearchStabilityMetadata(
        JsonObject payload,
        DbReader reader,
        SearchCursor? cursor,
        IReadOnlyList<SearchResult> results,
        bool moreAvailable = false)
    {
        var freshness = reader.GetFreshnessHint();
        payload["result_stable_at"] = freshness.IndexedAt.HasValue
            ? JsonSerializer.SerializeToNode(freshness.IndexedAt.Value)
            : null;
        payload["freshness_available"] = freshness.FreshnessAvailable;
        if (!freshness.FreshnessAvailable && freshness.FreshnessDegradedReason != null)
            payload["freshness_degraded_reason"] = freshness.FreshnessDegradedReason;

        if (moreAvailable && results.Count > 0)
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


}

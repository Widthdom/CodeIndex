using System.Globalization;
using System.Text.Json;
using System.Text.Json.Nodes;
using CodeIndex.Cli;
using CodeIndex.Database;
using CodeIndex.Models;

namespace CodeIndex.Mcp;

public partial class McpServer
{
    /// <summary>
    /// Build the server instructions string for the initialize response.
    /// Keeps first-contact guidance bounded and skips guidance for any tool the operator
    /// disabled through the #1561 enablement gate so scoped deployments do not advertise
    /// tools that the gate would reject.
    /// initializeレスポンス用のサーバー指示文字列を構築。
    /// 初回案内を bounded に保ち、#1561 の有効化ゲートで無効化されたツールについての案内は
    /// 除外する（scoped デプロイで無効ツールが advertise されないように）。
    /// </summary>
    private string BuildInstructions()
    {
        bool On(string name) => _toolFilter.IsEnabled(name);
        bool All(params string[] names) => names.All(On);
        var parts = new List<string>
        {
            "cdidx is a local-first code-index server. Prefer its focused MCP tools before shell grep/find/cat or whole-file reads. cdidx は local-first なコード索引サーバーです。shell の grep/find/cat やファイル全体の読み取りより、絞り込んだ MCP tool を優先してください。",
            "The default tools/list page is a bounded catalog with authoritative invocation schemas. Request format=full with exact names only when detailed descriptions, output schemas, or examples are needed, and continue pagination with nextCursor unchanged. 既定の tools/list は呼び出し用 schema を保持した bounded catalog です。詳細説明、output schema、example が必要な場合だけ正確な names と format=full を指定し、nextCursor は変更せず継続してください。",
        };

        if (All("map", "search", "symbols", "definition", "references", "excerpt"))
            parts.Add("Use prompts/list and prompts/get for extended workflows such as investigate_before_edit. 詳細な workflow は prompts/list と prompts/get（例: investigate_before_edit）から取得してください。");

        if (On("index"))
            parts.Add("If no index exists, call 'index' first. index が無い場合は最初に 'index' を呼んでください。");

        var guidedFlowTools = new List<string>();
        foreach (var name in new[] { "status", "map", "search", "definition", "references", "callers", "callees", "outline", "excerpt", "read_resource" })
            if (On(name)) guidedFlowTools.Add(name);
        if (guidedFlowTools.Count > 0)
        {
            var advertisedNames = string.Join(", ", guidedFlowTools.Select(name => $"'{name}'"));
            parts.Add($"Enabled investigation tools: {advertisedNames}. Narrow with pagination and path/language filters before reading source. 有効な調査 tool: {advertisedNames}。source を読む前に pagination と path/language filter で絞り込んでください。");
        }

        if (On("read_resource"))
            parts.Add("For a known path, expand cdidx://file-path/{path} from resources/templates/list and call 'read_resource'; resources/read remains compatible. 既知の path は resources/templates/list の template を展開して 'read_resource' を呼び、従来の resources/read も利用できます。");
        else
            parts.Add("For a known path, expand cdidx://file-path/{path} from resources/templates/list and call resources/read. 既知の path は resources/templates/list の template を展開して resources/read を呼んでください。");

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

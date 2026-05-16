namespace CodeIndex.Mcp;

/// <summary>
/// Per-deployment enablement gate for MCP tools (#1561).
/// `CDIDX_MCP_TOOLS_ALLOW` pins the visible/callable set; `CDIDX_MCP_TOOLS_DENY` removes
/// individual tools from the default-all-enabled set. Allow wins over deny when both are set,
/// because operators that set an allowlist are explicit about the surface they want exposed.
/// Tool names are compared with `OrdinalIgnoreCase`; unknown names are filtered against
/// `KnownToolNames` so they cannot resurrect a tool that does not exist. A typo'd allowlist
/// that names only unknown tools intentionally exposes nothing — the absent surface is
/// visible at the next `tools/list` call and the operator can fix the env var.
/// デプロイ単位での MCP ツール有効化ゲート (#1561)。`CDIDX_MCP_TOOLS_ALLOW` が指定された
/// ときは tools/list と tools/call の集合をそれに固定し、`CDIDX_MCP_TOOLS_DENY` は既定の
/// 全ツール集合から個別に除外する。両方指定された場合は allow を優先する。ツール名比較は
/// `OrdinalIgnoreCase`、未知の名前は `KnownToolNames` で弾く。allowlist が typo で未知名
/// のみになった場合は意図的に空集合となり、次の tools/list で空であることが見えるため、
/// オペレータは env var を修正できる。
/// </summary>
public sealed class McpToolFilter
{
    internal const string AllowEnvVarName = "CDIDX_MCP_TOOLS_ALLOW";
    internal const string DenyEnvVarName = "CDIDX_MCP_TOOLS_DENY";

    private readonly HashSet<string> _enabled;

    private McpToolFilter(HashSet<string> enabled)
    {
        _enabled = enabled;
    }

    /// <summary>
    /// The full set of MCP tool names this server can dispatch. Kept here so the filter,
    /// `HandleToolsList`, and `HandleToolsCall` cannot drift out of sync.
    /// このサーバーが dispatch できる全 MCP ツール名。filter / `HandleToolsList` /
    /// `HandleToolsCall` の三者が乖離しないようここに集約する。
    /// </summary>
    public static readonly IReadOnlyList<string> KnownToolNames = new[]
    {
        "search",
        "definition",
        "references",
        "callers",
        "callees",
        "symbols",
        "files",
        "find_in_file",
        "excerpt",
        "map",
        "analyze_symbol",
        "status",
        "outline",
        "batch_query",
        "deps",
        "impact_analysis",
        "languages",
        "validate",
        "unused_symbols",
        "symbol_hotspots",
        "ping",
        "index",
        "backfill_fold",
        "suggest_improvement",
    };

    /// <summary>
    /// All tools enabled. Used as the default when no environment override is present.
    /// 全ツール有効。環境変数による override が無い場合の既定。
    /// </summary>
    public static McpToolFilter AllowAll() =>
        new(new HashSet<string>(KnownToolNames, StringComparer.OrdinalIgnoreCase));

    /// <summary>
    /// Build a filter from `CDIDX_MCP_TOOLS_ALLOW` / `CDIDX_MCP_TOOLS_DENY`. When both are
    /// unset or only contain unknown names, returns <see cref="AllowAll"/> so default
    /// behavior is preserved.
    /// `CDIDX_MCP_TOOLS_ALLOW` / `CDIDX_MCP_TOOLS_DENY` から filter を組み立てる。両方とも
    /// 未指定、または未知の名前しか含まない場合は <see cref="AllowAll"/> を返し既定挙動を保つ。
    /// </summary>
    public static McpToolFilter FromEnvironment() =>
        Parse(
            Environment.GetEnvironmentVariable(AllowEnvVarName),
            Environment.GetEnvironmentVariable(DenyEnvVarName));

    internal static McpToolFilter Parse(string? allowValue, string? denyValue)
    {
        var allow = SplitCsv(allowValue);
        if (allow.Count > 0)
        {
            var filtered = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
            foreach (var name in KnownToolNames)
            {
                if (allow.Contains(name))
                    filtered.Add(name);
            }
            return new McpToolFilter(filtered);
        }

        var enabled = new HashSet<string>(KnownToolNames, StringComparer.OrdinalIgnoreCase);
        var deny = SplitCsv(denyValue);
        if (deny.Count > 0)
        {
            foreach (var name in deny)
                enabled.Remove(name);
        }
        return new McpToolFilter(enabled);
    }

    public bool IsEnabled(string toolName) =>
        !string.IsNullOrEmpty(toolName) && _enabled.Contains(toolName);

    /// <summary>
    /// True when <paramref name="toolName"/> matches an entry in <see cref="KnownToolNames"/>.
    /// Callers use this to distinguish "operator disabled this tool" from "client invoked a
    /// name this server never had", so disabled tools surface as `-32601 Tool not enabled`
    /// while typos still surface as `-32602 Unknown tool`.
    /// `KnownToolNames` に存在する名前かを返す。呼び出し側はこれで「オペレータが無効化した」
    /// と「サーバーに元から無い名前」を区別し、前者を `-32601 Tool not enabled`、後者を
    /// `-32602 Unknown tool` として返し分ける。
    /// </summary>
    public static bool IsKnownTool(string? toolName) =>
        !string.IsNullOrEmpty(toolName)
        && KnownToolNames.Any(known => string.Equals(known, toolName, StringComparison.OrdinalIgnoreCase));

    private static HashSet<string> SplitCsv(string? value)
    {
        var set = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
        if (string.IsNullOrWhiteSpace(value))
            return set;
        foreach (var raw in value.Split(','))
        {
            var trimmed = raw.Trim();
            if (trimmed.Length == 0)
                continue;
            set.Add(trimmed);
        }
        return set;
    }
}

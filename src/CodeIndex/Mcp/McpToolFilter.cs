using CodeIndex.Cli;

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
    internal const int MaxToolFilterCsvLength = 2048;
    internal const int MaxToolFilterCsvEntries = 128;
    internal const int MaxToolFilterUnknownNamesReported = 8;

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
    /// unset, returns <see cref="AllowAll"/> so default behavior is preserved.
    /// `CDIDX_MCP_TOOLS_ALLOW` / `CDIDX_MCP_TOOLS_DENY` から filter を組み立てる。両方とも
    /// 未指定の場合は <see cref="AllowAll"/> を返し既定挙動を保つ。
    /// </summary>
    public static McpToolFilter FromEnvironment() =>
        Parse(
            CdidxEnvironment.GetEnvironmentVariable(AllowEnvVarName),
            CdidxEnvironment.GetEnvironmentVariable(DenyEnvVarName));

    internal static McpToolFilter Parse(string? allowValue, string? denyValue)
    {
        var allow = SplitCsv(allowValue, AllowEnvVarName, out var allowSpecified, out var allowInvalid);
        if (allowSpecified)
        {
            if (allowInvalid)
                return new McpToolFilter(new HashSet<string>(StringComparer.OrdinalIgnoreCase));

            WarnUnknownNames(AllowEnvVarName, allow);
            var filtered = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
            foreach (var name in KnownToolNames)
            {
                if (allow.Contains(name))
                    filtered.Add(name);
            }
            if (filtered.Count == 0)
                McpEnvironment.WriteWarning(AllowEnvVarName, "did not contain any known MCP tool names; failing closed with no tools enabled.");
            return new McpToolFilter(filtered);
        }

        var enabled = new HashSet<string>(KnownToolNames, StringComparer.OrdinalIgnoreCase);
        var deny = SplitCsv(denyValue, DenyEnvVarName, out var denySpecified, out var denyInvalid);
        if (denySpecified && !denyInvalid)
        {
            WarnUnknownNames(DenyEnvVarName, deny);
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

    private static HashSet<string> SplitCsv(string? value, string source, out bool specified, out bool invalid)
    {
        var set = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
        specified = value != null;
        invalid = false;

        if (value == null)
            return set;
        if (string.IsNullOrWhiteSpace(value))
        {
            McpEnvironment.WriteWarning(source, "is empty; no MCP tool names were provided.");
            return set;
        }
        if (!ValidateCsvBounds(source, value))
        {
            invalid = true;
            return set;
        }

        var emptyEntries = 0;
        foreach (var raw in value.Split(','))
        {
            var trimmed = raw.Trim();
            if (trimmed.Length == 0)
            {
                emptyEntries++;
                continue;
            }
            set.Add(trimmed);
        }
        if (emptyEntries > 0)
            McpEnvironment.WriteWarning(source, $"ignored {emptyEntries} empty comma-separated entr{(emptyEntries == 1 ? "y" : "ies")}.");
        return set;
    }

    private static void WarnUnknownNames(string source, HashSet<string> names)
    {
        var unknown = names
            .Where(name => !IsKnownTool(name))
            .OrderBy(name => name, StringComparer.OrdinalIgnoreCase)
            .ToArray();
        if (unknown.Length == 0)
            return;

        var displayed = unknown
            .Take(MaxToolFilterUnknownNamesReported)
            .Select(McpEnvironment.FormatDiagnosticValue)
            .ToArray();
        var suffix = unknown.Length > displayed.Length
            ? $", ... ({unknown.Length - displayed.Length} more)"
            : "";
        McpEnvironment.WriteWarning(source, $"ignored {unknown.Length} unknown MCP tool name{(unknown.Length == 1 ? "" : "s")}: {string.Join(", ", displayed)}{suffix}.");
    }

    private static bool ValidateCsvBounds(string source, string value)
    {
        if (value.Length > MaxToolFilterCsvLength)
        {
            McpEnvironment.WriteWarning(source, $"is too long ({value.Length} characters; max {MaxToolFilterCsvLength}) and was rejected.");
            return false;
        }

        var entries = CountCsvEntries(value);
        if (entries > MaxToolFilterCsvEntries)
        {
            McpEnvironment.WriteWarning(source, $"accepts at most {MaxToolFilterCsvEntries} comma-separated entries and was rejected.");
            return false;
        }

        return true;
    }

    private static int CountCsvEntries(string value)
    {
        if (value.Length == 0)
            return 0;

        var count = 1;
        foreach (var ch in value)
        {
            if (ch == ',')
                count++;
        }

        return count;
    }
}

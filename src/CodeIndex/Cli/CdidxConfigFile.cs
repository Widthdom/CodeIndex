using System.Text.Json;
using CodeIndex.Indexer;

namespace CodeIndex.Cli;

/// <summary>
/// Project-local configuration file (`.cdidxrc.json`) loader (#1571). Walks upward from a
/// starting directory looking for the first `.cdidxrc.json`, validates its schema, and
/// materializes recognized keys as process environment variables so the existing env-var
/// consumers (debug, metrics, MCP tool/rate-limit gates, persistent log) pick them up
/// without further changes. A real environment variable always wins over a config-file
/// value, which yields the documented precedence: CLI &gt; env &gt; config file &gt; defaults.
/// Secrets (`CDIDX_GITHUB_TOKEN`, `CDIDX_MCP_AUTH_TOKEN`, `CDIDX_MCP_HTTP_TOKEN`) are
/// intentionally NOT loadable from the config file to keep tokens out of version control.
/// プロジェクトローカル設定ファイル `.cdidxrc.json` のローダー (#1571)。指定ディレクトリから
/// 上方向に走査して最初に見つかった `.cdidxrc.json` をスキーマ検証し、認識済みキーを
/// プロセス環境変数として展開する。既存の env-var 消費側（debug / metrics / MCP ツール ＆
/// レート制限ゲート / 永続ログ）は追加変更なしに継承する。実際の環境変数は常に config
/// ファイル値より優先し、結果として「CLI &gt; env &gt; config file &gt; 既定」の優先順位を満たす。
/// 秘匿値 (`CDIDX_GITHUB_TOKEN`, `CDIDX_MCP_AUTH_TOKEN`, `CDIDX_MCP_HTTP_TOKEN`) は
/// バージョン管理に漏れないよう、意図的に config ファイルからは読まない。
/// </summary>
internal static class CdidxConfigFile
{
    internal const string FileName = ".cdidxrc.json";
    internal const string DisableEnvVar = "CDIDX_DISABLE_CONFIG_FILE";

    private static readonly IReadOnlyList<string> KnownTopLevelKeys = new[]
    {
        "$schema",
        "debug",
        "metrics_path",
        "disable_persistent_log",
        "global_tool_log_dir",
        "stale_after",
        "mcp",
    };

    private static readonly IReadOnlyList<string> KnownMcpKeys = new[] { "tools", "rate_limit" };
    private static readonly IReadOnlyList<string> KnownMcpToolsKeys = new[] { "allow", "deny" };
    private static readonly IReadOnlyList<string> KnownMcpRateLimitKeys = new[] { "rps", "burst" };

    internal sealed record LoadResult(string? Path, string? Error)
    {
        internal bool Loaded => Path is not null && Error is null;
        internal bool Failed => Error is not null;
    }

    /// <summary>
    /// Walk upward from <paramref name="startingDirectory"/> looking for `.cdidxrc.json`. When
    /// found, parse it and materialize recognized keys into the process environment (only when
    /// the matching env var is currently unset). Returns a result describing what happened so
    /// callers can surface validation errors. No-op when `CDIDX_DISABLE_CONFIG_FILE=1` is set.
    /// </summary>
    internal static LoadResult LoadAndApply(string startingDirectory)
        => LoadAndApply(startingDirectory, Environment.GetEnvironmentVariable, Environment.SetEnvironmentVariable);

    internal static LoadResult LoadAndApply(
        string startingDirectory,
        Func<string, string?> envReader,
        Action<string, string?> envWriter)
    {
        if (string.Equals(envReader(DisableEnvVar), "1", StringComparison.Ordinal))
            return new LoadResult(Path: null, Error: null);

        var path = FindConfigFile(startingDirectory);
        if (path is null)
            return new LoadResult(Path: null, Error: null);

        string text;
        try
        {
            text = File.ReadAllText(path);
        }
        catch (Exception ex)
        {
            return new LoadResult(Path: path, Error: $"[cdidx] Failed to read {FileName} at {path}: {ex.Message}");
        }

        JsonDocument document;
        try
        {
            document = JsonDocument.Parse(text, new JsonDocumentOptions
            {
                CommentHandling = JsonCommentHandling.Skip,
                AllowTrailingCommas = true,
            });
        }
        catch (JsonException ex)
        {
            return new LoadResult(Path: path, Error: $"[cdidx] Invalid JSON in {path}: {ex.Message}");
        }

        using (document)
        {
            var root = document.RootElement;
            if (root.ValueKind != JsonValueKind.Object)
                return new LoadResult(Path: path, Error: $"[cdidx] {path}: top-level value must be a JSON object.");

            if (TryFindUnknownKey(root, KnownTopLevelKeys, out var unknownTopKey))
                return new LoadResult(Path: path, Error: $"[cdidx] {path}: unknown key `{unknownTopKey}`. Supported keys: {string.Join(", ", KnownTopLevelKeys.Where(k => k != "$schema"))}.");

            var pending = new List<(string EnvName, string Value)>();

            if (root.TryGetProperty("debug", out var debug))
            {
                if (!TryReadString(debug, "debug", path, out var value, out var err))
                    return new LoadResult(Path: path, Error: err);
                pending.Add(("CDIDX_DEBUG", value!));
            }

            if (root.TryGetProperty("metrics_path", out var metrics))
            {
                if (!TryReadString(metrics, "metrics_path", path, out var value, out var err))
                    return new LoadResult(Path: path, Error: err);
                pending.Add(("CDIDX_METRICS", value!));
            }

            if (root.TryGetProperty("disable_persistent_log", out var disableLog))
            {
                if (disableLog.ValueKind != JsonValueKind.True && disableLog.ValueKind != JsonValueKind.False)
                    return new LoadResult(Path: path, Error: $"[cdidx] {path}: `disable_persistent_log` must be a boolean.");
                if (disableLog.GetBoolean())
                    pending.Add(("CDIDX_DISABLE_PERSISTENT_LOG", "1"));
            }

            if (root.TryGetProperty("global_tool_log_dir", out var logDir))
            {
                if (!TryReadString(logDir, "global_tool_log_dir", path, out var value, out var err))
                    return new LoadResult(Path: path, Error: err);
                pending.Add(("CDIDX_GLOBAL_TOOL_LOG_DIR", value!));
            }

            if (root.TryGetProperty("stale_after", out var staleAfter))
            {
                if (!TryReadString(staleAfter, "stale_after", path, out var value, out var err))
                    return new LoadResult(Path: path, Error: err);
                pending.Add((QueryCommandRunner.StaleAfterEnvironmentVariable, value!));
            }

            if (root.TryGetProperty("mcp", out var mcp))
            {
                if (mcp.ValueKind != JsonValueKind.Object)
                    return new LoadResult(Path: path, Error: $"[cdidx] {path}: `mcp` must be a JSON object.");
                if (TryFindUnknownKey(mcp, KnownMcpKeys, out var unknownMcpKey))
                    return new LoadResult(Path: path, Error: $"[cdidx] {path}: unknown key `mcp.{unknownMcpKey}`. Supported keys: {string.Join(", ", KnownMcpKeys)}.");

                if (mcp.TryGetProperty("tools", out var tools))
                {
                    if (tools.ValueKind != JsonValueKind.Object)
                        return new LoadResult(Path: path, Error: $"[cdidx] {path}: `mcp.tools` must be a JSON object.");
                    if (TryFindUnknownKey(tools, KnownMcpToolsKeys, out var unknownToolsKey))
                        return new LoadResult(Path: path, Error: $"[cdidx] {path}: unknown key `mcp.tools.{unknownToolsKey}`. Supported keys: {string.Join(", ", KnownMcpToolsKeys)}.");

                    if (tools.TryGetProperty("allow", out var allow))
                    {
                        if (!TryReadStringArray(allow, "mcp.tools.allow", path, out var value, out var err))
                            return new LoadResult(Path: path, Error: err);
                        if (value!.Length > 0)
                            pending.Add(("CDIDX_MCP_TOOLS_ALLOW", string.Join(",", value)));
                    }

                    if (tools.TryGetProperty("deny", out var deny))
                    {
                        if (!TryReadStringArray(deny, "mcp.tools.deny", path, out var value, out var err))
                            return new LoadResult(Path: path, Error: err);
                        if (value!.Length > 0)
                            pending.Add(("CDIDX_MCP_TOOLS_DENY", string.Join(",", value)));
                    }
                }

                if (mcp.TryGetProperty("rate_limit", out var rateLimit))
                {
                    if (rateLimit.ValueKind != JsonValueKind.Object)
                        return new LoadResult(Path: path, Error: $"[cdidx] {path}: `mcp.rate_limit` must be a JSON object.");
                    if (TryFindUnknownKey(rateLimit, KnownMcpRateLimitKeys, out var unknownRlKey))
                        return new LoadResult(Path: path, Error: $"[cdidx] {path}: unknown key `mcp.rate_limit.{unknownRlKey}`. Supported keys: {string.Join(", ", KnownMcpRateLimitKeys)}.");

                    if (rateLimit.TryGetProperty("rps", out var rps))
                    {
                        if (!TryReadNumberAsString(rps, "mcp.rate_limit.rps", path, out var value, out var err))
                            return new LoadResult(Path: path, Error: err);
                        pending.Add(("CDIDX_MCP_RATE_LIMIT_RPS", value!));
                    }

                    if (rateLimit.TryGetProperty("burst", out var burst))
                    {
                        if (!TryReadNumberAsString(burst, "mcp.rate_limit.burst", path, out var value, out var err))
                            return new LoadResult(Path: path, Error: err);
                        pending.Add(("CDIDX_MCP_RATE_LIMIT_BURST", value!));
                    }
                }
            }

            // Apply only when the matching env var is not present (null), preserving the
            // documented precedence (real env wins over config-file value). An explicit
            // empty string still counts as "set" because several existing consumers
            // (e.g. RateLimiterOptions.FromEnvironment) treat empty as "feature off",
            // so a user clearing a checked-in value must be able to override with `export FOO=`.
            foreach (var (name, value) in pending)
            {
                if (envReader(name) is null)
                    envWriter(name, value);
            }

            return new LoadResult(Path: path, Error: null);
        }
    }

    private static string? FindConfigFile(string startingDirectory)
    {
        if (string.IsNullOrWhiteSpace(startingDirectory))
            return null;

        DirectoryInfo? current;
        try
        {
            current = new DirectoryInfo(Path.GetFullPath(startingDirectory));
        }
        catch
        {
            return null;
        }

        while (current is not null)
        {
            var candidate = Path.Combine(current.FullName, FileName);
            if (File.Exists(LongPath.EnsureWindowsPrefix(candidate)))
                return candidate;
            current = current.Parent;
        }
        return null;
    }

    private static bool TryFindUnknownKey(JsonElement obj, IReadOnlyList<string> knownKeys, out string? unknown)
    {
        foreach (var property in obj.EnumerateObject())
        {
            var matched = false;
            for (var i = 0; i < knownKeys.Count; i++)
            {
                if (string.Equals(knownKeys[i], property.Name, StringComparison.Ordinal))
                {
                    matched = true;
                    break;
                }
            }
            if (!matched)
            {
                unknown = property.Name;
                return true;
            }
        }
        unknown = null;
        return false;
    }

    private static bool TryReadString(JsonElement element, string key, string path, out string? value, out string? error)
    {
        value = null;
        error = null;
        if (element.ValueKind != JsonValueKind.String)
        {
            error = $"[cdidx] {path}: `{key}` must be a string.";
            return false;
        }
        var raw = element.GetString();
        if (string.IsNullOrWhiteSpace(raw))
        {
            error = $"[cdidx] {path}: `{key}` must be a non-empty string.";
            return false;
        }
        value = raw;
        return true;
    }

    private static bool TryReadStringArray(JsonElement element, string key, string path, out string[]? value, out string? error)
    {
        value = null;
        error = null;
        if (element.ValueKind != JsonValueKind.Array)
        {
            error = $"[cdidx] {path}: `{key}` must be an array of strings.";
            return false;
        }
        var collected = new List<string>(element.GetArrayLength());
        foreach (var item in element.EnumerateArray())
        {
            if (item.ValueKind != JsonValueKind.String)
            {
                error = $"[cdidx] {path}: `{key}` must contain only strings.";
                return false;
            }
            var raw = item.GetString();
            if (string.IsNullOrWhiteSpace(raw))
                continue;
            collected.Add(raw);
        }
        value = collected.ToArray();
        return true;
    }

    private static bool TryReadNumberAsString(JsonElement element, string key, string path, out string? value, out string? error)
    {
        value = null;
        error = null;
        if (element.ValueKind != JsonValueKind.Number)
        {
            error = $"[cdidx] {path}: `{key}` must be a number.";
            return false;
        }
        value = element.GetRawText();
        return true;
    }
}

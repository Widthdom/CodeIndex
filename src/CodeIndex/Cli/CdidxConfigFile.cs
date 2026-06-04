using System.Globalization;
using System.Text.Json;
using CodeIndex.Indexer;

namespace CodeIndex.Cli;

/// <summary>
/// Project-local configuration file (`.cdidx/config.json` / `.cdidxrc.json`) loader (#1571).
/// Walks upward from a starting directory looking for the first supported config file,
/// validates its schema, and
/// materializes recognized keys as process environment variables so the existing env-var
/// consumers (debug, metrics, MCP tool/rate-limit gates, persistent log) pick them up
/// without further changes. A real environment variable always wins over a config-file
/// value, which yields the documented precedence: CLI &gt; env &gt; config file &gt; defaults.
/// Secrets (`CDIDX_GITHUB_TOKEN`, `CDIDX_MCP_AUTH_TOKEN`, `CDIDX_MCP_HTTP_TOKEN`) are
/// intentionally NOT loadable from the config file to keep tokens out of version control.
/// プロジェクトローカル設定ファイル `.cdidx/config.json` / `.cdidxrc.json` のローダー (#1571)。
/// 指定ディレクトリから上方向に走査して最初に見つかった対応 config file をスキーマ検証し、認識済みキーを
/// プロセス環境変数として展開する。既存の env-var 消費側（debug / metrics / MCP ツール ＆
/// レート制限ゲート / 永続ログ）は追加変更なしに継承する。実際の環境変数は常に config
/// ファイル値より優先し、結果として「CLI &gt; env &gt; config file &gt; 既定」の優先順位を満たす。
/// 秘匿値 (`CDIDX_GITHUB_TOKEN`, `CDIDX_MCP_AUTH_TOKEN`, `CDIDX_MCP_HTTP_TOKEN`) は
/// バージョン管理に漏れないよう、意図的に config ファイルからは読まない。
/// </summary>
internal static class CdidxConfigFile
{
    internal const string FileName = ".cdidxrc.json";
    internal static readonly string ProjectConfigRelativePath = Path.Combine(".cdidx", "config.json");
    internal const string DisableEnvVar = "CDIDX_DISABLE_CONFIG_FILE";
    internal const string ConfigSourceEnvironmentVariablePrefix = "CDIDX_CONFIG_SOURCE__";
    internal const int MaxConfigFileBytes = 64 * 1024;
    internal const int MaxConfigJsonDepth = 32;
    internal const int MaxConfigStringArrayItems = 128;
    internal const int MaxConfigStringArrayItemChars = 256;

    private static readonly IReadOnlyList<string> KnownTopLevelKeys = new[]
    {
        "$schema",
        "debug",
        "metrics_path",
        "disable_persistent_log",
        "global_tool_log_dir",
        "stale_after",
        "indexing",
        "search",
        "output",
        "graph",
        "folding",
        "suggestion_dedup_threshold",
        "suggestion_max_age_days",
        "suggestion_max_count",
        "mcp",
    };

    private static readonly IReadOnlyList<string> KnownIndexingKeys = new[] { "includeKinds", "excludeKinds" };
    private static readonly IReadOnlyList<string> KnownSearchKeys = new[] { "limit", "snippet_lines", "max_line_width" };
    private static readonly IReadOnlyList<string> KnownOutputKeys = new[] { "format", "locale" };
    private static readonly IReadOnlyList<string> KnownGraphKeys = new[] { "max_hops" };
    private static readonly IReadOnlyList<string> KnownFoldingKeys = new[] { "fold_key_version" };
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
            text = DataDirectorySecurity.ReadTextWithinLimit(path, MaxConfigFileBytes)
                   ?? throw new InvalidDataException($"{FileName} exceeds the {MaxConfigFileBytes} byte limit.");
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
                MaxDepth = MaxConfigJsonDepth,
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

            if (AddTopLevelEnvironmentSettings(root, path, pending) is { } topLevelError)
                return topLevelError;
            if (AddSuggestionEnvironmentSettings(root, path, pending) is { } suggestionError)
                return suggestionError;
            if (AddIndexingEnvironmentSettings(root, path, pending) is { } indexingError)
                return indexingError;
            if (AddSearchEnvironmentSettings(root, path, pending) is { } searchError)
                return searchError;

            if (!ValidateOptionalObject(root, "output", KnownOutputKeys, path, out var optionalObjectError)
                || !ValidateOptionalObject(root, "graph", KnownGraphKeys, path, out optionalObjectError)
                || !ValidateOptionalObject(root, "folding", KnownFoldingKeys, path, out optionalObjectError))
                return new LoadResult(Path: path, Error: optionalObjectError);

            if (AddMcpEnvironmentSettings(root, path, pending) is { } mcpError)
                return mcpError;

            // Apply only when the matching env var is not present (null), preserving the
            // documented precedence (real env wins over config-file value). An explicit
            // empty string still counts as "set" because several existing consumers
            // (e.g. RateLimiterOptions.FromEnvironment) treat empty as "feature off",
            // so a user clearing a checked-in value must be able to override with `export FOO=`.
            ApplyPendingEnvironmentSettings(pending, path, envReader, envWriter);

            return new LoadResult(Path: path, Error: null);
        }
    }

    private static LoadResult? AddTopLevelEnvironmentSettings(JsonElement root, string path, List<(string EnvName, string Value)> pending)
    {
        if (root.TryGetProperty("debug", out var debug))
        {
            if (!TryReadString(debug, "debug", path, out var value, out var err))
                return new LoadResult(Path: path, Error: err);
            pending.Add(("CDIDX_DEBUG", value!));
        }

        if (root.TryGetProperty("metrics_path", out var metrics))
        {
            if (!TryReadWorkspaceOutputPath(metrics, "metrics_path", path, out var value, out var err))
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
            if (!TryReadWorkspaceOutputPath(logDir, "global_tool_log_dir", path, out var value, out var err))
                return new LoadResult(Path: path, Error: err);
            pending.Add(("CDIDX_GLOBAL_TOOL_LOG_DIR", value!));
        }

        if (root.TryGetProperty("stale_after", out var staleAfter))
        {
            if (!TryReadString(staleAfter, "stale_after", path, out var value, out var err))
                return new LoadResult(Path: path, Error: err);
            pending.Add((QueryCommandRunner.StaleAfterEnvironmentVariable, value!));
        }

        return null;
    }

    private static LoadResult? AddSuggestionEnvironmentSettings(JsonElement root, string path, List<(string EnvName, string Value)> pending)
    {
        if (root.TryGetProperty("suggestion_dedup_threshold", out var suggestionDedupThreshold))
        {
            if (!TryReadNumberAsString(suggestionDedupThreshold, "suggestion_dedup_threshold", path, out var value, out var err))
                return new LoadResult(Path: path, Error: err);
            if (!double.TryParse(value, NumberStyles.Float, CultureInfo.InvariantCulture, out var threshold)
                || threshold < 0
                || threshold > 1)
            {
                return new LoadResult(Path: path, Error: $"[cdidx] {path}: `suggestion_dedup_threshold` must be between 0 and 1.");
            }

            pending.Add((SuggestionStore.DedupThresholdEnvironmentVariable, value!));
        }

        if (root.TryGetProperty("suggestion_max_age_days", out var suggestionMaxAgeDays))
        {
            if (!TryReadPositiveIntegerAsString(suggestionMaxAgeDays, "suggestion_max_age_days", path, out var value, out var err))
                return new LoadResult(Path: path, Error: err);
            var parsedMaxAgeDays = int.Parse(value!, CultureInfo.InvariantCulture);
            if (parsedMaxAgeDays > SuggestionStore.MaximumMaxAgeDays)
                return new LoadResult(Path: path, Error: $"[cdidx] {path}: `suggestion_max_age_days` must be <= {SuggestionStore.MaximumMaxAgeDays}.");
            pending.Add((SuggestionStore.MaxAgeDaysEnvironmentVariable, value!));
        }

        if (root.TryGetProperty("suggestion_max_count", out var suggestionMaxCount))
        {
            if (!TryReadPositiveIntegerAsString(suggestionMaxCount, "suggestion_max_count", path, out var value, out var err))
                return new LoadResult(Path: path, Error: err);
            var parsedMaxCount = int.Parse(value!, CultureInfo.InvariantCulture);
            if (parsedMaxCount > SuggestionStore.MaximumMaxCount)
                return new LoadResult(Path: path, Error: $"[cdidx] {path}: `suggestion_max_count` must be <= {SuggestionStore.MaximumMaxCount}.");
            pending.Add((SuggestionStore.MaxCountEnvironmentVariable, value!));
        }

        return null;
    }

    private static LoadResult? AddIndexingEnvironmentSettings(JsonElement root, string path, List<(string EnvName, string Value)> pending)
    {
        if (!root.TryGetProperty("indexing", out var indexing))
            return null;

        if (indexing.ValueKind != JsonValueKind.Object)
            return new LoadResult(Path: path, Error: $"[cdidx] {path}: `indexing` must be a JSON object.");
        if (TryFindUnknownKey(indexing, KnownIndexingKeys, out var unknownIndexingKey))
            return new LoadResult(Path: path, Error: $"[cdidx] {path}: unknown key `indexing.{unknownIndexingKey}`. Supported keys: {string.Join(", ", KnownIndexingKeys)}.");

        if (indexing.TryGetProperty("includeKinds", out var includeKinds))
        {
            if (!TryReadStringArray(includeKinds, "indexing.includeKinds", path, out var value, out var err))
                return new LoadResult(Path: path, Error: err);
            if (value!.Length > 0)
                pending.Add((IndexCommandRunner.IncludeSymbolKindsEnvironmentVariable, string.Join(",", value)));
        }

        if (indexing.TryGetProperty("excludeKinds", out var excludeKinds))
        {
            if (!TryReadStringArray(excludeKinds, "indexing.excludeKinds", path, out var value, out var err))
                return new LoadResult(Path: path, Error: err);
            if (value!.Length > 0)
                pending.Add((IndexCommandRunner.ExcludeSymbolKindsEnvironmentVariable, string.Join(",", value)));
        }

        return null;
    }

    private static LoadResult? AddSearchEnvironmentSettings(JsonElement root, string path, List<(string EnvName, string Value)> pending)
    {
        if (!root.TryGetProperty("search", out var search))
            return null;

        if (search.ValueKind != JsonValueKind.Object)
            return new LoadResult(Path: path, Error: $"[cdidx] {path}: `search` must be a JSON object.");
        if (TryFindUnknownKey(search, KnownSearchKeys, out var unknownSearchKey))
            return new LoadResult(Path: path, Error: $"[cdidx] {path}: unknown key `search.{unknownSearchKey}`. Supported keys: {string.Join(", ", KnownSearchKeys)}.");

        if (search.TryGetProperty("limit", out var limit))
        {
            if (!TryReadSearchInteger(limit, "search.limit", "--limit", allowZero: false, path, out var value, out var err))
                return new LoadResult(Path: path, Error: err);
            pending.Add((QueryCommandRunner.DefaultLimitEnvironmentVariable, value!));
        }

        if (search.TryGetProperty("snippet_lines", out var snippetLines))
        {
            if (!TryReadSearchInteger(snippetLines, "search.snippet_lines", "--snippet-lines", allowZero: false, path, out var value, out var err))
                return new LoadResult(Path: path, Error: err);
            pending.Add((QueryCommandRunner.DefaultSnippetLinesEnvironmentVariable, value!));
        }

        if (search.TryGetProperty("max_line_width", out var maxLineWidth))
        {
            if (!TryReadSearchInteger(maxLineWidth, "search.max_line_width", "--max-line-width", allowZero: true, path, out var value, out var err))
                return new LoadResult(Path: path, Error: err);
            pending.Add((QueryCommandRunner.DefaultMaxLineWidthEnvironmentVariable, value!));
        }

        return null;
    }

    private static LoadResult? AddMcpEnvironmentSettings(JsonElement root, string path, List<(string EnvName, string Value)> pending)
    {
        if (!root.TryGetProperty("mcp", out var mcp))
            return null;

        if (mcp.ValueKind != JsonValueKind.Object)
            return new LoadResult(Path: path, Error: $"[cdidx] {path}: `mcp` must be a JSON object.");
        if (TryFindUnknownKey(mcp, KnownMcpKeys, out var unknownMcpKey))
            return new LoadResult(Path: path, Error: $"[cdidx] {path}: unknown key `mcp.{unknownMcpKey}`. Supported keys: {string.Join(", ", KnownMcpKeys)}.");

        if (AddMcpToolEnvironmentSettings(mcp, path, pending) is { } toolsError)
            return toolsError;
        return AddMcpRateLimitEnvironmentSettings(mcp, path, pending);
    }

    private static LoadResult? AddMcpToolEnvironmentSettings(JsonElement mcp, string path, List<(string EnvName, string Value)> pending)
    {
        if (!mcp.TryGetProperty("tools", out var tools))
            return null;

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

        return null;
    }

    private static LoadResult? AddMcpRateLimitEnvironmentSettings(JsonElement mcp, string path, List<(string EnvName, string Value)> pending)
    {
        if (!mcp.TryGetProperty("rate_limit", out var rateLimit))
            return null;

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

        return null;
    }

    private static void ApplyPendingEnvironmentSettings(
        List<(string EnvName, string Value)> pending,
        string path,
        Func<string, string?> envReader,
        Action<string, string?> envWriter)
    {
        foreach (var (name, value) in pending)
        {
            if (envReader(name) is not null)
                continue;

            envWriter(name, value);
            envWriter(ConfigSourceEnvironmentVariablePrefix + name, path);
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
            var projectCandidate = Path.Combine(current.FullName, ProjectConfigRelativePath);
            if (File.Exists(LongPath.EnsureWindowsPrefix(projectCandidate)))
                return projectCandidate;
            var candidate = Path.Combine(current.FullName, FileName);
            if (File.Exists(LongPath.EnsureWindowsPrefix(candidate)))
                return candidate;
            current = current.Parent;
        }
        return null;
    }

    private static string ResolveConfigWorkspaceRoot(string configPath)
    {
        var fullConfigPath = Path.GetFullPath(configPath);
        var configDirectory = Path.GetDirectoryName(fullConfigPath) ?? Path.GetFullPath(".");
        if (string.Equals(Path.GetFileName(configDirectory), ".cdidx", StringComparison.Ordinal)
            && string.Equals(Path.GetFileName(fullConfigPath), "config.json", StringComparison.Ordinal))
        {
            return Path.GetDirectoryName(configDirectory) ?? configDirectory;
        }

        return configDirectory;
    }

    internal static int RunValidate(string[] args, JsonSerializerOptions jsonOptions)
    {
        if (args.Length > 0)
        {
            CommandErrorWriter.Write("validate-config does not accept positional arguments.", "run `cdidx validate-config` from the workspace whose config should be validated.");
            return CommandExitCodes.UsageError;
        }

        var result = LoadAndApply(Environment.CurrentDirectory, name => name == DisableEnvVar ? null : Environment.GetEnvironmentVariable(name), (_, _) => { });
        if (result.Failed)
        {
            Console.Error.WriteLine(result.Error);
            return CommandExitCodes.UsageError;
        }

        var payload = new Dictionary<string, object?>
        {
            ["valid"] = true,
            ["path"] = result.Path,
        };
        Console.WriteLine(JsonSerializer.Serialize(payload, jsonOptions));
        return CommandExitCodes.Success;
    }

    internal static int RunShow(string[] args, JsonSerializerOptions jsonOptions)
    {
        var json = args.Contains("--json", StringComparer.Ordinal);
        args = args.Where(a => a != "--json").ToArray();
        if (args.Length > 0)
            return CommandErrorWriter.WriteJsonOrHuman(json, jsonOptions, "config show does not accept positional arguments.", CommandExitCodes.UsageError, "run `cdidx config show` from the workspace whose config should be shown.");

        var path = FindConfigFile(Environment.CurrentDirectory);
        var active = ActiveWorkspace.Load();
        var payload = new ConfigShowJsonResult(
            path,
            active,
            ["cli", "env", "config_file", "active_workspace", "cwd_default"],
            [ProjectConfigRelativePath, FileName]);
        if (json)
            Console.WriteLine(JsonSerializer.Serialize(payload, jsonOptions));
        else
        {
            Console.WriteLine($"Config path      : {path ?? "(none)"}");
            Console.WriteLine($"Active workspace : {(active == null ? "(none)" : active.Name + " -> " + active.DbPath)}");
            Console.WriteLine("Precedence       : CLI > env > config file > active workspace > CWD default");
        }

        return CommandExitCodes.Success;
    }

    private static bool ValidateOptionalObject(JsonElement root, string key, IReadOnlyList<string> knownKeys, string path, out string? error)
    {
        error = null;
        if (!root.TryGetProperty(key, out var value))
            return true;
        if (value.ValueKind != JsonValueKind.Object)
        {
            error = $"[cdidx] {path}: `{key}` must be a JSON object.";
            return false;
        }
        if (TryFindUnknownKey(value, knownKeys, out var unknownKey))
        {
            error = $"[cdidx] {path}: unknown key `{key}.{unknownKey}`. Supported keys: {string.Join(", ", knownKeys)}.";
            return false;
        }
        return true;
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

    private static bool TryReadWorkspaceOutputPath(JsonElement element, string key, string path, out string? value, out string? error)
    {
        value = null;
        error = null;
        if (!TryReadString(element, key, path, out var raw, out error))
            return false;

        var workspaceRoot = ResolveConfigWorkspaceRoot(path);
        if (!TryResolveWorkspaceOutputPath(raw!, workspaceRoot, out value, out var pathError))
        {
            error = pathError;
            return false;
        }

        return true;

        bool TryResolveWorkspaceOutputPath(string rawPath, string root, out string? resolved, out string? pathError)
        {
            resolved = null;
            pathError = null;
            try
            {
                var normalizedRoot = NormalizeBoundaryPath(Path.GetFullPath(root));
                var fullPath = Path.IsPathRooted(rawPath)
                    ? Path.GetFullPath(rawPath)
                    : Path.GetFullPath(Path.Combine(normalizedRoot, rawPath));
                var normalizedPath = NormalizeBoundaryPath(fullPath);

                if (!PathCasing.IsPathEqualOrParent(normalizedRoot, normalizedPath))
                {
                    pathError = $"[cdidx] {path}: `{key}` must resolve inside the config workspace root `{normalizedRoot}`.";
                    return false;
                }

                resolved = fullPath;
                return true;
            }
            catch (Exception ex) when (ex is ArgumentException
                                           or IOException
                                           or NotSupportedException
                                           or PathTooLongException
                                           or UnauthorizedAccessException)
            {
                pathError = $"[cdidx] {path}: `{key}` path is invalid: {ex.Message}";
                return false;
            }
        }
    }

    private static string NormalizeBoundaryPath(string path)
    {
        var fullPath = Path.GetFullPath(path);
        var root = Path.GetPathRoot(fullPath);
        if (!string.IsNullOrEmpty(root) && string.Equals(fullPath, root, StringComparison.Ordinal))
            return fullPath;
        return fullPath.TrimEnd(Path.DirectorySeparatorChar, Path.AltDirectorySeparatorChar);
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
        var arrayLength = element.GetArrayLength();
        if (arrayLength > MaxConfigStringArrayItems)
        {
            error = $"[cdidx] {path}: `{key}` must contain <= {MaxConfigStringArrayItems} items.";
            return false;
        }

        var collected = new List<string>(arrayLength);
        foreach (var item in element.EnumerateArray())
        {
            if (item.ValueKind != JsonValueKind.String)
            {
                error = $"[cdidx] {path}: `{key}` must contain only strings.";
                return false;
            }
            var raw = item.GetString() ?? string.Empty;
            if (raw.Length > MaxConfigStringArrayItemChars)
            {
                error = $"[cdidx] {path}: `{key}` items must be <= {MaxConfigStringArrayItemChars} characters.";
                return false;
            }
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

    private static bool TryReadPositiveIntegerAsString(JsonElement element, string key, string path, out string? value, out string? error)
    {
        value = null;
        error = null;
        if (element.ValueKind != JsonValueKind.Number)
        {
            error = $"[cdidx] {path}: `{key}` must be a number.";
            return false;
        }

        if (!element.TryGetInt32(out var parsed) || parsed <= 0)
        {
            error = $"[cdidx] {path}: `{key}` must be a positive integer.";
            return false;
        }

        value = parsed.ToString(System.Globalization.CultureInfo.InvariantCulture);
        return true;
    }

    private static bool TryReadSearchInteger(JsonElement element, string key, string optionName, bool allowZero, string path, out string? value, out string? error)
    {
        value = null;
        error = null;
        if (element.ValueKind != JsonValueKind.Number)
        {
            error = $"[cdidx] {path}: `{key}` must be a number.";
            return false;
        }

        if (!element.TryGetInt32(out var parsed) || parsed < 0 || (!allowZero && parsed == 0))
        {
            error = allowZero
                ? $"[cdidx] {path}: `{key}` must be a non-negative integer."
                : $"[cdidx] {path}: `{key}` must be a positive integer.";
            return false;
        }

        if (QueryCommandRunner.NumericFlagUpperBounds.TryGetValue(optionName, out var maxAllowed) && parsed > maxAllowed)
        {
            error = $"[cdidx] {path}: `{key}` must be <= {maxAllowed}.";
            return false;
        }

        value = parsed.ToString(System.Globalization.CultureInfo.InvariantCulture);
        return true;
    }
}

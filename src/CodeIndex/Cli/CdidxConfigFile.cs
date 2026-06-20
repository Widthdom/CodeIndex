using System.Globalization;
using System.Text.Json;
using CodeIndex.Indexer;
using CodeIndex.Mcp;

namespace CodeIndex.Cli;

/// <summary>
/// Project-local configuration file (`.cdidx/config.json` / `.cdidxrc.json`) loader (#1571).
/// Walks upward from a starting directory looking for the first supported config file,
/// validates its schema, and returns recognized keys as scoped runtime settings so the
/// existing env-var consumers (debug, metrics, MCP tool/rate-limit gates, persistent log)
/// can pick them up without mutating the process environment. A real environment variable
/// always wins over a config-file value, which yields the documented precedence:
/// CLI &gt; env &gt; config file &gt; defaults.
/// Secrets (`CDIDX_GITHUB_TOKEN`, `CDIDX_MCP_AUTH_TOKEN`, `CDIDX_MCP_HTTP_TOKEN`) are
/// intentionally NOT loadable from the config file to keep tokens out of version control.
/// プロジェクトローカル設定ファイル `.cdidx/config.json` / `.cdidxrc.json` のローダー (#1571)。
/// 指定ディレクトリから上方向に走査して最初に見つかった対応 config file をスキーマ検証し、認識済みキーを
/// scoped runtime settings として返す。既存の env-var 消費側（debug / metrics / MCP ツール ＆
/// レート制限ゲート / 永続ログ）は process environment を変更せずに値を読める。実際の環境変数は
/// 常に config ファイル値より優先し、結果として「CLI &gt; env &gt; config file &gt; 既定」の優先順位を満たす。
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
    internal const int MaxConfigScalarStringChars = 1024;
    internal const int MaxConfigPathStringChars = 4096;
    internal const int MaxConfigDurationStringChars = 256;

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

    private static readonly IReadOnlyList<string> KnownIndexingKeys = new[] { "includeKinds", "excludeKinds", "generatedCodePatterns" };
    private static readonly IReadOnlyList<string> KnownSearchKeys = new[] { "limit", "snippet_lines", "max_line_width" };
    private static readonly IReadOnlyList<string> KnownOutputKeys = new[] { "format", "locale" };
    private static readonly IReadOnlyList<string> KnownGraphKeys = new[] { "max_hops" };
    private static readonly IReadOnlyList<string> KnownFoldingKeys = new[] { "fold_key_version" };
    private static readonly IReadOnlyList<string> KnownMcpKeys = new[] { "tools", "rate_limit" };
    private static readonly IReadOnlyList<string> KnownMcpToolsKeys = new[] { "allow", "deny" };
    private static readonly IReadOnlyList<string> KnownMcpRateLimitKeys = new[] { "rps", "burst", "bucket_idle_seconds" };
    private static readonly IReadOnlyList<string> ConfigDiscoveryBoundaryDirectories = new[] { ".git", ".hg", ".svn" };
    private static readonly IReadOnlyList<string> ConfigDiscoveryBoundaryFiles = new[]
    {
        WorkspaceManifestLoader.FileName,
        WorkspaceManifestLoader.DotFileName,
    };

    internal sealed record LoadResult(string? Path, string? Error)
    {
        private static readonly IReadOnlyDictionary<string, string> EmptySettings = new Dictionary<string, string>(StringComparer.Ordinal);

        internal IReadOnlyDictionary<string, string> Settings { get; init; } = EmptySettings;
        internal IReadOnlyDictionary<string, string> Sources { get; init; } = EmptySettings;
        internal bool Loaded => Path is not null && Error is null;
        internal bool Failed => Error is not null;
    }

    /// <summary>
    /// Walk upward from <paramref name="startingDirectory"/> looking for `.cdidxrc.json`.
    /// When found, parse it and return recognized settings (only when the matching env var is
    /// currently unset). Returns a result describing what happened so callers can surface
    /// validation errors. No-op when `CDIDX_DISABLE_CONFIG_FILE=1` is set.
    /// </summary>
    internal static LoadResult Load(string startingDirectory)
        => Load(startingDirectory, CdidxEnvironment.GetEnvironmentVariable);

    internal static LoadResult Load(
        string startingDirectory,
        Func<string, string?> envReader)
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

            var pending = new List<(string EnvName, string Value)>();
            var errors = new List<string>();

            AddUnknownKeyDiagnostics(root, KnownTopLevelKeys, null, path, string.Join(", ", KnownTopLevelKeys.Where(k => k != "$schema")), errors);
            AddTopLevelEnvironmentSettings(root, path, pending, errors);
            AddSuggestionEnvironmentSettings(root, path, pending, errors);
            AddIndexingEnvironmentSettings(root, path, pending, errors);
            AddSearchEnvironmentSettings(root, path, pending, errors);
            ValidateOptionalObject(root, "output", KnownOutputKeys, path, errors);
            ValidateOptionalObject(root, "graph", KnownGraphKeys, path, errors);
            ValidateOptionalObject(root, "folding", KnownFoldingKeys, path, errors);
            AddMcpEnvironmentSettings(root, path, pending, errors);

            if (errors.Count > 0)
                return new LoadResult(Path: path, Error: string.Join(Environment.NewLine, errors));

            // Include values only when the matching env var is not present (null), preserving the
            // documented precedence (real env wins over config-file value). An explicit
            // empty string still counts as "set" because several existing consumers
            // (e.g. RateLimiterOptions.FromEnvironment) treat empty as "feature off",
            // so a user clearing a checked-in value must be able to override with `export FOO=`.
            var (settings, sources) = BuildScopedEnvironmentSettings(pending, path, envReader);

            return new LoadResult(Path: path, Error: null)
            {
                Settings = settings,
                Sources = sources,
            };
        }
    }

    private static void AddTopLevelEnvironmentSettings(JsonElement root, string path, List<(string EnvName, string Value)> pending, List<string> errors)
    {
        if (root.TryGetProperty("debug", out var debug))
        {
            if (!TryReadString(debug, "debug", path, MaxConfigScalarStringChars, out var value, out var err))
                errors.Add(err!);
            else
                pending.Add(("CDIDX_DEBUG", value!));
        }

        if (root.TryGetProperty("metrics_path", out var metrics))
        {
            if (!TryReadWorkspaceOutputPath(metrics, "metrics_path", path, out var value, out var err))
                errors.Add(err!);
            else
                pending.Add(("CDIDX_METRICS", value!));
        }

        if (root.TryGetProperty("disable_persistent_log", out var disableLog))
        {
            if (disableLog.ValueKind != JsonValueKind.True && disableLog.ValueKind != JsonValueKind.False)
                errors.Add($"[cdidx] {path}: `disable_persistent_log` must be a boolean.");
            else if (disableLog.GetBoolean())
                pending.Add(("CDIDX_DISABLE_PERSISTENT_LOG", "1"));
        }

        if (root.TryGetProperty("global_tool_log_dir", out var logDir))
        {
            if (!TryReadWorkspaceOutputPath(logDir, "global_tool_log_dir", path, out var value, out var err))
                errors.Add(err!);
            else
                pending.Add(("CDIDX_GLOBAL_TOOL_LOG_DIR", value!));
        }

        if (root.TryGetProperty("stale_after", out var staleAfter))
        {
            if (!TryReadString(staleAfter, "stale_after", path, MaxConfigDurationStringChars, out var value, out var err))
                errors.Add(err!);
            else
                pending.Add((QueryCommandRunner.StaleAfterEnvironmentVariable, value!));
        }
    }

    private static void AddSuggestionEnvironmentSettings(JsonElement root, string path, List<(string EnvName, string Value)> pending, List<string> errors)
    {
        if (root.TryGetProperty("suggestion_dedup_threshold", out var suggestionDedupThreshold))
        {
            if (!TryReadFiniteDoubleAsString(
                    suggestionDedupThreshold,
                    "suggestion_dedup_threshold",
                    path,
                    maxInclusive: 1.0,
                    allowZero: true,
                    out var value,
                    out var err))
                errors.Add(err!);
            else
            {
                pending.Add((SuggestionStore.DedupThresholdEnvironmentVariable, value!));
            }
        }

        if (root.TryGetProperty("suggestion_max_age_days", out var suggestionMaxAgeDays))
        {
            if (!TryReadPositiveIntegerAsString(suggestionMaxAgeDays, "suggestion_max_age_days", path, out var value, out var err))
                errors.Add(err!);
            else
            {
                var parsedMaxAgeDays = int.Parse(value!, CultureInfo.InvariantCulture);
                if (parsedMaxAgeDays > SuggestionStore.MaximumMaxAgeDays)
                    errors.Add($"[cdidx] {path}: `suggestion_max_age_days` must be <= {SuggestionStore.MaximumMaxAgeDays}.");
                else
                    pending.Add((SuggestionStore.MaxAgeDaysEnvironmentVariable, value!));
            }
        }

        if (root.TryGetProperty("suggestion_max_count", out var suggestionMaxCount))
        {
            if (!TryReadPositiveIntegerAsString(suggestionMaxCount, "suggestion_max_count", path, out var value, out var err))
                errors.Add(err!);
            else
            {
                var parsedMaxCount = int.Parse(value!, CultureInfo.InvariantCulture);
                if (parsedMaxCount > SuggestionStore.MaximumMaxCount)
                    errors.Add($"[cdidx] {path}: `suggestion_max_count` must be <= {SuggestionStore.MaximumMaxCount}.");
                else
                    pending.Add((SuggestionStore.MaxCountEnvironmentVariable, value!));
            }
        }
    }

    private static void AddIndexingEnvironmentSettings(JsonElement root, string path, List<(string EnvName, string Value)> pending, List<string> errors)
    {
        if (!root.TryGetProperty("indexing", out var indexing))
            return;

        if (indexing.ValueKind != JsonValueKind.Object)
        {
            errors.Add($"[cdidx] {path}: `indexing` must be a JSON object.");
            return;
        }

        AddUnknownKeyDiagnostics(indexing, KnownIndexingKeys, "indexing", path, string.Join(", ", KnownIndexingKeys), errors);

        if (indexing.TryGetProperty("includeKinds", out var includeKinds))
        {
            if (!TryReadStringArray(includeKinds, "indexing.includeKinds", path, out var value, out var err))
                errors.Add(err!);
            else if (value!.Length > 0)
                pending.Add((IndexCommandRunner.IncludeSymbolKindsEnvironmentVariable, string.Join(",", value)));
        }

        if (indexing.TryGetProperty("excludeKinds", out var excludeKinds))
        {
            if (!TryReadStringArray(excludeKinds, "indexing.excludeKinds", path, out var value, out var err))
                errors.Add(err!);
            else if (value!.Length > 0)
                pending.Add((IndexCommandRunner.ExcludeSymbolKindsEnvironmentVariable, string.Join(",", value)));
        }

        if (indexing.TryGetProperty("generatedCodePatterns", out var generatedCodePatterns))
        {
            if (!TryReadStringArray(generatedCodePatterns, "indexing.generatedCodePatterns", path, out var value, out var err))
                errors.Add(err!);
            else if (value!.Length > 0)
                pending.Add((IndexCommandRunner.GeneratedCodePatternsEnvironmentVariable, string.Join(",", value)));
        }
    }

    private static void AddSearchEnvironmentSettings(JsonElement root, string path, List<(string EnvName, string Value)> pending, List<string> errors)
    {
        if (!root.TryGetProperty("search", out var search))
            return;

        if (search.ValueKind != JsonValueKind.Object)
        {
            errors.Add($"[cdidx] {path}: `search` must be a JSON object.");
            return;
        }

        AddUnknownKeyDiagnostics(search, KnownSearchKeys, "search", path, string.Join(", ", KnownSearchKeys), errors);

        if (search.TryGetProperty("limit", out var limit))
        {
            if (!TryReadSearchInteger(limit, "search.limit", "--limit", allowZero: false, path, out var value, out var err))
                errors.Add(err!);
            else
                pending.Add((QueryCommandRunner.DefaultLimitEnvironmentVariable, value!));
        }

        if (search.TryGetProperty("snippet_lines", out var snippetLines))
        {
            if (!TryReadSearchInteger(snippetLines, "search.snippet_lines", "--snippet-lines", allowZero: false, path, out var value, out var err))
                errors.Add(err!);
            else
                pending.Add((QueryCommandRunner.DefaultSnippetLinesEnvironmentVariable, value!));
        }

        if (search.TryGetProperty("max_line_width", out var maxLineWidth))
        {
            if (!TryReadSearchInteger(maxLineWidth, "search.max_line_width", "--max-line-width", allowZero: true, path, out var value, out var err))
                errors.Add(err!);
            else
                pending.Add((QueryCommandRunner.DefaultMaxLineWidthEnvironmentVariable, value!));
        }
    }

    private static void AddMcpEnvironmentSettings(JsonElement root, string path, List<(string EnvName, string Value)> pending, List<string> errors)
    {
        if (!root.TryGetProperty("mcp", out var mcp))
            return;

        if (mcp.ValueKind != JsonValueKind.Object)
        {
            errors.Add($"[cdidx] {path}: `mcp` must be a JSON object.");
            return;
        }

        AddUnknownKeyDiagnostics(mcp, KnownMcpKeys, "mcp", path, string.Join(", ", KnownMcpKeys), errors);

        AddMcpToolEnvironmentSettings(mcp, path, pending, errors);
        AddMcpRateLimitEnvironmentSettings(mcp, path, pending, errors);
    }

    private static void AddMcpToolEnvironmentSettings(JsonElement mcp, string path, List<(string EnvName, string Value)> pending, List<string> errors)
    {
        if (!mcp.TryGetProperty("tools", out var tools))
            return;

        if (tools.ValueKind != JsonValueKind.Object)
        {
            errors.Add($"[cdidx] {path}: `mcp.tools` must be a JSON object.");
            return;
        }

        AddUnknownKeyDiagnostics(tools, KnownMcpToolsKeys, "mcp.tools", path, string.Join(", ", KnownMcpToolsKeys), errors);

        if (tools.TryGetProperty("allow", out var allow))
        {
            if (!TryReadStringArray(allow, "mcp.tools.allow", path, out var value, out var err))
                errors.Add(err!);
            else if (value!.Length > 0)
                pending.Add(("CDIDX_MCP_TOOLS_ALLOW", string.Join(",", value)));
        }

        if (tools.TryGetProperty("deny", out var deny))
        {
            if (!TryReadStringArray(deny, "mcp.tools.deny", path, out var value, out var err))
                errors.Add(err!);
            else if (value!.Length > 0)
                pending.Add(("CDIDX_MCP_TOOLS_DENY", string.Join(",", value)));
        }
    }

    private static void AddMcpRateLimitEnvironmentSettings(JsonElement mcp, string path, List<(string EnvName, string Value)> pending, List<string> errors)
    {
        if (!mcp.TryGetProperty("rate_limit", out var rateLimit))
            return;

        if (rateLimit.ValueKind != JsonValueKind.Object)
        {
            errors.Add($"[cdidx] {path}: `mcp.rate_limit` must be a JSON object.");
            return;
        }

        AddUnknownKeyDiagnostics(rateLimit, KnownMcpRateLimitKeys, "mcp.rate_limit", path, string.Join(", ", KnownMcpRateLimitKeys), errors);

        if (rateLimit.TryGetProperty("rps", out var rps))
        {
            if (!TryReadFiniteDoubleAsString(
                    rps,
                    "mcp.rate_limit.rps",
                    path,
                    maxInclusive: RateLimiterOptions.MaxRefillTokensPerSecond,
                    allowZero: false,
                    out var value,
                    out var err))
                errors.Add(err!);
            else
                pending.Add((RateLimiterOptions.RpsEnvVar, value!));
        }

        if (rateLimit.TryGetProperty("burst", out var burst))
        {
            if (!TryReadFiniteDoubleAsString(
                    burst,
                    "mcp.rate_limit.burst",
                    path,
                    maxInclusive: RateLimiterOptions.MaxBurstCapacity,
                    allowZero: false,
                    out var value,
                    out var err))
                errors.Add(err!);
            else
                pending.Add((RateLimiterOptions.BurstEnvVar, value!));
        }

        if (rateLimit.TryGetProperty("bucket_idle_seconds", out var bucketIdleSeconds))
        {
            if (!TryReadFiniteDoubleAsString(
                    bucketIdleSeconds,
                    "mcp.rate_limit.bucket_idle_seconds",
                    path,
                    maxInclusive: TimeSpan.MaxValue.TotalSeconds,
                    allowZero: false,
                    out var value,
                    out var err))
                errors.Add(err!);
            else
                pending.Add((RateLimiterOptions.BucketIdleSecondsEnvVar, value!));
        }
    }

    private static (IReadOnlyDictionary<string, string> Settings, IReadOnlyDictionary<string, string> Sources) BuildScopedEnvironmentSettings(
        List<(string EnvName, string Value)> pending,
        string path,
        Func<string, string?> envReader)
    {
        var settings = new Dictionary<string, string>(StringComparer.Ordinal);
        var sources = new Dictionary<string, string>(StringComparer.Ordinal);
        foreach (var (name, value) in pending)
        {
            if (envReader(name) is not null)
                continue;

            settings[name] = value;
            sources[name] = path;
        }

        return (settings, sources);
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
            if (IsConfigDiscoveryBoundary(current))
                break;
            current = current.Parent;
        }
        return null;
    }

    private static bool IsConfigDiscoveryBoundary(DirectoryInfo directory)
    {
        foreach (var directoryName in ConfigDiscoveryBoundaryDirectories)
        {
            var path = Path.Combine(directory.FullName, directoryName);
            if (Directory.Exists(LongPath.EnsureWindowsPrefix(path)))
                return true;
        }

        foreach (var fileName in ConfigDiscoveryBoundaryFiles)
        {
            var path = Path.Combine(directory.FullName, fileName);
            if (File.Exists(LongPath.EnsureWindowsPrefix(path)))
                return true;
        }

        return false;
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

        var result = Load(Environment.CurrentDirectory, name => name == DisableEnvVar ? null : Environment.GetEnvironmentVariable(name));
        if (result.Failed)
        {
            CommandErrorWriter.WriteStderr(result.Error);
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

    private static void ValidateOptionalObject(JsonElement root, string key, IReadOnlyList<string> knownKeys, string path, List<string> errors)
    {
        if (!root.TryGetProperty(key, out var value))
            return;
        if (value.ValueKind != JsonValueKind.Object)
        {
            errors.Add($"[cdidx] {path}: `{key}` must be a JSON object.");
            return;
        }

        AddUnknownKeyDiagnostics(value, knownKeys, key, path, string.Join(", ", knownKeys), errors);
    }

    private static void AddUnknownKeyDiagnostics(
        JsonElement obj,
        IReadOnlyList<string> knownKeys,
        string? prefix,
        string path,
        string supportedKeys,
        List<string> errors)
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
                var qualifiedName = prefix is null ? property.Name : $"{prefix}.{property.Name}";
                errors.Add($"[cdidx] {path}: unknown key `{qualifiedName}`. Supported keys: {supportedKeys}.");
            }
        }
    }

    private static bool TryReadString(JsonElement element, string key, string path, int maxChars, out string? value, out string? error)
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
        if (raw.Length > maxChars)
        {
            error = $"[cdidx] {path}: `{key}` must be <= {maxChars} characters.";
            return false;
        }

        value = raw;
        return true;
    }

    private static bool TryReadWorkspaceOutputPath(JsonElement element, string key, string path, out string? value, out string? error)
    {
        value = null;
        error = null;
        if (!TryReadString(element, key, path, MaxConfigPathStringChars, out var raw, out error))
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
                pathError = $"[cdidx] {path}: `{key}` path is invalid (invalid_path).";
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

    private static bool TryReadFiniteDoubleAsString(
        JsonElement element,
        string key,
        string path,
        double maxInclusive,
        bool allowZero,
        out string? value,
        out string? error)
    {
        value = null;
        error = null;
        if (element.ValueKind != JsonValueKind.Number)
        {
            error = $"[cdidx] {path}: `{key}` must be a number.";
            return false;
        }

        if (!element.TryGetDouble(out var parsed)
            || !double.IsFinite(parsed)
            || (allowZero ? parsed < 0 : parsed <= 0)
            || parsed > maxInclusive)
        {
            var minimum = allowZero ? "non-negative" : "positive";
            error = $"[cdidx] {path}: `{key}` must be a finite {minimum} number <= {maxInclusive.ToString(CultureInfo.InvariantCulture)}.";
            return false;
        }

        value = parsed.ToString(CultureInfo.InvariantCulture);
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

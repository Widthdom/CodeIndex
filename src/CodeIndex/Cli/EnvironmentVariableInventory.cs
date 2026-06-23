using System.Text.Json.Serialization;
using CodeIndex.Database;
using CodeIndex.Indexer;
using CodeIndex.Indexer.Extensibility;
using CodeIndex.Indexer.Hooks;
using CodeIndex.Mcp;

namespace CodeIndex.Cli;

internal static class EnvironmentVariableInventory
{
    internal const string SensitivityPublic = "public_config";
    internal const string SensitivitySecret = "secret";

    internal static IReadOnlyList<EnvironmentVariableInventoryItem> Items { get; } =
    [
        Item(DbPathResolver.DataDirEnvironmentVariable, "path", SensitivityPublic, "io", "workspace .cdidx directory, XDG cache, then current directory", "no", "Override data directory used to find codeindex.db.", Location("src/CodeIndex/Cli/DbPathResolver.cs", 15, "DbPathResolver")),
        Item(ActiveWorkspace.EnvironmentVariable, "workspace", SensitivityPublic, "io", "no active workspace", "no", "Active workspace state file path.", Location("src/CodeIndex/Cli/ActiveWorkspace.cs", 10, "ActiveWorkspace")),
        Item(CdidxConfigFile.DisableEnvVar, "config", SensitivityPublic, "security", "config discovery enabled", "no", "Disable project-local config file discovery when set to 1.", Location("src/CodeIndex/Cli/CdidxConfigFile.cs", 31, "CdidxConfigFile")),
        Item(ProgramRunner.QuietEnvironmentVariable, "output", SensitivityPublic, "diagnostic", "informational stderr enabled", "yes", "Suppress informational stderr output.", Location("src/CodeIndex/Cli/ProgramRunner.cs", 26, "ProgramRunner")),
        Item("CDIDX_METRICS", "telemetry", SensitivityPublic, "io", "metrics disabled", "yes", "JSONL metrics destination path.", Location("src/CodeIndex/Cli/MetricsSink.cs", 22, "MetricsSink")),
        Item(UiLanguageResolver.EnvVarName, "locale", SensitivityPublic, "display", "current UI culture, then English", "no", "Preferred UI language for localized messages.", Location("src/CodeIndex/Cli/UiI18n.cs", 25, "UiLanguageResolver")),

        Item(QueryCommandRunner.DefaultLimitEnvironmentVariable, "query", SensitivityPublic, "performance", "20", "yes", "Default query result limit.", Location("src/CodeIndex/Cli/QueryCommandRunner.cs", 44, "QueryCommandRunner")),
        Item(QueryCommandRunner.DefaultSnippetLinesEnvironmentVariable, "query", SensitivityPublic, "display", "formatter default", "yes", "Default search/reference snippet line count.", Location("src/CodeIndex/Cli/QueryCommandRunner.cs", 45, "QueryCommandRunner")),
        Item(QueryCommandRunner.DefaultMaxLineWidthEnvironmentVariable, "query", SensitivityPublic, "display", LineWidthFormatter.DefaultMaxLineWidth.ToString(System.Globalization.CultureInfo.InvariantCulture), "yes", "Default max line width before result snippets are clamped.", Location("src/CodeIndex/Cli/QueryCommandRunner.cs", 46, "QueryCommandRunner")),
        Item(QueryCommandRunner.StaleAfterEnvironmentVariable, "status", SensitivityPublic, "diagnostic", "24h", "yes", "Default freshness threshold for status checks.", Location("src/CodeIndex/Cli/QueryCommandRunner.cs", 47, "QueryCommandRunner")),

        Item(FileIndexer.MaxFileSizeEnvironmentVariable, "indexing", SensitivityPublic, "performance", "4 MiB", "no", "Default maximum file size to index.", Location("src/CodeIndex/Indexer/Scanning/FileIndexer.cs", 457, "FileIndexer")),
        Item(IndexCommandRunner.CompletionNotificationEnvironmentVariable, "indexing", SensitivityPublic, "display", "auto", "no", "Long index completion notification mode.", Location("src/CodeIndex/Cli/IndexCommandRunner.Parse.cs", 24, "IndexCommandRunner")),
        Item(IndexCommandRunner.IndexParallelismEnvironmentVariable, "indexing", SensitivityPublic, "performance", "CPU count capped at 16", "no", "Full-scan extraction worker count.", Location("src/CodeIndex/Cli/IndexCommandRunner.Parse.cs", 25, "IndexCommandRunner")),
        Item(IndexCommandRunner.WatchPendingPathLimitEnvironmentVariable, "indexing", SensitivityPublic, "performance", "watch default", "yes", "Pending changed-path queue limit before full watch rescan.", Location("src/CodeIndex/Cli/IndexCommandRunner.Parse.cs", 26, "IndexCommandRunner")),

        Item(DbContext.CacheSizeEnvironmentVariable, "sqlite", SensitivityPublic, "performance", DbContext.DefaultCacheSizeKb.ToString(System.Globalization.CultureInfo.InvariantCulture), "no", "SQLite cache size in KiB.", Location("src/CodeIndex/Database/DbContext.cs", 21, "DbContext")),
        Item(DbContext.MmapSizeEnvironmentVariable, "sqlite", SensitivityPublic, "performance", DbContext.DefaultMmapSizeBytes.ToString(System.Globalization.CultureInfo.InvariantCulture), "no", "SQLite mmap size in bytes for 64-bit processes.", Location("src/CodeIndex/Database/DbContext.cs", 22, "DbContext")),
        Item(DbContext.BusyTimeoutEnvironmentVariable, "sqlite", SensitivityPublic, "performance", DbPragmaPolicy.DefaultBusyTimeoutMs.ToString(System.Globalization.CultureInfo.InvariantCulture), "no", "SQLite busy timeout in milliseconds.", Location("src/CodeIndex/Database/DbContext.cs", 23, "DbContext")),
        Item(PreparedCommandCache.CapacityEnvironmentVariable, "sqlite", SensitivityPublic, "performance", PreparedCommandCache.DefaultCapacity.ToString(System.Globalization.CultureInfo.InvariantCulture), "no", "Prepared SQLite command cache capacity.", Location("src/CodeIndex/Database/PreparedCommandCache.cs", 26, "PreparedCommandCache")),
        Item("CDIDX_SLOW_QUERY_MS", "sqlite", SensitivityPublic, "diagnostic", "disabled", "no", "Log slow SQLite queries at or above a millisecond threshold.", Location("src/CodeIndex/Database/DbDebug.cs", 260, "DbDebug")),
        Item(McpServer.DebugEnvironmentVariable, "debug", SensitivityPublic, "security", "off", "yes", "Enable redacted debug diagnostics; unsafe raw dumps still require --debug-unsafe.", Location("src/CodeIndex/Mcp/McpServer.cs", 193, "McpServer"), Location("src/CodeIndex/Database/DbDebug.cs", 12, "DbDebug")),

        Item(GlobalToolLog.LogFormatEnvironmentVariable, "logging", SensitivityPublic, "diagnostic", "text", "yes", "Persistent stderr log format.", Location("src/CodeIndex/Cli/GlobalToolLog.cs", 17, "GlobalToolLog")),
        Item(GlobalToolLog.LogRetainEnvironmentVariable, "logging", SensitivityPublic, "diagnostic", "30", "yes", "Persistent stderr log retention count.", Location("src/CodeIndex/Cli/GlobalToolLog.cs", 18, "GlobalToolLog")),
        Item(GlobalToolLog.LogMaxSizeMbEnvironmentVariable, "logging", SensitivityPublic, "io", "50", "yes", "Persistent stderr log rotation size cap in MiB.", Location("src/CodeIndex/Cli/GlobalToolLog.cs", 19, "GlobalToolLog")),
        Item(GlobalToolLog.GlobalToolLogMaxBytesEnvironmentVariable, "logging", SensitivityPublic, "io", "internal cap", "no", "Maximum persistent stderr log bytes.", Location("src/CodeIndex/Cli/GlobalToolLog.cs", 20, "GlobalToolLog")),
        Item("CDIDX_GLOBAL_TOOL_LOG_DIR", "logging", SensitivityPublic, "io", "platform log directory", "yes", "Persistent stderr log directory.", Location("src/CodeIndex/Cli/GlobalToolLog.cs", 736, "GlobalToolLog")),

        Item(SearchAuditRecipes.RecipePathsEnvironmentVariable, "search", SensitivityPublic, "io", "built-in recipes only", "no", "Additional search recipe files.", Location("src/CodeIndex/Cli/SearchAuditRecipes.cs", 13, "SearchAuditRecipes")),

        Item(McpToolFilter.AllowEnvVarName, "mcp", SensitivityPublic, "security", "all known tools enabled", "yes", "Allowlist visible/callable MCP tools.", Location("src/CodeIndex/Mcp/McpToolFilter.cs", 23, "McpToolFilter")),
        Item(McpToolFilter.DenyEnvVarName, "mcp", SensitivityPublic, "security", "no denied tools", "yes", "Denylist MCP tools from the default enabled set.", Location("src/CodeIndex/Mcp/McpToolFilter.cs", 24, "McpToolFilter")),
        Item(RateLimiterOptions.RpsEnvVar, "mcp", SensitivityPublic, "performance", "disabled", "yes", "MCP rate-limit refill tokens per second.", Location("src/CodeIndex/Mcp/RateLimiter.cs", 272, "RateLimiterOptions")),
        Item(RateLimiterOptions.BurstEnvVar, "mcp", SensitivityPublic, "performance", "max(rps, 1) when rate limiting enabled", "yes", "MCP rate-limit burst capacity.", Location("src/CodeIndex/Mcp/RateLimiter.cs", 273, "RateLimiterOptions")),
        Item(RateLimiterOptions.BucketIdleSecondsEnvVar, "mcp", SensitivityPublic, "performance", "15 minutes", "yes", "MCP rate-limit bucket idle TTL.", Location("src/CodeIndex/Mcp/RateLimiter.cs", 274, "RateLimiterOptions")),
        Item("CDIDX_MCP_RESPONSE_MAX_BYTES", "mcp", SensitivityPublic, "performance", "server default", "no", "Maximum MCP response payload bytes.", Location("src/CodeIndex/Mcp/McpServer.cs", 191, "McpServer")),
        Item("CDIDX_MCP_KEEP_ALIVE_INTERVAL_S", "mcp", SensitivityPublic, "performance", "disabled", "no", "MCP keep-alive notification interval.", Location("src/CodeIndex/Mcp/McpServer.cs", 192, "McpServer")),
        Item("CDIDX_MCP_SAMPLING", "mcp", SensitivityPublic, "security", "disabled", "no", "MCP sampling feature gate.", Location("src/CodeIndex/Mcp/McpServer.cs", 194, "McpServer")),

        Item(McpAuthenticatorFactory.AuthTokenEnvVar, "mcp", SensitivitySecret, "security", "local stdio auth or no HTTP bearer auth", "no", "Generic MCP bearer token.", Location("src/CodeIndex/Mcp/McpAuthentication.cs", 264, "McpAuthenticatorFactory")),
        Item(ProgramRunner.McpHttpTokenEnvVar, "mcp", SensitivitySecret, "security", "falls back to CDIDX_MCP_AUTH_TOKEN", "no", "HTTP MCP bearer token override.", Location("src/CodeIndex/Cli/ProgramRunner.cs", 2081, "ProgramRunner")),

        Item("CDIDX_GITHUB_TOKEN", "github", SensitivitySecret, "security", "GitHub submission disabled", "no", "GitHub token used only for explicit suggestion issue submission.", Location("src/CodeIndex/Cli/GitHubIssueReporter.cs", 35, "GitHubIssueReporter")),
        Item("CDIDX_GITHUB_SUBMIT_TIMEOUT_SECONDS", "github", SensitivityPublic, "performance", "10", "no", "GitHub suggestion submission timeout in seconds.", Location("src/CodeIndex/Cli/GitHubIssueReporter.cs", 53, "GitHubIssueReporter")),
        Item(GitHubHttpClientFactory.ProxyDefaultCredentialsEnvironmentVariable, "github", SensitivityPublic, "security", "disabled", "no", "Allow default proxy credentials for GitHub HTTP calls.", Location("src/CodeIndex/Cli/GitHubHttpClientFactory.cs", 8, "GitHubHttpClientFactory")),

        Item(ExtractorPluginRegistry.TrustWorkspacePluginsEnvironmentVariable, "plugins", SensitivityPublic, "security", "untrusted", "no", "Trust workspace plugin assemblies after path safety checks.", Location("src/CodeIndex/Indexer/Extensibility/ExtractorPluginRegistry.cs", 9, "ExtractorPluginRegistry")),
        Item(PostExtractionHookRunner.HooksDirectoryEnvironmentVariable, "plugins", SensitivityPublic, "io", "user config hooks directory", "no", "Override post-extraction hooks directory.", Location("src/CodeIndex/Indexer/Hooks/PostExtractionHooks.cs", 38, "PostExtractionHookRunner")),
        Item(PostExtractionHookRunner.CallbackBudgetEnvironmentVariable, "plugins", SensitivityPublic, "performance", "5 seconds", "no", "Post-extraction hook callback budget in milliseconds.", Location("src/CodeIndex/Indexer/Hooks/PostExtractionHooks.cs", 39, "PostExtractionHookRunner")),
        Item(PostExtractionHookRunner.DiscoveryLimitEnvironmentVariable, "plugins", SensitivityPublic, "performance", "128 DLLs", "no", "Post-extraction hook discovery DLL limit.", Location("src/CodeIndex/Indexer/Hooks/PostExtractionHooks.cs", 40, "PostExtractionHookRunner")),
        Item(PostExtractionHookRunner.DiscoveryMaxBytesEnvironmentVariable, "plugins", SensitivityPublic, "performance", "64 MiB", "no", "Post-extraction hook discovery byte budget.", Location("src/CodeIndex/Indexer/Hooks/PostExtractionHooks.cs", 41, "PostExtractionHookRunner")),

        Item("CLICOLOR_FORCE", "terminal", SensitivityPublic, "display", "auto", "no", "Force color output.", Location("src/CodeIndex/Cli/ConsoleUi.cs", 2234, "ConsoleUi")),
        Item("NO_COLOR", "terminal", SensitivityPublic, "display", "auto", "no", "Disable color output.", Location("src/CodeIndex/Cli/ConsoleUi.cs", 2241, "ConsoleUi")),
        Item("CLICOLOR", "terminal", SensitivityPublic, "display", "auto", "no", "Disable color output when set to 0.", Location("src/CodeIndex/Cli/ConsoleUi.cs", 2245, "ConsoleUi")),
        Item("CDIDX_COLOR_PALETTE", "terminal", SensitivityPublic, "display", "auto", "no", "Override ANSI color palette.", Location("src/CodeIndex/Cli/ConsoleUi.cs", 1941, "ConsoleUi")),
        Item(ConsoleUi.DisableProgressEnvironmentVariable, "terminal", SensitivityPublic, "display", "progress enabled", "no", "Disable animated progress output.", Location("src/CodeIndex/Cli/ConsoleUi.cs", 385, "ConsoleUi")),
        Item(ConsoleUi.PrefersReducedMotionEnvironmentVariable, "terminal", SensitivityPublic, "display", "progress enabled", "no", "Disable animation when truthy.", Location("src/CodeIndex/Cli/ConsoleUi.cs", 392, "ConsoleUi")),
        Item("CDIDX_ASCII", "terminal", SensitivityPublic, "display", "auto unicode", "no", "Force ASCII glyphs.", Location("src/CodeIndex/Cli/ConsoleUi.cs", 2270, "ConsoleUi")),
        Item("NO_UNICODE", "terminal", SensitivityPublic, "display", "auto unicode", "no", "Force ASCII glyphs.", Location("src/CodeIndex/Cli/ConsoleUi.cs", 2274, "ConsoleUi")),
        Item("TERM", "terminal", SensitivityPublic, "display", "auto", "no", "Terminal identity and dumb-terminal detection.", Location("src/CodeIndex/Cli/ConsoleUi.cs", 2174, "ConsoleUi")),
        Item("TERM_PROGRAM", "terminal", SensitivityPublic, "display", "auto", "no", "Terminal capability hint.", Location("src/CodeIndex/Cli/ConsoleUi.cs", 2171, "ConsoleUi")),
        Item("WT_SESSION", "terminal", SensitivityPublic, "display", "auto", "no", "Windows Terminal capability hint.", Location("src/CodeIndex/Cli/ConsoleUi.cs", 2167, "ConsoleUi")),
        Item("WT_PROFILE_ID", "terminal", SensitivityPublic, "display", "auto", "no", "Windows Terminal capability hint.", Location("src/CodeIndex/Cli/ConsoleUi.cs", 2169, "ConsoleUi")),
        Item("CI", "terminal", SensitivityPublic, "display", "auto", "no", "Disable interactive terminal behavior in CI.", Location("src/CodeIndex/Cli/ConsoleUi.cs", 2184, "ConsoleUi")),
        Item("COLUMNS", "terminal", SensitivityPublic, "display", "console width fallback", "no", "Console width override.", Location("src/CodeIndex/Cli/ConsoleUi.cs", 2318, "ConsoleUi")),
        Item("LC_ALL", "terminal", SensitivityPublic, "display", "current culture", "no", "Locale and ambiguous-width detection.", Location("src/CodeIndex/Database/LineWidthFormatter.cs", 238, "LineWidthFormatter"), Location("src/CodeIndex/Cli/ConsoleUi.cs", 2286, "ConsoleUi")),
        Item("LC_CTYPE", "terminal", SensitivityPublic, "display", "current culture", "no", "Locale and ambiguous-width detection.", Location("src/CodeIndex/Database/LineWidthFormatter.cs", 240, "LineWidthFormatter"), Location("src/CodeIndex/Cli/ConsoleUi.cs", 2287, "ConsoleUi")),
        Item("LANG", "terminal", SensitivityPublic, "display", "current culture", "no", "Locale and ambiguous-width detection.", Location("src/CodeIndex/Database/LineWidthFormatter.cs", 242, "LineWidthFormatter"), Location("src/CodeIndex/Cli/ConsoleUi.cs", 2288, "ConsoleUi")),
        Item("AT_BRIDGE_TYPE", "terminal", SensitivityPublic, "display", "auto unicode", "no", "Accessibility hint that disables Unicode glyphs.", Location("src/CodeIndex/Cli/ConsoleUi.cs", 2278, "ConsoleUi")),
        Item("ACCESSIBILITY_ENABLED", "terminal", SensitivityPublic, "display", "auto unicode", "no", "Accessibility hint that disables Unicode glyphs.", Location("src/CodeIndex/Cli/ConsoleUi.cs", 2282, "ConsoleUi")),
    ];

    private static EnvironmentVariableInventoryItem Item(
        string name,
        string category,
        string sensitivity,
        string policy,
        string defaultBehavior,
        string configFileSupported,
        string description,
        params EnvironmentVariableInventoryLocation[] locations) =>
        new(name, category, sensitivity, policy, defaultBehavior, configFileSupported, description, locations);

    private static EnvironmentVariableInventoryLocation Location(string path, int line, string member)
        => new(path, line, member);
}

internal sealed record EnvironmentVariableInventoryItem(
    [property: JsonPropertyName("name")] string Name,
    [property: JsonPropertyName("category")] string Category,
    [property: JsonPropertyName("sensitivity")] string Sensitivity,
    [property: JsonPropertyName("policy")] string Policy,
    [property: JsonPropertyName("default_behavior")] string DefaultBehavior,
    [property: JsonPropertyName("config_file_supported")] string ConfigFileSupported,
    [property: JsonPropertyName("description")] string Description,
    [property: JsonPropertyName("locations")] IReadOnlyList<EnvironmentVariableInventoryLocation> Locations);

internal sealed record EnvironmentVariableInventoryLocation(
    [property: JsonPropertyName("path")] string Path,
    [property: JsonPropertyName("line")] int Line,
    [property: JsonPropertyName("member")] string Member);

using System.Collections.Generic;
using System.Linq;

namespace CodeIndex.Cli;

/// <summary>
/// Single source of truth for cdidx CLI flags. Drives the unsupported-option allowlists
/// (`TryWriteUnsupportedOptionError`, `ValidateFindArgs`) and the generated shell
/// completion scripts (bash / zsh / fish / PowerShell). Adding a new flag means appending one row
/// here instead of editing every completion template;
/// `CliFlagSchemaTests` fails closed if the schema and the per-command allowlists drift
/// apart (#1570).
/// cdidx CLI フラグの単一情報源。未対応オプション拒否リスト
/// (`TryWriteUnsupportedOptionError` / `ValidateFindArgs`) と bash / zsh / fish / PowerShell の
/// 補完スクリプト生成を駆動する。新しいフラグを追加しても各補完テンプレートを
/// 個別に同期する必要はなく、スキーマとコマンド別 allowlist がずれた場合は
/// `CliFlagSchemaTests` のカバレッジ検査が失敗する (#1570)。
/// </summary>
internal sealed record CliFlag
{
    public required string Name { get; init; }
    public string? ShortName { get; init; }
    public string? ValuePlaceholder { get; init; }
    public required string Description { get; init; }

    /// <summary>
    /// Commands for which this flag is "primary" — accepted by the parser AND surfaced
    /// in shell completions.
    /// このフラグが「主用途」となるコマンド集合。パーサが受理し、シェル補完にも提示される。
    /// </summary>
    public required IReadOnlySet<string> Commands { get; init; }

    /// <summary>
    /// Commands for which the parser accepts the flag (typically to emit a friendlier
    /// error like "use --exact-substring on search instead of --exact-name") but for
    /// which shell completions deliberately omit it to avoid recommending the wrong
    /// flag to the user.
    /// パーサが受理するもののシェル補完では出さないコマンド集合。`search` 上の
    /// `--exact-name` のように、より親切なエラーを返すために allowlist には載るが
    /// 補完候補としては推奨しないケースで使う。
    /// </summary>
    public IReadOnlySet<string> AlsoAcceptedBy { get; init; } = new HashSet<string>(StringComparer.Ordinal);

    public bool IsValueBearing => ValuePlaceholder is not null;
    public bool AppliesTo(string command) => Commands.Contains(command);
    public bool IsAcceptedBy(string command) => AppliesTo(command) || AlsoAcceptedBy.Contains(command);
}

internal static class CliFlagSchema
{
    // Authoritative list of subcommands. Mirrored by ConsoleUi.Commands; tests guard parity.
    // サブコマンド一覧の正本。ConsoleUi.Commands と一致することをテストで確認する。
    public static IReadOnlyList<string> AllCommands { get; } =
    [
        "index", "backfill-fold", "optimize", "search", "definition", "goto", "references", "callers", "callees",
        "symbols", "files", "find", "excerpt", "map", "inspect", "outline", "status", "validate-config",
        "validate", "deps", "impact", "unused", "hotspots", "languages", "batch", "mcp", "completions", "db", "vacuum", "report", "license", "upgrade",
    ];

    // Commands that accept the `--` end-of-options marker so a user can pass a literal
    // query token starting with `-`. `find` reroutes through `ValidateFindArgs`; everything
    // else uses `TryWriteUnsupportedOptionError`'s allowlist.
    // クエリ先頭が `-` で始まる場合に `--` end-of-options を受け付けるコマンド集合。
    private static readonly string[] PassthroughCommands =
    [
        "search", "definition", "goto", "references", "callers", "callees", "symbols", "files", "inspect", "impact",
    ];

    private static readonly string[] QueryCommands =
    [
        "search", "definition", "goto", "references", "callers", "callees",
        "symbols", "files", "find", "inspect", "impact",
    ];

    private static readonly string[] LimitCapableCommands =
    [
        "search", "definition", "goto", "references", "callers", "callees", "symbols",
        "files", "find", "map", "inspect", "deps", "impact", "unused", "hotspots",
    ];

    private static readonly string[] LangCapableCommands = LimitCapableCommands;

    private static readonly string[] PathFilterCommands =
    [
        "search", "definition", "goto", "references", "callers", "callees", "symbols", "files",
        "find", "map", "inspect", "deps", "impact", "unused", "hotspots", "validate",
    ];

    private static readonly string[] ExcludeFilterCommands =
    [
        "search", "definition", "references", "callers", "callees", "symbols", "files",
        "find", "map", "inspect", "deps", "impact", "unused", "hotspots",
    ];

    private static readonly string[] CountCommands =
    [
        "search", "definition", "references", "callers", "callees", "symbols",
        "files", "find", "impact", "unused", "hotspots",
    ];
    private static readonly string[] StrictNotFoundCommands =
    [
        "search", "definition", "references", "callers", "callees", "symbols",
        "files", "find", "excerpt", "map", "inspect", "deps", "impact", "unused", "hotspots",
    ];

    private static readonly string[] KindCommands =
    [
        "definition", "goto", "references", "callers", "callees", "symbols", "unused", "hotspots", "validate",
    ];
    private static readonly string[] VisibilityCommands =
    [
        "definition", "symbols", "unused", "hotspots",
    ];
    private static readonly string[] RawKindsCommands = ["callers", "callees"];
    private static readonly string[] RankByCommands = ["callers", "callees"];
    private static readonly string[] ByBucketCommands = ["unused"];
    private static readonly string[] AllResultCommands = ["goto"];

    private static readonly string[] SinceCommands = ["search", "definition", "symbols", "files"];
    private static readonly string[] ByteFormatCommands = ["files", "map"];
    private static readonly string[] EntrypointConfidenceCommands = ["map"];
    private static readonly string[] MapSectionCommands = ["map"];
    private static readonly string[] DependencyCycleCommands = ["deps"];

    // `--exact` is the legacy shorthand that every name-resolution command accepts.
    // `--exact` は名前解決系の全コマンドで受け付けるレガシー shorthand。
    private static readonly string[] ExactCommands =
    [
        "search", "definition", "references", "callers", "callees", "symbols", "find", "inspect",
    ];

    private static readonly string[] ExactNameCommands =
    [
        "definition", "goto", "references", "callers", "callees", "symbols", "inspect",
    ];

    // `--exact-substring` is only meaningful on `search`; other name commands accept it
    // for cross-command error parity but the shell completion hides it.
    // `--exact-substring` は実用上 `search` のみで意味を持ち、他コマンドはエラー互換のため
    // パーサで受理するだけ。補完では `search` 以外には出さない。
    private static readonly string[] ExactSubstringAccepted =
    [
        "definition", "goto", "references", "callers", "callees", "symbols", "inspect",
    ];

    private static readonly string[] BodyCommands = ["definition", "references", "callers", "callees", "impact", "inspect"];

    private static readonly string[] MaxLineWidthCommands =
    [
        "search", "references", "callers", "callees", "find", "excerpt", "impact", "inspect",
    ];

    private static readonly string[] DbPathCommands =
    [
        "index", "backfill-fold", "optimize", "search", "definition", "goto", "references", "callers", "callees",
        "symbols", "files", "find", "excerpt", "map", "inspect", "outline", "status",
        "validate", "deps", "impact", "unused", "hotspots", "db", "vacuum", "report", "batch", "mcp",
    ];

    private static readonly string[] WorkspaceDbCommands = ["deps"];

    private static readonly string[] DataDirCommands =
    [
        "index", "search", "definition", "goto", "references", "callers", "callees",
        "symbols", "files", "find", "excerpt", "map", "inspect", "outline", "status",
        "validate", "deps", "impact", "unused", "hotspots", "batch",
    ];

    private static readonly string[] ReadOnlyDbCommands =
    [
        "search", "definition", "goto", "references", "callers", "callees",
        "symbols", "files", "find", "excerpt", "map", "inspect", "outline", "status",
        "validate", "deps", "impact", "unused", "hotspots",
    ];

    private static readonly string[] JsonCommands =
    [
        "index", "backfill-fold", "optimize", "vacuum", "search", "definition", "goto", "references", "callers", "callees",
        "symbols", "files", "find", "excerpt", "map", "inspect", "outline", "status",
        "validate", "deps", "impact", "unused", "hotspots", "languages", "db", "report",
    ];

    private static readonly string[] FormatCommands =
    [
        "search", "definition", "references", "callers", "callees", "find", "validate", "deps",
    ];

    private static readonly string[] ProfileCommands =
    [
        "search", "definition", "references", "callers", "callees", "symbols", "files",
        "find", "excerpt", "map", "inspect", "outline", "status", "validate", "deps",
        "impact", "unused", "hotspots",
    ];

    private static readonly string[] VerboseQueryCommands = ProfileCommands;
    private static readonly string[] TraceCommands = ProfileCommands;

    public static IReadOnlyList<CliFlag> All { get; } = BuildAll();

    private static IReadOnlyList<CliFlag> BuildAll()
    {
        return new List<CliFlag>
        {
            new() { Name = "--db", ValuePlaceholder = "<path>", Description = "Database path", Commands = Set(DbPathCommands) },
            new() { Name = "--read-only", Description = "Open the query database as immutable read-only storage", Commands = Set(ReadOnlyDbCommands) },
            new() { Name = "--immutable", Description = "Alias for --read-only", Commands = Set(ReadOnlyDbCommands) },
            new() { Name = "--workspace-db", ValuePlaceholder = "<path>", Description = "Additional workspace member database path for dependency aggregation", Commands = Set(WorkspaceDbCommands) },
            new() { Name = "--data-dir", ValuePlaceholder = "<dir>", Description = "Directory containing codeindex.db; overrides CDIDX_DATA_DIR/XDG/workspace defaults", Commands = Set(DataDirCommands) },
            new() { Name = "--json", Description = "JSON output; search also accepts --json=array for a single JSON array", Commands = Set(JsonCommands) },
            new() { Name = "--format", ValuePlaceholder = "<text|json|count|compact|csv|tsv|lsp|qf|sarif>", Description = "Standard output format for token budgets, editor integrations, and CI", Commands = Set(FormatCommands) },
            new() { Name = "--quiet", ShortName = "-q", Description = "Suppress informational stderr output; errors still print", Commands = Set(AllCommands.ToArray()) },
            new() { Name = "--silent", Description = "Alias for --quiet", Commands = Set(AllCommands.ToArray()) },
            new() { Name = "--profile", Description = "Emit SQL timing and EXPLAIN QUERY PLAN profile JSON after the normal result", Commands = Set(ProfileCommands) },
            new() { Name = "--verbose", Description = "Emit query debug diagnostics to stderr, or _debug JSON when combined with --json", Commands = Set(VerboseQueryCommands.Concat(new[] { "index" }).ToArray()) },
            new() { Name = "--notify", ValuePlaceholder = "<auto|bell|osc9|desktop|none>", Description = "Signal long index completion; desktop currently emits OSC 9 terminal notification", Commands = Set("index") },
            new() { Name = "--slow-query-ms", ValuePlaceholder = "<n>", Description = "Log profiled SQL statements at or above this millisecond threshold", Commands = Set(ProfileCommands) },
            new() { Name = "--trace", ValuePlaceholder = "<none|stderr|file>", Description = "Emit one structured JSON query trace line to stderr or a daily log file", Commands = Set(TraceCommands) },
            new() { Name = "--limit", ValuePlaceholder = "<n>", Description = "Max results", Commands = Set(LimitCapableCommands) },
            new() { Name = "--top", ValuePlaceholder = "<n>", Description = "Max results", Commands = Set(LimitCapableCommands) },
            new() { Name = "--lang", ValuePlaceholder = "<lang>", Description = "Filter by language", Commands = Set(LangCapableCommands) },
            new() { Name = "--path", ValuePlaceholder = "<glob>", Description = "Path filter", Commands = Set(PathFilterCommands) },
            new() { Name = "--project", ValuePlaceholder = "<name|path>", Description = "Filter to a .sln/.csproj project", Commands = Set(PathFilterCommands.Concat(new[] { "index" }).ToArray()) },
            new() { Name = "--solution", ValuePlaceholder = "<path>", Description = "Solution file used to resolve --project", Commands = Set(PathFilterCommands.Concat(new[] { "index" }).ToArray()) },
            new() { Name = "--exclude-path", ValuePlaceholder = "<glob>", Description = "Exclude path", Commands = Set(ExcludeFilterCommands) },
            new() { Name = "--exclude-tests", Description = "Exclude tests", Commands = Set(ExcludeFilterCommands) },
            new() { Name = "--include-generated", Description = "Include generated files", Commands = Set(ExcludeFilterCommands) },
            new() { Name = "--kind", ValuePlaceholder = "<kind>", Description = "Filter by kind", Commands = Set(KindCommands) },
            new() { Name = "--visibility", ValuePlaceholder = "<visibility[,visibility]>", Description = "Filter by symbol visibility", Commands = Set(VisibilityCommands) },
            new() { Name = "--exclude-visibility", ValuePlaceholder = "<visibility[,visibility]>", Description = "Exclude symbol visibility", Commands = Set(VisibilityCommands) },
            new() { Name = "--by-bucket", Description = "Unused: include per-bucket grouped result arrays in JSON output", Commands = Set(ByBucketCommands) },
            new() { Name = "--all", Description = "goto: return all matching LSP locations instead of requiring a single target", Commands = Set(AllResultCommands) },
            new() { Name = "--rank-by", ValuePlaceholder = "<weighted|count|kind>", Description = "Rank callers/callees by weighted structural score, raw count, or kind bucket", Commands = Set(RankByCommands) },
            new() { Name = "--raw-kinds", Description = "Show raw reference kinds instead of logical graph kinds", Commands = Set(RawKindsCommands) },
            new() { Name = "--count", Description = "Count only", Commands = Set(CountCommands) },
            new() { Name = "--strict-not-found", Description = "Return exit code 2 when a valid query has zero rows", Commands = Set(StrictNotFoundCommands) },
            new() { Name = "--strict", Description = "Return exit code 4 when impact preconditions are unmet", Commands = Set("impact") },
            new() { Name = "--since", ValuePlaceholder = "<datetime>", Description = "Filter by modified-since timestamp", Commands = Set(SinceCommands) },
            new() { Name = "--bytes", Description = "Show raw byte counts in human output", Commands = Set(ByteFormatCommands) },
            new() { Name = "--min-entrypoint-confidence", ValuePlaceholder = "<0.0..1.0>", Description = "Map: omit entrypoint candidates below this confidence", Commands = Set(EntrypointConfidenceCommands) },
            new() { Name = "--sections", ValuePlaceholder = "<tree,languages,hotspots,metrics>", Description = "Map: comma-separated response sections to include", Commands = Set(MapSectionCommands) },
            new() { Name = "--cycles", Description = "Deps: return dependency cycles instead of edge rows", Commands = Set(DependencyCycleCommands) },
            new() { Name = "--query", ValuePlaceholder = "<query>", Description = "Literal query", Commands = Set(QueryCommands) },
            new() { Name = "--body", Description = "Include body", Commands = Set(BodyCommands) },
            new() { Name = "--exact", Description = "Backward-compatible exact shorthand", Commands = Set(ExactCommands) },
            new() { Name = "--regex", Description = "Use regular expression matching", Commands = Set("find") },
            new() { Name = "--exact-name", Description = "Exact symbol-name equality", Commands = Set(ExactNameCommands), AlsoAcceptedBy = Set("search") },
            new() { Name = "--exact-substring", Description = "Search-only exact substring match", Commands = Set("search"), AlsoAcceptedBy = Set(ExactSubstringAccepted) },
            new() { Name = "--prefix", Description = "Trailing-asterisk prefix shorthand", Commands = Set("search") },
            new() { Name = "--require-before", ValuePlaceholder = "<query>", Description = "Search: require a nearby guard query before each primary match", Commands = Set("search") },
            new() { Name = "--require-after", ValuePlaceholder = "<query>", Description = "Search: require a nearby guard query after each primary match", Commands = Set("search") },
            new() { Name = "--reject-before", ValuePlaceholder = "<query>", Description = "Search: reject primary matches with a nearby guard query before them", Commands = Set("search") },
            new() { Name = "--reject-after", ValuePlaceholder = "<query>", Description = "Search: reject primary matches with a nearby guard query after them", Commands = Set("search") },
            new() { Name = "--guard-window", ValuePlaceholder = "<n>", Description = "Search: line window for require/reject guard queries", Commands = Set("search") },
            new() { Name = "--no-progress", Description = "Disable animated progress and spinner output", Commands = Set(AllCommands.ToArray()) },
            new() { Name = "--name", ValuePlaceholder = "<name>", Description = "Exact symbol name", Commands = Set("symbols") },
            new() { Name = "--max-line-width", ValuePlaceholder = "<n>", Description = "Clamp long single-line payloads (0 disables clamping)", Commands = Set(MaxLineWidthCommands) },
            new() { Name = "--snippet-lines", ValuePlaceholder = "<n>", Description = "Snippet length", Commands = Set("search", "find", "references", "callers", "callees", "impact") },
            new() { Name = "--snippet-focus", ValuePlaceholder = "<leftmost|quality|proximity>", Description = "Search snippet long-line focus mode", Commands = Set("search") },
            new() { Name = "--fts", Description = "Raw FTS5 syntax", Commands = Set("search") },
            new() { Name = "--no-dedup", Description = "Show duplicate chunks", Commands = Set("search") },
            new() { Name = "--no-visibility-rank", Description = "Keep legacy search ranking without symbol visibility weighting", Commands = Set("search") },
            new() { Name = "--before", ValuePlaceholder = "<n>", Description = "Context lines before", Commands = Set("find", "excerpt") },
            new() { Name = "--after", ValuePlaceholder = "<n>", Description = "Context lines after", Commands = Set("find", "excerpt") },
            new() { Name = "--start", ValuePlaceholder = "<line>", Description = "Start line", Commands = Set("excerpt") },
            new() { Name = "--start-line", ValuePlaceholder = "<line>", Description = "Alias for --start", Commands = Set("excerpt") },
            new() { Name = "--end", ValuePlaceholder = "<line>", Description = "End line", Commands = Set("excerpt") },
            new() { Name = "--end-line", ValuePlaceholder = "<line>", Description = "Alias for --end", Commands = Set("excerpt") },
            new() { Name = "--focus-line", ValuePlaceholder = "<line>", Description = "Focused line to keep visible when clamping", Commands = Set("find", "excerpt") },
            new() { Name = "--focus-column", ValuePlaceholder = "<n>", Description = "Focused column to keep visible when clamping", Commands = Set("find", "excerpt") },
            new() { Name = "--focus-length", ValuePlaceholder = "<n>", Description = "Focused span width when clamping", Commands = Set("excerpt") },
            new() { Name = "--max-hops", ValuePlaceholder = "<n>", Description = "Impact: max BFS hops", Commands = Set("impact") },
            new() { Name = "--depth", ValuePlaceholder = "<n>", Description = "Map: cap module depth; impact: deprecated alias for --max-hops", Commands = Set("impact", "map") },
            new() { Name = "--with-paths", Description = "Impact: include shortest call chains per caller", Commands = Set("impact") },
            new() { Name = "--reverse", Description = "Reverse direction (show dependents)", Commands = Set("deps") },
            new() { Name = "--group-by", ValuePlaceholder = "<symbol|file|statement>", Description = "Hotspots: choose grouping unit", Commands = Set("hotspots") },
            new() { Name = "--group-by-name", Description = "Hotspots: collapse same-name rows; count is name/kind groups", Commands = Set("hotspots") },
            new() { Name = "--check", Description = "Verify status freshness/readiness", Commands = Set("status") },
            new() { Name = "--config", Description = "Print effective configuration with source attribution", Commands = Set("status") },
            new() { Name = "--stale-after", ValuePlaceholder = "<duration>", Description = "Status: freshness age threshold (e.g. 30m, 2h, 7d)", Commands = Set("status") },
            new() { Name = "--explain", ValuePlaceholder = "<field>", Description = "Explain one status readiness field", Commands = Set("status") },
            new() { Name = "--log-path", Description = "Print the active persistent log directory", Commands = Set("status") },
            new() { Name = "--check-updates", Description = "Check whether a newer cdidx release is available", Commands = Set("status", "upgrade") },
            new() { Name = "--check-only", Description = "Upgrade: only report whether an upgrade is available", Commands = Set("upgrade") },
            new() { Name = "--channel", ValuePlaceholder = "<stable|beta>", Description = "Upgrade channel selector (reserved)", Commands = Set("upgrade") },
            new() { Name = "--prerelease", Description = "Upgrade: include prerelease versions (reserved)", Commands = Set("upgrade") },
            new() { Name = "--integrity-check", Description = "Run PRAGMA integrity_check on the database", Commands = Set("db") },
            new() { Name = "--rebuild", Description = "Delete existing DB and rebuild from scratch", Commands = Set("index") },
            new() { Name = "--optimize", Description = "Optimize the existing FTS5 table without scanning files", Commands = Set("index") },
            new() { Name = "--dry-run", Description = "Preview without writing", Commands = Set("index", "backfill-fold") },
            new() { Name = "--no-checkpoint", Description = "Skip the automatic DB checkpoint before maintenance", Commands = Set("backfill-fold") },
            new() { Name = "--force", Description = "Bypass the per-database index lock", Commands = Set("index") },
            new() { Name = "--duration-format", ValuePlaceholder = "<auto|seconds|hms>", Description = "Index elapsed time display format", Commands = Set("index") },
            new() { Name = "--max-file-bytes", ValuePlaceholder = "<bytes>", Description = "Override the per-file indexing size limit", Commands = Set("index") },
            new() { Name = "--max-symbols-per-file", ValuePlaceholder = "<n>", Description = "Skip file content, symbols, and references when one file emits too many symbols", Commands = Set("index") },
            new() { Name = "--parallelism", ValuePlaceholder = "<n>", Description = "Full-scan extraction worker count (default: CPU count capped at 16; also honors CDIDX_INDEX_PARALLELISM)", Commands = Set("index") },
            new() { Name = "--memory-trace", Description = "Include phase memory samples in index JSON output", Commands = Set("index") },
            new() { Name = "--commits", ValuePlaceholder = "<commit-ref>", Description = "Update files changed in given git commits", Commands = Set("index") },
            new() { Name = "--changed-between", ValuePlaceholder = "<old-ref> <new-ref>", Description = "Update files changed between two git refs", Commands = Set("index") },
            new() { Name = "--files", ValuePlaceholder = "<path>", Description = "Update only the specified files", Commands = Set("index") },
            new() { Name = "--watch", Description = "Continuous reindex on file changes (rejects --commits / --changed-between / --files / --dry-run)", Commands = Set("index") },
            new() { Name = "--debounce", ValuePlaceholder = "<ms>", Description = "Watch only: coalesce file events into one update after <ms> of quiet (default 500)", Commands = Set("index") },
            new() { Name = "--output", ShortName = "-o", ValuePlaceholder = "<path>", Description = "Output bundle path", Commands = Set("report") },
            new() { Name = "--no-log", Description = "Exclude global tool log from bundle", Commands = Set("report") },
            new() { Name = "--include-args", Description = "Include args in bundle log", Commands = Set("report") },
            new() { Name = "--log-lines", ValuePlaceholder = "<n>", Description = "Number of log lines to include in bundle", Commands = Set("report") },
            new() { Name = "--transport", ValuePlaceholder = "<stdio|http>", Description = "MCP transport", Commands = Set("mcp") },
            new() { Name = "--http-listen", ValuePlaceholder = "<host:port>", Description = "MCP HTTP listen address", Commands = Set("mcp") },
        };
    }

    /// <summary>
    /// Names of every flag the parser must accept for a given command, including the
    /// `--` end-of-options marker where applicable.
    /// 指定コマンドでパーサが受理すべき全フラグ名（必要に応じて `--` end-of-options も含む）。
    /// </summary>
    public static IReadOnlySet<string> GetAcceptedFlagNamesForCommand(string command)
    {
        var names = new HashSet<string>(StringComparer.Ordinal);
        foreach (var flag in All)
        {
            if (flag.IsAcceptedBy(command))
                names.Add(flag.Name);
        }
        if (PassthroughCommands.Contains(command))
            names.Add("--");
        return names;
    }

    /// <summary>
    /// Flags that should be surfaced in shell completion for the given command — that
    /// is, only the flags whose `Commands` set includes this command. Used by bash /
    /// zsh / fish completion generators.
    /// 指定コマンドの補完候補に出すべきフラグ集合（`AlsoAcceptedBy` は含めない）。
    /// </summary>
    public static IReadOnlyList<CliFlag> GetCompletionFlagsForCommand(string command)
    {
        return All.Where(f => f.AppliesTo(command)).ToList();
    }

    /// <summary>
    /// Same allowlist as <see cref="GetAcceptedFlagNamesForCommand"/> but partitioned
    /// into value-bearing options vs flag-only options, for parsers that need to know
    /// whether to consume the next token. Used by <c>ValidateFindArgs</c>. The `--`
    /// end-of-options marker is excluded since it is consumed before validation.
    /// `--` を除いた受理オプションを「値を取る」「フラグ単体」の 2 集合に分割して返す。
    /// </summary>
    public static (HashSet<string> WithValues, HashSet<string> FlagOnly) GetParserFlagsPartitionedByValueBearing(string command)
    {
        var withValues = new HashSet<string>(StringComparer.Ordinal);
        var flagOnly = new HashSet<string>(StringComparer.Ordinal);
        foreach (var flag in All)
        {
            if (!flag.IsAcceptedBy(command))
                continue;
            if (flag.IsValueBearing)
                withValues.Add(flag.Name);
            else
                flagOnly.Add(flag.Name);
        }
        return (withValues, flagOnly);
    }

    private static HashSet<string> Set(params string[] items) =>
        new(items, StringComparer.Ordinal);
}

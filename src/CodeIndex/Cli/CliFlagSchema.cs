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
    public required IReadOnlySet<string> PrimaryCommands { get; init; }

    /// <summary>
    /// Whether this flag is accepted before a subcommand and should be surfaced in
    /// top-level completion/help contracts.
    /// サブコマンド前に受理され、トップレベル補完 / help 契約にも出すフラグかどうか。
    /// </summary>
    public bool TopLevel { get; init; }

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
    public bool AppliesTo(string command) => PrimaryCommands.Contains(command);
    public bool IsAcceptedBy(string command) => AppliesTo(command) || AlsoAcceptedBy.Contains(command);
}

internal static class CliFlagSchema
{
    // Authoritative top-level command inventory comes from the shared command catalog.
    // top-level command 一覧の正本は共有 command catalog に置く。
    public static IReadOnlyList<string> AllCommands { get; } = CliCommandMetadata.PublicCommandNames;

    // These commands use the shared flag parser as the authoritative source for their
    // per-command option inventory. Command families with dedicated/nested parsers keep
    // their exact syntax in CommandUsageLines until their subcommand metadata is complete;
    // emitting a partial parent-command flag list would be actively misleading.
    // 共有 flag parser が command ごとの option 一覧の正本になっている command 集合。
    // 専用 parser / nested parser を持つ command family は subcommand metadata が揃うまで
    // CommandUsageLines の正確な構文を使い、誤解を招く不完全な親 command の一覧は出さない。
    private static readonly IReadOnlySet<string> AuthoritativeHelpOptionCommands = Set(
        "index", "backfill-fold", "optimize", "vacuum",
        "search", "recipes", "audit", "definition", "goto", "references", "callers", "callees",
        "symbols", "files", "find", "excerpt", "map", "inspect", "outline", "status",
        "validate", "deps", "impact", "unused", "hotspots", "languages");

    public static bool HasAuthoritativeHelpOptions(string command) =>
        AuthoritativeHelpOptionCommands.Contains(command);

    // Commands that accept the `--` end-of-options marker so a user can pass a literal
    // query token starting with `-`. `find` reroutes through `ValidateFindArgs`; everything
    // else uses `TryWriteUnsupportedOptionError`'s allowlist.
    // クエリ先頭が `-` で始まる場合に `--` end-of-options を受け付けるコマンド集合。
    private static readonly string[] PassthroughCommands =
    [
        "search", "definition", "goto", "references", "callers", "callees", "symbols", "files", "excerpt", "inspect", "impact",
    ];

    private static readonly string[] QueryCommands =
    [
        "search", "recipes", "definition", "goto", "references", "callers", "callees",
        "symbols", "files", "find", "inspect", "impact",
    ];

    private static readonly string[] LimitCapableCommands =
    [
        "search", "definition", "goto", "references", "callers", "callees", "symbols",
        "files", "find", "map", "inspect", "outline", "deps", "impact", "unused", "hotspots", "validate", "audit",
    ];

    private static readonly string[] LangCapableCommands =
    [
        "search", "definition", "goto", "references", "callers", "callees", "symbols",
        "files", "find", "map", "inspect", "deps", "impact", "unused", "hotspots",
    ];

    private static readonly string[] PathFilterCommands =
    [
        "search", "definition", "goto", "references", "callers", "callees", "symbols", "files",
        "find", "map", "inspect", "deps", "impact", "unused", "hotspots", "validate",
    ];

    private static readonly string[] ExcludeFilterCommands =
    [
        "search", "definition", "goto", "references", "callers", "callees", "symbols", "files",
        "find", "map", "inspect", "validate", "deps", "impact", "unused", "hotspots",
    ];

    private static readonly string[] CountCommands =
    [
        "search", "definition", "references", "callers", "callees", "symbols",
        "files", "find", "impact", "unused", "hotspots", "audit",
    ];
    private static readonly string[] StrictNotFoundCommands =
    [
        "search", "definition", "references", "callers", "callees", "symbols",
        "files", "find", "excerpt", "map", "inspect", "deps", "impact", "unused", "hotspots",
    ];
    private static readonly string[] AllowPartialCommands = ["search", "symbols", "files", "find", "index"];

    private static readonly string[] KindCommands =
    [
        "definition", "goto", "references", "callers", "callees", "symbols", "inspect", "outline", "unused", "hotspots", "validate",
    ];
    private static readonly string[] SeverityCommands = ["validate"];
    private static readonly string[] VisibilityCommands =
    [
        "definition", "symbols", "unused", "hotspots",
    ];
    private static readonly string[] RawKindsCommands = ["callers", "callees"];
    private static readonly string[] RankByCommands = ["callers", "callees"];
    private static readonly string[] SymbolSortCommands = ["symbols", "outline"];
    private static readonly string[] ByBucketCommands = ["unused"];
    private static readonly string[] UnusedFilterCommands = ["unused"];
    private static readonly string[] BoundedProjectionCommands =
    [
        "definition", "find", "status", "hotspots", "references", "callers", "callees", "impact", "map",
    ];
    private static readonly string[] CursorCommands = ["search", "outline", "unused", "deps", .. BoundedProjectionCommands];
    private static readonly string[] AllResultCommands = ["goto", "find", "unused"];

    private static readonly string[] SinceCommands = ["search", "definition", "symbols", "files", "suggestions"];
    private static readonly string[] ByteFormatCommands = ["files", "map"];
    private static readonly string[] EntrypointConfidenceCommands = ["map"];
    private static readonly string[] MapSectionCommands = ["map"];
    private static readonly string[] SummaryOnlyCommands = ["map", "search", "recipes", "audit", "symbols", "files", "deps", "unused", "hotspots", "languages"];
    private static readonly string[] DependencyCycleCommands = ["deps"];
    private static readonly string[] LanguagesFilterCommands = ["languages"];

    // `--exact` is the legacy shorthand that every name-resolution command accepts.
    // `--exact` は名前解決系の全コマンドで受け付けるレガシー shorthand。
    private static readonly string[] ExactCommands =
    [
        "search", "definition", "references", "callers", "callees", "symbols", "find", "inspect",
    ];

    private static readonly string[] ExactNameCommands =
    [
        "definition", "goto", "references", "callers", "callees", "symbols", "impact", "inspect",
    ];

    // `--exact-substring` is only meaningful on `search`; other name commands accept it
    // for cross-command error parity but the shell completion hides it.
    // `--exact-substring` は実用上 `search` のみで意味を持ち、他コマンドはエラー互換のため
    // パーサで受理するだけ。補完では `search` 以外には出さない。
    private static readonly string[] ExactSubstringAccepted =
    [
        "definition", "goto", "references", "callers", "callees", "symbols", "impact", "inspect",
    ];

    private static readonly string[] BodyCommands = ["definition", "references", "callers", "callees", "impact", "inspect"];
    private static readonly string[] InspectFieldCommands = ["inspect"];
    private static readonly string[] InspectSourceExcerptCommands = ["inspect", "excerpt"];

    private static readonly string[] MaxLineWidthCommands =
    [
        "search", "references", "callers", "callees", "find", "excerpt", "impact", "inspect",
    ];

    private static readonly string[] DbPathCommands =
    [
        "index", "backfill-fold", "optimize", "search", "definition", "goto", "references", "callers", "callees",
        "symbols", "files", "find", "excerpt", "map", "inspect", "outline", "status",
        "validate", "deps", "impact", "unused", "hotspots", "suggestions", "languages", "db", "vacuum", "report", "batch", "mcp",
    ];

    private static readonly string[] WorkspaceDbCommands = ["deps"];

    private static readonly string[] DataDirCommands =
    [
        "index", "search", "definition", "goto", "references", "callers", "callees",
        "symbols", "files", "find", "excerpt", "map", "inspect", "outline", "status",
        "validate", "deps", "impact", "unused", "hotspots", "languages", "batch",
    ];

    private static readonly string[] ReadOnlyDbCommands =
    [
        "search", "definition", "goto", "references", "callers", "callees",
        "symbols", "files", "find", "excerpt", "map", "inspect", "outline", "status",
        "validate", "deps", "impact", "unused", "hotspots", "languages",
    ];

    private static readonly string[] JsonCommands =
    [
        "index", "backfill-fold", "optimize", "vacuum", "search", "recipes", "audit", "definition", "goto", "references", "callers", "callees",
        "symbols", "files", "find", "excerpt", "map", "inspect", "outline", "status",
        "validate", "deps", "impact", "unused", "hotspots", "suggestions", "languages", "db", "report", "upgrade", "doctor", "license",
    ];

    private static readonly string[] CompactJsonCommands = ["map", "inspect", "outline", "symbols", "unused", "status", "hotspots", "impact"];

    private static readonly string[] FormatCommands =
    [
        "search", "recipes", "audit", "definition", "references", "callers", "callees", "symbols", "files", "find", "map", "inspect", "status", "validate", "deps", "impact", "hotspots", "suggestions", "languages",
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
            new() { Name = "--db", ValuePlaceholder = "<path>", Description = "Database path", PrimaryCommands = Set(DbPathCommands) },
            new() { Name = "--read-only", Description = "Open the query database as immutable read-only storage", PrimaryCommands = Set(ReadOnlyDbCommands) },
            new() { Name = "--immutable", Description = "Alias for --read-only", PrimaryCommands = Set(ReadOnlyDbCommands) },
            new() { Name = "--workspace-db", ValuePlaceholder = "<path>", Description = "Additional workspace member database path for dependency aggregation; repeat up to 7 distinct additional DBs", PrimaryCommands = Set(WorkspaceDbCommands) },
            new() { Name = "--data-dir", ValuePlaceholder = "<dir>", Description = "Directory containing codeindex.db; overrides CDIDX_DATA_DIR/XDG/workspace defaults", PrimaryCommands = Set(DataDirCommands) },
            new() { Name = "--json", Description = "JSON output; search/symbols/files/validate also accept --json=array for a single JSON array", PrimaryCommands = Set(JsonCommands) },
            new() { Name = "--json-summary", Description = "Batch: emit one typed result/error record per input plus a final summary", PrimaryCommands = Set("batch") },
            new() { Name = "--max-input-lines", ValuePlaceholder = "<n>", Description = $"Batch: input-line budget (default {QueryCommandRunner.BatchDefaultInputLines}, max {QueryCommandRunner.BatchMaxInputLines})", PrimaryCommands = Set("batch") },
            new() { Name = "--max-output-chars", ValuePlaceholder = "<n>", Description = $"Batch JSON-summary output budget (default {QueryCommandRunner.BatchDefaultTotalOutputChars}, max {QueryCommandRunner.BatchMaxTotalOutputChars})", PrimaryCommands = Set("batch") },
            new() { Name = "--parallel", ValuePlaceholder = "<n>", Description = $"Batch JSON-summary worker count (default 1, max {QueryCommandRunner.BatchMaxParallelism}); results retain input order", PrimaryCommands = Set("batch") },
            new() { Name = "--pretty", Description = CliOutputFormatCapabilities.PrettyDescription, PrimaryCommands = Set(JsonCommands), TopLevel = true },
            new() { Name = "--compact", Description = "AI-oriented compact JSON with capped list sections and truncation metadata", PrimaryCommands = Set(CompactJsonCommands) },
            new() { Name = "--format", ValuePlaceholder = CliOutputFormatCapabilities.FormatValuePlaceholder, Description = CliOutputFormatCapabilities.FormatDescription, PrimaryCommands = Set(FormatCommands) },
            new() { Name = "--quiet", ShortName = "-q", Description = "Suppress informational stderr output; errors still print", PrimaryCommands = Set(AllCommands.ToArray()), TopLevel = true },
            new() { Name = "--silent", Description = "Alias for --quiet", PrimaryCommands = Set(AllCommands.ToArray()), TopLevel = true },
            new() { Name = "--color", ValuePlaceholder = "<auto|always|never>", Description = "Color output mode", PrimaryCommands = Set(), TopLevel = true },
            new() { Name = "--palette", ValuePlaceholder = "<basic|256|truecolor>", Description = "ANSI color palette", PrimaryCommands = Set(), TopLevel = true },
            new() { Name = "--ascii", Description = "Use ASCII progress glyphs", PrimaryCommands = Set(), TopLevel = true },
            new() { Name = "--metrics", ValuePlaceholder = "<path>", Description = "Append command metrics JSONL to a file", PrimaryCommands = Set(), TopLevel = true },
            new() { Name = "--debug-unsafe", Description = "Allow raw debug dumps when CDIDX_DEBUG=unsafe is also set", PrimaryCommands = Set(), TopLevel = true },
            new() { Name = "--strict-version", Description = "Fail when the workspace version pin does not match this binary", PrimaryCommands = Set(), TopLevel = true },
            new() { Name = "--log-format", ValuePlaceholder = "<text|json>", Description = "Persistent stderr log format", PrimaryCommands = Set(), TopLevel = true },
            new() { Name = "--log-retain-count", ValuePlaceholder = "<n>", Description = "Persistent stderr log file retention count", PrimaryCommands = Set(), TopLevel = true },
            new() { Name = "--log-max-size-mb", ValuePlaceholder = "<n>", Description = "Persistent stderr log rotation size cap in MiB", PrimaryCommands = Set(), TopLevel = true },
            new() { Name = "--profile", Description = "Emit SQL timing and EXPLAIN QUERY PLAN profile JSON after the normal result", PrimaryCommands = Set(ProfileCommands) },
            new() { Name = "--verbose", Description = "Emit query debug diagnostics to stderr, or _debug JSON when combined with --json", PrimaryCommands = Set(VerboseQueryCommands.Concat(new[] { "index" }).ToArray()) },
            new() { Name = "--notify", ValuePlaceholder = "<auto|bell|osc9|desktop|none>", Description = "Signal long index completion; desktop currently emits OSC 9 terminal notification", PrimaryCommands = Set("index") },
            new() { Name = "--slow-query-ms", ValuePlaceholder = "<n>", Description = "Log profiled SQL statements at or above this millisecond threshold", PrimaryCommands = Set(ProfileCommands) },
            new() { Name = "--trace", ValuePlaceholder = "<none|stderr|file>", Description = "Emit one structured JSON query trace line to stderr or a daily log file", PrimaryCommands = Set(TraceCommands) },
            new() { Name = "--limit", ValuePlaceholder = "<n>", Description = "Max results", PrimaryCommands = Set(LimitCapableCommands.Concat(new[] { "suggestions" }).ToArray()) },
            new() { Name = "--max-results", ValuePlaceholder = "<n>", Description = "Search alias for --limit", PrimaryCommands = Set("search") },
            new() { Name = "--top", ValuePlaceholder = "<n>", Description = "Max results", PrimaryCommands = Set(LimitCapableCommands) },
            new() { Name = "--offset", ValuePlaceholder = "<n>", Description = "Suggestions: skip this many filtered rows before output", PrimaryCommands = Set("suggestions") },
            new() { Name = "--lang", ValuePlaceholder = "<lang>", Description = "Filter by language", PrimaryCommands = Set(LangCapableCommands), AlsoAcceptedBy = Set("suggestions") },
            new() { Name = "--language", ValuePlaceholder = "<lang>", Description = "Suggestions: filter by language; languages: look up one language by canonical name or recognized language spelling", PrimaryCommands = Set("suggestions", "languages") },
            new() { Name = "--extension", ValuePlaceholder = "<ext>", Description = "Languages: look up language support by extension or recognized filename pattern", PrimaryCommands = Set(LanguagesFilterCommands) },
            new() { Name = "--alias", ValuePlaceholder = "<alias>", Description = "Languages: look up language support by display alias", PrimaryCommands = Set(LanguagesFilterCommands) },
            new() { Name = "--path", ValuePlaceholder = "<glob>", Description = "Path filter", PrimaryCommands = Set(PathFilterCommands) },
            new() { Name = "--project", ValuePlaceholder = "<name|path>", Description = "Filter to a .sln/.csproj project", PrimaryCommands = Set(PathFilterCommands.Concat(new[] { "index" }).ToArray()) },
            new() { Name = "--solution", ValuePlaceholder = "<path>", Description = "Solution file used to resolve --project", PrimaryCommands = Set(PathFilterCommands.Concat(new[] { "index" }).ToArray()) },
            new() { Name = "--exclude-path", ValuePlaceholder = "<glob>", Description = "Exclude path", PrimaryCommands = Set(ExcludeFilterCommands) },
            new() { Name = "--exclude-tests", Description = "Exclude tests", PrimaryCommands = Set(ExcludeFilterCommands) },
            new() { Name = "--include-generated", Description = "Include generated files", PrimaryCommands = Set(ExcludeFilterCommands) },
            new() { Name = "--generated", Description = "Files alias for --include-generated", PrimaryCommands = Set("files") },
            new() { Name = "--kind", ValuePlaceholder = "<kind>", Description = "Filter by kind", PrimaryCommands = Set(KindCommands) },
            new() { Name = "--severity", ValuePlaceholder = "<info|warning|error>", Description = "Validate: filter validation issues by severity", PrimaryCommands = Set(SeverityCommands) },
            new() { Name = "--visibility", ValuePlaceholder = "<visibility[,visibility]>", Description = "Filter by symbol visibility", PrimaryCommands = Set(VisibilityCommands) },
            new() { Name = "--exclude-visibility", ValuePlaceholder = "<visibility[,visibility]>", Description = "Exclude symbol visibility", PrimaryCommands = Set(VisibilityCommands) },
            new() { Name = "--by-bucket", Description = "Unused: include per-bucket grouped result arrays in JSON output, or count/representative summaries with --compact", PrimaryCommands = Set(ByBucketCommands) },
            new() { Name = "--bucket", ValuePlaceholder = "<bucket>", Description = "Unused: return only one confidence bucket", PrimaryCommands = Set(UnusedFilterCommands) },
            new() { Name = "--confidence", ValuePlaceholder = "<medium|low>", Description = "Unused: alias for --min-confidence", PrimaryCommands = Set(UnusedFilterCommands) },
            new() { Name = "--min-confidence", ValuePlaceholder = "<medium|low>", Description = "Unused: return symbols at or above this confidence", PrimaryCommands = Set(UnusedFilterCommands) },
            new() { Name = "--actionable", Description = "Unused: preset for private medium-confidence cleanup candidates", PrimaryCommands = Set(UnusedFilterCommands) },
            new() { Name = "--all", Description = "goto: return all matching LSP locations; find: search all indexed files instead of requiring --path; unused: include low-confidence contract-domain candidates suppressed by default", PrimaryCommands = Set(AllResultCommands) },
            new() { Name = "--line-scan-limit", ValuePlaceholder = "<n>", Description = "Find: override the --all indexed-line scan cap", PrimaryCommands = Set("find") },
            new() { Name = "--rank-by", ValuePlaceholder = "<weighted|count|kind>", Description = "Rank callers/callees by weighted structural score, raw count, or kind bucket", PrimaryCommands = Set(RankByCommands) },
            new() { Name = "--sort", ValuePlaceholder = "<mode>", Description = "Symbols/outline: order audit output by a ranking signal; outline also accepts source, kind, references, size, complexity, path, and name", PrimaryCommands = Set(SymbolSortCommands) },
            new() { Name = "--raw-kinds", Description = "Show raw reference kinds instead of logical graph kinds", PrimaryCommands = Set(RawKindsCommands) },
            new() { Name = "--count", Description = "Count only; result limits are ignored by count modes, but scan caps can still mark approximate counts as degraded", PrimaryCommands = Set(CountCommands) },
            new() { Name = "--group-partials", Description = "Definition/Symbols/Inspect: collapse C# partial-type declarations into logical families", PrimaryCommands = Set("definition", "symbols", "inspect") },
            new() { Name = "--strict-not-found", Description = "Return exit code 2 when a valid query has zero rows", PrimaryCommands = Set(StrictNotFoundCommands) },
            new() { Name = "--allow-partial", Description = "Return exit code 0 instead of 11 for accepted partial query output or an incomplete index generation", PrimaryCommands = Set(AllowPartialCommands) },
            new() { Name = "--strict", Description = "Return exit code 4 when impact preconditions are unmet", PrimaryCommands = Set("impact") },
            new() { Name = "--since", ValuePlaceholder = "<datetime>", Description = "Filter by modified-since timestamp", PrimaryCommands = Set(SinceCommands) },
            new() { Name = "--bytes", Description = "Files: sort by size and show raw byte counts in human output; map: show raw byte counts", PrimaryCommands = Set(ByteFormatCommands) },
            new() { Name = "--min-entrypoint-confidence", ValuePlaceholder = "<0.0..1.0>", Description = "Map: omit entrypoint candidates below this confidence", PrimaryCommands = Set(EntrypointConfidenceCommands) },
            new() { Name = "--sections", ValuePlaceholder = "<summary,tree,languages,hotspots,metrics|list>", Description = "Map: comma-separated response sections to include, or list to discover sections", PrimaryCommands = Set(MapSectionCommands) },
            new() { Name = "--summary-only", Description = "Map/Diff/Recipes/Audit/Files/Symbols/Deps/Hotspots/Languages: return only aggregate summary fields where supported", PrimaryCommands = Set(SummaryOnlyCommands) },
            new() { Name = "--cycles", Description = "Deps: return deterministically ranked dependency SCCs with stable pagination", PrimaryCommands = Set(DependencyCycleCommands) },
            new() { Name = "--graph-budget", ValuePlaceholder = "<n>", Description = $"Deps cycles: maximum graph edges analyzed for SCC completeness (default: {QueryCommandRunner.DefaultDependencyCycleGraphBudget})", PrimaryCommands = Set(DependencyCycleCommands) },
            new() { Name = "--suppress-noise", Description = "Deps: suppress generic framework/noise symbols in edge symbol samples", PrimaryCommands = Set("deps") },
            new() { Name = "--symbol", ValuePlaceholder = "<name>", Description = "Deps: keep only edges with an exact sampled symbol name", PrimaryCommands = Set("deps") },
            new() { Name = "--symbol-family", ValuePlaceholder = "<prefix>", Description = "Deps: keep only edges with a sampled symbol prefix/family", PrimaryCommands = Set("deps") },
            new() { Name = "--indexed-only", Description = "Languages: list only languages present in the current index", PrimaryCommands = Set(LanguagesFilterCommands) },
            new() { Name = "--capability", ValuePlaceholder = "<all|none|graph|references|symbols|missing-any|missing-graph|missing-references|missing-symbols|search-only>", Description = "Languages: filter by language capability or capability gap", PrimaryCommands = Set(LanguagesFilterCommands) },
            new() { Name = "--query", ValuePlaceholder = "<query>", Description = "Literal query", PrimaryCommands = Set(QueryCommands) },
            new() { Name = "--recipe", ValuePlaceholder = "<name|name/query>", Description = "Search: run a built-in audit recipe query set, optionally selecting one child query", PrimaryCommands = Set("search") },
            new() { Name = "--include-query", ValuePlaceholder = "<name>", Description = "Search recipe: include one child query; repeat or comma-separate values", PrimaryCommands = Set("search") },
            new() { Name = "--exclude-query", ValuePlaceholder = "<name>", Description = "Search recipe: exclude one child query; repeat or comma-separate values", PrimaryCommands = Set("search") },
            new() { Name = "--list-recipes", Description = "Search: list built-in audit recipes", PrimaryCommands = Set("search") },
            new() { Name = "--names", Description = "Recipes: emit only deterministic recipe names", PrimaryCommands = Set("search", "recipes") },
            new() { Name = "--audit-scope", ValuePlaceholder = "<source|all>", Description = "Search/Unused: use production source defaults or include all indexed paths", PrimaryCommands = Set("search", "unused") },
            new() { Name = "--source-only", Description = "Search: alias for --audit-scope source on ad hoc and named searches", PrimaryCommands = Set("search") },
            new() { Name = "--show-excluded", Description = "Search recipes: include effective scope and exclusion diagnostics in recipe output", PrimaryCommands = Set("search") },
            new() { Name = "--named-query", ValuePlaceholder = "<name>=<query>", Description = "Search: add one named ad hoc batch query", PrimaryCommands = Set("search") },
            new() { Name = "--open-issues", ValuePlaceholder = "<path|github|github:owner/name>", Description = "Preflight issue drafts against issue JSON or GitHub issues", PrimaryCommands = Set("search", "map", "suggestions") },
            new() { Name = "--repo", ValuePlaceholder = "<owner/name>", Description = "Issue-drafts: GitHub repository for --open-issues github", PrimaryCommands = Set("search", "map", "suggestions") },
            new() { Name = "--issue-state", ValuePlaceholder = "<open|closed|all>", Description = "Issue-drafts: GitHub issue history state to inspect", PrimaryCommands = Set("search", "map", "suggestions") },
            new() { Name = "--duplicate-confidence", ValuePlaceholder = "<low|medium|high>", Description = "Issue-drafts: preset duplicate-preflight match threshold", PrimaryCommands = Set("search", "suggestions") },
            new() { Name = "--duplicate-threshold", ValuePlaceholder = "<score>", Description = "Issue-drafts: explicit duplicate-preflight minimum score from 0 to 1", PrimaryCommands = Set("search", "suggestions") },
            new() { Name = "--issue-title", ValuePlaceholder = "<title>", Description = "Search issue-drafts: override the title for an ad hoc search draft", PrimaryCommands = Set("search") },
            new() { Name = "--issue-label", ValuePlaceholder = "<label>", Description = "Search issue-drafts: add a label hint; repeat or comma-separate values", PrimaryCommands = Set("search") },
            new() { Name = "--cursor", ValuePlaceholder = "<cursor>", Description = "Pagination cursor returned as next_cursor; bounded query responses use response:v1 cursors", PrimaryCommands = Set(CursorCommands) },
            new() { Name = "--status", ValuePlaceholder = "<all|draft|submitted_pending_triage|open_in_upstream|resolved_in_upstream|wont_fix|duplicate|superseded|submitted|unsubmitted>", Description = "Suggestions: filter by suggestion status", PrimaryCommands = Set("suggestions") },
            new() { Name = "--category", ValuePlaceholder = "<symbol_extraction|reference_extraction|search_ranking|language_support|output_format|crash_report|unexpected_error|other>", Description = "Suggestions: filter by category", PrimaryCommands = Set("suggestions") },
            new() { Name = "--agent", ValuePlaceholder = "<agent>", Description = "Suggestions: filter by agent", PrimaryCommands = Set("suggestions") },
            new() { Name = "--actor", ValuePlaceholder = "<name>", Description = "Suggestions update: actor recorded for a manual status transition", PrimaryCommands = Set("suggestions") },
            new() { Name = "--reason", ValuePlaceholder = "<text>", Description = "Suggestions update: optional reason recorded for a manual status transition", PrimaryCommands = Set("suggestions") },
            new() { Name = "--description", ValuePlaceholder = "<text>", Description = "Suggestions add: local suggestion description", PrimaryCommands = Set("suggestions") },
            new() { Name = "--title", ValuePlaceholder = "<title>", Description = "Suggestions add: optional issue-draft title source", PrimaryCommands = Set("suggestions") },
            new() { Name = "--evidence-path", ValuePlaceholder = "<path>", Description = "Suggestions add: repository-relative evidence path; repeat for multiple paths", PrimaryCommands = Set("suggestions") },
            new() { Name = "--overwrite", Description = "Suggestions export: atomically replace an existing --output file", PrimaryCommands = Set("suggestions") },
            new() { Name = "--body", Description = "Include definition body snippets in JSON-capable result rows", PrimaryCommands = Set(BodyCommands) },
            new() { Name = "--body-start", ValuePlaceholder = "<line>", Description = "Inspect: start definition body slice at this 1-based source line", PrimaryCommands = Set(InspectFieldCommands) },
            new() { Name = "--body-lines", ValuePlaceholder = "<n>", Description = "Inspect: return at most this many definition body lines", PrimaryCommands = Set(InspectFieldCommands) },
            new() { Name = "--body-line-count", ValuePlaceholder = "<n>", Description = "Inspect: alias for --body-lines", PrimaryCommands = Set(InspectFieldCommands) },
            new() { Name = "--fields", ValuePlaceholder = "<csv>", Description = "Project bounded-response row fields; inspect selects top-level evidence groups; nested collections accept collection.field", PrimaryCommands = Set(InspectFieldCommands.Concat(BoundedProjectionCommands).ToArray()) },
            new() { Name = "--body-only", Description = "Inspect: body-focused JSON shorthand for --body --fields definitions", PrimaryCommands = Set(InspectFieldCommands) },
            new() { Name = "--outline-only", Description = "Inspect: outline-first JSON shorthand for --fields file,definitions,nearby_symbols", PrimaryCommands = Set(InspectFieldCommands) },
            new() { Name = "--exact", Description = "Backward-compatible exact shorthand", PrimaryCommands = Set(ExactCommands) },
            new() { Name = "--regex", Description = "Use regular expression matching", PrimaryCommands = Set("find") },
            new() { Name = "--exact-name", Description = "Exact symbol-name equality", PrimaryCommands = Set(ExactNameCommands), AlsoAcceptedBy = Set("search") },
            new() { Name = "--exact-substring", Description = "Search-only exact substring match", PrimaryCommands = Set("search"), AlsoAcceptedBy = Set(ExactSubstringAccepted) },
            new() { Name = "--token-boundary", Description = "Search-only exact substring match with identifier/token boundaries", PrimaryCommands = Set("search") },
            new() { Name = "--prefix", Description = "Trailing-asterisk prefix shorthand", PrimaryCommands = Set("search") },
            new() { Name = "--require-before", ValuePlaceholder = "<query>", Description = "Search: require a nearby guard query before each primary match", PrimaryCommands = Set("search") },
            new() { Name = "--require-after", ValuePlaceholder = "<query>", Description = "Search: require a nearby guard query after each primary match", PrimaryCommands = Set("search") },
            new() { Name = "--reject-before", ValuePlaceholder = "<query>", Description = "Search: reject primary matches with a nearby guard query before them", PrimaryCommands = Set("search") },
            new() { Name = "--reject-after", ValuePlaceholder = "<query>", Description = "Search: reject primary matches with a nearby guard query after them", PrimaryCommands = Set("search") },
            new() { Name = "--guard-window", ValuePlaceholder = "<n>", Description = "Search: line window for require/reject guard queries", PrimaryCommands = Set("search") },
            new() { Name = "--guard-scope", ValuePlaceholder = "<window|same-line>", Description = "Search: evaluate guard queries in the line window or on the same line as the primary match", PrimaryCommands = Set("search") },
            new() { Name = "--unique", ValuePlaceholder = "<path|file|symbol|origin|return-type|subsystem>", Description = "Search/Audit recipes: emit unique aggregation rows", PrimaryCommands = Set("search", "audit") },
            new() { Name = "--count-by", ValuePlaceholder = "<path|file|symbol|origin|return-type|subsystem>", Description = "Search/Audit recipes: count matches grouped by path/file, symbol, origin, enclosing return type, or subsystem", PrimaryCommands = Set("search", "audit") },
            new() { Name = "--origin", ValuePlaceholder = "<code|comment|string_literal|regex_literal|help_text>", Description = "Search: alias for --match-origin; keep only matches from selected origins", PrimaryCommands = Set("search") },
            new() { Name = "--match-origin", ValuePlaceholder = "<code|comment|string_literal|regex_literal|help_text>", Description = "Search: keep only matches from selected origins; repeat or comma-separate values", PrimaryCommands = Set("search") },
            new() { Name = "--exclude-origin", ValuePlaceholder = "<code|comment|string_literal|regex_literal|help_text>", Description = "Search: drop matches from selected origins; repeat or comma-separate values", PrimaryCommands = Set("search") },
            new() { Name = "--result-kind", ValuePlaceholder = "<call_site|declaration|identifier|comment|string_literal>", Description = "Search: keep only projected result kinds; repeat or comma-separate values", PrimaryCommands = Set("search") },
            new() { Name = "--search-fields", ValuePlaceholder = "<path,line,column,symbol,origin,kind,score,snippet,query_name,recipe>", Description = "Search/Audit: project JSON/NDJSON result fields for audit pipelines", PrimaryCommands = Set("search", "audit") },
            new() { Name = "--outline-fields", ValuePlaceholder = "<kind,name,path,line,signature,...>", Description = "Outline JSON: project symbol fields for audit pipelines", PrimaryCommands = Set("outline") },
            new() { Name = "--results-only", Description = "Search/Symbols/Files/Audit: emit result-only NDJSON without stream terminal records", PrimaryCommands = Set("search", "symbols", "files", "audit") },
            new() { Name = "--first-per-file", Description = "Search: keep the first returned match for each file", PrimaryCommands = Set("search") },
            new() { Name = "--sample", ValuePlaceholder = "<n>", Description = "Search: deterministically sample returned rows down to n results", PrimaryCommands = Set("search") },
            new() { Name = "--per-file-limit", ValuePlaceholder = "<n>", Description = "Search/Audit grouped output: representative matches per file", PrimaryCommands = Set("search", "audit") },
            new() { Name = "--total-limit", ValuePlaceholder = "<n>", Description = "Search/Audit recipes: cap emitted rows across all child queries", PrimaryCommands = Set("search", "audit") },
            new() { Name = "--env-inventory", Description = "Doctor: include a compact environment-variable summary; use --env-inventory=full for the full inventory", PrimaryCommands = Set("doctor") },
            new() { Name = "--env-domain", ValuePlaceholder = "<domain>", Description = "Doctor full environment inventory: filter by exact domain", PrimaryCommands = Set("doctor") },
            new() { Name = "--env-category", ValuePlaceholder = "<category>", Description = "Doctor full environment inventory: filter by exact category", PrimaryCommands = Set("doctor") },
            new() { Name = "--env-sensitivity", ValuePlaceholder = "<sensitivity>", Description = "Doctor full environment inventory: filter by exact sensitivity", PrimaryCommands = Set("doctor") },
            new() { Name = "--max-json-bytes", ValuePlaceholder = "<n>", Description = "Bound emitted JSON bytes; bounded high-volume responses truncate projected rows with paging metadata", PrimaryCommands = Set("search", "definition", "find", "status", "references", "callers", "callees", "excerpt", "inspect", "impact", "recipes", "audit", "map", "files", "symbols", "deps", "hotspots", "doctor") },
            new() { Name = "--next-steps", Description = "Search: print inspect/excerpt follow-up commands for top hits", PrimaryCommands = Set("search") },
            new() { Name = "--exclude-comments", Description = "Search: suppress comment-only matches after origin classification", PrimaryCommands = Set("search") },
            new() { Name = "--exclude-strings", Description = "Search: suppress string, regex, and help-text matches after origin classification", PrimaryCommands = Set("search") },
            new() { Name = "--exclude-fixtures", Description = "Search: suppress fixture-only matches in tests after origin classification", PrimaryCommands = Set("search") },
            new() { Name = "--no-progress", Description = "Disable animated progress and spinner output", PrimaryCommands = Set(AllCommands.ToArray()), TopLevel = true },
            new() { Name = "--name", ValuePlaceholder = "<name>", Description = "Exact symbol name", PrimaryCommands = Set("symbols") },
            new() { Name = "--max-line-width", ValuePlaceholder = "<n>", Description = "Clamp long single-line payloads (0 disables clamping)", PrimaryCommands = Set(MaxLineWidthCommands) },
            new() { Name = "--snippet-lines", ValuePlaceholder = "<n>", Description = "Snippet length; issue-drafts accept 0 for path/line-only evidence", PrimaryCommands = Set("search", "audit", "find", "references", "callers", "callees", "impact") },
            new() { Name = "--snippet-focus", ValuePlaceholder = "<leftmost|quality|proximity>", Description = "Search snippet long-line focus mode", PrimaryCommands = Set("search") },
            new() { Name = "--fts", Description = "Raw FTS5 syntax", PrimaryCommands = Set("search") },
            new() { Name = "--no-dedup", Description = "Show duplicate chunks", PrimaryCommands = Set("search") },
            new() { Name = "--no-visibility-rank", Description = "Keep legacy search ranking without symbol visibility weighting", PrimaryCommands = Set("search") },
            new() { Name = "--line", ValuePlaceholder = "<line>", Description = "Inspect/excerpt: include one source line as source_excerpt or excerpt window", PrimaryCommands = Set(InspectSourceExcerptCommands) },
            new() { Name = "--context", ValuePlaceholder = "<n>", Description = "Find/inspect/excerpt: symmetric context lines; explicit --before/--after override the corresponding find side", PrimaryCommands = Set(InspectSourceExcerptCommands.Concat(new[] { "find" }).ToArray()), AlsoAcceptedBy = Set("suggestions") },
            new() { Name = "--before", ValuePlaceholder = "<n>", Description = "Context lines before", PrimaryCommands = Set("find", "excerpt", "inspect") },
            new() { Name = "--after", ValuePlaceholder = "<n>", Description = "Context lines after", PrimaryCommands = Set("find", "excerpt", "inspect") },
            new() { Name = "--start", ValuePlaceholder = "<line>", Description = "Start line", PrimaryCommands = Set("excerpt") },
            new() { Name = "--start-line", ValuePlaceholder = "<line>", Description = "Alias for --start; inspect source_excerpt start line", PrimaryCommands = Set("excerpt", "inspect") },
            new() { Name = "--end", ValuePlaceholder = "<line>", Description = "End line", PrimaryCommands = Set("excerpt") },
            new() { Name = "--end-line", ValuePlaceholder = "<line>", Description = "Alias for --end; inspect source_excerpt end line", PrimaryCommands = Set("excerpt", "inspect") },
            new() { Name = "--focus-line", ValuePlaceholder = "<line>", Description = "Focused line to keep visible when clamping", PrimaryCommands = Set("find", "excerpt") },
            new() { Name = "--focus-column", ValuePlaceholder = "<n>", Description = "Focused column to keep visible when clamping", PrimaryCommands = Set("find", "excerpt") },
            new() { Name = "--focus-length", ValuePlaceholder = "<n>", Description = "Focused span width when clamping", PrimaryCommands = Set("excerpt") },
            new() { Name = "--no-semantic-tokens", Description = "Excerpt JSON: omit semantic_tokens to keep payloads compact", PrimaryCommands = Set("excerpt") },
            new() { Name = "--max-hops", ValuePlaceholder = "<n>", Description = "Impact: max BFS hops", PrimaryCommands = Set("impact") },
            new() { Name = "--depth", ValuePlaceholder = "<n>", Description = "Map: cap module depth; impact: deprecated alias for --max-hops", PrimaryCommands = Set("impact", "map") },
            new() { Name = "--with-paths", Description = "Impact: include shortest call chains per caller", PrimaryCommands = Set("impact") },
            new() { Name = "--reverse", Description = "Reverse direction (show dependents)", PrimaryCommands = Set("deps") },
            new() { Name = "--group-by", ValuePlaceholder = "<file|symbol|origin|return-type|subsystem|statement>", Description = "Search/Audit: group --count rows by file, symbol, origin, enclosing return type, or subsystem; hotspots: symbol/file grouping, with statement only for --lang sql", PrimaryCommands = Set("hotspots", "search", "audit") },
            new() { Name = "--group-by-name", Description = "Hotspots: collapse same-name rows; JSON keeps capped paths plus full definition details", PrimaryCommands = Set("hotspots") },
            new() { Name = "--check", Description = "Verify status freshness/readiness", PrimaryCommands = Set("status") },
            new() { Name = "--config", Description = "Print effective configuration with source attribution", PrimaryCommands = Set("status") },
            new() { Name = "--stale-after", ValuePlaceholder = "<duration>", Description = "Status: freshness age threshold (e.g. 30m, 2h, 7d)", PrimaryCommands = Set("status") },
            new() { Name = "--explain", ValuePlaceholder = "<field>", Description = "Explain one visible status field", PrimaryCommands = Set("status") },
            new() { Name = "--log-path", Description = "Print the active persistent log directory", PrimaryCommands = Set("status") },
            new() { Name = "--check-updates", Description = "Check whether a newer cdidx release is available", PrimaryCommands = Set("status", "upgrade") },
            new() { Name = "--check-only", Description = "Upgrade: only report whether an upgrade is available", PrimaryCommands = Set("upgrade") },
            new() { Name = "--channel", ValuePlaceholder = "<stable|latest|prerelease>", Description = "Upgrade: select stable/latest or prerelease releases", PrimaryCommands = Set("upgrade") },
            new() { Name = "--prerelease", Description = "Upgrade: select the newest prerelease", PrimaryCommands = Set("upgrade") },
            new() { Name = "--version", ValuePlaceholder = "<tag>", Description = "Upgrade: install a specific release tag", PrimaryCommands = Set("upgrade") },
            new() { Name = "--integrity-check", Description = "Run PRAGMA integrity_check on the database", PrimaryCommands = Set("db") },
            new() { Name = "--rebuild", Description = "Delete existing DB and rebuild from scratch", PrimaryCommands = Set("index") },
            new() { Name = "--yes", Description = "Confirm --rebuild when stdin is redirected; interactive terminals prompt instead", PrimaryCommands = Set("index") },
            new() { Name = "--optimize", Description = "Optimize the existing FTS5 table without scanning files", PrimaryCommands = Set("index") },
            new() { Name = "--symbols-only", Description = "Build chunks and symbols while skipping reference graph extraction", PrimaryCommands = Set("index") },
            new() { Name = "--dry-run", Description = "Preview without writing", PrimaryCommands = Set("index", "backfill-fold", "optimize", "vacuum") },
            new() { Name = "--dry-run-path-limit", ValuePlaceholder = "<n>", Description = "Dry run only: candidate path processing limit before truncated lower-bound estimates", PrimaryCommands = Set("index") },
            new() { Name = "--no-checkpoint", Description = "Skip the automatic DB checkpoint before maintenance", PrimaryCommands = Set("backfill-fold") },
            new() { Name = "--force", Description = "Bypass the per-database index lock", PrimaryCommands = Set("index") },
            new() { Name = "--duration-format", ValuePlaceholder = "<auto|seconds|hms>", Description = "Index elapsed time display format", PrimaryCommands = Set("index") },
            new() { Name = "--max-file-bytes", ValuePlaceholder = "<bytes>", Description = "Override the per-file indexing size limit", PrimaryCommands = Set("index") },
            new() { Name = "--max-symbols-per-file", ValuePlaceholder = "<n>", Description = "Skip file content, symbols, and references when one file emits too many symbols (max 50000)", PrimaryCommands = Set("index") },
            new() { Name = "--max-references-per-file", ValuePlaceholder = "<n>", Description = "Skip references when one file emits too many references (max 1000000)", PrimaryCommands = Set("index") },
            new() { Name = "--parallelism", ValuePlaceholder = "<n>", Description = "Full-scan extraction worker count (default: CPU count capped at 8; explicit max: 16; also honors CDIDX_INDEX_PARALLELISM)", PrimaryCommands = Set("index") },
            new() { Name = "--memory-trace", Description = "Include phase memory samples in index JSON output", PrimaryCommands = Set("index") },
            new() { Name = "--commits", ValuePlaceholder = "<commit-ref>", Description = "Update files changed in given git commits", PrimaryCommands = Set("index") },
            new() { Name = "--changed-between", ValuePlaceholder = "<old-ref> <new-ref>", Description = "Update files changed between two git refs", PrimaryCommands = Set("index") },
            new() { Name = "--files", ValuePlaceholder = "<path>", Description = "Update only the specified files", PrimaryCommands = Set("index") },
            new() { Name = "--watch", Description = "Continuous reindex on file changes (rejects --commits / --changed-between / --files / --dry-run)", PrimaryCommands = Set("index") },
            new() { Name = "--debounce", ValuePlaceholder = "<ms>", Description = "Watch only: coalesce file events into one update after <ms> of quiet (default 500)", PrimaryCommands = Set("index") },
            new() { Name = "--watch-pending-path-limit", ValuePlaceholder = "<n>", Description = "Watch only: changed-path queue limit before full-rescan fallback", PrimaryCommands = Set("index") },
            new() { Name = "--output", ShortName = "-o", ValuePlaceholder = "<path>", Description = "Report bundle or suggestions export output path", PrimaryCommands = Set("report", "suggestions") },
            new() { Name = "--redact-paths", Description = "Report: accepted for doctor/report parity; report paths are redacted by default", PrimaryCommands = Set("report") },
            new() { Name = "--no-log", Description = "Exclude global tool log from bundle", PrimaryCommands = Set("report") },
            new() { Name = "--include-args", Description = "Include args in bundle log", PrimaryCommands = Set("report") },
            new() { Name = "--log-lines", ValuePlaceholder = "<n>", Description = "Number of log lines to include in bundle (clamped to 2000)", PrimaryCommands = Set("report") },
            new() { Name = "--transport", ValuePlaceholder = "<stdio|http>", Description = "MCP transport", PrimaryCommands = Set("mcp") },
            new() { Name = "--http-listen", ValuePlaceholder = "<host:port>", Description = "MCP HTTP listen address", PrimaryCommands = Set("mcp") },
            new() { Name = "--allow-unauthenticated-http", Description = "MCP HTTP: explicitly allow unsafe unauthenticated loopback mode", PrimaryCommands = Set("mcp") },
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
    /// Render a next-step/help token from the same flag metadata used by parsing and
    /// completion. Callers may narrow a generic placeholder for their recovery context.
    /// parser / completion と同じ flag metadata から next-step / help token を生成する。
    /// recovery 文脈に応じて generic placeholder を狭めてもよい。
    /// </summary>
    public static string GetUsageTokenForCommand(
        string command,
        string flagName,
        string? valuePlaceholderOverride = null)
    {
        var flag = All.FirstOrDefault(candidate =>
            string.Equals(candidate.Name, flagName, StringComparison.Ordinal)
            && candidate.AppliesTo(command));
        if (flag is null)
            throw new ArgumentException($"{flagName} is not a documented option for {command}.", nameof(flagName));

        var placeholder = valuePlaceholderOverride ?? flag.ValuePlaceholder;
        return placeholder is null ? flag.Name : $"{flag.Name} {placeholder}";
    }

    /// <summary>
    /// Flags accepted before a subcommand and surfaced in top-level shell completion.
    /// サブコマンド前に受理され、トップレベル補完に出すフラグ集合。
    /// </summary>
    public static IReadOnlyList<CliFlag> GetTopLevelCompletionFlags()
    {
        return All.Where(f => f.TopLevel).ToList();
    }

    public static HashSet<string> GetTopLevelGlobalOptionNames(bool includeLogOptions)
    {
        var names = new HashSet<string>(StringComparer.Ordinal);
        foreach (var flag in All)
        {
            if (!flag.TopLevel)
                continue;
            if (!includeLogOptions && flag.Name.StartsWith("--log-", StringComparison.Ordinal))
                continue;
            names.Add(flag.Name);
            if (flag.ShortName is not null)
                names.Add(flag.ShortName);
        }
        return names;
    }

    public static HashSet<string> GetTopLevelValueOptionNames()
    {
        var names = new HashSet<string>(StringComparer.Ordinal);
        foreach (var flag in All)
        {
            if (flag is { TopLevel: true, IsValueBearing: true })
                names.Add(flag.Name);
        }
        return names;
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

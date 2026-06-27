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
    public bool AppliesTo(string command) => Commands.Contains(command);
    public bool IsAcceptedBy(string command) => AppliesTo(command) || AlsoAcceptedBy.Contains(command);
}

internal static class CliFlagSchema
{
    // Authoritative list of subcommands. Mirrored by ConsoleUi.Commands; tests guard parity.
    // サブコマンド一覧の正本。ConsoleUi.Commands と一致することをテストで確認する。
    public static IReadOnlyList<string> AllCommands { get; } =
    [
        "index", "backfill-fold", "optimize", "search", "recipes", "audit", "definition", "goto", "references", "callers", "callees",
        "symbols", "files", "find", "excerpt", "map", "inspect", "outline", "status", "validate-config",
        "validate", "deps", "impact", "unused", "hotspots", "suggestions", "languages", "batch", "mcp", "completions", "db", "vacuum", "report", "license", "upgrade",
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
        "files", "find", "map", "inspect", "outline", "deps", "impact", "unused", "hotspots", "validate",
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
        "files", "find", "impact", "unused", "hotspots",
    ];
    private static readonly string[] StrictNotFoundCommands =
    [
        "search", "definition", "references", "callers", "callees", "symbols",
        "files", "find", "excerpt", "map", "inspect", "deps", "impact", "unused", "hotspots",
    ];

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
    private static readonly string[] SymbolSortCommands = ["symbols"];
    private static readonly string[] ByBucketCommands = ["unused"];
    private static readonly string[] UnusedFilterCommands = ["unused"];
    private static readonly string[] CursorCommands = ["search", "outline", "unused"];
    private static readonly string[] AllResultCommands = ["goto", "find"];

    private static readonly string[] SinceCommands = ["search", "definition", "symbols", "files", "suggestions"];
    private static readonly string[] ByteFormatCommands = ["files", "map"];
    private static readonly string[] EntrypointConfidenceCommands = ["map"];
    private static readonly string[] MapSectionCommands = ["map"];
    private static readonly string[] SummaryOnlyCommands = ["map"];
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
        "validate", "deps", "impact", "unused", "hotspots", "suggestions", "languages", "db", "report", "upgrade",
    ];

    private static readonly string[] CompactJsonCommands = ["map", "inspect", "outline", "unused"];

    private static readonly string[] FormatCommands =
    [
        "search", "audit", "definition", "references", "callers", "callees", "symbols", "find", "map", "inspect", "validate", "deps", "suggestions",
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
            new() { Name = "--workspace-db", ValuePlaceholder = "<path>", Description = "Additional workspace member database path for dependency aggregation; repeat up to 7 distinct additional DBs", Commands = Set(WorkspaceDbCommands) },
            new() { Name = "--data-dir", ValuePlaceholder = "<dir>", Description = "Directory containing codeindex.db; overrides CDIDX_DATA_DIR/XDG/workspace defaults", Commands = Set(DataDirCommands) },
            new() { Name = "--json", Description = "JSON output; search/files/validate also accept --json=array for a single JSON array", Commands = Set(JsonCommands) },
            new() { Name = "--pretty", Description = "Pretty-print JSON output with indentation", Commands = Set(JsonCommands), TopLevel = true },
            new() { Name = "--compact", Description = "AI-oriented compact JSON with capped list sections and truncation metadata", Commands = Set(CompactJsonCommands) },
            new() { Name = "--format", ValuePlaceholder = "<text|json|count|compact|csv|tsv|lsp|qf|sarif|markdown|issue-drafts>", Description = "Standard output format for token budgets, editor integrations, and CI; supported values vary by command, and search recipes/suggestions export also accept issue-drafts", Commands = Set(FormatCommands) },
            new() { Name = "--quiet", ShortName = "-q", Description = "Suppress informational stderr output; errors still print", Commands = Set(AllCommands.ToArray()), TopLevel = true },
            new() { Name = "--silent", Description = "Alias for --quiet", Commands = Set(AllCommands.ToArray()), TopLevel = true },
            new() { Name = "--color", ValuePlaceholder = "<auto|always|never>", Description = "Color output mode", Commands = Set(), TopLevel = true },
            new() { Name = "--palette", ValuePlaceholder = "<basic|256|truecolor>", Description = "ANSI color palette", Commands = Set(), TopLevel = true },
            new() { Name = "--ascii", Description = "Use ASCII progress glyphs", Commands = Set(), TopLevel = true },
            new() { Name = "--metrics", ValuePlaceholder = "<path>", Description = "Append command metrics JSONL to a file", Commands = Set(), TopLevel = true },
            new() { Name = "--debug-unsafe", Description = "Allow raw debug dumps when CDIDX_DEBUG=unsafe is also set", Commands = Set(), TopLevel = true },
            new() { Name = "--strict-version", Description = "Fail when the workspace version pin does not match this binary", Commands = Set(), TopLevel = true },
            new() { Name = "--log-format", ValuePlaceholder = "<text|json>", Description = "Persistent stderr log format", Commands = Set(), TopLevel = true },
            new() { Name = "--log-retain-count", ValuePlaceholder = "<n>", Description = "Persistent stderr log file retention count", Commands = Set(), TopLevel = true },
            new() { Name = "--log-max-size-mb", ValuePlaceholder = "<n>", Description = "Persistent stderr log rotation size cap in MiB", Commands = Set(), TopLevel = true },
            new() { Name = "--profile", Description = "Emit SQL timing and EXPLAIN QUERY PLAN profile JSON after the normal result", Commands = Set(ProfileCommands) },
            new() { Name = "--verbose", Description = "Emit query debug diagnostics to stderr, or _debug JSON when combined with --json", Commands = Set(VerboseQueryCommands.Concat(new[] { "index" }).ToArray()) },
            new() { Name = "--notify", ValuePlaceholder = "<auto|bell|osc9|desktop|none>", Description = "Signal long index completion; desktop currently emits OSC 9 terminal notification", Commands = Set("index") },
            new() { Name = "--slow-query-ms", ValuePlaceholder = "<n>", Description = "Log profiled SQL statements at or above this millisecond threshold", Commands = Set(ProfileCommands) },
            new() { Name = "--trace", ValuePlaceholder = "<none|stderr|file>", Description = "Emit one structured JSON query trace line to stderr or a daily log file", Commands = Set(TraceCommands) },
            new() { Name = "--limit", ValuePlaceholder = "<n>", Description = "Max results", Commands = Set(LimitCapableCommands.Concat(new[] { "suggestions" }).ToArray()) },
            new() { Name = "--max-results", ValuePlaceholder = "<n>", Description = "Search alias for --limit", Commands = Set("search") },
            new() { Name = "--top", ValuePlaceholder = "<n>", Description = "Max results", Commands = Set(LimitCapableCommands) },
            new() { Name = "--offset", ValuePlaceholder = "<n>", Description = "Suggestions: skip this many filtered rows before output", Commands = Set("suggestions") },
            new() { Name = "--lang", ValuePlaceholder = "<lang>", Description = "Filter by language", Commands = Set(LangCapableCommands) },
            new() { Name = "--language", ValuePlaceholder = "<lang>", Description = "Suggestions: filter by language; languages: look up one language by canonical name or recognized language spelling", Commands = Set("suggestions", "languages") },
            new() { Name = "--extension", ValuePlaceholder = "<ext>", Description = "Languages: look up language support by extension or recognized filename pattern", Commands = Set(LanguagesFilterCommands) },
            new() { Name = "--alias", ValuePlaceholder = "<alias>", Description = "Languages: look up language support by display alias", Commands = Set(LanguagesFilterCommands) },
            new() { Name = "--path", ValuePlaceholder = "<glob>", Description = "Path filter", Commands = Set(PathFilterCommands) },
            new() { Name = "--project", ValuePlaceholder = "<name|path>", Description = "Filter to a .sln/.csproj project", Commands = Set(PathFilterCommands.Concat(new[] { "index" }).ToArray()) },
            new() { Name = "--solution", ValuePlaceholder = "<path>", Description = "Solution file used to resolve --project", Commands = Set(PathFilterCommands.Concat(new[] { "index" }).ToArray()) },
            new() { Name = "--exclude-path", ValuePlaceholder = "<glob>", Description = "Exclude path", Commands = Set(ExcludeFilterCommands) },
            new() { Name = "--exclude-tests", Description = "Exclude tests", Commands = Set(ExcludeFilterCommands) },
            new() { Name = "--include-generated", Description = "Include generated files", Commands = Set(ExcludeFilterCommands) },
            new() { Name = "--kind", ValuePlaceholder = "<kind>", Description = "Filter by kind", Commands = Set(KindCommands) },
            new() { Name = "--severity", ValuePlaceholder = "<info|warning|error>", Description = "Validate: filter validation issues by severity", Commands = Set(SeverityCommands) },
            new() { Name = "--visibility", ValuePlaceholder = "<visibility[,visibility]>", Description = "Filter by symbol visibility", Commands = Set(VisibilityCommands) },
            new() { Name = "--exclude-visibility", ValuePlaceholder = "<visibility[,visibility]>", Description = "Exclude symbol visibility", Commands = Set(VisibilityCommands) },
            new() { Name = "--by-bucket", Description = "Unused: include per-bucket grouped result arrays in JSON output", Commands = Set(ByBucketCommands) },
            new() { Name = "--bucket", ValuePlaceholder = "<bucket>", Description = "Unused: return only one confidence bucket", Commands = Set(UnusedFilterCommands) },
            new() { Name = "--confidence", ValuePlaceholder = "<medium|low>", Description = "Unused: alias for --min-confidence", Commands = Set(UnusedFilterCommands) },
            new() { Name = "--min-confidence", ValuePlaceholder = "<medium|low>", Description = "Unused: return symbols at or above this confidence", Commands = Set(UnusedFilterCommands) },
            new() { Name = "--actionable", Description = "Unused: preset for private medium-confidence cleanup candidates", Commands = Set(UnusedFilterCommands) },
            new() { Name = "--all", Description = "goto: return all matching LSP locations; find: search all indexed files instead of requiring --path", Commands = Set(AllResultCommands) },
            new() { Name = "--rank-by", ValuePlaceholder = "<weighted|count|kind>", Description = "Rank callers/callees by weighted structural score, raw count, or kind bucket", Commands = Set(RankByCommands) },
            new() { Name = "--sort", ValuePlaceholder = "<hotspot|references|size|complexity|path>", Description = "Symbols: order audit output by a ranking signal", Commands = Set(SymbolSortCommands) },
            new() { Name = "--raw-kinds", Description = "Show raw reference kinds instead of logical graph kinds", Commands = Set(RawKindsCommands) },
            new() { Name = "--count", Description = "Count only", Commands = Set(CountCommands) },
            new() { Name = "--strict-not-found", Description = "Return exit code 2 when a valid query has zero rows", Commands = Set(StrictNotFoundCommands) },
            new() { Name = "--strict", Description = "Return exit code 4 when impact preconditions are unmet", Commands = Set("impact") },
            new() { Name = "--since", ValuePlaceholder = "<datetime>", Description = "Filter by modified-since timestamp", Commands = Set(SinceCommands) },
            new() { Name = "--bytes", Description = "Files: sort by size and show raw byte counts in human output; map: show raw byte counts", Commands = Set(ByteFormatCommands) },
            new() { Name = "--min-entrypoint-confidence", ValuePlaceholder = "<0.0..1.0>", Description = "Map: omit entrypoint candidates below this confidence", Commands = Set(EntrypointConfidenceCommands) },
            new() { Name = "--sections", ValuePlaceholder = "<tree,languages,hotspots,metrics>", Description = "Map: comma-separated response sections to include", Commands = Set(MapSectionCommands) },
            new() { Name = "--summary-only", Description = "Map/Diff: return only aggregate summary fields", Commands = Set(SummaryOnlyCommands) },
            new() { Name = "--cycles", Description = "Deps: return dependency cycles from a bounded candidate-edge scan", Commands = Set(DependencyCycleCommands) },
            new() { Name = "--suppress-noise", Description = "Deps: suppress generic framework/noise symbols in edge symbol samples", Commands = Set("deps") },
            new() { Name = "--symbol", ValuePlaceholder = "<name>", Description = "Deps: keep only edges with an exact sampled symbol name", Commands = Set("deps") },
            new() { Name = "--symbol-family", ValuePlaceholder = "<prefix>", Description = "Deps: keep only edges with a sampled symbol prefix/family", Commands = Set("deps") },
            new() { Name = "--indexed-only", Description = "Languages: list only languages present in the current index", Commands = Set(LanguagesFilterCommands) },
            new() { Name = "--capability", ValuePlaceholder = "<graph|references|symbols|missing-graph|missing-references|missing-symbols|search-only>", Description = "Languages: filter by language capability or capability gap", Commands = Set(LanguagesFilterCommands) },
            new() { Name = "--query", ValuePlaceholder = "<query>", Description = "Literal query", Commands = Set(QueryCommands) },
            new() { Name = "--recipe", ValuePlaceholder = "<name|name/query>", Description = "Search: run a built-in audit recipe query set, optionally selecting one child query", Commands = Set("search") },
            new() { Name = "--include-query", ValuePlaceholder = "<name>", Description = "Search recipe: include one child query; repeat or comma-separate values", Commands = Set("search") },
            new() { Name = "--exclude-query", ValuePlaceholder = "<name>", Description = "Search recipe: exclude one child query; repeat or comma-separate values", Commands = Set("search") },
            new() { Name = "--list-recipes", Description = "Search: list built-in audit recipes", Commands = Set("search") },
            new() { Name = "--audit-scope", ValuePlaceholder = "<source|all>", Description = "Search/Unused: use production source defaults or include all indexed paths", Commands = Set("search", "unused") },
            new() { Name = "--source-only", Description = "Search: alias for --audit-scope source on ad hoc and named searches", Commands = Set("search") },
            new() { Name = "--show-excluded", Description = "Search recipes: include effective scope and exclusion diagnostics in recipe output", Commands = Set("search") },
            new() { Name = "--named-query", ValuePlaceholder = "<name>=<query>", Description = "Search: add one named ad hoc batch query", Commands = Set("search") },
            new() { Name = "--open-issues", ValuePlaceholder = "<path|github|github:owner/name>", Description = "Preflight issue drafts against open issue JSON or GitHub open issues", Commands = Set("search", "suggestions") },
            new() { Name = "--repo", ValuePlaceholder = "<owner/name>", Description = "Issue-drafts: GitHub repository for --open-issues github", Commands = Set("search", "suggestions") },
            new() { Name = "--duplicate-confidence", ValuePlaceholder = "<low|medium|high>", Description = "Issue-drafts: preset duplicate-preflight match threshold", Commands = Set("search", "suggestions") },
            new() { Name = "--duplicate-threshold", ValuePlaceholder = "<score>", Description = "Issue-drafts: explicit duplicate-preflight minimum score from 0 to 1", Commands = Set("search", "suggestions") },
            new() { Name = "--issue-title", ValuePlaceholder = "<title>", Description = "Search issue-drafts: override the title for an ad hoc search draft", Commands = Set("search") },
            new() { Name = "--issue-label", ValuePlaceholder = "<label>", Description = "Search issue-drafts: add a label hint; repeat or comma-separate values", Commands = Set("search") },
            new() { Name = "--cursor", ValuePlaceholder = "<cursor>", Description = "Search recipe, outline, or unused pagination cursor returned as next_cursor", Commands = Set(CursorCommands) },
            new() { Name = "--status", ValuePlaceholder = "<status>", Description = "Suggestions: filter by suggestion status", Commands = Set("suggestions") },
            new() { Name = "--category", ValuePlaceholder = "<category>", Description = "Suggestions: filter by category", Commands = Set("suggestions") },
            new() { Name = "--agent", ValuePlaceholder = "<agent>", Description = "Suggestions: filter by agent", Commands = Set("suggestions") },
            new() { Name = "--body", Description = "Include definition body snippets in JSON-capable result rows", Commands = Set(BodyCommands) },
            new() { Name = "--body-start", ValuePlaceholder = "<line>", Description = "Inspect: start definition body slice at this 1-based source line", Commands = Set(InspectFieldCommands) },
            new() { Name = "--body-lines", ValuePlaceholder = "<n>", Description = "Inspect: return at most this many definition body lines", Commands = Set(InspectFieldCommands) },
            new() { Name = "--body-line-count", ValuePlaceholder = "<n>", Description = "Inspect: alias for --body-lines", Commands = Set(InspectFieldCommands) },
            new() { Name = "--fields", ValuePlaceholder = "<file,workspace,graph,definitions,body,source_excerpt,nearby_symbols,references,callers,callees,all>", Description = "Inspect: select top-level JSON evidence groups", Commands = Set(InspectFieldCommands) },
            new() { Name = "--body-only", Description = "Inspect: body-focused JSON shorthand for --body --fields definitions", Commands = Set(InspectFieldCommands) },
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
            new() { Name = "--guard-scope", ValuePlaceholder = "<window|same-line>", Description = "Search: evaluate guard queries in the line window or on the same line as the primary match", Commands = Set("search") },
            new() { Name = "--unique", ValuePlaceholder = "<path|file|symbol|origin>", Description = "Search recipes: emit unique aggregation rows", Commands = Set("search") },
            new() { Name = "--count-by", ValuePlaceholder = "<path|file|symbol|origin>", Description = "Search recipes: count matches grouped by path/file, symbol, or origin", Commands = Set("search") },
            new() { Name = "--origin", ValuePlaceholder = "<code|comment|string_literal|regex_literal|help_text>", Description = "Search: alias for --match-origin; keep only matches from selected origins", Commands = Set("search") },
            new() { Name = "--match-origin", ValuePlaceholder = "<code|comment|string_literal|regex_literal|help_text>", Description = "Search: keep only matches from selected origins; repeat or comma-separate values", Commands = Set("search") },
            new() { Name = "--exclude-origin", ValuePlaceholder = "<code|comment|string_literal|regex_literal|help_text>", Description = "Search: drop matches from selected origins; repeat or comma-separate values", Commands = Set("search") },
            new() { Name = "--result-kind", ValuePlaceholder = "<call_site|declaration|identifier|comment|string_literal>", Description = "Search: keep only projected result kinds; repeat or comma-separate values", Commands = Set("search") },
            new() { Name = "--search-fields", ValuePlaceholder = "<path,line,column,symbol,origin,kind,score,snippet,query_name,recipe>", Description = "Search: project JSON/NDJSON result fields for audit pipelines", Commands = Set("search") },
            new() { Name = "--outline-fields", ValuePlaceholder = "<kind,name,path,line,signature,...>", Description = "Outline JSON: project symbol fields for audit pipelines", Commands = Set("outline") },
            new() { Name = "--results-only", Description = "Search: emit result-only NDJSON without stream done records", Commands = Set("search") },
            new() { Name = "--first-per-file", Description = "Search: keep the first returned match for each file", Commands = Set("search") },
            new() { Name = "--sample", ValuePlaceholder = "<n>", Description = "Search: deterministically sample returned rows down to n results", Commands = Set("search") },
            new() { Name = "--per-file-limit", ValuePlaceholder = "<n>", Description = "Search grouped output: representative matches per file", Commands = Set("search") },
            new() { Name = "--total-limit", ValuePlaceholder = "<n>", Description = "Search recipes: cap emitted rows across all child queries", Commands = Set("search") },
            new() { Name = "--max-json-bytes", ValuePlaceholder = "<n>", Description = "Search NDJSON: stop before emitting more than this many JSON bytes", Commands = Set("search") },
            new() { Name = "--next-steps", Description = "Search: print inspect/excerpt follow-up commands for top hits", Commands = Set("search") },
            new() { Name = "--exclude-comments", Description = "Search: suppress comment-only matches after origin classification", Commands = Set("search") },
            new() { Name = "--exclude-strings", Description = "Search: suppress string, regex, and help-text matches after origin classification", Commands = Set("search") },
            new() { Name = "--exclude-fixtures", Description = "Search: suppress fixture-only matches in tests after origin classification", Commands = Set("search") },
            new() { Name = "--no-progress", Description = "Disable animated progress and spinner output", Commands = Set(AllCommands.ToArray()), TopLevel = true },
            new() { Name = "--name", ValuePlaceholder = "<name>", Description = "Exact symbol name", Commands = Set("symbols") },
            new() { Name = "--max-line-width", ValuePlaceholder = "<n>", Description = "Clamp long single-line payloads (0 disables clamping)", Commands = Set(MaxLineWidthCommands) },
            new() { Name = "--snippet-lines", ValuePlaceholder = "<n>", Description = "Snippet length", Commands = Set("search", "find", "references", "callers", "callees", "impact") },
            new() { Name = "--snippet-focus", ValuePlaceholder = "<leftmost|quality|proximity>", Description = "Search snippet long-line focus mode", Commands = Set("search") },
            new() { Name = "--fts", Description = "Raw FTS5 syntax", Commands = Set("search") },
            new() { Name = "--no-dedup", Description = "Show duplicate chunks", Commands = Set("search") },
            new() { Name = "--no-visibility-rank", Description = "Keep legacy search ranking without symbol visibility weighting", Commands = Set("search") },
            new() { Name = "--line", ValuePlaceholder = "<line>", Description = "Inspect/excerpt: include one source line as source_excerpt or excerpt window", Commands = Set(InspectSourceExcerptCommands) },
            new() { Name = "--context", ValuePlaceholder = "<n>", Description = "Inspect/excerpt: context lines before and after", Commands = Set(InspectSourceExcerptCommands) },
            new() { Name = "--before", ValuePlaceholder = "<n>", Description = "Context lines before", Commands = Set("find", "excerpt", "inspect") },
            new() { Name = "--after", ValuePlaceholder = "<n>", Description = "Context lines after", Commands = Set("find", "excerpt", "inspect") },
            new() { Name = "--start", ValuePlaceholder = "<line>", Description = "Start line", Commands = Set("excerpt") },
            new() { Name = "--start-line", ValuePlaceholder = "<line>", Description = "Alias for --start; inspect source_excerpt start line", Commands = Set("excerpt", "inspect") },
            new() { Name = "--end", ValuePlaceholder = "<line>", Description = "End line", Commands = Set("excerpt") },
            new() { Name = "--end-line", ValuePlaceholder = "<line>", Description = "Alias for --end; inspect source_excerpt end line", Commands = Set("excerpt", "inspect") },
            new() { Name = "--focus-line", ValuePlaceholder = "<line>", Description = "Focused line to keep visible when clamping", Commands = Set("find", "excerpt") },
            new() { Name = "--focus-column", ValuePlaceholder = "<n>", Description = "Focused column to keep visible when clamping", Commands = Set("find", "excerpt") },
            new() { Name = "--focus-length", ValuePlaceholder = "<n>", Description = "Focused span width when clamping", Commands = Set("excerpt") },
            new() { Name = "--no-semantic-tokens", Description = "Excerpt JSON: omit semantic_tokens to keep payloads compact", Commands = Set("excerpt") },
            new() { Name = "--max-hops", ValuePlaceholder = "<n>", Description = "Impact: max BFS hops", Commands = Set("impact") },
            new() { Name = "--depth", ValuePlaceholder = "<n>", Description = "Map: cap module depth; impact: deprecated alias for --max-hops", Commands = Set("impact", "map") },
            new() { Name = "--with-paths", Description = "Impact: include shortest call chains per caller", Commands = Set("impact") },
            new() { Name = "--reverse", Description = "Reverse direction (show dependents)", Commands = Set("deps") },
            new() { Name = "--group-by", ValuePlaceholder = "<file|symbol|origin|statement>", Description = "Search: group --count rows by file, symbol, or origin; hotspots: choose grouping unit", Commands = Set("hotspots", "search") },
            new() { Name = "--group-by-name", Description = "Hotspots: collapse same-name rows; JSON keeps capped paths plus full definition details", Commands = Set("hotspots") },
            new() { Name = "--check", Description = "Verify status freshness/readiness", Commands = Set("status") },
            new() { Name = "--config", Description = "Print effective configuration with source attribution", Commands = Set("status") },
            new() { Name = "--stale-after", ValuePlaceholder = "<duration>", Description = "Status: freshness age threshold (e.g. 30m, 2h, 7d)", Commands = Set("status") },
            new() { Name = "--explain", ValuePlaceholder = "<field>", Description = "Explain one visible status field", Commands = Set("status") },
            new() { Name = "--log-path", Description = "Print the active persistent log directory", Commands = Set("status") },
            new() { Name = "--check-updates", Description = "Check whether a newer cdidx release is available", Commands = Set("status", "upgrade") },
            new() { Name = "--check-only", Description = "Upgrade: only report whether an upgrade is available", Commands = Set("upgrade") },
            new() { Name = "--channel", ValuePlaceholder = "<stable|latest|prerelease>", Description = "Upgrade: select stable/latest or prerelease releases", Commands = Set("upgrade") },
            new() { Name = "--prerelease", Description = "Upgrade: select the newest prerelease", Commands = Set("upgrade") },
            new() { Name = "--version", ValuePlaceholder = "<tag>", Description = "Upgrade: install a specific release tag", Commands = Set("upgrade") },
            new() { Name = "--integrity-check", Description = "Run PRAGMA integrity_check on the database", Commands = Set("db") },
            new() { Name = "--rebuild", Description = "Delete existing DB and rebuild from scratch", Commands = Set("index") },
            new() { Name = "--optimize", Description = "Optimize the existing FTS5 table without scanning files", Commands = Set("index") },
            new() { Name = "--symbols-only", Description = "Build chunks and symbols while skipping reference graph extraction", Commands = Set("index") },
            new() { Name = "--dry-run", Description = "Preview without writing", Commands = Set("index", "backfill-fold", "vacuum") },
            new() { Name = "--dry-run-path-limit", ValuePlaceholder = "<n>", Description = "Dry run only: candidate path processing limit before truncated lower-bound estimates", Commands = Set("index") },
            new() { Name = "--no-checkpoint", Description = "Skip the automatic DB checkpoint before maintenance", Commands = Set("backfill-fold") },
            new() { Name = "--force", Description = "Bypass the per-database index lock", Commands = Set("index") },
            new() { Name = "--duration-format", ValuePlaceholder = "<auto|seconds|hms>", Description = "Index elapsed time display format", Commands = Set("index") },
            new() { Name = "--max-file-bytes", ValuePlaceholder = "<bytes>", Description = "Override the per-file indexing size limit", Commands = Set("index") },
            new() { Name = "--max-symbols-per-file", ValuePlaceholder = "<n>", Description = "Skip file content, symbols, and references when one file emits too many symbols (max 50000)", Commands = Set("index") },
            new() { Name = "--max-references-per-file", ValuePlaceholder = "<n>", Description = "Skip references when one file emits too many references (max 1000000)", Commands = Set("index") },
            new() { Name = "--parallelism", ValuePlaceholder = "<n>", Description = "Full-scan extraction worker count (default: CPU count capped at 16; also honors CDIDX_INDEX_PARALLELISM)", Commands = Set("index") },
            new() { Name = "--memory-trace", Description = "Include phase memory samples in index JSON output", Commands = Set("index") },
            new() { Name = "--commits", ValuePlaceholder = "<commit-ref>", Description = "Update files changed in given git commits", Commands = Set("index") },
            new() { Name = "--changed-between", ValuePlaceholder = "<old-ref> <new-ref>", Description = "Update files changed between two git refs", Commands = Set("index") },
            new() { Name = "--files", ValuePlaceholder = "<path>", Description = "Update only the specified files", Commands = Set("index") },
            new() { Name = "--watch", Description = "Continuous reindex on file changes (rejects --commits / --changed-between / --files / --dry-run)", Commands = Set("index") },
            new() { Name = "--debounce", ValuePlaceholder = "<ms>", Description = "Watch only: coalesce file events into one update after <ms> of quiet (default 500)", Commands = Set("index") },
            new() { Name = "--watch-pending-path-limit", ValuePlaceholder = "<n>", Description = "Watch only: changed-path queue limit before full-rescan fallback", Commands = Set("index") },
            new() { Name = "--output", ShortName = "-o", ValuePlaceholder = "<path>", Description = "Output bundle path", Commands = Set("report") },
            new() { Name = "--no-log", Description = "Exclude global tool log from bundle", Commands = Set("report") },
            new() { Name = "--include-args", Description = "Include args in bundle log", Commands = Set("report") },
            new() { Name = "--log-lines", ValuePlaceholder = "<n>", Description = "Number of log lines to include in bundle (clamped to 2000)", Commands = Set("report") },
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

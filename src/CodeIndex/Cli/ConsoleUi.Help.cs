using CodeIndex.Database;
using CodeIndex.Diagnostics;
using CodeIndex.Indexer;
using System.Globalization;
using System.Reflection;
using System.Runtime.InteropServices;
using System.Text;
using System.Text.Json;

namespace CodeIndex.Cli;

public static partial class ConsoleUi
{
    public static void PrintUsage(bool showBanner = true)
        => PrintUsageBrief(showBanner);

    public static void PrintUsageBrief(bool showBanner = true)
    {
        if (showBanner)
        {
            PrintBanner();
        }

        Console.WriteLine("Usage:");
        Console.WriteLine("  cdidx <projectPath>");
        Console.WriteLine("  cdidx <command> [options]");
        Console.WriteLine("  cdidx --help-all");
        Console.WriteLine("  cdidx --help-flags");
        Console.WriteLine();
        PrintCommandSummary();
        Console.WriteLine();
        Console.WriteLine("Run `cdidx --help-all` for every command and option, `cdidx --help-flags` for shared flags, or `cdidx <command> --help` for one command.");
        Console.WriteLine();
        Console.WriteLine("Examples:");
        Console.WriteLine("  cdidx ./myproject");
        Console.WriteLine("  cdidx search \"authenticate\"");
        Console.WriteLine("  cdidx inspect Run --body --exclude-tests");
    }

    public static void PrintUsageFull(bool showBanner = true)
    {
        if (showBanner)
        {
            PrintBanner();
        }

        var helpWidth = ShouldUseInteractiveConsole() ? Math.Min(GetWindowWidth(), 120) : 0;
        void WriteHelpLine(string line = "")
        {
            if (helpWidth <= 0)
            {
                Console.WriteLine(line);
                return;
            }

            foreach (var wrapped in WrapHelpLine(line, helpWidth))
                Console.WriteLine(wrapped);
        }

        Console.WriteLine("Usage:");
        Console.WriteLine("  cdidx <projectPath>");
        foreach (var (name, usage) in CommandUsageLines)
        {
            if (HiddenCommandUsageNames.Contains(name))
                continue;

            WriteHelpLine($"  {usage}");
        }
        Console.WriteLine();
        PrintCommandSummary();
        Console.WriteLine();
        PrintFlagReference(WriteHelpLine);
        Console.WriteLine();
        PrintExamples();
    }

    public static void PrintFlagUsage(bool showBanner = true)
    {
        if (showBanner)
        {
            PrintBanner();
        }

        var helpWidth = ShouldUseInteractiveConsole() ? Math.Min(GetWindowWidth(), 120) : 0;
        void WriteHelpLine(string line = "")
        {
            if (helpWidth <= 0)
            {
                Console.WriteLine(line);
                return;
            }

            foreach (var wrapped in WrapHelpLine(line, helpWidth))
                Console.WriteLine(wrapped);
        }

        Console.WriteLine("Usage:");
        Console.WriteLine("  cdidx --help-flags");
        Console.WriteLine();
        PrintFlagReference(WriteHelpLine);
        Console.WriteLine();
        Console.WriteLine("Run `cdidx --help-all` to show commands and examples.");
    }

    private static void PrintCommandSummary()
    {
        Console.WriteLine("Commands:");
        Console.WriteLine("  help <command> [subcommand]  Show help without running the command");
        Console.WriteLine("  index <projectPath>        Build or update the index for a project");
        Console.WriteLine("  hooks                      Install, uninstall, or inspect git hook integration");
        Console.WriteLine("  backfill-fold              Upgrade folded-name columns in an existing index DB");
        Console.WriteLine("  optimize                   Optimize FTS5 segments in an existing index DB");
        Console.WriteLine("  vacuum                     Reclaim free SQLite pages from an existing index DB");
        Console.WriteLine("  search <query>             Full-text search across indexed chunks");
        Console.WriteLine("  recipes                    List built-in search audit recipes");
        Console.WriteLine("  audit <recipe>             Run a built-in search audit recipe");
        Console.WriteLine("  definition <query>         Resolve symbol definitions with extracted ranges");
        Console.WriteLine("  goto <query>               Return one best LSP Location for a definition");
        Console.WriteLine("  references <query>         Find indexed references for a symbol (--kind uses reference kind)");
        Console.WriteLine("  callers <query>            Find callers of a symbol (--kind uses reference kind)");
        Console.WriteLine("  callees <query>            Find callees used by a caller (--kind uses reference kind)");
        Console.WriteLine("  symbols [query]            Search symbols (functions, classes, imports)");
        Console.WriteLine("  files [query|glob]         List indexed files (* and ? positionals use path-glob semantics)");
        Console.WriteLine("  find <query>               Find literal substring matches inside known indexed files");
        Console.WriteLine("  excerpt <path>             Reconstruct a line-range excerpt from indexed chunks");
        Console.WriteLine("  map                        Show a repo-level overview for AI orientation");
        Console.WriteLine("  inspect <query>            Bundle definition, graph, and nearby symbol context");
        Console.WriteLine("  outline <path>             Show a file outline ordered by line, start column, kind, and name");
        Console.WriteLine("  status                     Show database statistics; add --check for freshness, --config for effective config, --explain <field> for field details, or --log-path for logs");
        Console.WriteLine("  workspace                  List manifest members and manage the active workspace");
        Console.WriteLine("  config show                Show resolved workspace config and precedence");
        Console.WriteLine("  upgrade                    Check for and install the latest release via install.sh");
        Console.WriteLine("  validate-config            Validate .cdidx/config.json or .cdidxrc.json");
        Console.WriteLine("  doctor                     Print a redacted environment summary or env-var inventory for bug reports");
        Console.WriteLine("  db --integrity-check       Run SQLite `PRAGMA integrity_check` and report findings");
        Console.WriteLine("  db schema                  Dump SQLite schema entries and PRAGMA user_version");
        Console.WriteLine("  db prune --dry-run|--apply Count or delete orphaned DB rows");
        Console.WriteLine("  diff <db1> <db2>           Compare two index databases; exit 0 identical, 1 drift, 2 schema mismatch, 3 unreadable");
        Console.WriteLine("  report --output <bundle.tgz> Build a redacted crash-repro tarball without replacing existing output; use --overwrite to opt in");
        Console.WriteLine("  validate                   Report encoding issues (U+FFFD origin/severity, BOM, null bytes, mixed line endings, UTF-16 BOM, likely non-UTF8)");
        Console.WriteLine("  impact <query>             Show transitive callers; type queries may return heuristic file-level dependency hints");
        Console.WriteLine("  deps                       Show file-level dependency edges from the reference graph");
        Console.WriteLine("  unused                     Find symbols defined but never referenced (dead code)");
        Console.WriteLine("  hotspots                   Find high-impact symbols; duplicate-name families may fall back conservatively");
        Console.WriteLine("  suggestions                Add, list, inspect, and export local suggestion history");
        Console.WriteLine("  export                     Export ctags or a portable CodeIndex archive");
        Console.WriteLine("  import                     Import a portable CodeIndex archive");
        Console.WriteLine("  languages                  List supported languages and their capabilities");
        Console.WriteLine("  batch                      Run newline-delimited JSON query commands with one DB connection");
        Console.WriteLine("  mcp                        Start MCP server (for AI tools: Claude, Cursor, etc.)");
        Console.WriteLine("  lsp                        Start LSP server over stdio (for LSP-native editors)");
        Console.WriteLine("  completions <shell>        Generate shell completions for bash, zsh, fish, or PowerShell");
        Console.WriteLine("  license                    Show licensing, trademark, and commercial-use summary");
    }

    private static void PrintFlagReference(Action<string> WriteHelpLine)
    {
        Console.WriteLine();
        Console.WriteLine("Index and update options:");
        Console.WriteLine("  --db <path>                Database file path (default for index: <projectPath>/.cdidx/codeindex.db)");
        WriteHelpLine("  .cdidxignore               Optional project-local ignore file; loaded after .gitignore in each directory");
        Console.WriteLine("  --rebuild                  Delete existing DB and rebuild from scratch");
        Console.WriteLine("  --verbose                  Show per-file status ([OK  ]/[SKIP]/[DEL ]/[ERR ])");
        Console.WriteLine("  --dry-run                  Scan files without writing to the database");
        WriteHelpLine($"  --dry-run-path-limit <n>  Dry run only: process at most <n> candidate paths before returning truncated lower-bound estimates (default: {IndexCommandRunner.DefaultDryRunPathLimit}, max: {IndexCommandRunner.MaxDryRunPathLimit})");
        Console.WriteLine("  --force                    Bypass the per-database index lock; only use when no other cdidx index is active");
        WriteHelpLine("  --symbols-only             Build chunks and symbols but skip reference extraction; graph queries stay degraded until a normal index run");
        Console.WriteLine("  --json                     Output results as JSON (for AI/machine use)");
        Console.WriteLine("  --memory-trace             Include phase memory samples in index JSON output");
        Console.WriteLine("  --quiet, -q, --silent      Suppress informational stderr output; errors still print (also honors CDIDX_QUIET=1)");
        Console.WriteLine("  --duration-format <format> Index elapsed time format: `auto` (default), `seconds`, or `hms`; JSON keeps raw elapsed_ms");
        WriteHelpLine("  --notify <mode>           Long index completion signal: auto, bell, osc9, desktop, or none (also honors CDIDX_NOTIFY; quiet/json suppress it)");
        WriteHelpLine("  --max-file-bytes <bytes>  Index only files up to this size (default: 4MiB; also honors CDIDX_MAX_FILE_BYTES; accepts K/M/G suffixes)");
        WriteHelpLine("  --max-symbols-per-file <n> Skip file content, symbols, and references when one file emits too many symbols (default: 5000; max: 50000)");
        WriteHelpLine("  --max-references-per-file <n> Skip references when one file emits too many references (default: 100000; max: 1000000)");
        WriteHelpLine("  --parallelism <n>         Full-scan extraction workers (default: CPU count capped at 8; explicit max: 16; also honors CDIDX_INDEX_PARALLELISM)");
        WriteHelpLine("  --follow-symlinks <mode>  Symlink policy for directories and files: none (default), internal, or all");
        WriteHelpLine("  --include-symbol-kind <kind>[,<kind>]  Keep only matching symbol kinds during indexing");
        WriteHelpLine("  --exclude-symbol-kind <kind>[,<kind>]  Drop matching symbol kinds during indexing");
        Console.WriteLine("  --commits <commit-ref> [commit-ref ...]");
        Console.WriteLine($"                              Update only files changed in the specified git commits (preferred after commits; max {IndexCommandRunner.MaxCommitRefCount} refs, {IndexCommandRunner.MaxCommitRefLength} chars each)");
        Console.WriteLine("  --changed-between <old-ref> <new-ref>");
        Console.WriteLine("                              Update only files changed between two git refs (useful after branch switches)");
        Console.WriteLine("  --files <path> [path ...]  Update only the specified files; old rename/delete paths are not purged unless also listed");
        WriteHelpLine("  --watch                    After the initial scan, stay running and reindex on file changes (FileSystemWatcher / inotify / FSEvents); rejects --commits / --changed-between / --files / --dry-run");
        Console.WriteLine($"  --debounce <ms>            Watch only: coalesce bursts of file events into one update after <ms> of quiet (default: {IndexWatchRunner.DefaultDebounceMs}, max {IndexWatchRunner.MaxDebounceMs})");
        WriteHelpLine($"  --watch-pending-path-limit <n>  Watch only: pending changed-path queue limit before falling back to a full rescan (default: {IndexWatchRunner.DefaultWatchPendingPathLimit}, max: {IndexWatchRunner.MaxWatchPendingPathLimit}; also honors {IndexCommandRunner.WatchPendingPathLimitEnvironmentVariable})");
        Console.WriteLine("  --optimize                 index only: optimize the existing FTS5 table for this project's DB without scanning files");
        WriteHelpLine("  --color <when>             Color output: `auto` (default), `always`, or `never`; flag wins over `CLICOLOR_FORCE` / `NO_COLOR` / `CLICOLOR` env vars, which win over TTY auto-detect");
        WriteHelpLine("  --palette <name>           ANSI palette: `basic` (8-color, default fallback), `256`, or `truecolor`; flag wins over `CDIDX_COLOR_PALETTE` env var, which wins over `COLORTERM` / `TERM` auto-detect");
        WriteHelpLine("  --ascii                    Use ASCII spinner/progress glyphs instead of Unicode glyphs (also honors CDIDX_ASCII=1, NO_UNICODE, TERM=dumb, accessibility env hints, and non-UTF-8 locales)");
        WriteHelpLine("  --no-progress              Disable animated progress/spinner output (also honors CDIDX_DISABLE_PROGRESS=1 and PREFERS_REDUCED_MOTION)");
        Console.WriteLine("  --metrics <path>           Append one JSONL record per CLI command / MCP tool call to <path> (also honors CDIDX_METRICS=<path>)");
        Console.WriteLine("  --log-format <text|json>   Persistent stderr log format (also honors CDIDX_LOG_FORMAT)");
        Console.WriteLine("  --log-retain-count <n>     Persistent stderr log file retention count (also honors CDIDX_LOG_RETAIN)");
        Console.WriteLine("  --log-max-size-mb <n>      Persistent stderr log rotation size cap in MiB (also honors CDIDX_LOG_MAX_SIZE_MB)");
        WriteHelpLine("  --debug-unsafe             Allow raw debug dumps only when CDIDX_DEBUG=unsafe is also set; local troubleshooting only");
        WriteHelpLine("  --strict-version           Treat workspace version pin mismatches as exit code 64 instead of warnings");
        Console.WriteLine("  --help, -h                 Show this help message");
        Console.WriteLine("  --version, -V              Show version information");
        Console.WriteLine("  --license                  Show licensing, trademark, and commercial-use summary");
        Console.WriteLine("  --completions <shell>      Generate shell completions (bash, zsh, fish, powershell)");
        Console.WriteLine();
        Console.WriteLine("Update workflows:");
        Console.WriteLine("  Use --commits with a project path after normal commits; git diff sees rename/delete paths too.");
        Console.WriteLine("  Use --changed-between <old-ref> <new-ref> after switching branches to refresh only changed files.");
        Console.WriteLine("  Use --files only for known in-place edits or new files; old rename/delete paths stay indexed unless also listed.");
        Console.WriteLine("  Incremental writes optimize FTS5 opportunistically after a small maintenance threshold; run `cdidx optimize` for manual maintenance.");
        Console.WriteLine();
        Console.WriteLine("Query options:");
        Console.WriteLine("  --db <path>                Database file path (default: .cdidx/codeindex.db in current directory)");
        WriteHelpLine("  --json                     Output as JSON (search/symbols/files stream ndjson by default; search/symbols/files/validate accept --json=array for one array)");
        WriteHelpLine("  --verbose                  Query commands: emit debug diagnostics to stderr; with --json, append an _debug JSON object");
        WriteHelpLine("  --quiet, -q, --silent      Query commands: suppress informational stderr output, including zero-result hints and summaries; errors still print. Overrides --verbose stderr text.");
        WriteHelpLine("  --profile                  Read commands: append SQL timing, row-count, and EXPLAIN QUERY PLAN JSON after the normal result");
        WriteHelpLine("  --slow-query-ms <n>        Read commands: log profiled SQL statements that take at least <n> ms (use 0 to log every statement)");
        Console.WriteLine("  --limit <n>, --top <n>, --max-results <n>");
        Console.WriteLine("                              Max results to return (default: 20)");
        Console.WriteLine("  --lang <lang>              Filter by language (aliases: bat, cmd, cshtml, razor, ts, tsx, cts, mts)");
        Console.WriteLine("  --path <glob>              Restrict matches to glob-style path patterns (* and ?)");
        WriteHelpLine($"  --query <query>            Pass a query literal, useful when the query starts with '-' (`search`/`find` max {QueryLimits.MaxQueryLength} chars)");
        WriteHelpLine("  --named-query <name>=<query>  search only: add a named ad hoc batch query; repeat to run related searches with grouped compact results");
        Console.WriteLine("  --exclude-path <glob>      Exclude glob-style path patterns (* and ?) (repeatable)");
        Console.WriteLine("  --exclude-tests            Exclude likely test files");
        WriteHelpLine("  --audit-scope <source|all> search/unused: source uses production-code cleanup defaults; all disables source-scope defaults");
        Console.WriteLine("  --source-only              search only: shorthand for --audit-scope source on ad hoc and named searches");
        Console.WriteLine("  --exclude-comments         search only: suppress comment-only matches");
        Console.WriteLine("  --exclude-strings          search only: suppress string, regex, and help-text matches");
        Console.WriteLine("  --exclude-fixtures         search only: suppress fixture-only matches in tests");
        WriteHelpLine("  --origin/--match-origin <origin> search only: keep only matches from selected origins (code, comment, string_literal, regex_literal, help_text, unknown; repeatable or comma-separated)");
        WriteHelpLine("  --exclude-origin <origin>  search only: drop matches from selected origins while keeping other origins in the same result");
        Console.WriteLine("  --include-generated        Include generated files in query results");
        Console.WriteLine("  --snippet-lines <n>        search/find snippet length (1-20, default: search 8; find 1)");
        Console.WriteLine("  --snippet-focus <mode>     search only: long-line focus mode (leftmost|quality|proximity, default: quality)");
        WriteHelpLine($"  --max-line-width <n>       search/references/callers/callees/find/excerpt/impact/inspect only: clamp very long single-line snippet/context/excerpt payloads (`0` disables clamping; default: {LineWidthFormatter.DefaultMaxLineWidth})");
        WriteHelpLine("  --focus-line <line>        find/excerpt: focus a line; excerpt keeps the leading window when no column is supplied");
        Console.WriteLine("  --focus-column <n>         find/excerpt: focus a specific 1-based column");
        Console.WriteLine("  --focus-length <n>         excerpt: width of the focused span (default: 1, requires --focus-column)");
        Console.WriteLine("  --no-semantic-tokens       excerpt JSON: omit semantic_tokens for compact line/window payloads");
        WriteHelpLine($"  --fts                      Use raw FTS5 query syntax for search (content:term, NEAR(a b, 5), OR, NOT, groups, prefix*, \"phrase\"; search query max {QueryLimits.MaxQueryLength} chars; raw FTS parser max {DbReader.MaxRawFtsQueryLength} chars, {DbReader.MaxRawFtsBooleanOperators} boolean ops, {DbReader.MaxRawFtsNearOperators} NEAR ops; trailing * is a prefix shorthand in literal-safe mode)");
        Console.WriteLine("  --exact                    Backward-compatible shorthand.");
        Console.WriteLine("                              Prefer --exact-substring for search,");
        Console.WriteLine("                              --exact for find,");
        Console.WriteLine("                              and --exact-name for symbol/graph lookups.");
        Console.WriteLine("                              Combining exact-match flags is rejected.");
        Console.WriteLine("  --exact-substring          Search only: case-sensitive exact substring");
        Console.WriteLine("                              (no FTS5)");
        Console.WriteLine("  --token-boundary           Search only: exact substring plus code-token");
        Console.WriteLine("                              boundaries; excludes longer identifiers.");
        Console.WriteLine("  --exact-name               Exact name match for symbols, definition,");
        Console.WriteLine("                              references, callers, callees, and inspect.");
        Console.WriteLine("                              Uses NFKC + Unicode CaseFold when ready.");
        Console.WriteLine("                              Legacy/stale-fold DBs fall back to ASCII NOCASE;");
        Console.WriteLine("                              run `cdidx backfill-fold` or check fold_ready.");
        WriteHelpLine("  --kind <kind>              definition/symbols/outline/hotspots/unused: symbol kind; references: reference kind (call/instantiate/subscribe/attribute/annotation/type_tag/bcl_regex_without_timeout); callers/callees: call-graph kinds only (call/instantiate/subscribe — metadata kinds rejected, use references instead); validate: issue kind");
        WriteHelpLine("  --sort <mode>              Symbols/outline: order audit output by a ranking signal; outline also accepts source, kind, references, size, complexity, path, and name");
        Console.WriteLine("  --severity <s>             validate only: filter issues by severity: info, warning, error");
        Console.WriteLine("  --visibility <v[,v]>       Filter symbols/definitions/unused/hotspots by visibility: public, protected, internal, private");
        WriteHelpLine("  --exclude-visibility <v[,v]> Exclude symbols/definitions/unused/hotspots by visibility");
        WriteHelpLine("  --count                    Count only; result limits are ignored by count modes, but scan caps can still mark approximate counts as degraded");
        WriteHelpLine("  --group-partials           definition/symbols/inspect symbol mode: collapse partial type declarations into logical families while preserving physical counts and definition_sites");
        WriteHelpLine("  --bucket <bucket>          unused only: filter one unused confidence bucket");
        WriteHelpLine("  --min-confidence <s>       unused only: filter medium or low confidence candidates; --confidence is an alias");
        WriteHelpLine("  --all                     unused only: include low-confidence contract-domain candidates suppressed by default");
        WriteHelpLine("  --actionable               unused only: preset for private medium-confidence cleanup candidates");
        Console.WriteLine("  --since <datetime>         Filter to files modified since this timestamp (ISO 8601)");
        Console.WriteLine("  --no-dedup                 search only: return every raw overlapping chunk hit (debug/density)");
        WriteHelpLine($"  --require-before/--require-after <query>  search only: keep primary matches only when the guard query appears within --guard-window lines before/after the match (default {DbReader.DefaultSearchGuardWindow}, max {DbReader.MaxSearchGuardWindow})");
        WriteHelpLine("  --reject-before/--reject-after <query>    search only: drop primary matches when the guard query appears within the same before/after window; useful for finding API calls missing nearby checks");
        WriteHelpLine("  --guard-scope <window|same-line> search only: evaluate guards in the line window (default) or only on the same line before/after the primary match");
        WriteHelpLine("  --bytes                    files: sort by size and show raw byte counts in human output; map: show raw byte counts; JSON always keeps raw integer bytes");
        Console.WriteLine("  --min-entrypoint-confidence <n>  map only: omit entrypoint candidates below this 0.0..1.0 confidence");
        WriteHelpLine("  --max-hops <n>             Max BFS hops for impact analysis, inclusive (default: 5; --max-hops 2 returns callers at hop 1 and 2; --max-hops 0 resolves the symbol without traversing callers)");
        Console.WriteLine("  --depth <n>                Deprecated alias for --max-hops");
        Console.WriteLine("  --reverse                  Reverse direction for deps (show dependents)");
        WriteHelpLine("  --group-by <unit>          search: with --count, group rows by file, symbol, origin, return-type, or subsystem; hotspots: group by symbol or file, or by statement only with --lang sql");
        WriteHelpLine("  --group-by-name            hotspots: collapse rows sharing (name, kind) across files; JSON keeps capped paths plus full definition_site_details");
        WriteHelpLine("  --with-paths               impact: also emit `paths` per caller — the shortest call chains [root, ..., caller] (diamond graphs surface every converging route, capped per row)");
        WriteHelpLine("  unused reflection note     C# nameof/typeof and direct reflection member-name literals such as GetMethod(\"Foo\") are indexed; dynamically constructed reflection names may need manual review");
        WriteHelpLine("  Note: if a query itself starts with '-', pass it with --query <query> or -- <query>; for option values that start with '--', use --opt=<value>.");
    }

    private static void PrintExamples()
    {
        Console.WriteLine("Examples:");
        Console.WriteLine("  cdidx ./myproject                             Index a project");
        Console.WriteLine("  cdidx backfill-fold                           Upgrade folded-name columns in an existing DB");
        Console.WriteLine("  cdidx optimize --dry-run --json               Preview FTS5 optimization work without writing");
        Console.WriteLine("  cdidx optimize                                Optimize FTS5 segments in an existing DB");
        Console.WriteLine("  cdidx vacuum --dry-run --json                 Estimate DB free pages and maintenance guidance");
        Console.WriteLine("  cdidx index ./myproject --commits abc123      Update DB from one commit");
        Console.WriteLine("  cdidx index ./myproject --commits abc123 def456");
        Console.WriteLine("                                              Update DB from multiple commits");
        Console.WriteLine("  cdidx index ./myproject --changed-between main feature");
        Console.WriteLine("                                              Update DB from files changed between two refs");
        Console.WriteLine("  cdidx index ./myproject --files src/app.cs    Update specific files");
        Console.WriteLine("  cdidx index ./myproject --watch               Run an initial scan, then keep the index live as files change (Ctrl+C to stop)");
        Console.WriteLine("  cdidx export ctags --output tags              Export editor tags for Vim, Emacs, and Sublime");
        Console.WriteLine("  cdidx export codeindex.cdidx.zip              Export a portable CodeIndex archive");
        Console.WriteLine("  cdidx export codeindex.cdidx.zip --overwrite  Explicitly replace an existing portable archive");
        Console.WriteLine("  cdidx import codeindex.cdidx.zip              Import a portable CodeIndex archive");
        Console.WriteLine("  cdidx import codeindex.cdidx.zip --dry-run    Validate an archive without replacing the DB");
        Console.WriteLine("  cdidx search \"authenticate\"                    Full-text search");
        Console.WriteLine("  cdidx search \"auth*\"                          Prefix shorthand in literal-safe mode");
        Console.WriteLine("  cdidx search --query --path --path README.md   Search for a literal option token");
        Console.WriteLine("  cdidx search --named-query pack=\"dotnet pack\" --named-query push=\"nuget push\" --format compact");
        Console.WriteLine("                                              Run named ad hoc searches with compact snippets");
        Console.WriteLine("  cdidx search \"Run();\" --exact-substring        Case-sensitive exact substring search");
        Console.WriteLine("  cdidx search \"File.ReadAllText\" --exact-substring --reject-before \"Length\" --guard-window 8");
        Console.WriteLine("                                              Find calls without a nearby preceding size guard");
        Console.WriteLine("  cdidx search authenticate --json=array         Emit search results as one JSON array");
        Console.WriteLine("  cdidx search authenticate --profile            Append SQL profile JSON for slow-query debugging");
        Console.WriteLine("  cdidx search authenticate --verbose            Emit query debug diagnostics on stderr");
        Console.WriteLine("  cdidx definition ResolveGitCommonDir --body   Show a symbol definition and body");
        Console.WriteLine("  cdidx references ResolveGitCommonDir          Find indexed references");
        Console.WriteLine("  cdidx references DbContext --kind instantiate Filter constructor sites by reference kind");
        Console.WriteLine("  cdidx references e --path dist/app.js --max-line-width 120");
        Console.WriteLine("                                              Clamp a minified single-line context window");
        Console.WriteLine("  cdidx excerpt src/app.js --start 120 --focus-column 88 --max-line-width 120");
        Console.WriteLine("                                              Keep the requested token visible inside a long line");
        Console.WriteLine("  cdidx callers ResolveGitCommonDir             Find callers");
        Console.WriteLine("  cdidx callees AddToGitExclude                 Find callees used by a caller");
        Console.WriteLine("  cdidx symbols Run --exact-name                Exact symbol-name match");
        Console.WriteLine("  cdidx symbols UserService --kind class        Find class definitions");
        Console.WriteLine("  cdidx find guard --path src/Auth.cs --after 2 Find literal matches inside a known file");
        Console.WriteLine("  cdidx find --path README.md -- --path         Search a literal that starts with '-'");
        Console.WriteLine("  cdidx excerpt src/app.cs --start 10 --end 20  Reconstruct a file excerpt");
        Console.WriteLine("  cdidx map --path src/ --exclude-tests          Show a repo map for source code");
        Console.WriteLine("  cdidx inspect Run --body --exclude-tests       Inspect one symbol with bundled context");
        Console.WriteLine("  cdidx outline src/app.cs --json                Symbol outline of a single file");
        Console.WriteLine("  cdidx deps --path src/ --exclude-tests          Show file-level dependency edges");
        Console.WriteLine("  cdidx deps --reverse --path src/app.cs          Show what depends on a file");
        Console.WriteLine("  cdidx unused --lang csharp --actionable          Find private cleanup candidates");
        Console.WriteLine("  cdidx hotspots --lang csharp --exclude-tests    Find high-impact symbols with conservative duplicate fallback");
        Console.WriteLine("  cdidx hotspots --group-by=file --json           Compare hotspot volume by target file");
        Console.WriteLine("  cdidx hotspots --group-by-name --exclude-tests  Collapse same-name hotspots across files");
        Console.WriteLine("  cdidx impact Run --max-hops 0 --exclude-tests  Resolve a symbol without traversing callers");
        Console.WriteLine("  cdidx impact FolderDiffService --json           Type query may return heuristic file-level dependency hints");
        Console.WriteLine("  cdidx files --lang python                      List Python files");
        Console.WriteLine("  cdidx files --since 2024-01-01                 Files modified since a date");
        Console.WriteLine("  cdidx status --json                            DB stats as JSON");
        Console.WriteLine("  cdidx status --config                          Effective configuration as JSON");
        Console.WriteLine("  cdidx validate-config                          Validate checked-in config");
        Console.WriteLine("  cdidx languages                                Show supported languages");
        Console.WriteLine("  cdidx --completions zsh > ~/.zfunc/_cdidx      Generate a zsh completion script");
        Console.WriteLine("  cdidx license                                  Show licensing and commercial-use terms");
    }

    internal static IReadOnlyList<string> WrapHelpLine(string line, int maxWidth)
    {
        if (maxWidth <= 0 || line.Length <= maxWidth)
            return [line];

        var continuationIndent = GetHelpContinuationIndent(line);
        return WrapLineByWords(line, maxWidth, continuationIndent);
    }

    private static string GetHelpContinuationIndent(string line)
    {
        var leading = 0;
        while (leading < line.Length && line[leading] == ' ')
            leading++;

        for (var i = leading + 1; i < line.Length - 1; i++)
        {
            if (line[i] == ' ' && line[i + 1] == ' ')
            {
                while (i < line.Length && line[i] == ' ')
                    i++;
                if (i < line.Length)
                    return new string(' ', i);
                break;
            }
        }

        return new string(' ', Math.Min(leading + 2, 8));
    }

    private static IReadOnlyList<string> WrapLineByWords(string line, int maxWidth, string continuationIndent)
    {
        maxWidth = Math.Max(1, maxWidth);
        if (continuationIndent.Length >= maxWidth)
            continuationIndent = new string(' ', Math.Max(0, Math.Min(2, maxWidth - 1)));

        var lines = new List<string>();
        var current = line;
        while (current.Length > maxWidth)
        {
            var breakAt = current.LastIndexOf(' ', Math.Min(maxWidth, current.Length - 1));
            if (breakAt <= 0 || current[..breakAt].Trim().Length == 0)
                breakAt = maxWidth;

            lines.Add(current[..breakAt].TrimEnd());
            var nextStart = breakAt < current.Length && current[breakAt] == ' ' ? breakAt + 1 : breakAt;
            current = continuationIndent + current[nextStart..].TrimStart();
        }

        lines.Add(current);
        return lines;
    }

    public static void PrintLicenseSummary()
    {
        Console.WriteLine("cdidx / CodeIndex license");
        Console.WriteLine();
        Console.WriteLine("License: Functional Source License, Version 1.1, ALv2 Future License (FSL-1.1-ALv2)");
        Console.WriteLine("Copyright: Copyright 2026 Widthdom.");
        Console.WriteLine("Summary: use, modification, and distribution are allowed for non-competing purposes, including internal, commercial, AI, IDE, MCP, CI, and scripting integrations.");
        Console.WriteLine("Competing commercial products or services require a separate written agreement with Widthdom.");
        Console.WriteLine("Names and trademarks: CodeIndex and cdidx are not licensed for derivative product, package, service, or endorsement branding.");
        Console.WriteLine();
        Console.WriteLine("See LICENSE, LICENSES/FSL-1.1-ALv2.txt, LICENSES/Apache-2.0.txt, COMMERCIAL_LICENSE.md, INTEGRATION_POLICY.md, and TRADEMARKS.md for the controlling terms.");
    }

    internal static LicenseJsonResult BuildLicenseJsonResult() =>
        new(
            JsonOutputContract.ApiVersion,
            new LicenseTermsJsonResult(
                "FSL-1.1-ALv2",
                "Functional Source License, Version 1.1, ALv2 Future License",
                "Apache-2.0",
                "LICENSE"),
            "Copyright 2026 Widthdom.",
            new LicenseCommercialUseJsonResult(
                NonCompetingUseAllowed: true,
                CompetingProductsOrServicesRequireSeparateAgreement: true,
                "Use, modification, and distribution are allowed for non-competing purposes, including internal, commercial, AI, IDE, MCP, CI, and scripting integrations."),
            new LicenseTrademarkJsonResult(
                ["CodeIndex", "cdidx"],
                DerivativeBrandingAllowed: false,
                EndorsementBrandingAllowed: false,
                "CodeIndex and cdidx are not licensed for derivative product, package, service, or endorsement branding."),
            [
                "LICENSE",
                "LICENSES/FSL-1.1-ALv2.txt",
                "LICENSES/Apache-2.0.txt",
                "COMMERCIAL_LICENSE.md",
                "INTEGRATION_POLICY.md",
                "TRADEMARKS.md",
            ]);

    public static string? GetUsageLine(string command)
    {
        command = NormalizeCommandUsageName(command);
        foreach (var (name, usage) in CommandUsageLines)
        {
            if (string.Equals(name, command, StringComparison.Ordinal))
                return usage;
        }

        return null;
    }

    public static bool PrintCommandUsage(string command)
    {
        command = NormalizeCommandUsageName(command);
        var usages = GetCommandUsageLines(command);
        if (usages.Count == 0)
            return false;

        Console.WriteLine("Usage:");
        foreach (var usage in usages)
            Console.WriteLine($"  {usage}");
        var schemaCommand = GetFlagSchemaCommandName(command);
        var helpFlags = string.Equals(command, schemaCommand, StringComparison.Ordinal)
            && CliFlagSchema.HasAuthoritativeHelpOptions(schemaCommand)
                ? CliFlagSchema.GetCompletionFlagsForCommand(schemaCommand)
                : [];
        if (helpFlags.Count > 0)
        {
            Console.WriteLine();
            Console.WriteLine("Options:");
            foreach (var flag in helpFlags)
            {
                var names = flag.ShortName is null ? flag.Name : $"{flag.Name}, {flag.ShortName}";
                var token = flag.ValuePlaceholder is null ? names : $"{names} {flag.ValuePlaceholder}";
                Console.WriteLine($"  {token}");
                Console.WriteLine($"      {flag.Description}");
            }
        }
        var notes = GetCommandUsageNotes(command);
        if (notes.Count > 0)
        {
            Console.WriteLine();
            Console.WriteLine("Notes:");
            foreach (var note in notes)
                Console.WriteLine($"  {note}");
        }
        Console.WriteLine();
        Console.WriteLine("Run `cdidx --help` to show all commands and shared options.");
        return true;
    }

    private static IReadOnlyList<string> GetCommandUsageLines(string command)
    {
        command = NormalizeCommandUsageName(command);
        var usages = new List<string>();
        foreach (var (name, usage) in CommandUsageLines)
        {
            if (string.Equals(name, command, StringComparison.Ordinal)
                || string.Equals(command, "index", StringComparison.Ordinal) && name.StartsWith("index-", StringComparison.Ordinal))
            {
                usages.Add(usage);
            }
        }

        return usages;
    }

    private static IReadOnlyList<string> GetCommandUsageNotes(string command)
    {
        command = NormalizeCommandUsageName(command);
        var notes = new List<string>();
        foreach (var (name, note) in CommandUsageNotes)
        {
            if (string.Equals(name, command, StringComparison.Ordinal))
                notes.Add(note);
        }

        return notes;
    }

    private static string GetFlagSchemaCommandName(string command)
    {
        if (command.StartsWith("db-", StringComparison.Ordinal))
            return "db";
        if (command.StartsWith("hooks-", StringComparison.Ordinal))
            return "hooks";
        return command == "--completions" ? "completions" : command;
    }

    private static string NormalizeCommandUsageName(string command) =>
        CliCommandCatalog.NormalizePublicCommandName(command);

    // --- Did-you-mean / もしかして ---

    /// <summary>
    /// Find the closest matching command name using Damerau-Levenshtein distance.
    /// Short commands use a stricter threshold to avoid unrelated suggestions.
    /// Damerau-Levenshtein距離で最も近いコマンド名を返す。短いコマンドは無関係な推薦を避けるため閾値を厳しくする。
    /// </summary>
    public static string? FindClosestCommand(string input) =>
        FindClosestMatch(input, CliCommandCatalog.PublicCommandNames);

    /// <summary>
    /// Find the closest match for <paramref name="input"/> from <paramref name="candidates"/>
    /// using Damerau-Levenshtein distance with the same length-aware threshold the
    /// command suggester uses (#1582). Comparison is case-insensitive. Returns the original
    /// (cased) candidate string, or <c>null</c> when no candidate is within the threshold.
    /// 任意の候補集合に対して Damerau-Levenshtein 距離で最も近い候補を返す (#1582)。
    /// 短い入力には厳しめの距離閾値を適用し、無関係な推薦を避ける。比較は case-insensitive。
    /// </summary>
    public static string? FindClosestMatch(string? input, IEnumerable<string> candidates)
    {
        var normalized = NormalizeSuggestionInput(input);
        if (normalized == null)
            return null;

        string? best = null;
        var bestDist = int.MaxValue;
        foreach (var candidate in candidates)
        {
            if (string.IsNullOrEmpty(candidate))
                continue;
            if (candidate.Length > MaxSuggestionInputCharLength)
                continue;
            var candidateNormalized = candidate.ToLowerInvariant();
            if (string.Equals(normalized, candidateNormalized, StringComparison.Ordinal))
                return candidate;
            var dist = DamerauLevenshteinDistance(normalized, candidateNormalized);
            if (dist > GetSuggestionDistanceThreshold(normalized.Length, candidateNormalized.Length))
                continue;
            if (dist < bestDist)
            {
                bestDist = dist;
                best = candidate;
            }
        }
        return best;
    }

    /// <summary>
    /// Return up to <paramref name="maxResults"/> closest candidates for <paramref name="input"/>,
    /// ordered by Damerau-Levenshtein distance. Useful for structured suggestions in MCP
    /// error payloads (#1582). Returns an empty list when no candidate is within the threshold.
    /// Damerau-Levenshtein 距離で近い候補を最大 <paramref name="maxResults"/> 件まで返す。
    /// MCP の structured error payload で `similar_values` を返す用途を想定する (#1582)。
    /// </summary>
    public static IReadOnlyList<string> FindClosestMatches(string? input, IEnumerable<string> candidates, int maxResults = 3)
    {
        var normalized = NormalizeSuggestionInput(input);
        if (normalized == null || maxResults <= 0)
            return Array.Empty<string>();

        var matches = new List<(string Candidate, int Distance)>();
        foreach (var candidate in candidates)
        {
            if (string.IsNullOrEmpty(candidate))
                continue;
            if (candidate.Length > MaxSuggestionInputCharLength)
                continue;
            var candidateNormalized = candidate.ToLowerInvariant();
            if (string.Equals(normalized, candidateNormalized, StringComparison.Ordinal))
                continue;
            var dist = DamerauLevenshteinDistance(normalized, candidateNormalized);
            if (dist > GetSuggestionDistanceThreshold(normalized.Length, candidateNormalized.Length))
                continue;
            matches.Add((candidate, dist));
        }
        return matches
            .OrderBy(m => m.Distance)
            .ThenBy(m => m.Candidate, StringComparer.Ordinal)
            .Select(m => m.Candidate)
            .Take(maxResults)
            .ToList();
    }

    private static string? NormalizeSuggestionInput(string? input)
    {
        if (input == null || input.Length > MaxSuggestionInputCharLength || string.IsNullOrWhiteSpace(input))
            return null;

        return input.ToLowerInvariant();
    }

    private static int GetSuggestionDistanceThreshold(int inputLength, int commandLength)
    {
        var shorter = Math.Min(inputLength, commandLength);
        return shorter switch
        {
            <= 4 => 1,
            <= 10 => 2,
            _ => 3,
        };
    }

    private static int DamerauLevenshteinDistance(string s, string t)
    {
        var n = s.Length;
        var m = t.Length;
        var d = new int[n + 1, m + 1];
        for (var i = 0; i <= n; i++) d[i, 0] = i;
        for (var j = 0; j <= m; j++) d[0, j] = j;
        for (var i = 1; i <= n; i++)
        {
            for (var j = 1; j <= m; j++)
            {
                var cost = s[i - 1] == t[j - 1] ? 0 : 1;
                d[i, j] = Math.Min(Math.Min(d[i - 1, j] + 1, d[i, j - 1] + 1), d[i - 1, j - 1] + cost);
                if (i > 1 && j > 1 && s[i - 1] == t[j - 2] && s[i - 2] == t[j - 1])
                    d[i, j] = Math.Min(d[i, j], d[i - 2, j - 2] + 1);
            }
        }
        return d[n, m];
    }

    // --- Shell Completions / シェル補完 ---

    /// <summary>
    /// Print shell completion script. Returns false for unknown shells.
    /// シェル補完スクリプトを出力。不明なシェルの場合はfalseを返す。
    /// </summary>
    public static bool PrintCompletions(string shell)
    {
        try
        {
            Console.WriteLine(GetCompletionScript(shell));
            return true;
        }
        catch (ArgumentOutOfRangeException)
        {
            return false;
        }
    }

    internal static string GetCompletionScript(string shell) =>
        ConsoleCompletionRenderer.GetCompletionScript(shell);

    // --- Helpers / ヘルパー ---

    private static ColorMode _colorMode = ColorMode.Auto;
    private static ColorPalette? _explicitPalette;
    private static bool? _windowsVirtualTerminalProcessingEnabled;
    private static Func<bool>? _windowsVirtualTerminalProcessingDetectorForTests;
    private const int StdOutputHandle = -11;
    private const uint EnableVirtualTerminalProcessing = 0x0004;

    /// <summary>
    /// Set the active color-output mode. <see cref="ColorMode.Always"/> and
    /// <see cref="ColorMode.Never"/> short-circuit env / TTY checks in
    /// <see cref="ShouldUseColor"/>; <see cref="ColorMode.Auto"/> defers to
    /// the existing CLICOLOR_FORCE / NO_COLOR / CLICOLOR / TTY chain.
    /// 色出力モードを設定する。Always / Never は環境変数と TTY 判定を上書きする。
    /// </summary>
    public static void SetColorMode(ColorMode mode) => _colorMode = mode;

}

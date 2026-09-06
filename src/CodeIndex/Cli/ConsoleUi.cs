using CodeIndex.Database;
using CodeIndex.Diagnostics;
using CodeIndex.Indexer;
using System.Globalization;
using System.Reflection;
using System.Runtime.InteropServices;
using System.Text;
using System.Text.Json;

namespace CodeIndex.Cli;

/// <summary>
/// User-visible color output policy. Drives ANSI escape emission.
/// 色出力ポリシー。ANSI エスケープ発行を制御する。
/// </summary>
public enum ColorMode
{
    /// <summary>Honor env vars (NO_COLOR / CLICOLOR_FORCE / CLICOLOR) and fall back to TTY auto-detect.</summary>
    Auto = 0,
    /// <summary>Always emit ANSI escapes, even when stdout is redirected.</summary>
    Always = 1,
    /// <summary>Never emit ANSI escapes, even on a TTY.</summary>
    Never = 2,
}

/// <summary>
/// ANSI color palette to use when color output is enabled. <see cref="Basic"/>
/// stays within the 8 standard SGR colors (30–37) and avoids the bright-black
/// dim escape (<c>\x1b[90m</c>), which is unreadable on many SSH/CI terminals.
/// <see cref="Color256"/> uses 256-color codes (`\x1b[38;5;Nm`) for higher
/// contrast on capable terminals. <see cref="Truecolor"/> uses 24-bit RGB
/// (`\x1b[38;2;R;G;Bm`) for terminals that advertise truecolor via
/// <c>COLORTERM=truecolor|24bit</c>.
/// 色出力で使用する ANSI パレット。Basic は標準8色のみで `\x1b[90m`（dim）を
/// 避け、SSH / CI 端末でも可読性を確保する。Color256 / Truecolor はそれぞれ
/// 256色 / 24ビットRGB を用い、対応端末で高コントラストを実現する。
/// </summary>
public enum ColorPalette
{
    /// <summary>Standard 8-color ANSI palette (30–37); avoids dim (`\x1b[90m`).</summary>
    Basic = 0,
    /// <summary>256-color ANSI palette (`\x1b[38;5;Nm`).</summary>
    Color256 = 1,
    /// <summary>24-bit RGB / truecolor palette (`\x1b[38;2;R;G;Bm`).</summary>
    Truecolor = 2,
}

public enum DurationOutputFormat
{
    Auto = 0,
    Seconds = 1,
    Hms = 2,
}

public enum CompletionNotificationMode
{
    Auto = 0,
    None = 1,
    Bell = 2,
    Osc9 = 3,
}

/// <summary>
/// Console UI helpers: spinner, progress bar, banner, and easter egg messages.
/// コンソールUIヘルパー: スピナー、プログレスバー、バナー、イースターエッグメッセージ。
/// </summary>
public static partial class ConsoleUi
{
    public const string DisableProgressEnvironmentVariable = "CDIDX_DISABLE_PROGRESS";
    public const string PrefersReducedMotionEnvironmentVariable = "PREFERS_REDUCED_MOTION";
    public const int SummaryLabelWidth = 9;
    internal const int MaxVersionJsonBytes = 16 * 1024;
    internal const int MaxVersionJsonDepth = 8;
    private const string FallbackVersion = "0.0.0";

    private static readonly (string Command, string Usage)[] CommandUsageLines =
    [
        ("index", "cdidx index <projectPath> [--db <path>] [--rebuild [--yes]] [--optimize [--show-paths]] [--symbols-only] [--verbose] [--dry-run [--dry-run-path-limit <n>]] [--force] [--quiet] [--json] [--allow-partial] [--memory-trace] [--duration-format <auto|seconds|hms>] [--notify <auto|bell|osc9|desktop|none>] [--max-file-bytes <bytes>] [--max-symbols-per-file <n>] [--max-references-per-file <n>] [--follow-symlinks <none|internal|all>] [--include-symbol-kind <kind>[,<kind>]] [--exclude-symbol-kind <kind>[,<kind>]] [--watch [--debounce <ms>] [--watch-pending-path-limit <n>]]"),
        ("hooks", "cdidx hooks <install|uninstall|status> [--project <path>] [--force] [--dry-run] [--json]"),
        ("backfill-fold", "cdidx backfill-fold [--db <path>] [--dry-run] [--checkpoint|--no-checkpoint] [--show-paths] [--json]"),
        ("optimize", "cdidx optimize [--db <path>] [--dry-run] [--show-paths] [--json]"),
        ("vacuum", "cdidx vacuum [--db <path>] [--dry-run] [--show-paths] [--json]"),
        ("index-commits", "cdidx index <projectPath> --commits <commit-ref> [commit-ref ...] [--db <path>] [--verbose] [--dry-run [--dry-run-path-limit <n>]] [--json] [--allow-partial] [--memory-trace] [--duration-format <auto|seconds|hms>] [--max-file-bytes <bytes>] [--include-symbol-kind <kind>[,<kind>]] [--exclude-symbol-kind <kind>[,<kind>]]"),
        ("index-changed-between", "cdidx index <projectPath> --changed-between <old-ref> <new-ref> [--db <path>] [--verbose] [--dry-run [--dry-run-path-limit <n>]] [--json] [--allow-partial] [--memory-trace] [--duration-format <auto|seconds|hms>] [--max-file-bytes <bytes>] [--include-symbol-kind <kind>[,<kind>]] [--exclude-symbol-kind <kind>[,<kind>]]"),
        ("index-files", "cdidx index <projectPath> --files <path> [path ...] [--db <path>] [--verbose] [--dry-run [--dry-run-path-limit <n>]] [--json] [--allow-partial] [--memory-trace] [--duration-format <auto|seconds|hms>] [--max-file-bytes <bytes>] [--include-symbol-kind <kind>[,<kind>]] [--exclude-symbol-kind <kind>[,<kind>]]"),
        ("search", "cdidx search <query>|--query <query>|-- <query>|--named-query <name>=<query> [--named-query <name>=<query> ...]|--recipe <name|name/query> [--include-query <name>] [--exclude-query <name>]|--list-recipes [--query <filter>] [--names|--summary-only] [--cursor <cursor>] [--audit-scope <source|production-and-tooling|all>] [--source-only] [--show-excluded] [--db <path>] [--json[=ndjson|array]] [--pretty] [--format <text|json|count|compact|grouped|csv|tsv|lsp|qf|sarif|issue-drafts>] [--open-issues <path|github|github:owner/name>] [--repo <owner/name>] [--duplicate-confidence <low|medium|high>|--duplicate-threshold <score>] [--issue-title <title>] [--issue-label <label>] [--verbose] [--limit <n>|--top <n>|--max-results <n>] [--total-limit <n>] [--lang <lang>] [--path <glob>] [--exclude-path <glob>] [--exclude-tests] [--exclude-comments] [--exclude-strings] [--exclude-fixtures] [--snippet-lines <n>] [--snippet-focus <leftmost|quality|proximity>] [--max-line-width <n>] [--fts] [--exact|--exact-substring|--token-boundary] [--prefix] [--count] [--group-by <file|symbol|origin|return-type|subsystem>] [--since <datetime>] [--no-dedup] [--no-visibility-rank] [--require-before <query>] [--require-after <query>] [--reject-before <query>] [--reject-after <query>] [--guard-window <n>] [--guard-scope <window|same-line>] [--unique <path|file|symbol|origin|return-type|subsystem>] [--count-by <path|file|symbol|origin|return-type|subsystem>] [--origin <origin>] [--match-origin <origin>] [--exclude-origin <origin>] [--result-kind <kind>] [--search-fields <csv>] [--results-only] [--first-per-file] [--sample <n>] [--per-file-limit <n>] [--max-json-bytes <n>] [--allow-partial] [--next-steps]"),
        ("recipes", "cdidx recipes [list] [--query <filter>] [--names|--summary-only] [--json] [--pretty] [--format <text|json|compact>] [--max-json-bytes <n>]"),
        ("recipes-list", "cdidx recipes list [--query <filter>] [--names|--summary-only] [--json] [--pretty] [--format <text|json|compact>] [--max-json-bytes <n>]"),
        ("audit-baseline-export", "cdidx audit baseline-export <baseline.json> [--recipe <name>] [--db <path>] [--lang <lang>] [--path <glob>] [--exclude-path <glob>] [--exclude-tests] [--audit-scope <source|production-and-tooling|all>] [--since <datetime>] [--limit <n>] [--total-limit <n>] [--overwrite] [--json]"),
        ("audit-baseline-compare", "cdidx audit baseline-compare <baseline.json> [--recipe <name>] [--db <path>] [--lang <lang>] [--path <glob>] [--exclude-path <glob>] [--exclude-tests] [--audit-scope <source|production-and-tooling|all>] [--since <datetime>] [--limit <n>] [--total-limit <n>] [--json]"),
        ("audit-baseline-review", "cdidx audit baseline-review <baseline.json> <id> --actor <actor> --reason <reason> --overwrite [--json]"),
        ("audit", "cdidx audit (<recipe|recipe/query>|--all) [search filters] [--json[=ndjson]] [--format <text|json|count|compact|issue-drafts>] [--summary-only] [--limit <n>] [--total-limit <n>] [--continuation <token> (all only)] [--progress (all only)] [--no-progress] [--quiet] [--allow-partial] [--results-only] [--search-fields <csv>] [--first-per-file] [--sample <n>] [--max-json-bytes <n>] [--snippet-lines <n>]"),
        ("audit", QueryCommandRunner.AuditBaselineUsage),
        ("definition", "cdidx definition <query>|--query <query>|-- <query> [--db <path>] [--json] [--redact-paths|--show-paths] [--format <text|json|count|compact|csv|tsv|lsp|qf|sarif>] [--fields <csv>] [--cursor <next_cursor>] [--max-json-bytes <n>] [--verbose] [--limit <n>|--top <n>] [--lang <lang>] [--kind <kind>] [--visibility <v[,v]>] [--exclude-visibility <v[,v]>] [--path <glob>] [--exclude-path <glob>] [--exclude-tests] [--body] [--exact|--exact-name] [--count] [--group-partials] [--since <datetime>]"),
        ("goto", "cdidx goto <query>|--query <query>|-- <query> [--db <path>] [--json] [--limit <n>|--top <n>] [--lang <lang>] [--kind <kind>] [--path <glob>] [--exclude-path <glob>] [--exclude-tests] [--exact-name] [--all]"),
        ("references", "cdidx references <query>|--query <query>|-- <query>|--selector <id:n@g:fingerprint> [--db <path>] [--json] [--redact-paths|--show-paths] [--format <text|json|count|compact|csv|tsv|lsp|qf|sarif>] [--fields <csv>] [--cursor <next_cursor>] [--max-json-bytes <n>] [--verbose] [--limit <n>|--top <n>] [--lang <lang>] [--kind <kind>] [--path <glob>] [--exclude-path <glob>] [--exclude-tests] [--include-qualified-common-calls] [--body] [--snippet-lines <n>] [--max-line-width <n>] [--exact|--exact-name] [--count]"),
        ("callers", "cdidx callers <query>|--query <query>|-- <query>|--selector <id:n@g:fingerprint> [--db <path>] [--json] [--redact-paths|--show-paths] [--format <text|json|count|compact|csv|tsv|lsp|qf|sarif>] [--fields <csv>] [--cursor <next_cursor>] [--max-json-bytes <n>] [--verbose] [--limit <n>|--top <n>] [--lang <lang>] [--kind <kind>] [--rank-by <weighted|count|kind>] [--raw-kinds] [--path <glob>] [--exclude-path <glob>] [--exclude-tests] [--include-qualified-common-calls] [--include-member-reads] [--body] [--snippet-lines <n>] [--max-line-width <n>] [--exact|--exact-name] [--count]"),
        ("callees", "cdidx callees <query>|--query <query>|-- <query>|--selector <id:n@g:fingerprint> [--db <path>] [--json] [--redact-paths|--show-paths] [--format <text|json|count|compact|csv|tsv|lsp|qf|sarif>] [--fields <csv>] [--cursor <next_cursor>] [--max-json-bytes <n>] [--verbose] [--limit <n>|--top <n>] [--lang <lang>] [--kind <kind>] [--rank-by <weighted|count|kind>] [--raw-kinds] [--path <glob>] [--exclude-path <glob>] [--exclude-tests] [--include-qualified-common-calls] [--include-member-reads] [--body] [--snippet-lines <n>] [--max-line-width <n>] [--exact|--exact-name] [--count]"),
        ("symbols", "cdidx symbols [query|--query <query>|-- <query>] [--name <name>] [--db <path>] [--json[=ndjson|array]] [--compact] [--format <text|json|count|compact|lsp|qf|sarif>] [--summary-only] [--cursor <next_cursor>] [--max-json-bytes <n>] [--allow-partial] [--verbose] [--limit <n>|--top <n>] [--sort <hotspot|references|size|complexity|path>] [--lang <lang>] [--kind <kind>] [--visibility <v[,v]>] [--exclude-visibility <v[,v]>] [--path <glob>] [--exclude-path <glob>] [--exclude-tests] [--exact|--exact-name] [--count] [--group-partials] [--since <datetime>]"),
        ("files", "cdidx files [query|<glob>|--query <query>|-- <query>] [--db <path>] [--json[=ndjson|array]] [--format <text|json|count|compact>] [--summary-only] [--cursor <next_cursor>] [--max-json-bytes <n>] [--allow-partial] [--verbose] [--limit <n>|--top <n>] [--lang <lang>] [--path <glob>] [--exclude-path <glob>] [--exclude-tests] [--count] [--since <datetime>] [--bytes]"),
        ("find", "cdidx find <query> (--path <glob>|--all) [--db <path>] [--json] [--format <text|json|count|compact|csv|tsv|lsp|qf|sarif>] [--fields <csv>] [--cursor <next_cursor>] [--max-json-bytes <n>] [--verbose] [--limit <n>|--top <n>] [--lang <lang>] [--exclude-path <glob>] [--exclude-tests] [--context <n>] [--before <n>] [--after <n>] [--snippet-lines <n>] [--focus-line <line>] [--focus-column <n>] [--max-line-width <n>] [--line-scan-limit <n>] [--allow-partial] [--exact] [--regex] [--count]"),
        ("excerpt", "cdidx excerpt <path[:line|:start-end]> [--line <line>|--start <line>|--start-line <line>] [--end <line|eof>|--end-line <line>] [--start-column <column>] [--end-column <column>] [--clamp] [--context <n>|--before <n>|--after <n>] [--max-line-width <n>] [--focus-line <line>] [--focus-column <n>] [--focus-length <n>] [--db <path>] [--json] [--redact-paths|--show-paths] [--no-semantic-tokens] [--max-json-bytes <n>] [--verbose]"),
        ("map", "cdidx map [--db <path>] [--json] [--format <text|json|compact|issue-drafts>] [--pretty] [--compact] [--fields <csv>] [--cursor <next_cursor>] [--summary-only] [--verbose] [--limit <n>|--top <n>] [--lang <lang>] [--path <glob>] [--exclude-path <glob>] [--exclude-tests] [--bytes] [--sections <summary,tree,languages,hotspots,metrics|list>] [--depth <n>] [--min-entrypoint-confidence <0.0..1.0>] [--max-json-bytes <n>]"),
        ("inspect", "cdidx inspect <query>|--query <query>|-- <query>|--selector <id:n[@g:fingerprint]> [--db <path>] [--json] [--redact-paths|--show-paths] [--format <text|json|compact>] [--pretty] [--compact] [--fields <csv|list>] [--outline-only] [--body-only] [--cursor <next_cursor>] [--max-json-bytes <n>] [--verbose] [--limit <n>|--top <n>] [--lang <lang>] [--kind <kind>] [--path <glob>] [--exclude-path <glob>] [--exclude-tests] [--body] [--body-start <line>] [--body-lines <n>|--body-line-count <n>] [--context <n>|--before <n>|--after <n>] [--max-line-width <n>] [--exact|--exact-name] [--group-partials]"),
        ("inspect", "cdidx inspect --path <file> --line <line> [--end-line <line>] [--db <path>] [--json] [--redact-paths|--show-paths] [--format <text|json|compact>] [--pretty] [--compact] [--fields <csv|list>] [--outline-only] [--body-only] [--cursor <next_cursor>] [--max-json-bytes <n>] [--body] [--body-start <line>] [--body-lines <n>|--body-line-count <n>] [--context <n>|--before <n>|--after <n>] [--max-line-width <n>]"),
        ("outline", "cdidx outline <path> [--db <path>] [--json] [--pretty] [--compact] [--verbose] [--limit <n>|--top <n>] [--cursor <next_cursor>] [--max-json-bytes <n>] [--sort <source|kind|references|size|complexity|path|name>] [--kind <kind[,kind]>] [--outline-fields <csv>]"),
        ("status", "cdidx status [--db <path>] [--json] [--format <text|json|compact>] [--compact] [--fields <csv>] [--cursor <next_cursor>] [--max-json-bytes <n>] [--verbose] [--check[=workspace,fold,graph,issues,hotspot,csharp,sql,newer]] [--stale-after <duration>] [--explain <field>] [--log-path] [--config [--redact-paths|--show-paths]] [--check-updates]"),
        ("workspace", "cdidx workspace <list|status|use|current|clear|deactivate> [name-or-relative-path] [--json] [--check]"),
        ("workspace-list", "cdidx workspace list [--json]"),
        ("workspace-status", "cdidx workspace status [--json] [--check]"),
        ("workspace-use", "cdidx workspace use <name-or-relative-path|default> [--json]"),
        ("workspace-current", "cdidx workspace current [--json]"),
        ("workspace-clear", "cdidx workspace clear [--json]"),
        ("workspace-deactivate", "cdidx workspace deactivate [--json]"),
        ("config", "cdidx config show [--json] [--show-paths]"),
        ("config-show", "cdidx config show [--json] [--show-paths]"),
        ("validate-config", "cdidx validate-config [--json]"),
        ("doctor", "cdidx doctor [--integrations [--check]] [--json] [--redact-paths|--show-paths] [--env-inventory[=compact|full]] [--env-domain <domain>] [--env-category <category>] [--env-sensitivity <sensitivity>] [--max-json-bytes <n>]"),
        ("db", "cdidx db integrity|--integrity-check [--db <path>] [--show-paths] [--json]"),
        ("db", $"cdidx db schema [--type <table|index|trigger|view>] [--name <object>] [--limit <n<={DbCommandRunner.SchemaEntryLimit}>] [--max-sql-chars <n<={DbCommandRunner.SchemaSqlTextLimit}>] [--summary-only] [--include-internal|--exclude-internal] [--db <path>] [--json]"),
        ("db", "cdidx db prune --dry-run|--apply [--db <path>] [--json]"),
        ("db", "cdidx db checkpoint [name<=128] [--dry-run] [--db <path>] [--json]"),
        ("db", "cdidx db checkpoints --list|--delete <name<=128>|--prune [--keep <n>] [--dry-run] [--db <path>] [--json]"),
        ("db", "cdidx db restore <name<=128> [--dry-run] [--no-backup] [--db <path>] [--json]"),
        ("db", "cdidx db restore-backups --list|--prune [--keep <n>]|--restore <id> [--dry-run] [--no-backup] [--db <path>] [--json]"),
        ("db-integrity", "cdidx db integrity|--integrity-check [--db <path>] [--show-paths] [--json]"),
        ("db-schema", $"cdidx db schema [--type <table|index|trigger|view>] [--name <object>] [--limit <n<={DbCommandRunner.SchemaEntryLimit}>] [--max-sql-chars <n<={DbCommandRunner.SchemaSqlTextLimit}>] [--summary-only] [--include-internal|--exclude-internal] [--db <path>] [--json]"),
        ("db-prune", "cdidx db prune --dry-run|--apply [--db <path>] [--json]"),
        ("db-checkpoint", "cdidx db checkpoint [name<=128] [--dry-run] [--db <path>] [--json]"),
        ("db-checkpoints", "cdidx db checkpoints --list|--delete <name<=128>|--prune [--keep <n>] [--dry-run] [--db <path>] [--json]"),
        ("db-restore", "cdidx db restore <name<=128> [--dry-run] [--no-backup] [--db <path>] [--json]"),
        ("db-restore-backups", "cdidx db restore-backups --list|--prune [--keep <n>]|--restore <id> [--dry-run] [--no-backup] [--db <path>] [--json]"),
        ("diff", $"cdidx diff <db1> <db2> [--json] [--summary-only] [--detailed] [--data-only|--include-telemetry] [--include-content] [--max-json-bytes <n={DiffCommandRunner.MinDiffJsonBytes}..{DiffCommandRunner.MaxDiffJsonBytes}>] [--limit <n<=10000>] [--offset <n>|--cursor <cursor>]"),
        ("report", "cdidx report --output <bundle.tgz> [--overwrite] [--db <path>] [--json] [--redact-paths] [--log-lines <n<=2000>] [--no-log] [--include-args]"),
        ("validate", "cdidx validate [--db <path>] [--json[=array]] [--format <text|json|count|compact|csv|tsv|lsp|qf|sarif>] [--verbose] [--limit <n>|--top <n>] [--kind <kind>] [--severity <info|warning|error>] [--path <glob>]"),
        ("impact", "cdidx impact <query>|--query <query>|-- <query>|--selector <id:n@g:fingerprint> [--db <path>] [--json] [--redact-paths|--show-paths] [--format <text|json|compact>] [--compact] [--fields <csv>] [--cursor <next_cursor>] [--max-json-bytes <n>] [--verbose] [--limit <n>|--top <n>] [--lang <lang>] [--path <glob>] [--exclude-path <glob>] [--exclude-tests] [--body] [--snippet-lines <n>] [--max-line-width <n>] [--max-hops <n>] [--exact-name] [--count] [--with-paths] [--include-member-reads]"),
        ("deps", "cdidx deps [--db <path>] [--json] [--format <dot|graphml|json-graph|edgelist>] [--summary-only] [--max-json-bytes <n>] [--verbose] [--limit <n>|--top <n>] [--cursor <cursor>] [--graph-budget <n>] [--lang <lang>] [--path <glob>] [--exclude-path <glob>] [--exclude-tests] [--reverse] [--cycles] [--suppress-noise] [--resolution-state <state[,state]>] [--reference-kind <kind[,kind]>] [--symbol <name>] [--symbol-family <prefix>]"),
        ("unused", "cdidx unused [--db <path>] [--json] [--compact] [--summary-only] [--max-json-bytes <n>] [--verbose] [--limit <n>|--top <n>] [--cursor <next_cursor>] [--audit-scope <source|production-and-tooling|all>] [--kind <kind>] [--bucket <bucket>] [--min-confidence <medium|low>|--confidence <medium|low>] [--actionable] [--all] [--visibility <v[,v]>] [--exclude-visibility <v[,v]>] [--lang <lang>] [--path <glob>] [--exclude-path <glob>] [--exclude-tests] [--count] [--by-bucket]"),
        ("hotspots", "cdidx hotspots [--db <path>] [--json] [--format <text|json|count|compact>] [--compact] [--fields <csv>] [--cursor <next_cursor>] [--summary-only] [--max-json-bytes <n>] [--verbose] [--limit <n>|--top <n>] [--kind <kind>] [--visibility <v[,v]>] [--exclude-visibility <v[,v]>] [--lang <lang>] [--path <glob>] [--exclude-path <glob>] [--exclude-tests] [--count] [--group-by <symbol|file|statement>] [--group-by-name]"),
        ("suggestions", "cdidx suggestions [list|show|export|add|update|delete] [id|description] [--db <path>] [--json] [--description <text>] [--context <text>] [--title <text>] [--evidence-path <path>] [--status <all|draft|submitted_pending_triage|open_in_upstream|resolved_in_upstream|wont_fix|duplicate|superseded|submitted|unsubmitted>] [--actor <name>] [--reason <text>] [--language <lang>] [--category <category>] [--since <datetime>] [--agent <name>] [--query <text>] [--count|--summary-only|--compact] [--max-json-bytes <n>] [--limit <n>] [--offset <n>] [--format <json|markdown|issue-drafts>] [--output <path>] [--overwrite] [--open-issues <path|github|github:owner/name>] [--repo <owner/name>] [--issue-state <open|closed|all>] [--duplicate-confidence <low|medium|high>|--duplicate-threshold <score>]"),
        ("suggestions-list", "cdidx suggestions list [--status <state>] [--language <lang>] [--category <category>] [--since <datetime>] [--agent <name>] [--query <text>] [--count|--summary-only|--compact] [--max-json-bytes <n>] [--limit <n>] [--offset <n>] [--db <path>] [--json]"),
        ("suggestions-show", "cdidx suggestions show <id> [--status <state>] [--language <lang>] [--category <category>] [--since <datetime>] [--agent <name>] [--db <path>] [--json]"),
        ("suggestions-export", "cdidx suggestions export [--format <json|markdown|issue-drafts>] [--status <state>] [--language <lang>] [--category <category>] [--since <datetime>] [--agent <name>] [--query <text>] [--count|--summary-only|--compact] [--max-json-bytes <n>] [--limit <n>] [--offset <n>] [--output <path> [--overwrite]] [--open-issues <path|github|github:owner/name>] [--repo <owner/name>] [--issue-state <open|closed|all>] [--duplicate-confidence <low|medium|high>|--duplicate-threshold <score>] [--db <path>] [--json]"),
        ("suggestions-add", "cdidx suggestions add <description>|--description <text> [--category <value>] [--language <lang>] [--context <text>] [--title <text>] [--evidence-path <path>] [--agent <name>] [--db <path>] [--json]"),
        ("suggestions-update", "cdidx suggestions update <id> [--description <text>] [--context <text>] [--title <text>] [--evidence-path <path>] [--category <value>] [--language <lang>] [--agent <name>] [--db <path>] [--json]"),
        ("suggestions-update", "cdidx suggestions update <id> --status <draft|open_in_upstream|resolved_in_upstream|wont_fix|duplicate|superseded> [--actor <name>] [--reason <text>] [--db <path>] [--json]"),
        ("suggestions-delete", "cdidx suggestions delete <id> [--db <path>] [--json]"),
        ("export", "cdidx export <archive> [--db <path>] [--json] [--overwrite] [--redact-paths] [--lang <lang>] [--path <glob>] [--exclude-path <glob>] [--project <name|path>] [--solution <path>] [--exclude-tests]"),
        ("export", "cdidx export ctags [--output <path>] [--db <path>] [--json] [--lang <lang>] [--path <glob>] [--exclude-path <glob>] [--exclude-tests] [--include-generated]"),
        ("export-ctags", "cdidx export ctags [--output <path>] [--db <path>] [--json] [--lang <lang>] [--path <glob>] [--exclude-path <glob>] [--exclude-tests] [--include-generated]"),
        ("import", "cdidx import <archive> [--db <path>] [--prune-paths] [--no-backup] [--dry-run|--check] [--limit <n<=10000>] [--offset <n>] [--json]"),
        ("languages", "cdidx languages [--db <path>] [--json] [--format <text|json|count>] [--summary-only] [--limit <n>|--top <n>] [--cursor <next_cursor>] [--max-json-bytes <n>] [--indexed-only] [--language <lang>|--extension <ext>|--alias <alias>] [--capability <all|none|graph|references|symbols|missing-any|missing-graph|missing-references|missing-symbols|search-only>]"),
        ("batch", "cdidx batch [--db <path>] [--json-summary] [--include-raw-streams] [--max-input-lines <n>] [--max-output-chars <n>] [--parallel <n>]  # stdin is JSON Lines; --json-summary preserves safe child JSON errors and budget/retry fields; raw failed output requires --include-raw-streams"),
        ("hooks-install", "cdidx hooks install [--project <path>] [--force] [--dry-run] [--json]"),
        ("hooks-uninstall", "cdidx hooks uninstall [--project <path>] [--force] [--json]"),
        ("hooks-status", "cdidx hooks status [--project <path>] [--json]"),
        ("mcp", "cdidx mcp [--db <path>] [--transport stdio|http] [--http-listen <host:port>] [--allow-unauthenticated-http] [--audit-log <path>] [--audit-log-include-values] [--audit-log-max-bytes <n>] [--audit-log-strict] [--suggestion-dedup-threshold <0..1>]"),
        ("lsp", "cdidx lsp [--db <path>]"),
        ("completions", "cdidx completions <shell>"),
        ("--completions", "cdidx --completions <shell>"),
        ("upgrade", "cdidx upgrade [--check-only] [--json] [--channel <stable|latest|prerelease>] [--prerelease] [--version <tag>]"),
        ("license", "cdidx license [--json]"),
        ("help", "cdidx help <command> [subcommand]"),
    ];

    private static readonly (string Command, string Note)[] CommandUsageNotes =
    [
        ("index", "--rebuild deletes the existing index before a full rescan. Interactive terminals prompt unless --yes or --force bypasses confirmation; when stdin is redirected, either flag is required."),
        ("index", "Full scans, full-scan dry runs, and watch initialization report bounded unknown-language extension groups and remediation guidance; scoped updates preserve the last successful full-scan status inventory."),
        ("mcp", "--json is not supported; MCP requests and responses are JSON-RPC over the selected transport."),
        ("mcp", "stdio transport uses one UTF-8 JSON-RPC object per LF-delimited line, not LSP Content-Length framing; lifecycle diagnostics go to stderr."),
        ("mcp", "HTTP requires bearer authentication by default; --allow-unauthenticated-http is an explicit unsafe loopback-only opt-in."),
        ("lsp", "LSP stdio uses Content-Length framing; unsupported optional methods are not advertised and return JSON-RPC -32601."),
        ("lsp", "Completion is index-backed symbol completion with resolveProvider=false; unmatched or no-token positions return an empty item list."),
        ("completions", "--json is not supported; output is a shell script for the selected shell."),
        ("recipes", "Alias for `cdidx search --list-recipes`; use --names or --summary-only for small automation-friendly JSON."),
        ("audit", "Alias for `cdidx search --recipe <recipe>`; low-output flags include --results-only, --search-fields, --format count --summary-only, issue-drafts --summary-only, --max-json-bytes, and --snippet-lines 0."),
        ("symbols", "Default NDJSON ends with a terminal record unless --results-only suppresses it; --max-json-bytes covers the entire stream and partial output exits 11 unless --allow-partial is set."),
        ("files", "Default NDJSON ends with a terminal record unless --results-only suppresses it; --max-json-bytes covers the entire stream and partial output exits 11 unless --allow-partial is set."),
        ("find", "With --all, default JSON rows end with scan metadata and capped partial scans exit 11 unless --allow-partial is set. Count JSON carries the same scan state in its single result object."),
        ("find", "--context sets both sides symmetrically; explicit --before or --after overrides only that side regardless of option order, and these explicit context controls take precedence over --snippet-lines."),
        ("search", "Default NDJSON ends with a terminal record; --max-json-bytes covers the entire stream and partial output exits 11 unless --allow-partial is set. Unprojected --json=array rejects the whole output when it does not fit; --json=array with --fields removes only complete trailing rows."),
        ("inspect", "With --max-json-bytes, JSON output rejects the whole response when it does not fit."),
        ("map", "With --max-json-bytes, JSON and issue-drafts output reject the whole response when it does not fit."),
        ("references", "--json and --format json emit JSON Lines (one JSON object per result), not a single JSON array."),
        ("callers", "--json and --format json emit JSON Lines (one JSON object per result), not a single JSON array."),
        ("callees", "--json and --format json emit JSON Lines (one JSON object per result), not a single JSON array."),
        ("references", "`refs` is a compatibility alias for `references`; prefer `references` in scripts and documentation."),
        ("excerpt", "Line ranges can be supplied as `path:line` or `path:start-end`; explicit --start/--end flags override range parsing."),
        ("excerpt", "--focus-line may be used alone; without --focus-column, clamping keeps the leading window. --focus-length still requires --focus-column."),
        ("excerpt", "Recovery-command JSON redacts machine-specific absolute paths by default; use --show-paths only when a locally executable command is required."),
        ("inspect", "In query mode --path is a glob filter; in line mode --path <file> --line <line> selects a source location."),
        ("status", "`stats` is a compatibility alias for `status`; prefer `status` in scripts and documentation."),
        ("status", "--config redacts DB, data, and log paths by default; use --show-paths only for local inspection."),
        ("backfill-fold", "`fold` is a compatibility alias for `backfill-fold`; prefer `backfill-fold` in scripts and documentation."),
        ("batch", "Each stdin line may be a JSON string array such as [\"search\",\"Needle\",\"--json\"] or an object such as {\"command\":\"search\",\"args\":[\"Needle\",\"--json\"]}; blank lines are skipped."),
        ("batch", "By default child commands stream their normal stdout/stderr; with --json-summary each non-blank line writes a batch_result or batch_error envelope."),
        ("batch", $"--max-input-lines and --max-output-chars tune the default {QueryCommandRunner.BatchDefaultInputLines}-line / {QueryCommandRunner.BatchDefaultTotalOutputChars}-character budgets up to safe maxima of {QueryCommandRunner.BatchMaxInputLines} lines / {QueryCommandRunner.BatchMaxTotalOutputChars} characters."),
        ("batch", $"--parallel <n> (max {QueryCommandRunner.BatchMaxParallelism}) requires --json-summary, uses isolated read-only DB connections, and emits stable results in input order."),
        ("batch", "Successful child JSON is embedded as result, NDJSON as stable results, and text or failed output remains raw stdout; configured serialized-output budgets include envelopes and escaping."),
        ("batch", "Malformed lines and failed commands set a non-zero exit status after draining stdin; --json-summary still appends a batch_summary record."),
        ("hooks", "install writes `.git/hooks/pre-commit`; use `cdidx hooks install --dry-run` to preview the managed hook and planned action first."),
        ("hooks-install", "Writes `.git/hooks/pre-commit`; --dry-run reports the planned create, managed replacement, custom-hook chain, or no-op action and prints the managed hook without changing files; use --force only when replacing an existing chained-hook backup is intended."),
        ("hooks-install", "Example: `cdidx hooks install --project . --dry-run`."),
        ("hooks-uninstall", "Removes the managed `.git/hooks/pre-commit`; --force is required for unmanaged hook content."),
        ("hooks-uninstall", "Example: `cdidx hooks uninstall --project .`."),
        ("hooks-status", "Reports whether `.git/hooks/pre-commit` is managed by cdidx without changing hook files."),
        ("hooks-status", "Example: `cdidx hooks status --project . --json`."),
        ("workspace-list", "Discovers the nearest workspace manifest and lists its members without changing active workspace state."),
        ("workspace-list", "Example: `cdidx workspace list --json`."),
        ("workspace-status", "Lists manifest members and probes bounded index-health details without changing active workspace state. --check returns 0 for healthy members, 2 for a missing manifest, an empty workspace, or any missing required member/database, and 5 for other degraded member health."),
        ("workspace-status", "JSON keeps the legacy `exists` compatibility alias while adding unambiguous member-level `project_exists` and `db_exists` fields, aggregate health, and structured repair actions."),
        ("workspace-status", "Example: `cdidx workspace status --check --json`."),
        ("workspace-use", "Requires exactly one manifest member name or manifest-relative path; `default` selects the current directory without a manifest. The selection is persisted in the per-user cdidx configuration."),
        ("workspace-use", $"If {ActiveWorkspace.EnvironmentVariable} is set, environment configuration takes precedence over persisted active-workspace state."),
        ("workspace-use", "Example: `cdidx workspace use src/service --json`."),
        ("workspace-current", $"Reports the effective active workspace from {ActiveWorkspace.EnvironmentVariable} when set, otherwise the persisted selection, without changing it."),
        ("workspace-current", "Example: `cdidx workspace current --json`."),
        ("workspace-clear", $"Removes persisted active-workspace state. It refuses to run while {ActiveWorkspace.EnvironmentVariable} supplies the effective selection."),
        ("workspace-clear", "Example: `cdidx workspace clear --json`."),
        ("workspace-deactivate", $"Alias for `workspace clear`; removes persisted state but cannot override {ActiveWorkspace.EnvironmentVariable}."),
        ("workspace-deactivate", "Example: `cdidx workspace deactivate`."),
        ("config-show", "Reads the effective configuration without changing files; --show-paths includes resolved configuration paths."),
        ("config-show", "Example: `cdidx config show --json --show-paths`."),
        ("recipes-list", "Read-only alias form for recipe discovery; filters and bounded JSON options match the parent `recipes` command."),
        ("recipes-list", "Example: `cdidx recipes list --names --json`."),
        ("audit-baseline-export", "Example: `cdidx audit baseline-export .cdidx/audit-baseline.json --recipe risky-code --json`. Existing files require --overwrite; incomplete coverage remains explicit."),
        ("audit-baseline-compare", "Example: `cdidx audit baseline-compare .cdidx/audit-baseline.json --recipe risky-code --json`. Refresh the index first and preserve filters. Incomplete absence stays unknown."),
        ("audit-baseline-review", "Example: `cdidx audit baseline-review .cdidx/audit-baseline.json <id> --actor reviewer --reason 'Validated guard' --overwrite`. Only unchanged compatible evidence inherits the annotation."),
        ("db", "schema defaults to the full sqlite_master dump for support bundles; use --summary-only, --limit, --max-sql-chars, and --exclude-internal for bounded diagnostics."),
        ("db", "checkpoint --dry-run separates source DB/WAL/SHM bytes from every planned output, including the versioned manifest, its SHA-256, estimated final bytes, destination/conflict policy, and uncertainty."),
        ("db", "checkpoint names must be non-blank single file names of at most 128 characters and cannot contain C0 control characters, directory separators, or platform-invalid file-name characters."),
        ("db", "checkpoint creates a filesystem snapshot next to the DB; restore creates a verified managed rollback backup before replacing an existing DB unless --no-backup is explicit."),
        ("db", "restore --dry-run validates the checkpoint manifest, regular-file paths, rollback-backup policy, and destination free space without replacing the DB."),
        ("db", "checkpoints --delete and --prune remove snapshots; add --dry-run to report exact deleted/retained paths without mutation."),
        ("db", "restore-backups --list exposes managed IDs and provenance; --restore <id> validates the manifest, hash, schema, and free space before an atomic rollback-capable replacement."),
        ("db", "restore-backups --prune --dry-run reports exact deleted/retained paths; omit --dry-run to apply retention."),
        ("db", "prune --dry-run only counts orphan rows; prune --apply deletes them and may run WAL checkpoint maintenance."),
        ("db-integrity", "Runs SQLite `PRAGMA integrity_check`; no database content is changed."),
        ("db-integrity", "Example: `cdidx db integrity --db .cdidx/codeindex.db --json`."),
        ("db-schema", "Defaults to a bounded sqlite_master dump for support bundles; use --summary-only, --limit, --max-sql-chars, and --exclude-internal for smaller diagnostics."),
        ("db-schema", "Example: `cdidx db schema --type index --summary-only --json`."),
        ("db-prune", "--dry-run only counts orphan rows; --apply deletes them and may run WAL checkpoint maintenance."),
        ("db-prune", "Example: `cdidx db prune --dry-run --json`."),
        ("db-checkpoint", "--dry-run separates source DB/WAL/SHM files and bytes from every planned output, including the versioned manifest, its SHA-256, estimated final bytes, destination/conflict policy, and uncertainty; without --dry-run it creates a filesystem snapshot verified against that plan."),
        ("db-checkpoint", "Example: `cdidx db checkpoint before-upgrade --dry-run --json`."),
        ("db-checkpoints", "--list reports checkpoints; --delete and --prune remove snapshots, while --dry-run reports exact deleted/retained paths without mutation."),
        ("db-checkpoints", "Example: `cdidx db checkpoints --prune --keep 3 --dry-run --json`."),
        ("db-restore", "--dry-run validates the checkpoint manifest, regular-file paths, rollback-backup policy, and destination free space; without it restore creates a verified managed backup before replacing the DB."),
        ("db-restore", "Example: `cdidx db restore before-upgrade --dry-run --json`."),
        ("db-restore-backups", "--list reports managed IDs and provenance; --restore <id> validates manifest/hash/schema/space and atomically replaces the DB with rollback-on-failure. --dry-run performs every validation without mutation."),
        ("db-restore-backups", "--prune --dry-run reports exact deleted/retained paths, and --prune without it applies retention."),
        ("db-restore-backups", "Example: `cdidx db restore-backups --restore <id> --dry-run --json`."),
        ("suggestions-list", "Reads local suggestion records, applies filters first, then applies the non-negative --offset/--limit page without changing the store."),
        ("suggestions-list", "Example: `cdidx suggestions list --status draft --limit 20 --json`."),
        ("suggestions-list", "--query searches the redacted title, description, context, evidence paths, category, language, and stable id before pagination. --count, --summary-only, --compact, and --max-json-bytes provide bounded JSON projections."),
        ("suggestions-show", "Requires a full suggestion id or an unambiguous id prefix; filters are applied before id resolution and the store is not changed."),
        ("suggestions-show", "Example: `cdidx suggestions show <id> --json`."),
        ("suggestions-export", "--format defaults to json. --output is supported only for markdown and issue-drafts, and --overwrite replaces an existing export atomically; export never submits or opens GitHub issues."),
        ("suggestions-export", "--open-issues is available only with --format issue-drafts. A JSON file performs offline duplicate preflight; `github:owner/name` or `github` plus --repo performs bounded read-only GitHub API requests with GraphQL cursor pagination and requires CDIDX_GITHUB_TOKEN in the process environment."),
        ("suggestions-export", "--issue-state is limited to live GitHub preflight. Choose either --duplicate-confidence or --duplicate-threshold, not both."),
        ("suggestions-export", "Example: `cdidx suggestions export --format issue-drafts --open-issues github:owner/repo --issue-state open --output drafts.json`."),
        ("suggestions-add", "Writes one local draft to the selected suggestion store; normalized category, language, and description duplicates succeed without adding another record."),
        ("suggestions-add", "Example: `cdidx suggestions add \"Improve macro handling\" --category language_support --language rust --json`."),
        ("suggestions-update", "Content edits require at least one content-edit flag and mutate only an editable local draft. Status transitions are a separate form, may update records with upstream references according to lifecycle rules, and cannot be combined with content edits; submitted_pending_triage is managed by GitHub submission."),
        ("suggestions-update", "Example: `cdidx suggestions update <id> --status wont_fix --reason \"Not actionable\" --json`."),
        ("suggestions-delete", "Deletes only an editable local draft and accepts no query, export, or content-edit options."),
        ("suggestions-delete", "Example: `cdidx suggestions delete <id> --json`."),
        ("export-ctags", "Writes a ctags file without changing the index, defaulting to `tags` in the current directory when --output is omitted and replacing an existing destination; generated files are excluded unless --include-generated is set."),
        ("export-ctags", "Example: `cdidx export ctags --output tags --exclude-tests`."),
    ];

    private static readonly HashSet<string> HiddenCommandUsageNames = new(StringComparer.Ordinal)
    {
        "db-integrity",
        "db-schema",
        "db-prune",
        "db-checkpoint",
        "db-checkpoints",
        "db-restore",
        "db-restore-backups",
        "hooks-install",
        "hooks-uninstall",
        "hooks-status",
        "workspace-list",
        "workspace-status",
        "workspace-use",
        "workspace-current",
        "workspace-clear",
        "workspace-deactivate",
        "config-show",
        "recipes-list",
        "suggestions-list",
        "suggestions-show",
        "suggestions-export",
        "suggestions-add",
        "suggestions-update",
        "suggestions-delete",
        "export-ctags",
    };

    public static string FormatSummaryLine(string label, object? value, int labelWidth = SummaryLabelWidth, string indent = "")
        => $"{indent}{label.PadRight(labelWidth)}: {value}";

    internal const int DefaultDiagnosticValueCharLimit = 120;
    internal const int MaxSuggestionInputCharLength = DefaultDiagnosticValueCharLimit;

    internal readonly record struct BoundedDisplayText(string Text, bool Truncated, int OriginalLength);

    internal static BoundedDisplayText BoundDisplayText(string? value, int maxChars = DefaultDiagnosticValueCharLimit)
    {
        if (maxChars < 0)
            throw new ArgumentOutOfRangeException(nameof(maxChars), maxChars, "Display limit must be non-negative.");

        if (value == null)
            return new BoundedDisplayText("<null>", Truncated: false, OriginalLength: 0);

        var displayValue = FlattenDiagnosticControlChars(value);
        if (displayValue.Length <= maxChars)
            return new BoundedDisplayText(displayValue, Truncated: false, value.Length);

        var marker = string.Create(CultureInfo.InvariantCulture, $"... <truncated; original length {value.Length} chars>");
        var text = maxChars == 0
            ? marker.TrimStart('.', ' ')
            : displayValue[..maxChars] + marker;
        return new BoundedDisplayText(text, Truncated: true, value.Length);
    }

    internal static string FormatBoundedValue(string? value, int maxChars = DefaultDiagnosticValueCharLimit)
        => BoundDisplayText(value, maxChars).Text;

    private static string FlattenDiagnosticControlChars(string value)
    {
        for (var i = 0; i < value.Length; i++)
        {
            if (char.IsControl(value[i]))
            {
                var chars = value.ToCharArray();
                for (var j = i; j < chars.Length; j++)
                {
                    if (char.IsControl(chars[j]))
                        chars[j] = ' ';
                }

                return new string(chars);
            }
        }

        return value;
    }

    private const int SpinnerFrameDelayMs = 100;
    private const int ConsoleLineMargin = 1;
    private static readonly object TerminalLock = new();
    private static TextWriter? _synchronizedOut;
    private static TextWriter? _synchronizedError;
    private static readonly string[] ByteUnits = ["bytes", "KiB", "MiB", "GiB", "TiB", "PiB"];
    private static readonly AsyncLocal<int> JsonOutputDepth = new();

    private static readonly string[] DefaultBrailleSpinnerFrames =
    [
        "⠋", "⠙", "⠹", "⠸", "⠼",
        "⠴", "⠦", "⠧", "⠇", "⠏",
    ];
    private static readonly string[] AsciiSpinnerFrames = ["|", "/", "-", "\\"];

    internal static string Counted(int count, string singular, string? plural = null, string? format = null)
    {
        var formatted = format == null
            ? count.ToString(CultureInfo.InvariantCulture)
            : count.ToString(format, CultureInfo.InvariantCulture);
        return $"{formatted} {(count == 1 ? singular : plural ?? singular + "s")}";
    }

    internal static string FormatNumber(long value, string format = "N0")
        => value.ToString(format, CultureInfo.InvariantCulture);

    internal static string FormatNumber(int value, string format = "N0")
        => value.ToString(format, CultureInfo.InvariantCulture);

    internal static string FoundSummary(int count, string singular, string? plural = null)
    {
        plural ??= singular + "s";
        return count == 0
            ? $"No {plural} found."
            : $"Found {Counted(count, singular, plural)}.";
    }

    internal static void EnsureConsoleWritersSynchronized()
    {
        using var ownership = ConsoleStreamOwnership.Enter();
        lock (TerminalLock)
        {
            var output = Console.Out;
            if (!ReferenceEquals(output, _synchronizedOut))
            {
                _synchronizedOut = TextWriter.Synchronized(output);
                Console.SetOut(_synchronizedOut);
            }

            var error = Console.Error;
            if (!ReferenceEquals(error, _synchronizedError))
            {
                _synchronizedError = TextWriter.Synchronized(error);
                Console.SetError(_synchronizedError);
            }
        }
    }

    internal static void TryWriteErrorLine(string? value = null)
    {
        try
        {
            if (value == null)
                CommandErrorWriter.WriteStderr();
            else
                CommandErrorWriter.WriteStderr(value);
        }
        catch (ObjectDisposedException)
        {
        }
        catch (IOException)
        {
        }
    }

    internal static IDisposable SuppressAnsiForJsonOutput(bool enabled)
    {
        if (!enabled)
            return NoopDisposable.Instance;

        JsonOutputDepth.Value++;
        return new JsonOutputScope();
    }

    // --- Spinner / スピナー ---

    public static string FormatDuration(TimeSpan duration, DurationOutputFormat format = DurationOutputFormat.Auto)
    {
        if (duration < TimeSpan.Zero)
            duration = TimeSpan.Zero;

        return format switch
        {
            DurationOutputFormat.Seconds => FormatDurationAsSeconds(duration),
            DurationOutputFormat.Hms => FormatDurationAsHms(duration),
            _ => FormatDurationAuto(duration),
        };
    }

    private static string FormatDurationAuto(TimeSpan duration)
    {
        if (duration < TimeSpan.FromSeconds(1))
            return string.Create(CultureInfo.InvariantCulture, $"{Math.Floor(duration.TotalMilliseconds):0}ms");

        if (duration < TimeSpan.FromMinutes(1))
            return string.Create(CultureInfo.InvariantCulture, $"{duration.TotalSeconds:0.0}s");

        var totalSeconds = (long)Math.Floor(duration.TotalSeconds);
        if (duration < TimeSpan.FromHours(1))
        {
            var minutes = totalSeconds / 60;
            var seconds = totalSeconds % 60;
            return string.Create(CultureInfo.InvariantCulture, $"{minutes}m {seconds}s");
        }

        var hours = totalSeconds / 3600;
        var remainder = totalSeconds % 3600;
        var remMinutes = remainder / 60;
        var remSeconds = remainder % 60;
        return string.Create(CultureInfo.InvariantCulture, $"{hours}h {remMinutes}m {remSeconds}s");
    }

    private static string FormatDurationAsSeconds(TimeSpan duration)
        => string.Create(CultureInfo.InvariantCulture, $"{duration.TotalSeconds:0.0}s");

    private static string FormatDurationAsHms(TimeSpan duration)
    {
        var totalSeconds = (long)Math.Floor(duration.TotalSeconds);
        var hours = totalSeconds / 3600;
        var remainder = totalSeconds % 3600;
        var minutes = remainder / 60;
        var seconds = remainder % 60;
        return string.Create(CultureInfo.InvariantCulture, $"{hours:00}:{minutes:00}:{seconds:00}");
    }

    /// <summary>
    /// Start spinner on a background thread, returns CancellationTokenSource to stop it.
    /// バックグラウンドスレッドでスピナーを開始。停止用のCancellationTokenSourceを返す。
    /// </summary>
}

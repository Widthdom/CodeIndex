using System.Globalization;
using System.Text;
using System.Text.RegularExpressions;
using System.Text.Json;
using System.Text.Json.Nodes;
using System.Text.Json.Serialization.Metadata;
using CodeIndex.Database;
using CodeIndex.Indexer;
using CodeIndex.Indexer.Extensibility;
using CodeIndex.Indexer.Hooks;
using CodeIndex.Models;
using Microsoft.Data.Sqlite;

namespace CodeIndex.Cli;

/// <summary>
/// Runs query-style CLI commands.
/// クエリ系CLIコマンドを実行する。
/// </summary>
public static partial class QueryCommandRunner
{
    internal const int DefaultQueryLimit = 20;
    internal const int DefaultMapLimit = 10;
    internal const int DefaultCompactSectionLimit = 5;
    private const int MaxNamedSearchQueryNameLength = 128;
    internal const int DefaultImpactLimit = 50;
    internal const int DefaultDependencyCycleGraphLimit = 1_000;
    internal const int MaxWorkspaceDependencyDatabaseCount = 8;
    internal const int MaxWorkspaceDependencyDatabasePairCount = MaxWorkspaceDependencyDatabaseCount * (MaxWorkspaceDependencyDatabaseCount - 1);
    internal const int FindAllCandidateFileLimit = 4096;
    internal const int FindAllLineScanLimit = 250_000;
    internal const int BatchMaxLineChars = 1024 * 1024;
    internal const int BatchMaxArgumentCount = 256;
    internal const int BatchMaxArgumentChars = 8192;
    internal const int BatchMaxJsonDepth = 32;
    internal const int MaxStatusSymbolKindEntries = 32;
    internal const int MaxStatusSymbolKindNameLength = 64;
    private const int MaxSearchProjectionFieldsCsvLength = 256;
    private const int MaxSearchProjectionFieldsCsvEntries = 16;
    private const int MaxOutlineProjectionFieldsCsvLength = 256;
    private const int MaxOutlineProjectionFieldsCsvEntries = 16;
    private const int DefaultSearchGroupedPerFileLimit = 3;
    private const int MaxSearchGroupedPerFileLimit = 20;
    private const int MaxSearchNextStepLimit = 10;
    private const int MaxSearchJsonByteLimit = 16 * 1024 * 1024;
    internal const string DefaultLimitEnvironmentVariable = "CDIDX_DEFAULT_LIMIT";
    internal const string DefaultSnippetLinesEnvironmentVariable = "CDIDX_DEFAULT_SNIPPET_LINES";
    internal const string DefaultMaxLineWidthEnvironmentVariable = "CDIDX_DEFAULT_MAX_LINE_WIDTH";
    internal const string StaleAfterEnvironmentVariable = "CDIDX_STALE_AFTER";
    private const string LanguageCapabilityGraph = "graph";
    private const string LanguageCapabilityReferences = "references";
    private const string LanguageCapabilitySymbols = "symbols";
    private const string LanguageCapabilityMissingGraph = "missing-graph";
    private const string LanguageCapabilityMissingReferences = "missing-references";
    private const string LanguageCapabilityMissingSymbols = "missing-symbols";
    private const string LanguageCapabilitySearchOnly = "search-only";
    internal static readonly TimeSpan DefaultStaleAfter = TimeSpan.FromHours(24);
    internal static readonly TimeSpan MaxStaleAfter = TimeSpan.FromDays(30);
    internal const string MaxStaleAfterDisplay = "30d";
    internal static TimeProvider TimeProvider { get; set; } = TimeProvider.System;
    private static readonly JsonDocumentOptions BatchJsonDocumentOptions = new()
    {
        MaxDepth = BatchMaxJsonDepth,
    };

    [ThreadStatic]
    private static DbReader? s_batchReader;
    [ThreadStatic]
    private static string? s_batchDbPath;
    [ThreadStatic]
    private static bool s_batchDbPathExplicit;
    [ThreadStatic]
    private static string? s_activeQueryProjectRoot;

    internal const string ProjectFilterRootFallbackReasonCurrentDirectory = "project_root_unresolved_using_current_directory";

    internal readonly record struct ProjectFilterRootResolution(string Root, string? FallbackReason);

    private static DateTime GetUtcNow() => TimeProvider.GetUtcNow().UtcDateTime;

    // Cap OR-joined `symbols` names well below SQLite's 1000 expression-tree depth so oversized
    // batches fail fast with a clear usage error instead of a confusing SQLite exception.
    // OR 結合の `symbols` 名は SQLite の式木深さ上限 1000 を十分下回る値で頭打ちにし、
    // 大量バッチを SQLite 例外ではなく明確な usage error で早期に弾く。
    internal const int MaxSymbolQueryNames = 256;
    internal const int MaxMapSectionsCsvLength = 256;
    internal const int MaxMapSectionsCsvEntries = 16;
    internal const int MaxInspectFieldsCsvLength = 256;
    internal const int MaxInspectFieldsCsvEntries = 16;
    internal const int MaxStatusCheckScopesCsvLength = 256;
    internal const int MaxStatusCheckScopesCsvEntries = 16;
    internal const int MaxVisibilityFilterCsvLength = 256;
    internal const int MaxVisibilityFilterCsvEntries = 16;
    internal const int MaxIssueDraftLabelCount = 16;
    internal const int MaxIssueDraftTitleLength = GitHubIssueReporter.MaxGitHubIssueTitleLength;
    internal const int MaxSearchRecipeQuerySelectorCount = 64;
    internal const int MaxSearchRecipeQuerySelectorLength = 128;
    internal const int MaxQueryPathFilterCount = 128;
    internal const int MaxQueryPathFilterLength = 1024;
    internal const int ExactZeroHintProbeLimit = 1;
    internal const int ExactZeroHintSampleLimit = 5;
    private const int SearchOriginFilterMinCandidates = 200;
    private const int SearchOriginFilterOverFetchFactor = 50;
    private const int SearchOriginFilterMaxCandidates = 10_000;
    private const int SearchOriginFilterMaxPages = 50;
    private const int SearchEnvelopeMinCandidates = 200;
    private const int SearchEnvelopeOverFetchFactor = 50;
    private const int SearchEnvelopeMaxCandidates = 10_000;
    private const string SearchFilterNoMatchSentinel = "\0__cdidx_no_match__";
    private const string HotspotsGroupedByNameKind = "name_kind";
    private const string HotspotsGroupedBySymbol = "symbol";
    private const string HotspotsGroupedByFile = "file";
    private const string HotspotsGroupedByStatement = "statement";
    private const string JsonOutputFormatNdjson = "ndjson";
    private const string JsonOutputFormatArray = "array";
    private static readonly List<string> SearchRecipeSupportedFormats = ["text", "json", "compact", OutputFormatIssueDrafts];
    private static readonly SearchRecipeFilterSupportJsonResult SearchRecipeFilterSupport = new(
        Lang: true,
        Path: true,
        ExcludePath: true,
        ExcludeTests: true,
        Since: true,
        Dedup: true,
        VisibilityRank: true,
        GuardFilters: true,
        SnippetControls: true,
        ExactModeOverride: true);
    private static readonly SearchRecipeLimitSemanticsJsonResult SearchRecipeLimitSemantics = new(
        "per_query",
        DefaultQueryLimit,
        "--limit/--top is applied independently to each recipe child query; result_count is the sum of returned rows.");
    private static readonly Dictionary<string, string[]> LanguageDisplayAliases = new(StringComparer.Ordinal)
    {
        ["javascript"] = ["js", "jsx", "cjs", "mjs"],
        ["csharp"] = ["c#", "cs", "cshtml", "razor", "blazor"],
        ["java"] = ["jav"],
        ["cpp"] = ["c++", "cplusplus"],
        ["fsharp"] = ["f#", "fs"],
        ["ruby"] = ["rb"],
        ["vb"] = ["vb.net", "vbnet", "visual basic", "visual-basic", "visual_basic", "vbs", "vbscript"],
        ["python"] = ["py", "py3", "python3"],
        ["yaml"] = ["yml"],
        ["typescript"] = ["ts", "tsx", "cts", "mts"],
        ["rust"] = ["rs"],
        ["sql"] = ["tsql", "t-sql", "transact-sql", "transactsql", "sqlserver", "mssql"],
        ["xml"] = ["xaml", "axaml"],
        ["assembly"] = ["asm", "assembler", "nasm", "gas", "gnuasm", "gnu assembler"],
    };
    private static readonly HashSet<string> ValueTakingOptions =
    [
        "--db",
        "--data-dir",
        "--limit",
        "--max-results",
        "--top",
        "--lang",
        "--language",
        "--extension",
        "--alias",
        "--kind",
        "--bucket",
        "--confidence",
        "--min-confidence",
        "--severity",
        "--visibility",
        "--exclude-visibility",
        "--since",
        "--line",
        "--start",
        "--start-line",
        "--end",
        "--end-line",
        "--context",
        "--before",
        "--after",
        "--body-start",
        "--body-lines",
        "--body-line-count",
        "--name",
        "--snippet-lines",
        "--snippet-focus",
        "--path",
        "--require-before",
        "--require-after",
        "--reject-before",
        "--reject-after",
        "--guard-window",
        "--guard-scope",
        "--project",
        "--solution",
        "--exclude-path",
        "--max-hops",
        "--depth",
        "--query",
        "--recipe",
        "--include-query",
        "--exclude-query",
        "--named-query",
        "--open-issues",
        "--repo",
        "--duplicate-confidence",
        "--duplicate-threshold",
        "--issue-title",
        "--issue-label",
        "--cursor",
        "--group-by",
        "--unique",
        "--count-by",
        "--origin",
        "--match-origin",
        "--exclude-origin",
        "--result-kind",
        "--sample",
        "--per-file-limit",
        "--max-json-bytes",
        "--search-fields",
        "--outline-fields",
        "--focus-line",
        "--focus-column",
        "--focus-length",
        "--max-line-width",
        "--stale-after",
        "--explain",
        "--rank-by",
        "--sort",
        "--slow-query-ms",
        "--format",
        "--min-entrypoint-confidence",
        "--sections",
        "--fields",
    ];
    private sealed record StatusReadinessField(
        string FieldName,
        string Label,
        string ReadyText,
        string DegradedText,
        string Remediation);

    private static readonly StatusReadinessField[] StatusReadinessFields =
    [
        new(
            "graph_table_available",
            "Reference graph table",
            "reference, caller, callee, impact, unused, and hotspot queries can read indexed reference edges.",
            "reference graph queries degrade to empty or incomplete results because the symbol_references table is missing.",
            "Run `cdidx index <projectPath>` to rebuild the graph-capable index."),
        new(
            "issues_table_available",
            "Validation issues table",
            "the file_issues table exists in this index.",
            "validate output degrades to empty because the file_issues table is missing.",
            "Run `cdidx index <projectPath>` to rebuild the issue table."),
        new(
            "file_issues_data_current",
            "Validation issues data",
            "file_issues rows are stamped current for this index generation.",
            "file_issues rows may be stale or partial for this index generation.",
            "Run `cdidx index <projectPath>` to refresh file issue rows."),
        new(
            "migration_in_progress",
            "Migration/write state",
            "no index write or migration is currently in progress.",
            "an index write or migration is in progress, so readiness may be temporarily degraded.",
            "Wait for the active `cdidx index` run to finish, then rerun `cdidx status --json`."),
        new(
            "sql_graph_contract_ready",
            "SQL graph contract",
            "SQL reference/dependency rows were written with the current call-column and qualified-name contract.",
            "SQL graph/dependency readers may return stale or incomplete results.",
            "Run `cdidx index <projectPath>` to rewrite SQL graph rows."),
        new(
            "hotspot_family_ready",
            "Hotspot family contract",
            "cross-file hotspot family grouping is stamped for all supported languages in this index.",
            "cross-file hotspot grouping may be degraded for one or more languages.",
            "Run `cdidx index <projectPath> --rebuild` to restamp authoritative hotspot families for every indexed row."),
        new(
            "csharp_symbol_name_ready",
            "C# symbol-name contract",
            "C# exact-name lookup uses authoritative persisted names for operators, conversions, and indexers.",
            "C# exact-name lookup for operators, conversions, and indexers may fall back to older canonical names.",
            "Run `cdidx index <projectPath>` to upgrade canonical C# symbol names."),
        new(
            "csharp_metadata_target_ready",
            "C# metadata target contract",
            "deps and impact use authoritative C# metadata-attribute targets.",
            "deps and impact metadata-attribute edges fall back to legacy signature/name heuristics.",
            "Run `cdidx index <projectPath>` to restamp authoritative C# metadata targets."),
        new(
            "fold_ready",
            "Unicode exact-name fold contract",
            "--exact-name can use Unicode NFKC + CaseFold equality.",
            "--exact-name falls back to ASCII COLLATE NOCASE, so non-ASCII casing pairs may not match.",
            "Run `cdidx backfill-fold` to restamp folded-name columns in place, or `cdidx index <projectPath> --rebuild` for a full rebuild."),
        new(
            "index_newer_than_reader",
            "Reader compatibility",
            "this cdidx binary understands all persisted index contract versions.",
            "this DB was written by a newer cdidx, so older readers may degrade instead of trusting newer contract stamps.",
            "Run status with a current cdidx binary, or rebuild the DB with the version you intend to use."),
    ];

    private static readonly HashSet<string> FlagOnlyOptions =
    [
        "--json",
        "--fts",
        "--body",
        "--count",
        "--strict-not-found",
        "--strict",
        "--no-dedup",
        "--no-visibility-rank",
        "--exact",
        "--exact-name",
        "--exact-substring",
        "--prefix",
        "--reverse",
        "--help",
        "-h",
        "--version",
        "-V",
        "--verbose",
        "--quiet",
        "-q",
        "--silent",
        "--actionable",
        "--by-bucket",
        "--all",
        "--summary-only",
        "--cycles",
        "--group-by-name",
        "--with-paths",
        "--bytes",
        "--profile",
        "--check-updates",
        "--list-recipes",
        "--read-only",
        "--immutable",
        "--dry-run",
        "--pretty",
        "--compact",
        "--body-only",
        "--first-per-file",
        "--results-only",
        "--next-steps",
    ];
    private const string OutputFormatText = "text";
    private const string OutputFormatJson = "json";
    private const string OutputFormatLsp = "lsp";
    private const string OutputFormatQf = "qf";
    private const string OutputFormatSarif = "sarif";
    private const string OutputFormatCount = "count";
    private const string OutputFormatCompact = "compact";
    private const string OutputFormatGrouped = "grouped";
    private const string OutputFormatCsv = "csv";
    private const string OutputFormatTsv = "tsv";
    private const string OutputFormatIssueDrafts = "issue-drafts";
    private const string OutputFormatDot = "dot";
    private const string OutputFormatGraphMl = "graphml";
    private const string OutputFormatJsonGraph = "json-graph";
    private const string OutputFormatEdgeList = "edgelist";
    private static readonly HashSet<string> RepoMapOutputFormats = new(StringComparer.Ordinal)
    {
        OutputFormatText,
        OutputFormatJson,
        OutputFormatCompact,
    };
    private static readonly HashSet<string> SymbolOutputFormats = new(StringComparer.Ordinal)
    {
        OutputFormatText,
        OutputFormatJson,
        OutputFormatCount,
        OutputFormatLsp,
        OutputFormatQf,
        OutputFormatSarif,
    };
    private static readonly HashSet<string> InspectOutputFormats = new(StringComparer.Ordinal)
    {
        OutputFormatText,
        OutputFormatJson,
        OutputFormatCompact,
    };
    private static readonly HashSet<string> InlineValueOptions =
        new(
            ValueTakingOptions.Concat(["--json", "--log-format", "--log-retain-count", "--log-max-size-mb"]),
            StringComparer.Ordinal);
    private const string FindUsage = "Usage: cdidx find <query> (--path <glob>|--all) [--db <path>] [--json] [--format <text|json|count|compact|csv|tsv|lsp|qf|sarif>] [--verbose] [--limit <n>|--top <n>] [--lang <lang>] [--exclude-path <glob>] [--exclude-tests] [--before <n>] [--after <n>] [--snippet-lines <n>] [--focus-line <line>] [--focus-column <n>] [--max-line-width <n>] [--exact] [--regex] [--count]\n       cdidx find --query <query> (--path <glob>|--all) [...]\n       cdidx find [options] -- <query>";

    public static int RunSearch(
        string[] cmdArgs,
        JsonSerializerOptions jsonOptions,
        CancellationToken cancellationToken = default)
    {
        var previewOptionError = ValidatePreviewOptions("search", cmdArgs, allowMaxLineWidth: true, allowFocusOptions: false);
        if (previewOptionError != null)
        {
            CommandErrorWriter.WriteStderr(previewOptionError);
            return CommandExitCodes.UsageError;
        }
        var options = ParseArgs(cmdArgs, jsonDefault: false, allowNamedQuery: true, allowIssueDraftsFormat: true);
        if (TryWriteUnsupportedOptionError("search", cmdArgs, CliFlagSchema.GetAcceptedFlagNamesForCommand("search"), options.Query))
            return CommandExitCodes.UsageError;
        if (TryWriteParseError(options, "search"))
            return CommandExitCodes.UsageError;
        if (!TryResolveSearchExactMode(options, out var exact, out var exactError))
        {
            CommandErrorWriter.WriteStderr(exactError);
            return CommandExitCodes.UsageError;
        }
        if (options.OpenIssuesPath != null && options.OutputFormat != OutputFormatIssueDrafts)
        {
            WriteUsageError(
                "--open-issues can only be used with `cdidx search --format issue-drafts`.",
                GetUsageLineOrThrow("search"),
                "Use an open-issues JSON file from `gh issue list --state open --json number,title,labels,url`.");
            return CommandExitCodes.UsageError;
        }
        if (options.OpenIssuesRepository != null && !IssueDuplicatePreflight.IsGitHubOpenIssuesSource(options.OpenIssuesPath))
        {
            WriteUsageError(
                "--repo can only be used with `--open-issues github`.",
                GetUsageLineOrThrow("search"),
                "Use `--open-issues github --repo owner/name` to fetch open issues directly from GitHub.");
            return CommandExitCodes.UsageError;
        }
        if (options.DuplicatePreflightTuningExplicit && options.OutputFormat != OutputFormatIssueDrafts)
        {
            WriteUsageError(
                "--duplicate-confidence and --duplicate-threshold can only be used with `cdidx search --format issue-drafts`.",
                GetUsageLineOrThrow("search"),
                "Use these controls when exporting issue draft JSON with duplicate-preflight metadata.");
            return CommandExitCodes.UsageError;
        }
        if ((options.IncludeRecipeQueries.Count > 0 || options.ExcludeRecipeQueries.Count > 0) && options.RecipeName == null)
        {
            WriteUsageError(
                "--include-query and --exclude-query can only be used with --recipe.",
                GetUsageLineOrThrow("search"),
                "Use `--recipe risky-code --include-query raw-diagnostic-echo` to run a child query subset.");
            return CommandExitCodes.UsageError;
        }
        if (options.SearchCursor.HasValue && options.RecipeName == null)
        {
            WriteUsageError(
                "--cursor can only be used with --recipe.",
                GetUsageLineOrThrow("search"),
                "Use `--recipe risky-code/raw-diagnostic-echo --format compact --cursor <next_cursor>` to fetch the next page for one child query.");
            return CommandExitCodes.UsageError;
        }
        if (options.UnusedCursorOffset.HasValue)
        {
            WriteUsageError(
                "--cursor for search must be a search pagination cursor returned by recipe search.",
                GetUsageLineOrThrow("search"),
                "Use `--cursor <next_cursor>` only with `--recipe`; `unused:<offset>` cursors are for `cdidx unused`.");
            return CommandExitCodes.UsageError;
        }
        if (options.OutlineCursorOffset.HasValue)
        {
            WriteUsageError(
                "--cursor for search must be a search pagination cursor returned by recipe search.",
                GetUsageLineOrThrow("search"),
                "`outline:<offset>` cursors are for `cdidx outline <path>`.");
            return CommandExitCodes.UsageError;
        }
        if (options.AuditScopeExplicit && options.RecipeName == null)
        {
            WriteUsageError(
                "--audit-scope is only supported with `cdidx search --recipe <name>`.",
                GetUsageLineOrThrow("search"),
                "Use `--audit-scope source` for the production-code default or `--audit-scope all` when intentionally auditing docs, tests, and recipe definitions.");
            return CommandExitCodes.UsageError;
        }
        if (options.ShowExcluded && options.RecipeName == null)
        {
            WriteUsageError(
                "--show-excluded is only supported with `cdidx search --recipe <name>`.",
                GetUsageLineOrThrow("search"),
                "Use it with a recipe run to include the effective scope and exclusion diagnostics in JSON output.");
            return CommandExitCodes.UsageError;
        }
        if ((options.IssueTitle != null || options.IssueLabels.Count > 0) && options.OutputFormat != OutputFormatIssueDrafts)
        {
            WriteUsageError(
                "--issue-title and --issue-label can only be used with `cdidx search --format issue-drafts`.",
                GetUsageLineOrThrow("search"),
                "Use these hints when exporting issue draft JSON for a plain search.");
            return CommandExitCodes.UsageError;
        }
        if (options.IssueTitle != null && options.RecipeName != null)
        {
            WriteUsageError(
                "--issue-title is only supported for ad hoc search issue drafts.",
                GetUsageLineOrThrow("search"),
                "Recipe issue-drafts produce one draft per recipe query, so their titles are derived from the recipe metadata.");
            return CommandExitCodes.UsageError;
        }
        if (options.OutputFormat == OutputFormatIssueDrafts && options.CountOnly)
        {
            WriteUsageError(
                "--count cannot be combined with --format issue-drafts.",
                GetUsageLineOrThrow("search"),
                "Issue-draft export needs result evidence; remove --count.");
            return CommandExitCodes.UsageError;
        }
        if (options.OutputFormat == OutputFormatIssueDrafts && options.JsonOutputFormat == JsonOutputFormatArray)
        {
            WriteUsageError(
                "--json=array is not supported with --format issue-drafts because draft export is a JSON object.",
                GetUsageLineOrThrow("search"),
                "Use plain `--json` or omit --json when exporting issue drafts.");
            return CommandExitCodes.UsageError;
        }
        if (options.SearchCursor.HasValue && options.OutputFormat == OutputFormatIssueDrafts)
        {
            WriteUsageError(
                "--cursor cannot be combined with --format issue-drafts.",
                GetUsageLineOrThrow("search"),
                "Use --cursor with recipe JSON or compact output, then export issue drafts after choosing the desired query page.");
            return CommandExitCodes.UsageError;
        }
        if (exact && options.Prefix)
        {
            WriteValidationError(
                "--prefix cannot be combined with --exact / --exact-substring (exact uses instr(), not FTS5 prefix phrases).",
                "Drop --prefix to keep the exact substring path, or drop --exact to opt into FTS5 prefix matching.");
            return CommandExitCodes.UsageError;
        }
        if (options.GroupBy != null && (options.ListRecipes || options.NamedSearchQueries.Count > 0 || options.RecipeName != null))
        {
            var mode = options.ListRecipes
                ? "--list-recipes"
                : options.NamedSearchQueries.Count > 0
                    ? "--named-query"
                    : "--recipe";
            WriteUsageError(
                $"--group-by is not supported with {mode}.",
                GetUsageLineOrThrow("search"),
                "Use `cdidx search <query> --group-by file --count` or remove --group-by for recipe and named-batch output.");
            return CommandExitCodes.UsageError;
        }
        if (options.OutputFormat == OutputFormatGrouped && (options.ListRecipes || options.NamedSearchQueries.Count > 0 || options.RecipeName != null))
        {
            var mode = options.ListRecipes
                ? "--list-recipes"
                : options.NamedSearchQueries.Count > 0
                    ? "--named-query"
                    : "--recipe";
            WriteUsageError(
                "--format grouped is only supported for plain search output.",
                GetUsageLineOrThrow("search"),
                $"Remove {mode}, or run a plain `cdidx search <query> --format grouped`.");
            return CommandExitCodes.UsageError;
        }
        if (options.ListRecipes)
        {
            if (options.Query != null || options.RecipeName != null || options.NamedSearchQueries.Count > 0 || options.ExtraNames.Count > 0)
            {
                WriteUsageError(
                    "--list-recipes cannot be combined with a query, --recipe, --named-query, or extra positional arguments.",
                    GetUsageLineOrThrow("search"),
                    "Run `cdidx search --list-recipes` to list built-in audit recipes.");
                return CommandExitCodes.UsageError;
            }
            if (options.OutputFormat is not OutputFormatText and not OutputFormatJson)
            {
                WriteUsageError(
                    "--format count/compact/csv/tsv/lsp/qf/sarif/issue-drafts is not supported with --list-recipes.",
                    GetUsageLineOrThrow("search"),
                    "Use plain text output or `--json` / `--format json` for the recipe list.");
                return CommandExitCodes.UsageError;
            }
            if (options.JsonOutputFormat == JsonOutputFormatArray)
            {
                WriteUsageError(
                    "--json=array is not supported with --list-recipes because recipe-list output is a JSON object.",
                    GetUsageLineOrThrow("search"),
                    "Use plain `--json` for the recipe-list object.");
                return CommandExitCodes.UsageError;
            }

            return WriteSearchRecipeList(options, jsonOptions);
        }
        if (options.NamedSearchQueries.Count > 0)
        {
            if (options.Query != null || options.RecipeName != null || options.ExtraNames.Count > 0)
            {
                WriteUsageError(
                    "--named-query cannot be combined with a positional query, --query, --recipe, or extra positional arguments.",
                    GetUsageLineOrThrow("search"),
                    "Pass one or more `--named-query <name>=<query>` values, or run a plain `cdidx search <query>`.");
                return CommandExitCodes.UsageError;
            }
            if (options.OpenIssuesPath != null)
            {
                WriteUsageError(
                    "--open-issues can only be used with `cdidx search --recipe <name> --format issue-drafts`.",
                    GetUsageLineOrThrow("search"),
                    "Remove --open-issues for ad hoc named batches.");
                return CommandExitCodes.UsageError;
            }
            if (options.CountOnly)
            {
                WriteUsageError(
                    "--count is not supported with --named-query.",
                    GetUsageLineOrThrow("search"),
                    "Use `cdidx search --named-query <name>=<query> --json` for per-query counts.");
                return CommandExitCodes.UsageError;
            }
            if (options.OutputFormat is not OutputFormatText and not OutputFormatJson and not OutputFormatCompact)
            {
                WriteUsageError(
                    "--format count/csv/tsv/lsp/qf/sarif/issue-drafts is not supported with --named-query.",
                    GetUsageLineOrThrow("search"),
                    "Use plain text output, `--json`, or `--format compact` for grouped ad hoc results.");
                return CommandExitCodes.UsageError;
            }
            if (options.JsonOutputFormat == JsonOutputFormatArray)
            {
                WriteUsageError(
                    "--json=array is not supported with --named-query because named batch output is grouped by query.",
                    GetUsageLineOrThrow("search"),
                    "Use plain `--json` for the grouped named-query object.");
                return CommandExitCodes.UsageError;
            }

            return RunSearchNamedBatch(options, jsonOptions, exact);
        }
        if (options.RecipeName != null)
        {
            if (options.Query != null || options.ExtraNames.Count > 0)
            {
                WriteUsageError(
                    "--recipe expands into its own curated query set and cannot be combined with a search query.",
                    GetUsageLineOrThrow("search"),
                    "Remove the positional query, or run a plain `cdidx search <query>` without --recipe.");
                return CommandExitCodes.UsageError;
            }
            if (options.Prefix)
            {
                WriteUsageError(
                    "--prefix is not supported with --recipe because each recipe query defines its own match mode.",
                    GetUsageLineOrThrow("search"),
                    "Remove --prefix, or run the individual query from the recipe list yourself.");
                return CommandExitCodes.UsageError;
            }
            if (options.OutputFormat is not OutputFormatText and not OutputFormatJson and not OutputFormatCompact and not OutputFormatIssueDrafts)
            {
                WriteUsageError(
                    "--format count/csv/tsv/lsp/qf/sarif is not supported with --recipe.",
                    GetUsageLineOrThrow("search"),
                    "Use `--json` for grouped recipe results, `--format compact` for summary-first compact JSON, or `--format issue-drafts` for draft exports.");
                return CommandExitCodes.UsageError;
            }
            if (options.CountOnly)
            {
                WriteUsageError(
                    "--count is not supported with --recipe.",
                    GetUsageLineOrThrow("search"),
                    "Use `cdidx search --recipe <name> --json` for per-query result counts.");
                return CommandExitCodes.UsageError;
            }
            if (options.JsonOutputFormat == JsonOutputFormatArray)
            {
                WriteUsageError(
                    "--json=array is not supported with --recipe because recipe output is grouped by query.",
                    GetUsageLineOrThrow("search"),
                    "Use plain `--json` for the grouped recipe object.");
                return CommandExitCodes.UsageError;
            }

            if (options.OutputFormat == OutputFormatIssueDrafts)
                return RunSearchRecipeIssueDrafts(options, jsonOptions, exact, cancellationToken);

            return RunSearchRecipe(options, jsonOptions, exact);
        }
        if (TryWriteBlankQueryError(options, "search"))
            return CommandExitCodes.UsageError;
        if (options.Query == null)
        {
            WriteUsageError(
                "search requires a query argument",
                GetUsageLineOrThrow("search"),
                BuildMissingSearchQueryHint(cmdArgs));
            return CommandExitCodes.UsageError;
        }
        if (options.Query.Length > QueryLimits.MaxQueryLength)
        {
            WriteUsageError(
                QueryLimits.FormatQueryTooLongError(),
                GetUsageLineOrThrow("search"),
                "Shorten the search text or split generated input into smaller queries before running `cdidx search`.");
            return CommandExitCodes.UsageError;
        }
        if (TryWriteUnexpectedExtraPositionals("search", options))
            return CommandExitCodes.UsageError;
        if (options.GroupBy != null)
        {
            if (options.GroupBy is not "file" and not "symbol" and not "origin")
            {
                WriteUsageError(
                    "--group-by for search must be one of file, symbol, or origin.",
                    GetUsageLineOrThrow("search"),
                    "Use `cdidx search <query> --group-by file --count`, `--group-by symbol --count`, or `--count-by origin`.");
                return CommandExitCodes.UsageError;
            }
            if (!options.CountOnly)
            {
                WriteUsageError(
                    "search --group-by requires --count.",
                    GetUsageLineOrThrow("search"),
                    "Add --count to request grouped result counts, or remove --group-by to print matching snippets.");
                return CommandExitCodes.UsageError;
            }
            if (options.OutputFormat is not OutputFormatText and not OutputFormatJson and not OutputFormatCount)
            {
                WriteUsageError(
                    "--group-by for search only supports plain count output or JSON.",
                    GetUsageLineOrThrow("search"),
                    "Use `--count`, optionally with `--json`, instead of compact/location formats.");
                return CommandExitCodes.UsageError;
            }
            if (options.JsonOutputFormat == JsonOutputFormatArray)
            {
                WriteUsageError(
                    "--json=array is not supported with search --group-by because grouped count output is a JSON object.",
                    GetUsageLineOrThrow("search"),
                    "Use plain `--json` for the grouped-count object.");
                return CommandExitCodes.UsageError;
            }
        }
        if (options.CountBy != null && options.UniqueBy != null)
        {
            WriteUsageError(
                "--count-by cannot be combined with --unique.",
                GetUsageLineOrThrow("search"),
                "Run one aggregation mode at a time.");
            return CommandExitCodes.UsageError;
        }
        if (options.GroupBy != null && (options.CountBy != null || options.UniqueBy != null))
        {
            WriteUsageError(
                "--group-by cannot be combined with --count-by or --unique.",
                GetUsageLineOrThrow("search"),
                "Use either `--group-by <field> --count`, `--count-by <field>`, or `--unique <field>`.");
            return CommandExitCodes.UsageError;
        }
        if ((options.CountBy != null || options.UniqueBy != null) && options.JsonOutputFormat == JsonOutputFormatArray)
        {
            WriteUsageError(
                "--json=array is not supported with search aggregation because aggregation output is a JSON object.",
                GetUsageLineOrThrow("search"),
                "Use plain `--json` for `--count-by` or `--unique` aggregation output.");
            return CommandExitCodes.UsageError;
        }
        if (options.OutputFormat == OutputFormatGrouped && options.JsonOutputFormat == JsonOutputFormatArray)
        {
            WriteUsageError(
                "--json=array is not supported with search --format grouped because grouped output is a JSON object.",
                GetUsageLineOrThrow("search"),
                "Use plain `--json` or omit --json when using `--format grouped`.");
            return CommandExitCodes.UsageError;
        }
        if (options.ResultsOnly && options.JsonOutputFormat != JsonOutputFormatNdjson)
        {
            WriteUsageError(
                "--results-only is only supported with NDJSON search output.",
                GetUsageLineOrThrow("search"),
                "Use `--results-only --json=ndjson`, or remove --results-only when using --json=array.");
            return CommandExitCodes.UsageError;
        }
        if (options.MaxJsonBytes.HasValue && (!options.Json || options.JsonOutputFormat != JsonOutputFormatNdjson))
        {
            WriteUsageError(
                "--max-json-bytes is only supported with NDJSON search output.",
                GetUsageLineOrThrow("search"),
                "Use `--json=ndjson --max-json-bytes <n>` for bounded streaming output.");
            return CommandExitCodes.UsageError;
        }
        if (options.CountBy != null && options.CountBy is not "path" and not "file" and not "symbol" and not "origin")
        {
            WriteUsageError(
                "--count-by for search must be one of path, file, symbol, or origin.",
                GetUsageLineOrThrow("search"),
                "Use `--count-by path`, `--count-by symbol`, or `--count-by origin`.");
            return CommandExitCodes.UsageError;
        }
        if (options.UniqueBy != null && options.UniqueBy is not "path" and not "file" and not "symbol" and not "origin")
        {
            WriteUsageError(
                "--unique for search must be one of path, file, symbol, or origin.",
                GetUsageLineOrThrow("search"),
                "Use `--unique path`, `--unique symbol`, or `--unique origin`.");
            return CommandExitCodes.UsageError;
        }
        if (options.OutputFormat == OutputFormatIssueDrafts)
            return RunSearchIssueDrafts(options, jsonOptions, exact, cancellationToken);

        var exactSubstringHint = SearchQueryAdvisor.BuildExactSubstringHint(options.Query, options.RawFts, exact, options.Prefix);
        var ndjsonOptions = options.JsonOutputFormat == JsonOutputFormatNdjson ? GetCompactJsonOptions(jsonOptions) : jsonOptions;
        int? jsonDoneCount = null;
        var jsonDoneInterrupted = false;
        DbReader? jsonDoneReader = null;
        return WithDb(options, jsonOptions, reader =>
        {
            jsonDoneReader = reader;
            if (options.GroupBy != null)
            {
                return RunGroupedSearchCount(reader, options, jsonOptions, exact, exactSubstringHint);
            }
            if (options.CountBy != null || options.UniqueBy != null)
            {
                return RunSearchAggregation(reader, options, jsonOptions, exact, exactSubstringHint);
            }

            if (options.CountOnly)
            {
                var counts = HasSearchOriginFilters(options)
                    ? CountFilteredSearchResults(reader, options, exact)
                    : reader.CountSearchResults(options.Query, options.Lang, options.RawFts, options.PathPatterns, options.ExcludePaths, options.ExcludeTests, !options.NoDedup, options.Since, exact, options.Prefix, !options.NoVisibilityRank, options.GuardFilters, options.GuardWindow);
                var queryDiagnostics = DbReader.AnalyzeFtsQuery(options.Query, options.RawFts, options.Prefix, options.Lang);
                if (counts.Count == 0)
                {
                    if (options.Json)
                    {
                        Console.WriteLine(BuildCountJsonPayload(
                            reader,
                            jsonOptions,
                            count: 0,
                            files: 0,
                            query: options.Query,
                            queryOptions: options,
                            ftsQueryDiagnostics: queryDiagnostics,
                            exactSubstringHint: exactSubstringHint).ToJsonString(jsonOptions));
                    }
                    else
                    {
                        Console.WriteLine("0");
                        WriteExactSubstringHintIfNeeded(exactSubstringHint);
                    }
                    return CommandExitCodes.Success;
                }

                if (options.Json)
                {
                    Console.WriteLine(BuildCountJsonPayload(
                        reader,
                        jsonOptions,
                        counts.Count,
                        counts.FileCount,
                        query: options.Query,
                        queryOptions: options,
                        ftsQueryDiagnostics: queryDiagnostics,
                        exactSubstringHint: exactSubstringHint).ToJsonString(jsonOptions));
                }
                else
                {
                    Console.WriteLine($"{counts.Count}");
                    WriteExactSubstringHintIfNeeded(exactSubstringHint);
                }
                return CommandExitCodes.Success;
            }

            var ftsQueryDiagnostics = DbReader.AnalyzeFtsQuery(options.Query, options.RawFts, options.Prefix, options.Lang);
            var displayRows = ReadSearchDisplayRows(reader, options, exact);
            var selection = ApplySearchOutputSelection(displayRows, options);
            displayRows = selection.Rows;
            if (displayRows.Count == 0)
            {
                if (options.Json && (options.OutputFormat == OutputFormatCsv || options.OutputFormat == OutputFormatTsv))
                {
                    WriteDelimitedSearchResults([], options);
                    return ZeroResultExitCode(options);
                }
                if (options.Json && TryWriteEmptyFormattedResult(options, jsonOptions))
                    return ZeroResultExitCode(options);
                if (options.Json)
                {
                    if (TryWriteEmptyFormattedResult(options, jsonOptions))
                        return ZeroResultExitCode(options);
                    if (options.JsonOutputFormat == JsonOutputFormatArray)
                    {
                        Console.WriteLine(JsonSerializer.Serialize(
                            Array.Empty<CompactSearchResult>(),
                            CliJsonSerializerContextFactory.Create(jsonOptions).CompactSearchResultArray));
                    }
                    else
                    {
                        var pathHint = BuildSearchPathGlobHint(reader, options);
                        if (!options.ResultsOnly)
                            Console.WriteLine(BuildJsonZeroResultPayload(reader, ndjsonOptions, resultsKey: "results", query: options.Query, ftsQueryDiagnostics: ftsQueryDiagnostics, queryOptions: options, exactSubstringHint: exactSubstringHint, extraFields: payload => AddSearchPathHint(payload, pathHint)).ToJsonString(ndjsonOptions));
                        jsonDoneCount = 0;
                    }
                }
                else if (!options.Json)
                {
                    CommandErrorWriter.WriteStderr(BuildZeroResultLine("No results found", options));
                    WriteLangHint(options.Lang, reader);
                    WriteExactSubstringHintIfNeeded(exactSubstringHint);
                    var pathHint = BuildSearchPathGlobHint(reader, options);
                    WriteZeroResultHints(options, reader, filterHint: pathHint?.SuggestedAction);
                }
                return ZeroResultExitCode(options);
            }

            if (options.Json)
            {
                var compactResults = displayRows.Select(row => row.Compact).ToArray();
                AttachExactSubstringHint(compactResults, exactSubstringHint);
                AttachSearchNextSteps(compactResults, options);
                if (options.SearchFields != null)
                {
                    WriteProjectedSearchResults(compactResults, options, jsonOptions, ndjsonOptions, out var projectedDoneCount, out var projectedInterrupted);
                    jsonDoneCount = projectedDoneCount;
                    jsonDoneInterrupted = projectedInterrupted;
                    return CommandExitCodes.Success;
                }
                if (options.OutputFormat == OutputFormatCompact)
                {
                    WriteCompactSearchResults(compactResults, jsonOptions);
                    return CommandExitCodes.Success;
                }
                if (options.OutputFormat == OutputFormatGrouped)
                {
                    WriteGroupedSearchResults(displayRows, options, jsonOptions);
                    return CommandExitCodes.Success;
                }
                if (options.OutputFormat == OutputFormatCsv || options.OutputFormat == OutputFormatTsv)
                {
                    WriteDelimitedSearchResults(displayRows, options);
                    return CommandExitCodes.Success;
                }
                if (TryWriteFormattedLocations(
                    options,
                    displayRows.SelectMany(row => ToSearchFormattedLocations(row, options.Query, exact)),
                    jsonOptions))
                    return CommandExitCodes.Success;
                if (options.OutputFormat == OutputFormatLsp)
                {
                    WriteLspLocations(displayRows.SelectMany(row => ToSearchLspLocations(row, exact)), jsonOptions);
                    return CommandExitCodes.Success;
                }
                if (options.OutputFormat == OutputFormatQf)
                {
                    WriteQuickfix(displayRows.SelectMany(row => ToSearchQuickfixItems(row, options.Query, exact)));
                    return CommandExitCodes.Success;
                }
                if (options.OutputFormat == OutputFormatSarif)
                {
                    WriteSarif(displayRows.SelectMany(row => ToSearchSarifItems(row, options.Query, exact)), jsonOptions);
                    return CommandExitCodes.Success;
                }
                if (options.JsonOutputFormat == JsonOutputFormatArray)
                {
                    Console.WriteLine(JsonSerializer.Serialize(
                        compactResults,
                        CliJsonSerializerContextFactory.Create(jsonOptions).CompactSearchResultArray));
                }
                else
                {
                    WriteSearchNdjsonResults(compactResults, options, ndjsonOptions, out var emittedCount, out var interrupted);
                    jsonDoneCount = emittedCount;
                    jsonDoneInterrupted = interrupted;
                }
            }
            else
            {
                if (options.OutputFormat == OutputFormatGrouped)
                {
                    WriteGroupedSearchResultsHuman(displayRows, options);
                }
                else
                {
                    foreach (var row in displayRows)
                    {
                        var r = row.Result;
                        Console.WriteLine($"{r.Path}:{r.StartLine}-{r.EndLine}{FormatSearchVisibilitySuffix(r.Visibility)}");
                        var snippetLines = row.Compact.Snippet.Split('\n', StringSplitOptions.None);
                        foreach (var line in snippetLines)
                            Console.WriteLine($"  {line}");
                        Console.WriteLine();
                    }
                }
                var fileCount = displayRows.Select(row => row.Result.Path).Distinct().Count();
                CommandErrorWriter.WriteStderr($"({displayRows.Count} results in {fileCount} files)");
                WriteExactSubstringHintIfNeeded(exactSubstringHint);
                WriteSearchNextSteps(displayRows, options);
            }
            return CommandExitCodes.Success;
        }, exitCode =>
        {
            if (options.Json && options.JsonOutputFormat == JsonOutputFormatNdjson && jsonDoneCount.HasValue && !options.ResultsOnly)
                WriteJsonStreamDone(jsonDoneCount.Value, ndjsonOptions, jsonDoneInterrupted, jsonDoneReader);
        });
    }

    private static int RunGroupedSearchCount(DbReader reader, QueryCommandOptions options, JsonSerializerOptions jsonOptions, bool exact, SearchQueryHint? exactSubstringHint)
    {
        if (options.GroupBy == "file" && !HasSearchOriginFilters(options))
        {
            var fileGroups = reader.CountSearchResultsByFile(options.Query!, options.Lang, options.RawFts, options.PathPatterns, options.ExcludePaths, options.ExcludeTests, !options.NoDedup, options.Since, exact, options.Prefix, !options.NoVisibilityRank, options.GuardFilters, options.GuardWindow, options.GuardScope);
            var totalCount = fileGroups.Sum(group => group.Count);
            var fileCountGroups = fileGroups
                .Select(group => new SearchGroupedCountItemJsonResult(
                    group.Path,
                    group.Count,
                    group.Path,
                    null,
                    null,
                    null,
                    null,
                    null))
                .ToList();

            if (options.Json)
            {
                Console.WriteLine(JsonSerializer.Serialize(
                    new SearchGroupedCountJsonResult(
                        JsonOutputContract.ApiVersion,
                        options.Query!,
                        options.GroupBy!,
                        totalCount,
                        fileGroups.Count,
                        fileCountGroups),
                    CliJsonSerializerContextFactory.Create(jsonOptions).SearchGroupedCountJsonResult));
            }
            else
            {
                WriteSearchGroupedCounts(options.GroupBy!, fileCountGroups, totalCount, fileGroups.Count);
                WriteExactSubstringHintIfNeeded(exactSubstringHint);
            }

            return CommandExitCodes.Success;
        }

        var results = reader.Search(options.Query!, int.MaxValue, options.Lang, options.RawFts, options.PathPatterns, options.ExcludePaths, options.ExcludeTests, !options.NoDedup, options.Since, exact, options.Prefix, !options.NoVisibilityRank, guardFilters: options.GuardFilters, guardWindow: options.GuardWindow, guardScope: options.GuardScope);
        var displayRows = BuildSearchDisplayRows(results, options, exact);
        var groups = BuildSearchGroupedCounts(options.GroupBy!, displayRows);
        var fileCount = displayRows.Select(row => row.Result.Path).Distinct(StringComparer.Ordinal).Count();

        if (options.Json)
        {
            Console.WriteLine(JsonSerializer.Serialize(
                new SearchGroupedCountJsonResult(
                    JsonOutputContract.ApiVersion,
                    options.Query!,
                    options.GroupBy!,
                    displayRows.Count,
                    fileCount,
                    groups),
                CliJsonSerializerContextFactory.Create(jsonOptions).SearchGroupedCountJsonResult));
        }
        else
        {
            WriteSearchGroupedCounts(options.GroupBy!, groups, displayRows.Count, fileCount);
            WriteExactSubstringHintIfNeeded(exactSubstringHint);
        }

        return CommandExitCodes.Success;
    }

    private static List<SearchGroupedCountItemJsonResult> BuildSearchGroupedCounts(string groupBy, List<SearchDisplayRow> rows)
        => groupBy == "file"
            ? rows
                .GroupBy(row => row.Result.Path, StringComparer.Ordinal)
                .Select(group => new SearchGroupedCountItemJsonResult(
                    group.Key,
                    group.Count(),
                    group.Key,
                    null,
                    null,
                    null,
                    null,
                    null))
                .OrderByDescending(group => group.Count)
                .ThenBy(group => group.Key, StringComparer.Ordinal)
                .ToList()
            : groupBy == "origin"
                ? rows
                    .SelectMany(row => row.Compact.MatchOrigins.Count == 0
                        ? [SearchMatchClassifier.Unknown]
                        : row.Compact.MatchOrigins)
                    .GroupBy(origin => origin, StringComparer.Ordinal)
                    .Select(group => new SearchGroupedCountItemJsonResult(
                        group.Key,
                        group.Count(),
                        null,
                        null,
                        null,
                        null,
                        null,
                        null))
                    .OrderByDescending(group => group.Count)
                    .ThenBy(group => group.Key, StringComparer.Ordinal)
                    .ToList()
            : rows
                .GroupBy(row => BuildSearchSymbolGroupKey(row.Result), StringComparer.Ordinal)
                .Select(group =>
                {
                    var result = group.First().Result;
                    var key = BuildSearchSymbolDisplayKey(result);
                    return new SearchGroupedCountItemJsonResult(
                        key,
                        group.Count(),
                        result.Path,
                        result.EnclosingSymbolName,
                        result.EnclosingSymbolKind,
                        result.EnclosingSymbolStartLine,
                        result.EnclosingSymbolEndLine,
                        result.EnclosingContainerName);
                })
                .OrderByDescending(group => group.Count)
                .ThenBy(group => group.Key, StringComparer.Ordinal)
                .ToList();

    private static string BuildSearchSymbolGroupKey(SearchResult result)
        => result.EnclosingSymbolName == null
            ? string.Join('\0', result.Path, "<no-symbol>")
            : string.Join(
                '\0',
                result.Path,
                result.EnclosingSymbolKind ?? string.Empty,
                result.EnclosingSymbolName,
                result.EnclosingSymbolStartLine?.ToString(CultureInfo.InvariantCulture) ?? string.Empty,
                result.EnclosingSymbolEndLine?.ToString(CultureInfo.InvariantCulture) ?? string.Empty);

    private static string BuildSearchSymbolDisplayKey(SearchResult result)
    {
        if (result.EnclosingSymbolName == null)
            return $"{result.Path}:<no enclosing symbol>";

        var start = result.EnclosingSymbolStartLine?.ToString(CultureInfo.InvariantCulture) ?? "?";
        var kind = result.EnclosingSymbolKind ?? "symbol";
        return $"{result.Path}:{start}:{kind}:{result.EnclosingSymbolName}";
    }

    private static void WriteSearchGroupedCounts(string groupBy, List<SearchGroupedCountItemJsonResult> groups, int totalCount, int fileCount)
    {
        foreach (var group in groups)
        {
            if (groupBy == "file")
            {
                Console.WriteLine($"{group.Count,8} {group.File}");
                continue;
            }
            if (groupBy == "origin")
            {
                Console.WriteLine($"{group.Count,8} {group.Key}");
                continue;
            }

            var location = group.SymbolStartLine.HasValue
                ? $"{group.File}:{group.SymbolStartLine}-{group.SymbolEndLine ?? group.SymbolStartLine}"
                : group.File ?? group.Key;
            var symbol = group.SymbolName == null
                ? "<no enclosing symbol>"
                : $"{group.SymbolKind ?? "symbol"} {group.SymbolName}";
            var container = group.ContainerName == null ? string.Empty : $" ({group.ContainerName})";
            Console.WriteLine($"{group.Count,8} {location} {symbol}{container}");
        }

        CommandErrorWriter.WriteStderr($"({totalCount} results in {fileCount} files; grouped by {groupBy})");
    }

    private static int RunSearchAggregation(DbReader reader, QueryCommandOptions options, JsonSerializerOptions jsonOptions, bool exact, SearchQueryHint? exactSubstringHint)
    {
        var results = reader.Search(options.Query!, int.MaxValue, options.Lang, options.RawFts, options.PathPatterns, options.ExcludePaths, options.ExcludeTests, !options.NoDedup, options.Since, exact, options.Prefix, !options.NoVisibilityRank, guardFilters: options.GuardFilters, guardWindow: options.GuardWindow, guardScope: options.GuardScope);
        var rows = BuildSearchDisplayRows(results, options, exact);
        var groupBy = NormalizeSearchAggregationKey(options.CountBy ?? options.UniqueBy!);
        var groups = BuildSearchGroupedCounts(groupBy, rows);
        var uniqueOnly = options.UniqueBy != null;
        var fileCount = rows.Select(row => row.Result.Path).Distinct(StringComparer.Ordinal).Count();

        if (options.Json)
        {
            Console.WriteLine(JsonSerializer.Serialize(
                new SearchAggregationJsonResult(
                    JsonOutputContract.ApiVersion,
                    options.Query!,
                    uniqueOnly ? "unique" : "count_by",
                    groupBy,
                    rows.Count,
                    fileCount,
                    uniqueOnly,
                    groups),
                CliJsonSerializerContextFactory.Create(jsonOptions).SearchAggregationJsonResult));
        }
        else
        {
            if (uniqueOnly)
            {
                foreach (var group in groups)
                    Console.WriteLine(group.Key);
                CommandErrorWriter.WriteStderr($"({groups.Count} unique {groupBy} values from {rows.Count} results in {fileCount} files)");
            }
            else
            {
                WriteSearchGroupedCounts(groupBy, groups, rows.Count, fileCount);
            }
            WriteExactSubstringHintIfNeeded(exactSubstringHint);
        }

        return CommandExitCodes.Success;
    }

    private static string NormalizeSearchAggregationKey(string key)
        => key == "path" ? "file" : key;

    private static SearchOutputSelection ApplySearchOutputSelection(List<SearchDisplayRow> rows, QueryCommandOptions options)
    {
        var originalCount = rows.Count;
        if (options.FirstPerFile)
        {
            rows = rows
                .GroupBy(row => row.Result.Path, StringComparer.Ordinal)
                .Select(group => group.First())
                .ToList();
        }

        if (options.SampleSize.HasValue && rows.Count > options.SampleSize.Value)
            rows = SampleSearchRows(rows, options.SampleSize.Value);

        if (rows.Count > options.Limit)
            rows = rows.Take(options.Limit).ToList();

        return new SearchOutputSelection(rows, originalCount, rows.Count < originalCount);
    }

    private static List<SearchDisplayRow> SampleSearchRows(List<SearchDisplayRow> rows, int sampleSize)
    {
        if (sampleSize <= 0 || rows.Count <= sampleSize)
            return rows;
        if (sampleSize == 1)
            return [rows[0]];

        var sampled = new List<SearchDisplayRow>(sampleSize);
        var lastIndex = rows.Count - 1;
        for (var i = 0; i < sampleSize; i++)
        {
            var index = (int)Math.Round(i * (lastIndex / (double)(sampleSize - 1)), MidpointRounding.AwayFromZero);
            sampled.Add(rows[Math.Clamp(index, 0, lastIndex)]);
        }
        return sampled;
    }

    private static void WriteGroupedSearchResults(List<SearchDisplayRow> rows, QueryCommandOptions options, JsonSerializerOptions jsonOptions)
    {
        var groups = BuildSearchFileGroups(rows, options);
        var totalMatches = rows.Count;
        Console.WriteLine(JsonSerializer.Serialize(
            new SearchFileGroupedJsonResult(
                JsonOutputContract.ApiVersion,
                options.Query!,
                totalMatches,
                groups.Count,
                rows.Select(row => row.Result.Path).Distinct(StringComparer.Ordinal).Count(),
                options.GroupedPerFileLimit,
                groups.Any(group => group.Truncated),
                groups),
            CliJsonSerializerContextFactory.Create(jsonOptions).SearchFileGroupedJsonResult));
    }

    private static void WriteGroupedSearchResultsHuman(List<SearchDisplayRow> rows, QueryCommandOptions options)
    {
        foreach (var group in BuildSearchFileGroups(rows, options))
        {
            Console.WriteLine($"{group.Path} ({group.Count} results)");
            foreach (var result in group.Results)
            {
                Console.WriteLine($"  {result.Path}:{result.SnippetStartLine}-{result.SnippetEndLine}");
                var firstLine = result.Snippet.Split('\n', StringSplitOptions.None).FirstOrDefault();
                if (!string.IsNullOrWhiteSpace(firstLine))
                    Console.WriteLine($"    {firstLine.Trim()}");
            }
            if (group.Truncated)
                Console.WriteLine($"  ... {group.OmittedCount} more result(s)");
        }
    }

    private static List<SearchFileGroupJsonResult> BuildSearchFileGroups(List<SearchDisplayRow> rows, QueryCommandOptions options)
        => rows
            .GroupBy(row => row.Result.Path, StringComparer.Ordinal)
            .Select(group =>
            {
                var groupRows = group.ToList();
                var representative = groupRows.Take(options.GroupedPerFileLimit).Select(row => row.Compact).ToList();
                return new SearchFileGroupJsonResult(
                    group.Key,
                    groupRows.Count,
                    representative,
                    groupRows.Count > representative.Count,
                    Math.Max(0, groupRows.Count - representative.Count));
            })
            .OrderByDescending(group => group.Count)
            .ThenBy(group => group.Path, StringComparer.Ordinal)
            .ToList();

    private static void WriteProjectedSearchResults(CompactSearchResult[] results, QueryCommandOptions options, JsonSerializerOptions jsonOptions, JsonSerializerOptions ndjsonOptions, out int emittedCount, out bool interrupted)
    {
        var projected = results.Select(result => BuildProjectedSearchResult(result, options.SearchFields!)).ToArray();
        if (options.JsonOutputFormat == JsonOutputFormatArray)
        {
            Console.WriteLine(JsonSerializer.Serialize(projected, jsonOptions));
            emittedCount = projected.Length;
            interrupted = false;
            return;
        }

        emittedCount = 0;
        interrupted = false;
        var bytesWritten = 0;
        foreach (var result in projected)
        {
            var line = result.ToJsonString(ndjsonOptions);
            if (WouldExceedJsonByteLimit(options, bytesWritten, line, out interrupted))
                break;
            Console.WriteLine(line);
            bytesWritten += Encoding.UTF8.GetByteCount(line) + Environment.NewLine.Length;
            emittedCount++;
        }
    }

    private static JsonObject BuildProjectedSearchResult(CompactSearchResult result, IReadOnlyList<string> fields)
    {
        var payload = new JsonObject();
        foreach (var field in fields)
        {
            switch (field)
            {
                case "path":
                    payload["path"] = result.Path;
                    break;
                case "line":
                    payload["line"] = result.MatchLines.Count > 0 ? result.MatchLines[0] : result.ChunkStartLine;
                    break;
                case "end_line":
                    payload["end_line"] = result.ChunkEndLine;
                    break;
                case "lang":
                    payload["lang"] = result.Lang;
                    break;
                case "column":
                    payload["column"] = result.MatchFacets.Count > 0 ? result.MatchFacets[0].Column : (int?)null;
                    break;
                case "symbol":
                    payload["symbol"] = result.EnclosingSymbolName;
                    break;
                case "symbol_kind":
                    payload["symbol_kind"] = result.EnclosingSymbolKind;
                    break;
                case "origin":
                    payload["match_origins"] = JsonSerializer.SerializeToNode(result.MatchOrigins);
                    break;
                case "kind":
                    payload["result_kinds"] = JsonSerializer.SerializeToNode(result.ResultKinds);
                    break;
                case "score":
                    payload["score"] = result.Score;
                    break;
                case "snippet":
                    payload["snippet"] = result.Snippet;
                    break;
            }
        }
        return payload;
    }

    private static void WriteSearchNdjsonResults(CompactSearchResult[] results, QueryCommandOptions options, JsonSerializerOptions ndjsonOptions, out int emittedCount, out bool interrupted)
    {
        emittedCount = 0;
        interrupted = false;
        var bytesWritten = 0;
        foreach (var result in results)
        {
            var line = JsonSerializer.Serialize(result, CliJsonSerializerContextFactory.Create(ndjsonOptions).CompactSearchResult);
            if (WouldExceedJsonByteLimit(options, bytesWritten, line, out interrupted))
                break;
            Console.WriteLine(line);
            bytesWritten += Encoding.UTF8.GetByteCount(line) + Environment.NewLine.Length;
            emittedCount++;
        }
    }

    private static bool WouldExceedJsonByteLimit(QueryCommandOptions options, int bytesWritten, string nextLine, out bool interrupted)
    {
        interrupted = false;
        if (!options.MaxJsonBytes.HasValue)
            return false;
        var nextBytes = Encoding.UTF8.GetByteCount(nextLine) + Environment.NewLine.Length;
        if (bytesWritten + nextBytes <= options.MaxJsonBytes.Value)
            return false;
        interrupted = true;
        return true;
    }

    private static void WriteSearchNextSteps(List<SearchDisplayRow> rows, QueryCommandOptions options)
    {
        if (!options.NextSteps || rows.Count == 0)
            return;
        CommandErrorWriter.WriteStderr("Next steps:");
        foreach (var row in rows.Take(MaxSearchNextStepLimit))
        {
            var line = row.Compact.MatchLines.Count > 0 ? row.Compact.MatchLines[0] : row.Result.StartLine;
            CommandErrorWriter.WriteStderr($"  cdidx inspect --path \"{row.Result.Path}\" --line {line}");
            CommandErrorWriter.WriteStderr($"  cdidx excerpt --path \"{row.Result.Path}\" --start {Math.Max(1, line - 3)} --end {line + 3}");
        }
    }

    private static void AttachSearchNextSteps(CompactSearchResult[] results, QueryCommandOptions options)
    {
        if (!options.NextSteps || results.Length == 0)
            return;
        var truncated = results.Length > MaxSearchNextStepLimit;
        foreach (var result in results.Take(MaxSearchNextStepLimit))
        {
            var line = result.MatchLines.Count > 0 ? result.MatchLines[0] : result.ChunkStartLine;
            result.NextSteps =
            [
                new SearchCommandHint
                {
                    Command = $"cdidx inspect --path \"{result.Path}\" --line {line}",
                    Purpose = "inspect the enclosing symbol for this search hit",
                },
                new SearchCommandHint
                {
                    Command = $"cdidx excerpt --path \"{result.Path}\" --start {Math.Max(1, line - 3)} --end {line + 3}",
                    Purpose = "read a bounded source excerpt around this search hit",
                },
            ];
            result.NextStepsTruncated = truncated;
        }
    }

    private sealed record SearchOutputSelection(List<SearchDisplayRow> Rows, int OriginalCount, bool Truncated);

    private static int RunSearchNamedBatch(QueryCommandOptions options, JsonSerializerOptions jsonOptions, bool userExact)
    {
        return WithDb(options, jsonOptions, reader =>
        {
            var queryResults = CollectSearchNamedBatchQueryResults(reader, options, userExact, out var total);

            if (options.Json)
            {
                Console.WriteLine(JsonSerializer.Serialize(
                    new SearchNamedBatchRunJsonResult(
                        JsonOutputContract.ApiVersion,
                        queryResults.Count,
                        total,
                        queryResults),
                    CliJsonSerializerContextFactory.Create(jsonOptions).SearchNamedBatchRunJsonResult));
                return CommandExitCodes.Success;
            }

            Console.WriteLine("Named search batch");
            Console.WriteLine();
            foreach (var queryResult in queryResults)
            {
                Console.WriteLine($"[{queryResult.Name}] {queryResult.Query}");
                Console.WriteLine($"results: {queryResult.Count}");
                foreach (var result in queryResult.Results)
                {
                    Console.WriteLine($"{result.Path}:{result.ChunkStartLine}-{result.ChunkEndLine}");
                    foreach (var line in result.Snippet.Split('\n', StringSplitOptions.None))
                        Console.WriteLine($"  {line}");
                }
                Console.WriteLine();
            }

            CommandErrorWriter.WriteStderr($"({total} named-query results across {queryResults.Count} queries)");
            return CommandExitCodes.Success;
        });
    }

    private static int WriteSearchRecipeList(QueryCommandOptions options, JsonSerializerOptions jsonOptions)
    {
        var recipes = SearchAuditRecipes.All
            .Select(recipe => ToSearchRecipeListItem(recipe))
            .ToList();
        if (options.Json)
        {
            Console.WriteLine(JsonSerializer.Serialize(
                new SearchRecipeListJsonResult(JsonOutputContract.ApiVersion, recipes.Count, recipes),
                CliJsonSerializerContextFactory.Create(jsonOptions).SearchRecipeListJsonResult));
            return CommandExitCodes.Success;
        }

        foreach (var recipe in recipes)
        {
            Console.WriteLine($"{recipe.Name}: {recipe.Description}");
            Console.WriteLine($"  labels: {string.Join(", ", recipe.RecommendedLabels)}");
            Console.WriteLine($"  default scope: {recipe.DefaultScope}");
            if (recipe.DefaultPathPatterns.Count > 0)
                Console.WriteLine($"  default paths: {string.Join(", ", recipe.DefaultPathPatterns)}");
            if (recipe.DefaultExcludePaths.Count > 0)
                Console.WriteLine($"  default excludes: {string.Join(", ", recipe.DefaultExcludePaths)}");
            foreach (var query in recipe.Queries)
            {
                var mode = query.ExactSubstring ? "exact-substring" : "fts";
                Console.WriteLine($"  - {query.Name}: {query.Query} ({mode})");
                Console.WriteLine($"    {query.Description}");
                Console.WriteLine($"    false positives: {query.FalsePositiveGuidance}");
                if (query.BroadCatchTaxonomy is not null)
                {
                    Console.WriteLine($"    broad catch boundaries: {string.Join(", ", query.BroadCatchTaxonomy.BoundaryCategories.Select(category => category.Name))}");
                    Console.WriteLine($"    broad catch diagnostics: {string.Join(", ", query.BroadCatchTaxonomy.DiagnosticBehaviors.Select(behavior => behavior.Name))}");
                }
            }
        }

        return CommandExitCodes.Success;
    }

    private static bool TryResolveSearchRecipeSelection(
        QueryCommandOptions options,
        out SearchRecipeSelection selection,
        out string? error)
    {
        selection = default!;
        error = null;
        var recipeSelector = options.RecipeName!;
        var recipeName = recipeSelector;
        string? directQueryName = null;
        var slash = recipeSelector.IndexOf('/');
        if (slash >= 0)
        {
            if (slash == 0 || slash == recipeSelector.Length - 1 || slash != recipeSelector.LastIndexOf('/'))
            {
                error = "--recipe child selection must use recipe/query form.";
                return false;
            }
            if (options.IncludeRecipeQueries.Count > 0 || options.ExcludeRecipeQueries.Count > 0)
            {
                error = "--recipe recipe/query cannot be combined with --include-query or --exclude-query.";
                return false;
            }

            recipeName = recipeSelector[..slash];
            directQueryName = recipeSelector[(slash + 1)..];
        }

        if (!SearchAuditRecipes.TryGet(recipeName, out var recipe))
        {
            var available = string.Join(", ", SearchAuditRecipes.All.Select(r => r.Name));
            error = $"unknown search recipe '{recipeName}'. Available recipes: {available}.";
            return false;
        }

        var queryByName = recipe.Queries.ToDictionary(query => query.Name, StringComparer.OrdinalIgnoreCase);
        var availableQueries = string.Join(", ", recipe.Queries.Select(query => query.Name));
        if (!TryValidateRecipeQuerySelectors(queryByName, availableQueries, recipe.Name, options.IncludeRecipeQueries, "--include-query", out error) ||
            !TryValidateRecipeQuerySelectors(queryByName, availableQueries, recipe.Name, options.ExcludeRecipeQueries, "--exclude-query", out error))
        {
            return false;
        }
        if (directQueryName != null && !queryByName.ContainsKey(directQueryName))
        {
            error = $"unknown recipe query '{directQueryName}' for recipe '{recipe.Name}'. Available queries: {availableQueries}.";
            return false;
        }

        var selected = new List<SearchAuditRecipeQuery>();
        if (directQueryName != null)
        {
            selected.Add(queryByName[directQueryName]);
        }
        else if (options.IncludeRecipeQueries.Count > 0)
        {
            foreach (var queryName in options.IncludeRecipeQueries)
            {
                var query = queryByName[queryName];
                if (!selected.Any(existing => string.Equals(existing.Name, query.Name, StringComparison.OrdinalIgnoreCase)))
                    selected.Add(query);
            }
        }
        else
        {
            selected.AddRange(recipe.Queries);
        }

        if (options.ExcludeRecipeQueries.Count > 0)
        {
            var excludeSet = options.ExcludeRecipeQueries.ToHashSet(StringComparer.OrdinalIgnoreCase);
            selected = selected
                .Where(query => !excludeSet.Contains(query.Name))
                .ToList();
        }

        if (selected.Count == 0)
        {
            error = $"recipe query selection for '{recipe.Name}' is empty after applying --include-query/--exclude-query.";
            return false;
        }

        selection = new SearchRecipeSelection(recipe, selected);
        return true;
    }

    private static bool TryValidateRecipeQuerySelectors(
        IReadOnlyDictionary<string, SearchAuditRecipeQuery> queryByName,
        string availableQueries,
        string recipeName,
        IReadOnlyList<string> selectors,
        string optionName,
        out string? error)
    {
        foreach (var selector in selectors)
        {
            if (!queryByName.ContainsKey(selector))
            {
                error = $"unknown recipe query '{selector}' for recipe '{recipeName}' in {optionName}. Available queries: {availableQueries}.";
                return false;
            }
        }

        error = null;
        return true;
    }

    private sealed record SearchRecipeSelection(
        SearchAuditRecipe Recipe,
        List<SearchAuditRecipeQuery> Queries);

    private static int RunSearchRecipe(QueryCommandOptions options, JsonSerializerOptions jsonOptions, bool userExact)
    {
        if (!TryResolveSearchRecipeSelection(options, out var selection, out var selectionError))
        {
            WriteUsageError(
                selectionError!,
                GetUsageLineOrThrow("search"),
                "Use `cdidx search --recipe risky-code/raw-diagnostic-echo`, or `--include-query` / `--exclude-query` with a recipe name.");
            return CommandExitCodes.UsageError;
        }
        var recipe = selection.Recipe;
        var scope = BuildSearchRecipeScope(recipe, options);
        if (options.SearchCursor.HasValue && selection.Queries.Count != 1)
        {
            WriteUsageError(
                "--cursor requires exactly one selected recipe query.",
                GetUsageLineOrThrow("search"),
                "Use `--recipe recipe/query` or a single `--include-query` value with --cursor.");
            return CommandExitCodes.UsageError;
        }

        return WithDb(options, jsonOptions, reader =>
        {
            if (options.OutputFormat == OutputFormatCompact)
            {
                var compactQueryResults = CollectSearchRecipeCompactQueryResults(reader, selection.Queries, scope, options, userExact, out var compactTotal);
                Console.WriteLine(JsonSerializer.Serialize(
                    new SearchRecipeCompactRunJsonResult(
                        JsonOutputContract.ApiVersion,
                        ToSearchRecipeListItem(recipe, selection.Queries),
                        scope,
                        selection.Queries.Count,
                        compactTotal,
                        BuildSearchRecipeRunSummary(compactQueryResults, options.Limit, compactTotal),
                        compactQueryResults),
                    CliJsonSerializerContextFactory.Create(jsonOptions).SearchRecipeCompactRunJsonResult));
                return CommandExitCodes.Success;
            }

            var queryResults = CollectSearchRecipeQueryResults(reader, selection.Queries, scope, options, userExact, out var total);

            if (options.Json)
            {
                Console.WriteLine(JsonSerializer.Serialize(
                    new SearchRecipeRunJsonResult(
                        JsonOutputContract.ApiVersion,
                        ToSearchRecipeListItem(recipe, selection.Queries),
                        scope,
                        selection.Queries.Count,
                        total,
                        BuildSearchRecipeRunSummary(queryResults, options.Limit, total),
                        queryResults),
                    CliJsonSerializerContextFactory.Create(jsonOptions).SearchRecipeRunJsonResult));
                return CommandExitCodes.Success;
            }

            Console.WriteLine($"Recipe: {recipe.Name}");
            Console.WriteLine(recipe.Description);
            Console.WriteLine($"Scope: {scope.Name}");
            if (scope.PathPatterns.Count > 0)
                Console.WriteLine($"Paths: {string.Join(", ", scope.PathPatterns)}");
            if (scope.ExcludePaths.Count > 0)
                Console.WriteLine($"Excludes: {string.Join(", ", scope.ExcludePaths)}");
            Console.WriteLine($"Exclude tests: {scope.ExcludeTests.ToString().ToLowerInvariant()}");
            if (scope.ExcludedDiagnostics is { Count: > 0 })
            {
                Console.WriteLine("Excluded diagnostics:");
                foreach (var diagnostic in scope.ExcludedDiagnostics)
                {
                    var patterns = diagnostic.Patterns.Count == 0
                        ? string.Empty
                        : $" ({string.Join(", ", diagnostic.Patterns)})";
                    Console.WriteLine($"  - {diagnostic.Reason}: applied={diagnostic.Applied.ToString().ToLowerInvariant()}{patterns}");
                    Console.WriteLine($"    {diagnostic.Description}");
                }
            }
            Console.WriteLine();
            foreach (var queryResult in queryResults)
            {
                Console.WriteLine($"[{queryResult.Name}] {queryResult.Query}");
                Console.WriteLine(queryResult.Description);
                Console.WriteLine($"labels: {string.Join(", ", queryResult.RecommendedLabels)}");
                Console.WriteLine($"false positives: {queryResult.FalsePositiveGuidance}");
                if (queryResult.BroadCatchTaxonomy is not null)
                {
                    Console.WriteLine($"broad catch boundaries: {string.Join(", ", queryResult.BroadCatchTaxonomy.BoundaryCategories.Select(category => category.Name))}");
                    Console.WriteLine($"broad catch diagnostics: {string.Join(", ", queryResult.BroadCatchTaxonomy.DiagnosticBehaviors.Select(behavior => behavior.Name))}");
                }
                Console.WriteLine($"results: {queryResult.Count}");
                foreach (var result in queryResult.Results)
                {
                    Console.WriteLine($"{result.Path}:{result.ChunkStartLine}-{result.ChunkEndLine}");
                    foreach (var line in result.Snippet.Split('\n', StringSplitOptions.None))
                        Console.WriteLine($"  {line}");
                }
                Console.WriteLine();
            }

            CommandErrorWriter.WriteStderr($"({total} recipe results across {selection.Queries.Count} queries)");
            return CommandExitCodes.Success;
        });
    }

    private static int RunSearchRecipeIssueDrafts(
        QueryCommandOptions options,
        JsonSerializerOptions jsonOptions,
        bool userExact,
        CancellationToken cancellationToken)
    {
        if (!TryResolveSearchRecipeSelection(options, out var selection, out var selectionError))
        {
            WriteUsageError(
                selectionError!,
                GetUsageLineOrThrow("search"),
                "Use `cdidx search --recipe risky-code/raw-diagnostic-echo`, or `--include-query` / `--exclude-query` with a recipe name.");
            return CommandExitCodes.UsageError;
        }
        var recipe = selection.Recipe;
        var scope = BuildSearchRecipeScope(recipe, options);
        var preflightResult = IssueDuplicatePreflight.TryLoadAsync(
                options.OpenIssuesPath,
                options.OpenIssuesRepository,
                cancellationToken)
            .GetAwaiter()
            .GetResult();
        if (!preflightResult.Loaded)
        {
            WriteUsageError(
                preflightResult.Error!,
                GetUsageLineOrThrow("search"),
                "Pass a readable JSON array from `gh issue list --state open --json number,title,labels,url`, or use `--open-issues github --repo owner/name`.");
            return CommandExitCodes.UsageError;
        }
        var preflight = preflightResult.Preflight;

        return WithDb(options, jsonOptions, reader =>
        {
            var queryResults = CollectSearchRecipeQueryResults(reader, selection.Queries, scope, options, userExact, out var total);
            var drafts = queryResults
                .Where(queryResult => queryResult.Count > 0)
                .Select(queryResult => ToSearchIssueDraft(recipe, queryResult, preflight, options))
                .ToList();
            Console.WriteLine(JsonSerializer.Serialize(
                new SearchIssueDraftExportJsonResult(
                    JsonOutputContract.ApiVersion,
                    ToSearchRecipeListItem(recipe, selection.Queries),
                    scope,
                    selection.Queries.Count,
                    total,
                    drafts.Count,
                    new SuggestionIssueDraftPreflightSummaryJsonResult(
                        preflight.Checked,
                        preflight.Source,
                        preflight.OpenIssueCount,
                        options.DuplicateConfidence,
                        options.DuplicateThreshold),
                    drafts),
                CliJsonSerializerContextFactory.Create(jsonOptions).SearchIssueDraftExportJsonResult));
            return CommandExitCodes.Success;
        });
    }

    private static int RunSearchIssueDrafts(
        QueryCommandOptions options,
        JsonSerializerOptions jsonOptions,
        bool exact,
        CancellationToken cancellationToken)
    {
        var preflightResult = IssueDuplicatePreflight.TryLoadAsync(
                options.OpenIssuesPath,
                options.OpenIssuesRepository,
                cancellationToken)
            .GetAwaiter()
            .GetResult();
        if (!preflightResult.Loaded)
        {
            WriteUsageError(
                preflightResult.Error!,
                GetUsageLineOrThrow("search"),
                "Pass a readable JSON array from `gh issue list --state open --json number,title,labels,url`, or use `--open-issues github --repo owner/name`.");
            return CommandExitCodes.UsageError;
        }
        var preflight = preflightResult.Preflight;

        return WithDb(options, jsonOptions, reader =>
        {
            var results = reader.Search(
                options.Query!,
                options.Limit,
                options.Lang,
                options.RawFts,
                options.PathPatterns,
                options.ExcludePaths,
                options.ExcludeTests,
                !options.NoDedup,
                options.Since,
                exact,
                options.Prefix,
                !options.NoVisibilityRank,
                guardFilters: options.GuardFilters,
                guardWindow: options.GuardWindow,
                guardScope: options.GuardScope);
            var rows = BuildSearchDisplayRows(results, options, exact);
            var queryResult = new SearchRecipeQueryResultJsonResult(
                "ad-hoc",
                options.Query!,
                $"Ad hoc search for `{options.Query}`.",
                BuildAdHocIssueDraftLabels(options),
                "Review the evidence paths and surrounding code before filing.",
                exact,
                SearchAuditRecipes.DefaultQuerySeverity,
                [],
                [],
                [],
                [],
                [],
                null,
                rows.Count,
                options.Limit,
                0,
                BuildSearchRecipeTopFiles(rows),
                false,
                null,
                rows.Select(row => row.Compact).ToList());
            var drafts = rows.Count == 0
                ? []
                : new List<SearchIssueDraftJsonResult> { ToAdHocSearchIssueDraft(options, queryResult, preflight) };

            Console.WriteLine(JsonSerializer.Serialize(
                new SearchIssueDraftExportJsonResult(
                    JsonOutputContract.ApiVersion,
                    null,
                    null,
                    1,
                    rows.Count,
                    drafts.Count,
                    new SuggestionIssueDraftPreflightSummaryJsonResult(
                        preflight.Checked,
                        preflight.Source,
                        preflight.OpenIssueCount,
                        options.DuplicateConfidence,
                        options.DuplicateThreshold),
                    drafts),
                CliJsonSerializerContextFactory.Create(jsonOptions).SearchIssueDraftExportJsonResult));
            return CommandExitCodes.Success;
        });
    }

    private static List<SearchRecipeQueryResultJsonResult> CollectSearchRecipeQueryResults(
        DbReader reader,
        IReadOnlyList<SearchAuditRecipeQuery> recipeQueries,
        SearchRecipeScopeJsonResult scope,
        QueryCommandOptions options,
        bool userExact,
        out int total)
    {
        var queryResults = new List<SearchRecipeQueryResultJsonResult>();
        total = 0;
        foreach (var recipeQuery in recipeQueries)
        {
            var exact = userExact || recipeQuery.ExactSubstring;
            var queryScope = BuildSearchRecipeQueryScope(scope, recipeQuery);
            var results = reader.Search(
                recipeQuery.Query,
                FetchLimitForSearchEnvelope(options.Limit),
                options.Lang,
                false,
                queryScope.PathPatterns,
                queryScope.ExcludePaths,
                queryScope.ExcludeTests,
                !options.NoDedup,
                options.Since,
                exact,
                false,
                !options.NoVisibilityRank,
                cursor: options.SearchCursor,
                guardFilters: options.GuardFilters,
                guardWindow: options.GuardWindow,
                guardScope: options.GuardScope,
                requiredPathPatterns: GetSearchRecipeRequiredPathPatterns(options, recipeQuery));
            var rows = BuildSearchDisplayRows(results, options, exact, recipeQuery.Query, rawFtsOverride: false, recipeQuery: recipeQuery);
            var availableCount = rows.Count;
            var truncated = TrimSearchRowsToRequestedLimit(rows, options.Limit);
            var minimumOmitted = truncated ? Math.Max(1, availableCount - rows.Count) : 0;
            total += rows.Count;
            queryResults.Add(new SearchRecipeQueryResultJsonResult(
                recipeQuery.Name,
                recipeQuery.Query,
                recipeQuery.Description,
                recipeQuery.RecommendedLabels,
                recipeQuery.FalsePositiveGuidance,
                exact,
                recipeQuery.Severity,
                [.. recipeQuery.PathPatterns],
                [.. recipeQuery.ExcludePaths],
                [.. recipeQuery.MatchOrigins],
                [.. recipeQuery.ExcludeOrigins],
                [.. recipeQuery.ResultKinds],
                recipeQuery.BroadCatchTaxonomy,
                rows.Count,
                options.Limit,
                minimumOmitted,
                BuildSearchRecipeTopFiles(rows),
                truncated,
                truncated && rows.Count > 0 ? FormatSearchCursor(rows[^1].Result) : null,
                rows.Select(row => row.Compact).ToList()));
        }

        return queryResults;
    }

    private static List<SearchRecipeCompactQueryResultJsonResult> CollectSearchRecipeCompactQueryResults(
        DbReader reader,
        IReadOnlyList<SearchAuditRecipeQuery> recipeQueries,
        SearchRecipeScopeJsonResult scope,
        QueryCommandOptions options,
        bool userExact,
        out int total)
    {
        var queryResults = new List<SearchRecipeCompactQueryResultJsonResult>();
        total = 0;
        foreach (var recipeQuery in recipeQueries)
        {
            var exact = userExact || recipeQuery.ExactSubstring;
            var queryScope = BuildSearchRecipeQueryScope(scope, recipeQuery);
            var results = reader.Search(
                recipeQuery.Query,
                FetchLimitForSearchEnvelope(options.Limit),
                options.Lang,
                false,
                queryScope.PathPatterns,
                queryScope.ExcludePaths,
                queryScope.ExcludeTests,
                !options.NoDedup,
                options.Since,
                exact,
                false,
                !options.NoVisibilityRank,
                cursor: options.SearchCursor,
                guardFilters: options.GuardFilters,
                guardWindow: options.GuardWindow,
                guardScope: options.GuardScope,
                requiredPathPatterns: GetSearchRecipeRequiredPathPatterns(options, recipeQuery));
            var rows = BuildSearchDisplayRows(results, options, exact, recipeQuery.Query, recipeQuery: recipeQuery);
            var availableCount = rows.Count;
            var truncated = TrimSearchRowsToRequestedLimit(rows, options.Limit);
            var minimumOmitted = truncated ? Math.Max(1, availableCount - rows.Count) : 0;
            total += rows.Count;
            queryResults.Add(new SearchRecipeCompactQueryResultJsonResult(
                recipeQuery.Name,
                recipeQuery.Query,
                recipeQuery.Description,
                recipeQuery.Severity,
                [.. recipeQuery.PathPatterns],
                [.. recipeQuery.ExcludePaths],
                [.. recipeQuery.MatchOrigins],
                [.. recipeQuery.ExcludeOrigins],
                [.. recipeQuery.ResultKinds],
                recipeQuery.BroadCatchTaxonomy,
                rows.Count,
                options.Limit,
                minimumOmitted,
                BuildSearchRecipeTopFiles(rows),
                truncated,
                truncated && rows.Count > 0 ? FormatSearchCursor(rows[^1].Result) : null,
                rows.Select(row => new SearchRecipeCompactResultJsonResult(
                    row.Result.Path,
                    row.Result.Lang,
                    row.Result.Visibility,
                    row.Result.StartLine,
                    row.Result.EndLine,
                    row.Compact.MatchLines,
                    row.Compact.EnclosingSymbolName,
                    row.Compact.EnclosingSymbolKind)).ToList()));
        }

        return queryResults;
    }

    private static SearchRecipeRunSummaryJsonResult BuildSearchRecipeRunSummary(
        IReadOnlyList<SearchRecipeQueryResultJsonResult> queryResults,
        int limitPerQuery,
        int emittedResultCount)
        => new(
            limitPerQuery,
            emittedResultCount,
            queryResults.Count(query => query.Truncated),
            queryResults.Sum(query => query.MinimumOmittedResultCount),
            queryResults.Any(query => query.Truncated && !string.IsNullOrWhiteSpace(query.NextCursor)),
            "When a query is truncated, rerun a single child query with --recipe <recipe>/<query> --cursor <next_cursor> to page the next result set.");

    private static SearchRecipeRunSummaryJsonResult BuildSearchRecipeRunSummary(
        IReadOnlyList<SearchRecipeCompactQueryResultJsonResult> queryResults,
        int limitPerQuery,
        int emittedResultCount)
        => new(
            limitPerQuery,
            emittedResultCount,
            queryResults.Count(query => query.Truncated),
            queryResults.Sum(query => query.MinimumOmittedResultCount),
            queryResults.Any(query => query.Truncated && !string.IsNullOrWhiteSpace(query.NextCursor)),
            "When a query is truncated, rerun a single child query with --recipe <recipe>/<query> --cursor <next_cursor> to page the next result set.");

    private static SearchRecipeScopeJsonResult BuildSearchRecipeScope(SearchAuditRecipe recipe, QueryCommandOptions options)
    {
        var scopeName = options.AuditScopeExplicit ? options.AuditScope : recipe.DefaultScope;
        var pathPatterns = new List<string>(options.PathPatterns);
        var excludePaths = new List<string>(options.ExcludePaths);
        var excludeTests = options.ExcludeTests;

        if (string.Equals(scopeName, SearchAuditRecipes.DefaultAuditScope, StringComparison.OrdinalIgnoreCase))
        {
            if (pathPatterns.Count == 0)
                AddDistinct(pathPatterns, recipe.DefaultPathPatterns);
            AddDistinct(excludePaths, recipe.DefaultExcludePaths);
            excludeTests = true;
        }

        return new SearchRecipeScopeJsonResult(
            scopeName,
            pathPatterns,
            excludePaths,
            excludeTests,
            [.. recipe.DefaultPathPatterns],
            [.. recipe.DefaultExcludePaths],
            options.ShowExcluded ? BuildSearchRecipeExcludedDiagnostics(recipe, options, scopeName, excludeTests) : null);
    }

    private static SearchRecipeScopeJsonResult BuildSearchRecipeQueryScope(
        SearchRecipeScopeJsonResult scope,
        SearchAuditRecipeQuery query)
    {
        var pathPatterns = query.PathPatterns.Count > 0
            ? [.. query.PathPatterns]
            : new List<string>(scope.PathPatterns);
        var excludePaths = new List<string>(scope.ExcludePaths);
        AddDistinct(excludePaths, query.ExcludePaths);

        return scope with
        {
            PathPatterns = pathPatterns,
            ExcludePaths = excludePaths
        };
    }

    private static List<SearchRecipeExcludedDiagnosticJsonResult> BuildSearchRecipeExcludedDiagnostics(
        SearchAuditRecipe recipe,
        QueryCommandOptions options,
        string scopeName,
        bool excludeTests)
    {
        var diagnostics = new List<SearchRecipeExcludedDiagnosticJsonResult>();
        var sourceScope = string.Equals(scopeName, SearchAuditRecipes.DefaultAuditScope, StringComparison.OrdinalIgnoreCase);
        diagnostics.Add(new SearchRecipeExcludedDiagnosticJsonResult(
            "recipe_default_path_patterns",
            sourceScope && options.PathPatterns.Count == 0 && recipe.DefaultPathPatterns.Count > 0,
            [.. recipe.DefaultPathPatterns],
            "Default source-scope include patterns applied when a recipe runs without user --path filters."));
        diagnostics.Add(new SearchRecipeExcludedDiagnosticJsonResult(
            "recipe_default_exclude_paths",
            sourceScope && recipe.DefaultExcludePaths.Count > 0,
            [.. recipe.DefaultExcludePaths],
            "Default source-scope exclusions suppress recipe definitions, tests, docs, changelog text, and agent/workflow metadata."));
        if (options.ExcludePaths.Count > 0)
        {
            diagnostics.Add(new SearchRecipeExcludedDiagnosticJsonResult(
                "user_exclude_paths",
                true,
                [.. options.ExcludePaths],
                "User-provided --exclude-path filters are applied after recipe defaults."));
        }
        diagnostics.Add(new SearchRecipeExcludedDiagnosticJsonResult(
            "exclude_tests",
            excludeTests,
            [],
            "The test-file classifier is enabled for this recipe scope; exact excluded paths depend on indexed file metadata."));
        return diagnostics;
    }

    private static void AddDistinct(List<string> target, IEnumerable<string> values)
    {
        foreach (var value in values)
        {
            if (!target.Contains(value, StringComparer.Ordinal))
                target.Add(value);
        }
    }

    private static List<SearchRecipeTopFileJsonResult> BuildSearchRecipeTopFiles(IReadOnlyList<SearchDisplayRow> rows)
        => rows
            .GroupBy(row => row.Result.Path, StringComparer.Ordinal)
            .Select(group => new SearchRecipeTopFileJsonResult(group.Key, group.Count()))
            .OrderByDescending(file => file.Count)
            .ThenBy(file => file.Path, StringComparer.Ordinal)
            .Take(10)
            .ToList();

    private static IReadOnlyList<string>? GetSearchRecipeRequiredPathPatterns(QueryCommandOptions options, SearchAuditRecipeQuery recipeQuery)
        => options.PathPatterns.Count > 0 && recipeQuery.PathPatterns.Count > 0
            ? options.PathPatterns
            : null;

    private static int FetchLimitForSearchEnvelope(int limit)
    {
        if (limit >= int.MaxValue)
            return int.MaxValue;
        if (limit <= 0)
            return 1;

        var requested = (long)limit + 1;
        var overFetched = requested * SearchEnvelopeOverFetchFactor;
        var candidateLimit = Math.Max(SearchEnvelopeMinCandidates, Math.Max(requested, overFetched));
        return (int)Math.Min(SearchEnvelopeMaxCandidates, Math.Min(int.MaxValue, candidateLimit));
    }

    private static bool TrimSearchRowsToRequestedLimit(List<SearchDisplayRow> rows, int limit)
    {
        if (rows.Count <= limit)
            return false;
        rows.RemoveRange(limit, rows.Count - limit);
        return true;
    }

    private static List<SearchNamedBatchQueryResultJsonResult> CollectSearchNamedBatchQueryResults(
        DbReader reader,
        QueryCommandOptions options,
        bool userExact,
        out int total)
    {
        var queryResults = new List<SearchNamedBatchQueryResultJsonResult>();
        total = 0;
        foreach (var namedQuery in options.NamedSearchQueries)
        {
            var results = reader.Search(
                namedQuery.Query,
                FetchLimitForSearchEnvelope(options.Limit),
                options.Lang,
                options.RawFts,
                options.PathPatterns,
                options.ExcludePaths,
                options.ExcludeTests,
                !options.NoDedup,
                options.Since,
                userExact,
                options.Prefix,
                !options.NoVisibilityRank,
                guardFilters: options.GuardFilters,
                guardWindow: options.GuardWindow,
                guardScope: options.GuardScope);
            var rows = BuildSearchDisplayRows(results, options, userExact, namedQuery.Query);
            var truncated = TrimSearchRowsToRequestedLimit(rows, options.Limit);
            AttachExactSubstringHint(
                rows.Select(row => row.Compact),
                SearchQueryAdvisor.BuildExactSubstringHint(namedQuery.Query, options.RawFts, userExact, options.Prefix));
            total += rows.Count;
            queryResults.Add(new SearchNamedBatchQueryResultJsonResult(
                namedQuery.Name,
                namedQuery.Query,
                userExact,
                rows.Count,
                BuildSearchRecipeTopFiles(rows),
                truncated,
                null,
                rows.Select(row => row.Compact).ToList()));
        }

        return queryResults;
    }
    private static SearchIssueDraftJsonResult ToSearchIssueDraft(
        SearchAuditRecipe recipe,
        SearchRecipeQueryResultJsonResult queryResult,
        IssueDuplicatePreflight preflight,
        QueryCommandOptions options)
    {
        var labels = queryResult.RecommendedLabels
            .Concat(options.IssueLabels)
            .Where(label => !string.IsNullOrWhiteSpace(label))
            .Select(label => label.Trim())
            .Distinct(StringComparer.OrdinalIgnoreCase)
            .OrderBy(label => label, StringComparer.OrdinalIgnoreCase)
            .ToList();
        var title = BuildSearchIssueDraftTitle(recipe, queryResult);
        var evidencePaths = queryResult.Results
            .Select(result => result.Path)
            .Where(path => !string.IsNullOrWhiteSpace(path))
            .Distinct(StringComparer.Ordinal)
            .Take(10)
            .ToList();
        var duplicateProbeTriage = BuildSearchIssueDraftTriage(queryResult, preflight.Checked, 0);
        var duplicateProbeBody = BuildSearchIssueDraftBody(recipe, queryResult, evidencePaths, duplicateProbeTriage, options);
        var duplicateMatches = preflight.FindMatches(
            title,
            labels,
            options.DuplicateThreshold,
            evidencePaths,
            duplicateProbeBody);
        var triage = BuildSearchIssueDraftTriage(queryResult, preflight.Checked, duplicateMatches.Count);
        return new SearchIssueDraftJsonResult(
            $"{recipe.Name}/{queryResult.Name}",
            title,
            labels,
            evidencePaths,
            triage,
            BuildSearchIssueDraftBody(recipe, queryResult, evidencePaths, triage, options),
            new SearchIssueDraftSourceJsonResult(
                recipe.Name,
                queryResult.Name,
                queryResult.Query,
                queryResult.Description,
                queryResult.FalsePositiveGuidance,
                queryResult.ExactSubstring,
                queryResult.Count),
            new SuggestionIssueDraftDuplicatePreflightJsonResult(
                preflight.Checked,
                duplicateMatches.Count,
                duplicateMatches));
    }

    private static SearchIssueDraftJsonResult ToAdHocSearchIssueDraft(
        QueryCommandOptions options,
        SearchRecipeQueryResultJsonResult queryResult,
        IssueDuplicatePreflight preflight)
    {
        var labels = BuildAdHocIssueDraftLabels(options);
        var title = BuildAdHocSearchIssueDraftTitle(options);
        var evidencePaths = queryResult.Results
            .Select(result => result.Path)
            .Where(path => !string.IsNullOrWhiteSpace(path))
            .Distinct(StringComparer.Ordinal)
            .Take(10)
            .ToList();
        var duplicateProbeTriage = BuildSearchIssueDraftTriage(queryResult, preflight.Checked, 0);
        var duplicateProbeBody = BuildAdHocSearchIssueDraftBody(queryResult, evidencePaths, duplicateProbeTriage);
        var duplicateMatches = preflight.FindMatches(
            title,
            labels,
            options.DuplicateThreshold,
            evidencePaths,
            duplicateProbeBody);
        var triage = BuildSearchIssueDraftTriage(queryResult, preflight.Checked, duplicateMatches.Count);
        return new SearchIssueDraftJsonResult(
            "search/ad-hoc",
            title,
            labels,
            evidencePaths,
            triage,
            BuildAdHocSearchIssueDraftBody(queryResult, evidencePaths, triage),
            new SearchIssueDraftSourceJsonResult(
                null,
                null,
                queryResult.Query,
                queryResult.Description,
                queryResult.FalsePositiveGuidance,
                queryResult.ExactSubstring,
                queryResult.Count),
            new SuggestionIssueDraftDuplicatePreflightJsonResult(
                preflight.Checked,
                duplicateMatches.Count,
                duplicateMatches));
    }

    private static string BuildSearchIssueDraftTitle(SearchAuditRecipe recipe, SearchRecipeQueryResultJsonResult queryResult)
        => $"Search audit recipe {recipe.Name}: {queryResult.Name}";

    private static IssueDraftTriageMetadataJsonResult BuildSearchIssueDraftTriage(
        SearchRecipeQueryResultJsonResult queryResult,
        bool duplicatePreflightChecked,
        int duplicateMatchCount)
        => new(
            queryResult.Severity,
            queryResult.Count >= 3 ? "high" : queryResult.Count >= 2 ? "medium" : "low",
            queryResult.Count,
            BuildSearchIssueDraftDuplicateGuidance(duplicatePreflightChecked, duplicateMatchCount));

    private static string BuildSearchIssueDraftDuplicateGuidance(bool duplicatePreflightChecked, int duplicateMatchCount)
    {
        if (!duplicatePreflightChecked)
            return "Duplicate preflight was not checked; search open issues before filing.";
        if (duplicateMatchCount > 0)
            return "Review duplicate_preflight.matches before filing; merge evidence into an existing issue when the same root cause is already tracked.";
        return "No duplicate candidates were found by preflight; still verify open issues before filing.";
    }

    private static string BuildAdHocSearchIssueDraftTitle(QueryCommandOptions options)
        => string.IsNullOrWhiteSpace(options.IssueTitle)
            ? $"Search issue draft: {options.Query}"
            : options.IssueTitle.Trim();

    private static List<string> BuildAdHocIssueDraftLabels(QueryCommandOptions options)
        => options.IssueLabels
            .Where(label => !string.IsNullOrWhiteSpace(label))
            .Select(label => label.Trim())
            .Distinct(StringComparer.OrdinalIgnoreCase)
            .OrderBy(label => label, StringComparer.OrdinalIgnoreCase)
            .ToList();

    private static string BuildSearchIssueDraftBody(
        SearchAuditRecipe recipe,
        SearchRecipeQueryResultJsonResult queryResult,
        IReadOnlyList<string> evidencePaths,
        IssueDraftTriageMetadataJsonResult triage,
        QueryCommandOptions options)
    {
        var sb = new StringBuilder();
        sb.AppendLine("## Summary");
        sb.AppendLine(queryResult.Description);
        sb.AppendLine();
        sb.AppendLine("## Recipe");
        sb.AppendLine(recipe.Name);
        sb.AppendLine();
        sb.AppendLine("## Search query");
        sb.AppendLine(queryResult.Query);
        sb.AppendLine();
        sb.AppendLine("## Evidence paths");
        if (evidencePaths.Count == 0)
        {
            sb.AppendLine("N/A");
        }
        else
        {
            foreach (var path in evidencePaths)
                sb.AppendLine($"- {path}");
        }
        sb.AppendLine();
        AppendSearchIssueDraftTriageMetadata(sb, triage);
        sb.AppendLine();
        sb.AppendLine("## False-positive guidance");
        sb.AppendLine(queryResult.FalsePositiveGuidance);
        sb.AppendLine();
        if (queryResult.BroadCatchTaxonomy is not null)
        {
            AppendSearchIssueDraftBroadCatchTaxonomy(sb, queryResult.BroadCatchTaxonomy);
            sb.AppendLine();
        }
        sb.AppendLine("## Replay command");
        sb.AppendLine("```sh");
        sb.AppendLine(BuildSearchRecipeReplayCommand(recipe, options, queryResult.Name));
        sb.AppendLine("```");
        sb.AppendLine();
        sb.AppendLine("## Search metadata");
        sb.AppendLine($"- draft_id: `{recipe.Name}/{queryResult.Name}`");
        sb.AppendLine($"- recipe_query: `{queryResult.Name}`");
        sb.AppendLine($"- result_count: `{queryResult.Count}`");
        sb.AppendLine($"- exact_substring: `{queryResult.ExactSubstring.ToString().ToLowerInvariant()}`");
        return sb.ToString().TrimEnd();
    }

    private static void AppendSearchIssueDraftBroadCatchTaxonomy(StringBuilder sb, SearchRecipeBroadCatchTaxonomyJsonResult taxonomy)
    {
        sb.AppendLine("## Broad-catch taxonomy");
        sb.AppendLine(taxonomy.TriageGuidance);
        sb.AppendLine();
        sb.AppendLine("### Boundary categories");
        foreach (var category in taxonomy.BoundaryCategories)
            sb.AppendLine($"- `{category.Name}`: {category.Description} Expected diagnostic behavior: {category.ExpectedDiagnosticBehavior}");
        sb.AppendLine();
        sb.AppendLine("### Diagnostic behavior categories");
        foreach (var behavior in taxonomy.DiagnosticBehaviors)
            sb.AppendLine($"- `{behavior.Name}`: {behavior.Description}");
    }

    private static void AppendSearchIssueDraftTriageMetadata(StringBuilder sb, IssueDraftTriageMetadataJsonResult triage)
    {
        sb.AppendLine("## Triage metadata");
        sb.AppendLine($"- severity: `{triage.Severity}`");
        sb.AppendLine($"- confidence: `{triage.Confidence}`");
        sb.AppendLine($"- evidence_count: `{triage.EvidenceCount}`");
        sb.AppendLine($"- duplicate_guidance: {triage.DuplicateGuidance}");
    }

    private static string BuildSearchRecipeReplayCommand(SearchAuditRecipe recipe, QueryCommandOptions options, string? queryName = null)
    {
        var recipeSelector = string.IsNullOrWhiteSpace(queryName)
            ? recipe.Name
            : $"{recipe.Name}/{queryName}";
        var args = new List<string>
        {
            "cdidx",
            "search",
            "--recipe",
            recipeSelector,
            "--format",
            OutputFormatIssueDrafts,
            "--limit",
            options.Limit.ToString(CultureInfo.InvariantCulture),
        };

        if (options.DbPathExplicit)
            AddReplayValueOption(args, "--db", options.DbPath);
        if (!string.IsNullOrWhiteSpace(options.Lang))
            AddReplayValueOption(args, "--lang", options.Lang);
        foreach (var pathPattern in options.PathPatterns)
            AddReplayValueOption(args, "--path", pathPattern);
        foreach (var excludePath in options.ExcludePaths)
            AddReplayValueOption(args, "--exclude-path", excludePath);
        if (options.ExcludeTests)
            args.Add("--exclude-tests");
        if (options.Since.HasValue)
            AddReplayValueOption(args, "--since", options.Since.Value.ToUniversalTime().ToString("O", CultureInfo.InvariantCulture));
        if (options.NoDedup)
            args.Add("--no-dedup");
        if (options.NoVisibilityRank)
            args.Add("--no-visibility-rank");
        if (options.Exact)
            args.Add("--exact");
        if (options.ExactSubstring)
            args.Add("--exact-substring");
        foreach (var guardFilter in options.GuardFilters)
            AddReplayValueOption(args, BuildSearchGuardReplayOptionName(guardFilter), guardFilter.Query);
        if (options.GuardFilters.Count > 0 && options.GuardWindow != DbReader.DefaultSearchGuardWindow)
            AddReplayValueOption(args, "--guard-window", options.GuardWindow.ToString(CultureInfo.InvariantCulture));
        if (options.GuardFilters.Count > 0 && options.GuardScope != SearchGuardScope.Window)
            AddReplayValueOption(args, "--guard-scope", FormatSearchGuardScope(options.GuardScope));
        AddReplayValueOption(args, "--snippet-lines", options.SnippetLines.ToString(CultureInfo.InvariantCulture));
        AddReplayValueOption(args, "--snippet-focus", FormatSearchSnippetFocusMode(options.SnippetFocus));
        AddReplayValueOption(args, "--max-line-width", options.MaxLineWidth.ToString(CultureInfo.InvariantCulture));
        if (!string.IsNullOrWhiteSpace(options.OpenIssuesPath))
            AddReplayValueOption(args, "--open-issues", options.OpenIssuesPath);
        if (!string.IsNullOrWhiteSpace(options.OpenIssuesRepository))
            AddReplayValueOption(args, "--repo", options.OpenIssuesRepository);
        if (options.DuplicatePreflightTuningExplicit)
        {
            if (string.Equals(options.DuplicateConfidence, IssueDuplicatePreflight.CustomDuplicateConfidence, StringComparison.Ordinal))
                AddReplayValueOption(args, "--duplicate-threshold", options.DuplicateThreshold.ToString("0.###", CultureInfo.InvariantCulture));
            else
                AddReplayValueOption(args, "--duplicate-confidence", options.DuplicateConfidence);
        }
        if (queryName == null)
        {
            foreach (var includeQuery in options.IncludeRecipeQueries)
                AddReplayValueOption(args, "--include-query", includeQuery);
            foreach (var excludeQuery in options.ExcludeRecipeQueries)
                AddReplayValueOption(args, "--exclude-query", excludeQuery);
        }
        foreach (var label in options.IssueLabels)
            AddReplayValueOption(args, "--issue-label", label);

        return string.Join(" ", args.Select(QuoteReplayShellArg));
    }

    private static void AddReplayValueOption(List<string> args, string optionName, string? value)
    {
        if (string.IsNullOrWhiteSpace(value))
            return;
        args.Add(optionName);
        args.Add(value);
    }

    private static string BuildSearchGuardReplayOptionName(SearchGuardFilter guardFilter)
    {
        var role = guardFilter.Role == SearchGuardRole.Require ? "require" : "reject";
        var direction = guardFilter.Direction == SearchGuardDirection.Before ? "before" : "after";
        return $"--{role}-{direction}";
    }

    private static string FormatSearchSnippetFocusMode(SearchSnippetFocusMode mode)
        => mode.ToString().ToLowerInvariant();

    private static string FormatSearchCursor(SearchResult result)
        => string.Create(CultureInfo.InvariantCulture, $"{result.Score:R}:{result.ChunkId}:{result.NextOffset}");

    private static string FormatUnusedCursor(int offset)
        => string.Create(CultureInfo.InvariantCulture, $"unused:{offset}");

    private static string FormatOutlineCursor(int offset)
        => string.Create(CultureInfo.InvariantCulture, $"outline:{offset}");

    private static bool TryParseSearchCursor(string value, out SearchCursor cursor)
    {
        cursor = default;
        var lastSeparator = value.LastIndexOf(':');
        if (lastSeparator <= 0 || lastSeparator == value.Length - 1)
            return false;

        var firstSeparator = value.LastIndexOf(':', lastSeparator - 1);
        if (firstSeparator <= 0 || firstSeparator == lastSeparator - 1)
            return false;

        if (!double.TryParse(value.AsSpan(0, firstSeparator), NumberStyles.Float, CultureInfo.InvariantCulture, out var score)
            || !double.IsFinite(score))
            return false;
        if (!long.TryParse(value.AsSpan(firstSeparator + 1, lastSeparator - firstSeparator - 1), NumberStyles.None, CultureInfo.InvariantCulture, out var chunkId)
            || chunkId < 0)
            return false;
        if (!int.TryParse(value.AsSpan(lastSeparator + 1), NumberStyles.None, CultureInfo.InvariantCulture, out var offset) || offset < 0)
            return false;

        cursor = new SearchCursor(score, chunkId, offset);
        return true;
    }

    private static bool TryParseUnusedCursor(string value, out int offset)
    {
        offset = 0;
        const string prefix = "unused:";
        if (!value.StartsWith(prefix, StringComparison.Ordinal))
            return false;
        return int.TryParse(value[prefix.Length..], NumberStyles.Integer, CultureInfo.InvariantCulture, out offset)
            && offset >= 0;
    }

    private static bool TryParseOutlineCursor(string value, out int offset)
    {
        offset = 0;
        const string prefix = "outline:";
        if (!value.StartsWith(prefix, StringComparison.Ordinal))
            return false;
        return int.TryParse(value[prefix.Length..], NumberStyles.Integer, CultureInfo.InvariantCulture, out offset)
            && offset >= 0;
    }

    private static string QuoteReplayShellArg(string arg)
    {
        if (arg.Length > 0 && arg.All(c => char.IsLetterOrDigit(c) || c is '_' or '-' or '.' or '/' or ':' or '='))
            return arg;
        return "'" + arg.Replace("'", "'\\''", StringComparison.Ordinal) + "'";
    }

    private static string BuildAdHocSearchIssueDraftBody(
        SearchRecipeQueryResultJsonResult queryResult,
        IReadOnlyList<string> evidencePaths,
        IssueDraftTriageMetadataJsonResult triage)
    {
        var sb = new StringBuilder();
        sb.AppendLine("## Summary");
        sb.AppendLine(queryResult.Description);
        sb.AppendLine();
        sb.AppendLine("## Search query");
        sb.AppendLine(queryResult.Query);
        sb.AppendLine();
        sb.AppendLine("## Evidence paths");
        if (evidencePaths.Count == 0)
        {
            sb.AppendLine("N/A");
        }
        else
        {
            foreach (var path in evidencePaths)
                sb.AppendLine($"- {path}");
        }
        sb.AppendLine();
        AppendSearchIssueDraftTriageMetadata(sb, triage);
        sb.AppendLine();
        sb.AppendLine("## Review guidance");
        sb.AppendLine(queryResult.FalsePositiveGuidance);
        sb.AppendLine();
        sb.AppendLine("## Search metadata");
        sb.AppendLine("- draft_id: `search/ad-hoc`");
        sb.AppendLine($"- result_count: `{queryResult.Count}`");
        sb.AppendLine($"- exact_substring: `{queryResult.ExactSubstring.ToString().ToLowerInvariant()}`");
        return sb.ToString().TrimEnd();
    }

    private static SearchRecipeListItemJsonResult ToSearchRecipeListItem(SearchAuditRecipe recipe, IReadOnlyList<SearchAuditRecipeQuery>? queries = null) => new(
        recipe.Name,
        recipe.Description,
        recipe.RecommendedLabels,
        recipe.DefaultScope,
        [.. recipe.DefaultPathPatterns],
        [.. recipe.DefaultExcludePaths],
        SearchRecipeSupportedFormats,
        SearchRecipeFilterSupport,
        SearchRecipeLimitSemantics,
        (queries ?? recipe.Queries).Select(query => new SearchRecipeQueryListItemJsonResult(
            query.Name,
            query.Query,
            query.Description,
            query.RecommendedLabels,
            query.FalsePositiveGuidance,
            query.Severity,
            [.. query.PathPatterns],
            [.. query.ExcludePaths],
            [.. query.MatchOrigins],
            [.. query.ExcludeOrigins],
            [.. query.ResultKinds],
            query.BroadCatchTaxonomy,
            query.ExactSubstring)).ToList());

    private static List<SearchDisplayRow> BuildSearchDisplayRows(
        List<SearchResult> results,
        QueryCommandOptions options,
        bool exact,
        string? queryOverride = null,
        bool? rawFtsOverride = null,
        SearchAuditRecipeQuery? recipeQuery = null)
    {
        var rows = new List<SearchDisplayRow>(results.Count);
        var seenMatchLocations = options.NoDedup ? null : new HashSet<string>(StringComparer.Ordinal);
        var displayQuery = queryOverride ?? options.Query!;
        var rawFts = rawFtsOverride ?? options.RawFts;
        var facetFilters = BuildSearchDisplayFacetFilters(options, recipeQuery);
        var effectiveRawFts = rawFts && !exact;
        var queryContext = effectiveRawFts
            ? SearchSnippetFormatter.PrepareRawFtsQueryContext(displayQuery)
            : SearchSnippetFormatter.PrepareQueryContext(displayQuery);
        foreach (var result in results)
        {
            var compact = SearchSnippetFormatter.ToCompactResult(
                result,
                queryContext,
                options.SnippetLines,
                exact,
                options.MaxLineWidth,
                result.Lang,
                options.SnippetFocus,
                exposeLiteralHighlights: exact);
            var preferredOriginFilterLine = GetPreferredSearchOriginFilterLine(compact, facetFilters);
            if (preferredOriginFilterLine.HasValue && !IsLineWithinSnippet(compact, preferredOriginFilterLine.Value))
            {
                compact = SearchSnippetFormatter.ToCompactResult(
                    result,
                    queryContext,
                    options.SnippetLines,
                    exact,
                    options.MaxLineWidth,
                    result.Lang,
                    options.SnippetFocus,
                    exposeLiteralHighlights: exact,
                    preferredMatchLine: preferredOriginFilterLine.Value);
            }
            SearchSnippetFormatter.ApplyOutputMetadata(compact, options.SnippetLines, options.MaxLineWidth, exact, rawFts);

            if (!effectiveRawFts && compact.MatchLines.Count == 0 && compact.Highlights.Count == 0)
                continue;

            if (!ApplySearchOriginFilters(compact, facetFilters))
                continue;

            compact.ResultKinds = BuildSearchResultKinds(result, compact, displayQuery);
            if (!ApplySearchResultKindFilters(compact, facetFilters))
                continue;

            if (seenMatchLocations != null && compact.MatchLines.Count > 0)
            {
                var keptLines = new List<int>(compact.MatchLines.Count);
                foreach (var line in compact.MatchLines)
                {
                    var key = result.Path + "\0" + line.ToString(CultureInfo.InvariantCulture);
                    if (seenMatchLocations.Add(key))
                        keptLines.Add(line);
                }

                if (keptLines.Count == 0)
                    continue;

                if (keptLines.Count != compact.MatchLines.Count)
                {
                    var keptSet = keptLines.ToHashSet();
                    compact.MatchLines = keptLines;
                    compact.Highlights = compact.Highlights
                        .Where(highlight => keptSet.Contains(highlight.Line))
                        .ToList();
                }
            }

            rows.Add(new SearchDisplayRow(result, compact));
        }

        return rows;
    }

    private sealed record SearchDisplayFacetFilters(
        bool ExcludeComments,
        bool ExcludeStrings,
        bool ExcludeFixtures,
        List<string> MatchOrigins,
        List<string> ExcludeOrigins,
        List<string> ResultKinds);

    private static SearchDisplayFacetFilters BuildSearchDisplayFacetFilters(QueryCommandOptions options, SearchAuditRecipeQuery? recipeQuery)
        => new(
            options.ExcludeComments,
            options.ExcludeStrings,
            options.ExcludeFixtures,
            CombineInclusiveSearchFilters(options.MatchOrigins, recipeQuery?.MatchOrigins),
            CombineExclusiveSearchFilters(options.ExcludeOrigins, recipeQuery?.ExcludeOrigins),
            CombineInclusiveSearchFilters(options.ResultKinds, recipeQuery?.ResultKinds));

    private static List<string> CombineInclusiveSearchFilters(IReadOnlyList<string> optionValues, IReadOnlyList<string>? recipeValues)
    {
        if (recipeValues is not { Count: > 0 })
            return [.. optionValues];
        if (optionValues.Count == 0)
            return [.. recipeValues];

        var intersected = optionValues
            .Where(value => recipeValues.Contains(value, StringComparer.Ordinal))
            .Distinct(StringComparer.Ordinal)
            .ToList();
        return intersected.Count == 0 ? [SearchFilterNoMatchSentinel] : intersected;
    }

    private static List<string> CombineExclusiveSearchFilters(IReadOnlyList<string> optionValues, IReadOnlyList<string>? recipeValues)
        => optionValues
            .Concat(recipeValues ?? Array.Empty<string>())
            .Distinct(StringComparer.Ordinal)
            .ToList();

    private static int? GetPreferredSearchOriginFilterLine(CompactSearchResult compact, SearchDisplayFacetFilters filters)
    {
        if (!HasSearchOriginFilters(filters) || compact.MatchFacets.Count == 0)
            return null;

        return compact.MatchFacets
            .Where(facet => !IsSearchFacetExcluded(facet, filters))
            .Select(facet => (int?)facet.Line)
            .OrderBy(line => line)
            .FirstOrDefault();
    }

    private static bool IsLineWithinSnippet(CompactSearchResult compact, int line)
        => line >= compact.SnippetStartLine && line <= compact.SnippetEndLine;

    private static List<SearchDisplayRow> ReadSearchDisplayRows(DbReader reader, QueryCommandOptions options, bool exact)
    {
        if (!HasSearchOriginFilters(options))
            return BuildSearchDisplayRows(ReadSearchResults(reader, options, exact, GetSearchDisplayCandidateLimit(options)), options, exact);

        return ReadOriginFilteredSearchDisplayRows(reader, options, exact);
    }

    private static List<SearchDisplayRow> ReadOriginFilteredSearchDisplayRows(DbReader reader, QueryCommandOptions options, bool exact)
    {
        var requestedLimit = Math.Max(0, GetSearchDisplayCandidateLimit(options));
        if (requestedLimit == 0)
            return [];

        var candidateLimit = GetSearchOriginFilterCandidateLimit(requestedLimit);
        var batchLimit = GetSearchOriginFilterBatchLimit(requestedLimit);
        var candidates = new List<SearchResult>(Math.Min(candidateLimit, batchLimit));
        var displayRows = new List<SearchDisplayRow>();
        SearchCursor? cursor = null;
        var pagesRead = 0;
        while (displayRows.Count < requestedLimit && pagesRead < SearchOriginFilterMaxPages)
        {
            var currentOffset = Math.Max(0, cursor?.Offset ?? 0);
            if (currentOffset >= candidateLimit)
                break;

            var pageLimit = Math.Min(batchLimit, candidateLimit - currentOffset);
            if (pageLimit <= 0)
                break;

            var page = ReadSearchResults(reader, options, exact, pageLimit, cursor, requestedLimit);
            pagesRead++;
            if (page.Count == 0)
                break;

            candidates.AddRange(page);
            displayRows = BuildSearchDisplayRows(candidates, options, exact);

            var last = page[^1];
            if (last.NextOffset <= currentOffset)
                break;
            cursor = new SearchCursor(last.Score, last.ChunkId, last.NextOffset);
        }

        return displayRows.Count <= requestedLimit
            ? displayRows
            : displayRows.Take(requestedLimit).ToList();
    }

    private static int GetSearchOriginFilterBatchLimit(int requestedLimit)
    {
        var requested = Math.Max(1, requestedLimit);
        var overFetched = requested * SearchOriginFilterOverFetchFactor;
        return Math.Min(SearchOriginFilterMaxCandidates, Math.Max(SearchOriginFilterMinCandidates, overFetched));
    }

    private static int GetSearchOriginFilterCandidateLimit(int requestedLimit)
        => requestedLimit <= 0 ? 0 : SearchOriginFilterMaxCandidates;

    private static int GetSearchDisplayCandidateLimit(QueryCommandOptions options)
    {
        var requested = Math.Max(1, options.Limit);
        if (!options.FirstPerFile && !options.SampleSize.HasValue)
            return requested;
        var sampleTarget = Math.Max(requested, options.SampleSize ?? requested);
        return Math.Min(SearchOriginFilterMaxCandidates, Math.Max(requested, sampleTarget * SearchOriginFilterOverFetchFactor));
    }

    private static List<SearchResult> ReadSearchResults(DbReader reader, QueryCommandOptions options, bool exact, int limit, SearchCursor? cursor = null, int? guardRequestedLimit = null)
        => reader.Search(options.Query!, limit, options.Lang, options.RawFts, options.PathPatterns, options.ExcludePaths, options.ExcludeTests, !options.NoDedup, options.Since, exact, options.Prefix, !options.NoVisibilityRank, cursor, options.GuardFilters, options.GuardWindow, guardRequestedLimit, guardScope: options.GuardScope);

    private static QueryCountResult CountFilteredSearchResults(DbReader reader, QueryCommandOptions options, bool exact)
    {
        var results = ReadSearchResults(reader, options, exact, int.MaxValue);
        var rows = BuildSearchDisplayRows(results, options, exact);
        return new QueryCountResult(
            rows.Count,
            rows.Select(row => row.Result.Path).Distinct(StringComparer.Ordinal).Count());
    }

    private static bool ApplySearchOriginFilters(CompactSearchResult compact, SearchDisplayFacetFilters filters)
    {
        if (!HasSearchOriginFilters(filters))
            return true;
        if (compact.MatchFacets.Count == 0)
            return filters.MatchOrigins.Count == 0;

        var keptFacets = compact.MatchFacets
            .Where(facet => !IsSearchFacetExcluded(facet, filters))
            .ToList();
        if (keptFacets.Count == 0)
            return false;

        compact.MatchFacets = keptFacets;
        compact.MatchOrigins = keptFacets
            .Select(facet => facet.Origin)
            .Distinct(StringComparer.Ordinal)
            .OrderBy(origin => origin, StringComparer.Ordinal)
            .ToList();
        compact.TestFile = keptFacets.Any(facet => facet.TestFile);
        compact.TestSymbol = keptFacets.Any(facet => facet.TestSymbol);
        compact.TestFixture = keptFacets.Any(facet => facet.TestFixture);

        var keptLines = keptFacets.Select(facet => facet.Line).ToHashSet();
        compact.MatchLines = keptLines
            .OrderBy(line => line)
            .ToList();
        compact.Highlights = compact.Highlights
            .Where(highlight => keptLines.Contains(highlight.Line))
            .ToList();
        var keptFacetKeys = keptFacets
            .Select(facet => SearchFacetKey(facet.Line, facet.Column, facet.Length))
            .ToHashSet(StringComparer.Ordinal);
        foreach (var highlight in compact.Highlights)
        {
            var lineFacets = keptFacets.Where(facet => facet.Line == highlight.Line).ToList();
            highlight.MatchOrigins = lineFacets
                .Select(facet => facet.Origin)
                .Distinct(StringComparer.Ordinal)
                .OrderBy(origin => origin, StringComparer.Ordinal)
                .ToList();
            highlight.TestFile = lineFacets.Any(facet => facet.TestFile);
            highlight.TestSymbol = lineFacets.Any(facet => facet.TestSymbol);
            highlight.TestFixture = lineFacets.Any(facet => facet.TestFixture);
            highlight.TermOccurrences = FilterSearchOccurrences(highlight.TermOccurrences, highlight.Line, keptFacetKeys);
            if (highlight.LiteralTermOccurrences != null)
                highlight.LiteralTermOccurrences = FilterSearchOccurrences(highlight.LiteralTermOccurrences, highlight.Line, keptFacetKeys);
        }

        return keptFacets.Count > 0;
    }

    private static bool HasSearchOriginFilters(QueryCommandOptions options)
        => HasSearchOriginFilters(BuildSearchDisplayFacetFilters(options, recipeQuery: null));

    private static bool HasSearchOriginFilters(SearchDisplayFacetFilters filters)
        => filters.ExcludeComments ||
           filters.ExcludeStrings ||
           filters.ExcludeFixtures ||
           filters.MatchOrigins.Count > 0 ||
           filters.ExcludeOrigins.Count > 0 ||
           filters.ResultKinds.Count > 0;

    private static bool IsSearchFacetExcluded(SearchMatchFacet facet, SearchDisplayFacetFilters filters)
    {
        if (filters.ExcludeComments && string.Equals(facet.Origin, SearchMatchClassifier.Comment, StringComparison.Ordinal))
            return true;
        if (filters.ExcludeStrings && SearchMatchClassifier.IsStringLikeOrigin(facet.Origin))
            return true;
        if (filters.ExcludeFixtures && facet.TestFixture)
            return true;
        if (filters.MatchOrigins.Count > 0 && !filters.MatchOrigins.Contains(facet.Origin, StringComparer.Ordinal))
            return true;
        if (filters.ExcludeOrigins.Count > 0 && filters.ExcludeOrigins.Contains(facet.Origin, StringComparer.Ordinal))
            return true;
        return false;
    }

    private static bool ApplySearchResultKindFilters(CompactSearchResult compact, SearchDisplayFacetFilters filters)
        => filters.ResultKinds.Count == 0 || compact.ResultKinds.Any(kind => filters.ResultKinds.Contains(kind, StringComparer.Ordinal));

    private static List<string> BuildSearchResultKinds(SearchResult result, CompactSearchResult compact, string query)
    {
        var kinds = new SortedSet<string>(StringComparer.Ordinal);
        foreach (var origin in compact.MatchOrigins)
            kinds.Add(origin);

        if (compact.MatchFacets.Any(facet => string.Equals(facet.Origin, SearchMatchClassifier.Code, StringComparison.Ordinal)))
            kinds.Add("identifier");

        var declarationLine = result.EnclosingSymbolStartLine;
        if (declarationLine.HasValue && compact.MatchLines.Contains(declarationLine.Value))
            kinds.Add("declaration");

        if (LooksLikeSearchCallSite(result, compact, query))
            kinds.Add("call_site");

        if (kinds.Count == 0)
            kinds.Add(SearchMatchClassifier.Unknown);
        return kinds.ToList();
    }

    private static bool LooksLikeSearchCallSite(SearchResult result, CompactSearchResult compact, string query)
    {
        var identifier = ExtractSearchIdentifierProbe(query);
        if (identifier.Length == 0)
            return false;

        var callPattern = identifier + "(";
        return compact.Highlights.Any(highlight =>
            highlight.Line != result.EnclosingSymbolStartLine &&
            highlight.MatchOrigins.Contains(SearchMatchClassifier.Code, StringComparer.Ordinal) &&
            highlight.Text.Contains(callPattern, StringComparison.Ordinal));
    }

    private static string ExtractSearchIdentifierProbe(string query)
    {
        var trimmed = query.Trim();
        if (trimmed.Length == 0)
            return string.Empty;
        var match = Regex.Match(trimmed, @"[A-Za-z_@][A-Za-z0-9_@]*(?:\.[A-Za-z_@][A-Za-z0-9_@]*)*$");
        if (!match.Success)
            return string.Empty;
        var value = match.Value;
        return value.StartsWith("@", StringComparison.Ordinal) ? value[1..] : value;
    }

    private static List<SearchTermOccurrence> FilterSearchOccurrences(List<SearchTermOccurrence> occurrences, int line, HashSet<string> keptFacetKeys)
        => occurrences
            .Where(occurrence => keptFacetKeys.Contains(SearchFacetKey(line, occurrence.Column, occurrence.Length)))
            .ToList();

    private static string SearchFacetKey(int line, int column, int length)
        => $"{line}:{column}:{length}";

    private static IEnumerable<FormattedLocation> ToSearchFormattedLocations(SearchDisplayRow row, string query, bool useMatchLines)
    {
        if (!useMatchLines || row.Compact.MatchLines.Count == 0)
        {
            yield return new FormattedLocation(row.Result.Path, row.Result.StartLine, null, $"search match: {query}");
            yield break;
        }

        foreach (var line in row.Compact.MatchLines)
            yield return new FormattedLocation(row.Result.Path, line, null, $"search match: {query}");
    }

    private static IEnumerable<LspLocation> ToSearchLspLocations(SearchDisplayRow row, bool useMatchLines)
    {
        if (!useMatchLines || row.Compact.MatchLines.Count == 0)
        {
            yield return ToLspLocation(row.Result);
            yield break;
        }

        foreach (var line in row.Compact.MatchLines)
            yield return BuildLspLocation(row.Result.Path, line, 1, line + 1, 1);
    }

    private static IEnumerable<(string Path, int Line, int Column, string Message)> ToSearchQuickfixItems(SearchDisplayRow row, string query, bool useMatchLines)
    {
        if (!useMatchLines || row.Compact.MatchLines.Count == 0)
        {
            yield return (row.Result.Path, row.Result.StartLine, 1, $"search match: {query}");
            yield break;
        }

        foreach (var line in row.Compact.MatchLines)
            yield return (row.Result.Path, line, 1, $"search match: {query}");
    }

    private static IEnumerable<(string Path, int Line, int Column, string Message, string RuleId)> ToSearchSarifItems(SearchDisplayRow row, string query, bool useMatchLines)
    {
        if (!useMatchLines || row.Compact.MatchLines.Count == 0)
        {
            yield return (row.Result.Path, row.Result.StartLine, 1, $"search match: {query}", "search");
            yield break;
        }

        foreach (var line in row.Compact.MatchLines)
            yield return (row.Result.Path, line, 1, $"search match: {query}", "search");
    }

    private sealed record SearchDisplayRow(SearchResult Result, CompactSearchResult Compact);

    private static void AttachExactSubstringHint(IEnumerable<CompactSearchResult> results, SearchQueryHint? hint)
    {
        if (hint == null)
            return;
        foreach (var result in results)
            result.ExactSubstringHint = hint;
    }

    private static void WriteJsonStreamDone(int count, JsonSerializerOptions jsonOptions, bool interrupted = false, DbReader? reader = null)
    {
        var includeDiagnostics = HasReadOnlyFallbackDiagnostics(reader);
        Console.WriteLine(JsonSerializer.Serialize(
            new JsonStreamDoneResult(
                Done: true,
                Count: count,
                Interrupted: interrupted,
                ReadOnlyFallback: includeDiagnostics ? reader!.ReadOnlyFallback : null,
                WalCheckpointAttempted: includeDiagnostics ? reader!.WalCheckpointAttempted : null,
                WalCheckpointSucceeded: includeDiagnostics ? reader!.WalCheckpointSucceeded : null,
                ReadOnlyImmutableFallback: includeDiagnostics ? reader!.ReadOnlyImmutableFallback : null,
                WalCheckpointSkippedReason: includeDiagnostics ? reader!.WalCheckpointSkippedReason : null,
                WalCheckpointFailureReason: includeDiagnostics ? reader!.WalCheckpointFailureReason : null,
                WalStaleSnapshotRisk: includeDiagnostics ? reader!.WalStaleSnapshotRisk : null,
                WalStaleSnapshotReason: includeDiagnostics ? reader!.WalStaleSnapshotReason : null),
            CliJsonSerializerContextFactory.Create(jsonOptions).JsonStreamDoneResult));
    }

    private static JsonSerializerOptions GetCompactJsonOptions(JsonSerializerOptions jsonOptions)
        => jsonOptions.WriteIndented ? new JsonSerializerOptions(jsonOptions) { WriteIndented = false } : jsonOptions;

    public static void AttachLspLocations(IEnumerable<DefinitionResult> results)
    {
        foreach (var result in results)
        {
            var location = BuildLspLocation(result.Path, result.StartLine, 1, result.EndLine + 1, 1);
            result.Uri = location.Uri;
            result.Range = location.Range;
        }
    }

    public static void AttachLspLocations(IEnumerable<ReferenceResult> results)
    {
        foreach (var result in results)
        {
            var location = BuildLspLocation(result.Path, result.Line, result.Column, result.Line, result.Column + 1);
            result.Uri = location.Uri;
            result.Range = location.Range;
        }
    }

    public static LspLocation BuildLspLocation(string path, int startLine, int startColumn, int endLine, int endColumn, string? projectRoot = null)
    {
        var baseRoot = string.IsNullOrWhiteSpace(projectRoot)
            ? s_activeQueryProjectRoot ?? Environment.CurrentDirectory
            : projectRoot;
        var absolutePath = Path.IsPathFullyQualified(path)
            ? path
            : Path.GetFullPath(path, baseRoot);
        return new LspLocation
        {
            Uri = new Uri(absolutePath).AbsoluteUri,
            Range = new LspRange
            {
                Start = new LspPosition
                {
                    Line = Math.Max(0, startLine - 1),
                    Character = Math.Max(0, startColumn - 1),
                },
                End = new LspPosition
                {
                    Line = Math.Max(0, endLine - 1),
                    Character = Math.Max(0, endColumn - 1),
                },
            },
        };
    }

    private static LspLocation ToLspLocation(DefinitionResult result)
        => BuildLspLocation(result.Path, result.StartLine, 1, result.EndLine + 1, 1);

    private static LspLocation ToLspLocation(ReferenceResult result)
        => BuildLspLocation(result.Path, result.Line, result.Column, result.Line, result.Column + Math.Max(1, result.SymbolName.Length));

    private static LspLocation ToLspLocation(SearchResult result)
        => BuildLspLocation(result.Path, result.StartLine, 1, result.EndLine + 1, 1);

    private static LspLocation ToLspLocation(FileFindResult result)
        => BuildLspLocation(result.Path, result.Line, result.Column, result.Line, result.Column + 1);

    private static LspLocation ToLspLocation(FileIssue result)
        => BuildLspLocation(result.Path, result.Line, 1, result.Line, 1);

    private static LspLocation ToLspLocation(SymbolResult result)
    {
        var startLine = result.StartLine > 0 ? result.StartLine : result.Line;
        var endLine = result.EndLine >= startLine ? result.EndLine : startLine;
        return BuildLspLocation(result.Path, startLine, 1, endLine + 1, 1);
    }

    private static LspLocation ToLspLocation(CallerResult result)
        => BuildLspLocation(result.Path, result.FirstLine, 1, result.FirstLine, 1);

    private static LspLocation ToLspLocation(CalleeResult result)
        => BuildLspLocation(result.Path, result.FirstLine, 1, result.FirstLine, 1);

    private static void WriteLspLocations(IEnumerable<LspLocation> locations, JsonSerializerOptions jsonOptions)
    {
        var itemOptions = GetCompactJsonOptions(jsonOptions);
        var context = CliJsonSerializerContextFactory.Create(itemOptions);
        WriteJsonArray(
            locations,
            (writer, location) => writer.Write(JsonSerializer.Serialize(location, context.LspLocation)),
            jsonOptions);
    }

    private static bool TryWriteEmptyFormattedResult(QueryCommandOptions options, JsonSerializerOptions jsonOptions)
    {
        if (options.OutputFormat == OutputFormatCount)
        {
            WriteFormattedCount(0, jsonOptions);
            return true;
        }
        if (options.OutputFormat == OutputFormatCompact)
        {
            WriteCompactLocations([], jsonOptions);
            return true;
        }
        if (options.OutputFormat == OutputFormatCsv || options.OutputFormat == OutputFormatTsv)
        {
            WriteDelimitedLocations([], options.OutputFormat);
            return true;
        }
        if (options.OutputFormat == OutputFormatLsp)
        {
            WriteLspLocations([], jsonOptions);
            return true;
        }
        if (options.OutputFormat == OutputFormatQf)
            return true;
        if (options.OutputFormat == OutputFormatSarif)
        {
            WriteSarif([], jsonOptions);
            return true;
        }
        return false;
    }

    private sealed record FormattedLocation(string File, int Line, int? Column = null, string? Label = null);

    private static bool TryWriteFormattedLocations(QueryCommandOptions options, IEnumerable<FormattedLocation> locations, JsonSerializerOptions jsonOptions)
    {
        if (options.OutputFormat == OutputFormatCount)
        {
            WriteFormattedCount(locations.Count(), jsonOptions);
            return true;
        }
        if (options.OutputFormat == OutputFormatCompact)
        {
            WriteCompactLocations(locations, jsonOptions);
            return true;
        }
        if (options.OutputFormat == OutputFormatCsv || options.OutputFormat == OutputFormatTsv)
        {
            WriteDelimitedLocations(locations, options.OutputFormat);
            return true;
        }
        return false;
    }

    private static void WriteFormattedCount(int count, JsonSerializerOptions jsonOptions)
        => Console.WriteLine(new JsonObject
        {
            ["count"] = count,
            ["total_estimated"] = count,
        }.ToJsonString(jsonOptions));

    private static void WriteCompactLocations(IEnumerable<FormattedLocation> locations, JsonSerializerOptions jsonOptions)
    {
        var itemOptions = GetCompactJsonOptions(jsonOptions);
        WriteJsonArray(
            locations,
            (writer, location) =>
            {
                writer.Write("{\"file\":");
                writer.Write(JsonSerializer.Serialize(location.File, itemOptions));
                writer.Write(",\"line\":");
                writer.Write(location.Line.ToString(CultureInfo.InvariantCulture));
                if (location.Column.HasValue)
                {
                    writer.Write(",\"column\":");
                    writer.Write(location.Column.Value.ToString(CultureInfo.InvariantCulture));
                }
                writer.Write('}');
            },
            jsonOptions);
    }

    private static void WriteCompactSearchResults(IEnumerable<CompactSearchResult> results, JsonSerializerOptions jsonOptions)
    {
        var itemOptions = GetCompactJsonOptions(jsonOptions);
        var context = CliJsonSerializerContextFactory.Create(itemOptions);
        WriteJsonArray(
            results,
            (writer, result) => writer.Write(JsonSerializer.Serialize(result, context.CompactSearchResult)),
            jsonOptions);
    }

    private static void WriteJsonArray<T>(IEnumerable<T> items, Action<TextWriter, T> writeItem, JsonSerializerOptions jsonOptions)
    {
        var writer = Console.Out;
        if (!jsonOptions.WriteIndented)
        {
            writer.Write('[');
            var first = true;
            foreach (var item in items)
            {
                if (!first)
                    writer.Write(',');
                writeItem(writer, item);
                first = false;
            }
            writer.WriteLine(']');
            return;
        }

        writer.WriteLine("[");
        var wroteAny = false;
        foreach (var item in items)
        {
            if (wroteAny)
                writer.WriteLine(",");
            writer.Write("  ");
            writeItem(writer, item);
            wroteAny = true;
        }

        if (wroteAny)
            writer.WriteLine();
        writer.WriteLine("]");
    }

    private static void WriteDelimitedLocations(IEnumerable<FormattedLocation> locations, string outputFormat)
    {
        var delimiter = outputFormat == OutputFormatTsv ? "\t" : ",";
        Console.WriteLine(string.Join(delimiter, ["file", "line", "column", "label"]));
        foreach (var location in locations)
        {
            var values = new[]
            {
                location.File,
                location.Line.ToString(CultureInfo.InvariantCulture),
                location.Column?.ToString(CultureInfo.InvariantCulture) ?? string.Empty,
                location.Label ?? string.Empty,
            };
            Console.WriteLine(string.Join(delimiter, values.Select(value => EscapeDelimitedValue(value, outputFormat))));
        }
    }

    private static void WriteDelimitedSearchResults(IEnumerable<SearchDisplayRow> rows, QueryCommandOptions options)
    {
        var delimiter = options.OutputFormat == OutputFormatTsv ? "\t" : ",";
        Console.WriteLine(string.Join(delimiter,
        [
            "file",
            "line",
            "column",
            "label",
            "query",
            "recipe",
            "query_name",
            "lang",
            "visibility",
            "enclosing_symbol_name",
            "enclosing_symbol_kind",
            "match_lines",
        ]));
        foreach (var row in rows)
        {
            var result = row.Result;
            var compact = row.Compact;
            var values = new[]
            {
                result.Path,
                result.StartLine.ToString(CultureInfo.InvariantCulture),
                "1",
                $"search match: {options.Query}",
                options.Query ?? string.Empty,
                string.Empty,
                string.Empty,
                result.Lang ?? string.Empty,
                result.Visibility ?? string.Empty,
                compact.EnclosingSymbolName ?? string.Empty,
                compact.EnclosingSymbolKind ?? string.Empty,
                string.Join(";", compact.MatchLines.Select(line => line.ToString(CultureInfo.InvariantCulture))),
            };
            Console.WriteLine(string.Join(delimiter, values.Select(value => EscapeDelimitedValue(value, options.OutputFormat))));
        }
    }

    private static string EscapeDelimitedValue(string value, string outputFormat)
    {
        if (outputFormat == OutputFormatTsv)
            return value.Replace("\t", " ", StringComparison.Ordinal).Replace("\r", " ", StringComparison.Ordinal).Replace("\n", " ", StringComparison.Ordinal);
        if (!value.Contains('"', StringComparison.Ordinal) &&
            !value.Contains(',', StringComparison.Ordinal) &&
            !value.Contains('\r', StringComparison.Ordinal) &&
            !value.Contains('\n', StringComparison.Ordinal))
            return value;
        return "\"" + value.Replace("\"", "\"\"", StringComparison.Ordinal) + "\"";
    }

    private static void WriteQuickfix(IEnumerable<(string Path, int Line, int Column, string Message)> items)
    {
        foreach (var item in items)
            Console.WriteLine($"{item.Path}:{item.Line}:{item.Column}:{item.Message}");
    }

    private static (string Path, int Line, int Column, string Message) ToSymbolQuickfixItem(SymbolResult result)
        => (result.Path, GetSymbolDisplayLine(result), 1, FormatSymbolLocationLabel(result));

    private static (string Path, int Line, int Column, string Message, string RuleId) ToSymbolSarifItem(SymbolResult result)
        => (result.Path, GetSymbolDisplayLine(result), 1, FormatSymbolLocationLabel(result), string.IsNullOrWhiteSpace(result.Kind) ? "symbol" : $"symbol.{result.Kind}");

    private static int GetSymbolDisplayLine(SymbolResult result)
        => Math.Max(1, result.Line > 0 ? result.Line : result.StartLine);

    private static string FormatSymbolLocationLabel(SymbolResult result)
    {
        var kind = string.IsNullOrWhiteSpace(result.Kind) ? "symbol" : result.Kind;
        return string.IsNullOrWhiteSpace(result.Name) ? kind : $"{kind} {result.Name}";
    }

    private static void WriteSarif(IEnumerable<(string Path, int Line, int Column, string Message, string RuleId)> items, JsonSerializerOptions jsonOptions)
    {
        var writer = Console.Out;
        var itemOptions = GetCompactJsonOptions(jsonOptions);
        var itemList = items.ToList();
        writer.Write("{\"version\":\"2.1.0\",\"runs\":[{\"tool\":{\"driver\":{\"name\":\"cdidx\",\"informationUri\":\"https://github.com/Widthdom/CodeIndex\",\"rules\":");
        WriteJsonArrayInline(
            itemList
                .Select(item => item.RuleId)
                .Where(ruleId => !string.IsNullOrWhiteSpace(ruleId))
                .Distinct(StringComparer.Ordinal)
                .OrderBy(ruleId => ruleId, StringComparer.Ordinal),
            (ruleWriter, ruleId) => WriteSarifRule(ruleWriter, ruleId, itemOptions),
            separator: ",");
        writer.Write("}},\"results\":");
        WriteJsonArrayInline(
            itemList,
            (resultWriter, item) => WriteSarifResult(resultWriter, item, itemOptions),
            separator: ",");
        writer.WriteLine("}]}");
    }

    private static void WriteJsonArrayInline<T>(IEnumerable<T> items, Action<TextWriter, T> writeItem, string separator)
    {
        var writer = Console.Out;
        writer.Write('[');
        var first = true;
        foreach (var item in items)
        {
            if (!first)
                writer.Write(separator);
            writeItem(writer, item);
            first = false;
        }
        writer.Write(']');
    }

    private static void WriteSarifRule(TextWriter writer, string ruleId, JsonSerializerOptions jsonOptions)
    {
        writer.Write("{\"id\":");
        writer.Write(JsonSerializer.Serialize(ruleId, jsonOptions));
        writer.Write(",\"name\":");
        writer.Write(JsonSerializer.Serialize($"cdidx {ruleId}", jsonOptions));
        writer.Write(",\"shortDescription\":{\"text\":");
        writer.Write(JsonSerializer.Serialize($"cdidx {ruleId} result", jsonOptions));
        writer.Write("},\"fullDescription\":{\"text\":");
        writer.Write(JsonSerializer.Serialize("A machine-readable cdidx finding emitted from an indexed code query.", jsonOptions));
        writer.Write("},\"helpUri\":\"https://github.com/Widthdom/CodeIndex\",\"help\":{\"text\":");
        writer.Write(JsonSerializer.Serialize("Review the referenced location and surrounding code before filing or acting on this result.", jsonOptions));
        writer.Write("},\"defaultConfiguration\":{\"level\":\"warning\"},\"properties\":{\"tags\":[\"cdidx\",\"code-search\"]}}");
    }

    private static void WriteSarifResult(TextWriter writer, (string Path, int Line, int Column, string Message, string RuleId) item, JsonSerializerOptions jsonOptions)
    {
        writer.Write("{\"ruleId\":");
        writer.Write(JsonSerializer.Serialize(item.RuleId, jsonOptions));
        writer.Write(",\"level\":\"warning\",\"message\":{\"text\":");
        writer.Write(JsonSerializer.Serialize(item.Message, jsonOptions));
        writer.Write("},\"locations\":[{\"physicalLocation\":{\"artifactLocation\":{\"uri\":");
        writer.Write(JsonSerializer.Serialize(NormalizeSarifArtifactUri(item.Path), jsonOptions));
        writer.Write("},\"region\":{\"startLine\":");
        writer.Write(Math.Max(1, item.Line).ToString(CultureInfo.InvariantCulture));
        writer.Write(",\"startColumn\":");
        writer.Write(Math.Max(1, item.Column).ToString(CultureInfo.InvariantCulture));
        writer.Write("}}}]}");
    }

    private static string NormalizeSarifArtifactUri(string path)
    {
        var normalized = path.Replace('\\', '/');
        while (normalized.StartsWith("./", StringComparison.Ordinal))
            normalized = normalized[2..];
        return normalized;
    }

    public static int RunDefinition(string[] cmdArgs, JsonSerializerOptions jsonOptions)
    {
        var previewOptionError = ValidatePreviewOptions("definition", cmdArgs, allowMaxLineWidth: false, allowFocusOptions: false);
        if (previewOptionError != null)
        {
            CommandErrorWriter.WriteStderr(previewOptionError);
            return CommandExitCodes.UsageError;
        }
        var options = ParseArgs(
            cmdArgs,
            jsonDefault: false,
            allowNamedQuery: true,
            validateDefaultSnippetLines: false,
            validateDefaultMaxLineWidth: false);
        if (TryWriteUnsupportedOptionError("definition", cmdArgs, CliFlagSchema.GetAcceptedFlagNamesForCommand("definition"), options.Query))
            return CommandExitCodes.UsageError;
        if (TryWriteParseError(options, "definition"))
            return CommandExitCodes.UsageError;
        if (TryWriteInvalidKindFilterError(options, "definition", KnownSymbolKindFilters))
            return CommandExitCodes.InvalidArgument;
        if (!TryResolveNameExactMode(options, "definition", out var exact, out var exactError))
        {
            CommandErrorWriter.WriteStderr(exactError);
            return CommandExitCodes.UsageError;
        }
        if (exact && options.Query is not null && IsBareVerbatimQueryToken(options.Query) && options.CountOnly && string.Equals(options.Lang, "csharp", StringComparison.OrdinalIgnoreCase))
        {
            Console.WriteLine(options.Json
                ? JsonSerializer.Serialize(new QueryCountFilesJsonResult(0, 0, options.Query), CliJsonSerializerContextFactory.Create(jsonOptions).QueryCountFilesJsonResult)
                : "0");
            return CommandExitCodes.Success;
        }
        if (TryWriteBlankQueryError(options, "definition"))
            return CommandExitCodes.UsageError;
        if (string.IsNullOrWhiteSpace(options.Query))
        {
            WriteUsageError(
                "definition requires a symbol query argument",
                GetUsageLineOrThrow("definition"),
                "Add the symbol name after the command, for example: `cdidx definition QueryCommandRunner`.");
            return CommandExitCodes.UsageError;
        }
        if (IsBareVerbatimQueryToken(options.Query))
        {
            WriteUsageError(
                "definition requires a symbol query argument",
                GetUsageLineOrThrow("definition"),
                "Add a real symbol name after the command; bare verbatim prefixes like `@` are not valid queries.");
            return CommandExitCodes.UsageError;
        }
        if (TryWriteUnexpectedExtraPositionals("definition", options))
            return CommandExitCodes.UsageError;

        return WithDb(options, jsonOptions, reader =>
        {
            if (options.CountOnly)
            {
                var counts = reader.CountDefinitionsTotal(options.Query, options.Kind, options.Lang, options.PathPatterns, options.ExcludePaths, options.ExcludeTests, options.Since, exact, visibilityFilters: options.VisibilityFilters, excludeVisibilityFilters: options.ExcludeVisibilityFilters);
                var exactSignalForCount = reader.GetDefinitionExactQuerySignal(options.Lang, options.PathPatterns, options.ExcludePaths, options.ExcludeTests, options.Since);
                var exactZeroHintForCount = BuildExactZeroHint(
                    exact,
                    () => reader.CountSearchSymbols(options.Query, ExactZeroHintProbeLimit, options.Kind, options.Lang, options.PathPatterns, options.ExcludePaths, options.ExcludeTests, options.Since, exact: false, visibilityFilters: options.VisibilityFilters, excludeVisibilityFilters: options.ExcludeVisibilityFilters) > 0,
                    () => reader.CountSearchSymbols(options.Query, options.Limit, options.Kind, options.Lang, options.PathPatterns, options.ExcludePaths, options.ExcludeTests, options.Since, exact: false, visibilityFilters: options.VisibilityFilters, excludeVisibilityFilters: options.ExcludeVisibilityFilters),
                    () => reader.SearchSymbols(options.Query, Math.Min(options.Limit, ExactZeroHintSampleLimit), options.Kind, options.Lang, options.PathPatterns, options.ExcludePaths, options.ExcludeTests, options.Since, exact: false, visibilityFilters: options.VisibilityFilters, excludeVisibilityFilters: options.ExcludeVisibilityFilters),
                    r => r.Name);
                WriteExactSymbolWarningIfNeeded(exact, options.Json, exactSignalForCount, reader, options);
                if (counts.Count == 0)
                {
                    Console.WriteLine(options.Json
                        ? BuildCountJsonPayload(reader, jsonOptions, count: 0, files: 0, query: options.Query, exactZeroHint: exactZeroHintForCount, exactSignal: exact ? exactSignalForCount : null, queryOptions: options).ToJsonString(jsonOptions)
                        : "0");
                    return CommandExitCodes.Success;
                }

                if (options.Json)
                {
                    var payload = BuildCountJsonPayload(reader, jsonOptions, counts.Count, counts.FileCount, query: options.Query, exactSignal: exact ? exactSignalForCount : null, queryOptions: options);
                    Console.WriteLine(payload.ToJsonString(jsonOptions));
                }
                else
                {
                    Console.WriteLine($"{counts.Count}");
                }
                return CommandExitCodes.Success;
            }

            var results = reader.GetDefinitions(options.Query, options.Limit, options.Kind, options.Lang, options.IncludeBody, options.PathPatterns, options.ExcludePaths, options.ExcludeTests, options.Since, exact, visibilityFilters: options.VisibilityFilters, excludeVisibilityFilters: options.ExcludeVisibilityFilters);
            var exactSignal = reader.GetDefinitionExactQuerySignal(options.Lang, options.PathPatterns, options.ExcludePaths, options.ExcludeTests, options.Since);
            var exactZeroHint = BuildExactZeroHint(
                exact,
                () => reader.CountSearchSymbols(options.Query, ExactZeroHintProbeLimit, options.Kind, options.Lang, options.PathPatterns, options.ExcludePaths, options.ExcludeTests, options.Since, exact: false, visibilityFilters: options.VisibilityFilters, excludeVisibilityFilters: options.ExcludeVisibilityFilters) > 0,
                () => reader.CountSearchSymbols(options.Query, options.Limit, options.Kind, options.Lang, options.PathPatterns, options.ExcludePaths, options.ExcludeTests, options.Since, exact: false, visibilityFilters: options.VisibilityFilters, excludeVisibilityFilters: options.ExcludeVisibilityFilters),
                () => reader.SearchSymbols(options.Query, Math.Min(options.Limit, ExactZeroHintSampleLimit), options.Kind, options.Lang, options.PathPatterns, options.ExcludePaths, options.ExcludeTests, options.Since, exact: false, visibilityFilters: options.VisibilityFilters, excludeVisibilityFilters: options.ExcludeVisibilityFilters),
                r => r.Name);
            WriteExactSymbolWarningIfNeeded(exact, options.Json, exactSignal, reader, options);
            if (results.Count == 0)
            {
                if (options.Json && TryWriteEmptyFormattedResult(options, jsonOptions))
                    return ZeroResultExitCode(options);
                if (!options.Json)
                {
                    CommandErrorWriter.WriteStderr(BuildZeroResultLine("No definitions found", options));
                    WriteExactZeroHint(exactZeroHint);
                    WriteKindHint(options.Kind, reader);
                    WriteLangHint(options.Lang, reader);
                    WriteZeroResultHints(options, reader, "Try 'search' for full-text matches instead of symbol lookup.");
                }
                return ZeroResultExitCode(options);
            }

            ApplyBodyRecoveryCommands(results, options.DbPath);
            if (options.Json)
            {
                if (TryWriteFormattedLocations(
                    options,
                    results.Select(r => new FormattedLocation(r.Path, r.StartLine, null, $"{r.Kind} {r.Name}")),
                    jsonOptions))
                    return CommandExitCodes.Success;
                if (options.OutputFormat == OutputFormatLsp)
                {
                    WriteLspLocations(results.Select(ToLspLocation), jsonOptions);
                    return CommandExitCodes.Success;
                }
                if (options.OutputFormat == OutputFormatQf)
                {
                    WriteQuickfix(results.Select(r => (r.Path, r.StartLine, 1, $"{r.Kind} {r.Name}")));
                    return CommandExitCodes.Success;
                }
                if (options.OutputFormat == OutputFormatSarif)
                {
                    WriteSarif(results.Select(r => (r.Path, r.StartLine, 1, $"{r.Kind} {r.Name}", "definition")), jsonOptions);
                    return CommandExitCodes.Success;
                }
                foreach (var r in results)
                {
                    if (exact)
                        WriteJsonResultWithExactSignal(r, CliJsonSerializerContextFactory.Create(jsonOptions).DefinitionResult, exactSignal, jsonOptions);
                    else
                        Console.WriteLine(JsonSerializer.Serialize(r, CliJsonSerializerContextFactory.Create(jsonOptions).DefinitionResult));
                }
            }
            else
            {
                foreach (var r in results)
                {
                    var container = r.ContainerName != null ? $" in {r.ContainerName}" : "";
                    Console.WriteLine($"{r.Kind,-10} {r.Name,-40} {r.Path}:{r.StartLine}-{r.EndLine}{container}");
                    WriteNumberedExcerpt(r.StartLine, r.Content);
                    if (options.IncludeBody)
                    {
                        if (r.BodyContent != null && r.BodyStartLine != null)
                        {
                            Console.WriteLine();
                            Console.WriteLine("  Body:");
                            WriteNumberedExcerpt(r.BodyStartLine.Value, r.BodyContent);
                        }
                        else
                        {
                            Console.WriteLine("  Body: unavailable");
                        }
                    }
                    Console.WriteLine();
                }
                var defFileCount = results.Select(r => r.Path).Distinct().Count();
                CommandErrorWriter.WriteStderr($"({results.Count} definitions in {defFileCount} files)");
            }
            return CommandExitCodes.Success;
        });
    }

    public static int RunGoto(string[] cmdArgs, JsonSerializerOptions jsonOptions)
    {
        var all = cmdArgs.Any(arg => arg == "--all");
        var filteredArgs = cmdArgs.Where(arg => arg != "--all").ToArray();
        var options = ParseArgs(filteredArgs, jsonDefault: true, allowNamedQuery: true);
        if (TryWriteUnsupportedOptionError("goto", cmdArgs, CliFlagSchema.GetAcceptedFlagNamesForCommand("goto"), options.Query))
            return CommandExitCodes.UsageError;
        if (TryWriteParseError(options, "goto"))
            return CommandExitCodes.UsageError;
        if (TryWriteInvalidKindFilterError(options, "goto", KnownSymbolKindFilters))
            return CommandExitCodes.InvalidArgument;
        if (!TryResolveNameExactMode(options, "goto", out var exact, out var exactError))
        {
            CommandErrorWriter.WriteStderr(exactError);
            return CommandExitCodes.UsageError;
        }
        if (TryWriteBlankQueryError(options, "goto"))
            return CommandExitCodes.UsageError;
        if (string.IsNullOrWhiteSpace(options.Query))
        {
            WriteUsageError(
                "goto requires a symbol query argument",
                GetUsageLineOrThrow("goto"),
                "Add the symbol name after the command, for example: `cdidx goto QueryCommandRunner`.");
            return CommandExitCodes.UsageError;
        }
        if (IsBareVerbatimQueryToken(options.Query))
        {
            WriteUsageError(
                "goto requires a symbol query argument",
                GetUsageLineOrThrow("goto"),
                "Add a real symbol name after the command; bare verbatim prefixes like `@` are not valid queries.");
            return CommandExitCodes.UsageError;
        }
        if (TryWriteUnexpectedExtraPositionals("goto", options))
            return CommandExitCodes.UsageError;

        return WithDb(options, jsonOptions, reader =>
        {
            var limit = all ? options.Limit : Math.Max(options.Limit, 2);
            var results = reader.GetDefinitions(options.Query, limit, options.Kind, options.Lang, includeBody: false, options.PathPatterns, options.ExcludePaths, options.ExcludeTests, options.Since, exact, visibilityFilters: options.VisibilityFilters, excludeVisibilityFilters: options.ExcludeVisibilityFilters);
            if (results.Count == 0)
            {
                if (!options.Json)
                    CommandErrorWriter.WriteStderr(BuildZeroResultLine("No definitions found", options));
                return CommandExitCodes.NotFound;
            }

            if (all)
            {
                WriteLspLocations(results.Select(ToLspLocation), jsonOptions);
                return CommandExitCodes.Success;
            }

            if (results.Count > 1)
            {
                CommandErrorWriter.WriteStderr($"Error: goto found {results.Count} matching definitions for '{options.Query}'.");
                CommandErrorWriter.WriteStderr("Hint: narrow the query with --kind, --lang, --path, or pass --all to return all LSP locations.");
                return CommandExitCodes.UsageError;
            }

            Console.WriteLine(JsonSerializer.Serialize(ToLspLocation(results[0]), CliJsonSerializerContextFactory.Create(jsonOptions).LspLocation));
            return CommandExitCodes.Success;
        });
    }

    private static string FormatSearchVisibilitySuffix(string? visibility)
    {
        if (string.IsNullOrWhiteSpace(visibility)
            || string.Equals(visibility, "public", StringComparison.OrdinalIgnoreCase))
        {
            return string.Empty;
        }

        return $" [{visibility}]";
    }

    public static int RunReferences(string[] cmdArgs, JsonSerializerOptions jsonOptions)
    {
        var previewOptionError = ValidatePreviewOptions("references", cmdArgs, allowMaxLineWidth: true, allowFocusOptions: false);
        if (previewOptionError != null)
        {
            CommandErrorWriter.WriteStderr(previewOptionError);
            return CommandExitCodes.UsageError;
        }
        var options = ParseArgs(cmdArgs, jsonDefault: false, allowNamedQuery: true);
        if (TryWriteUnsupportedOptionError("references", cmdArgs, CliFlagSchema.GetAcceptedFlagNamesForCommand("references"), options.Query))
            return CommandExitCodes.UsageError;
        if (TryWriteInvalidKindFilterError(options, "references", AllValidReferenceKinds, AllValidKinds))
            return CommandExitCodes.InvalidArgument;
        if (TryWriteParseError(options, "references"))
            return CommandExitCodes.UsageError;
        if (!TryResolveNameExactMode(options, "references", out var exact, out var exactError))
        {
            CommandErrorWriter.WriteStderr(exactError);
            return CommandExitCodes.UsageError;
        }
        if (TryWriteBlankQueryError(options, "references"))
            return CommandExitCodes.UsageError;
        if (string.IsNullOrWhiteSpace(options.Query))
        {
            WriteUsageError(
                "references requires a symbol query argument",
                GetUsageLineOrThrow("references"),
                "Add the symbol name you want to trace, for example: `cdidx references QueryCommandRunner`.");
            return CommandExitCodes.UsageError;
        }
        if (IsBareVerbatimQueryToken(options.Query))
        {
            WriteUsageError(
                "references requires a symbol query argument",
                GetUsageLineOrThrow("references"),
                "Add a real symbol name after the command; bare verbatim prefixes like `@` are not valid queries.");
            return CommandExitCodes.UsageError;
        }
        if (TryWriteUnexpectedExtraPositionals("references", options))
            return CommandExitCodes.UsageError;

        return WithDb(options, jsonOptions, reader =>
        {
            WriteGraphReferenceKindHint("references", options.Kind, options.Json);
            var baseSqlGraphSignal = reader.GetSqlGraphContractSignal(options.Lang, options.PathPatterns, options.ExcludePaths, options.ExcludeTests);
            var exactGraphLanguage = exact
                ? reader.GetExactGraphSupportedDefinitionLanguage(options.Query, options.Lang, options.PathPatterns, options.ExcludePaths, options.ExcludeTests)
                : null;
            if (options.CountOnly)
            {
                var counts = reader.CountSearchReferencesTotal(options.Query, options.Lang, options.Kind, options.PathPatterns, options.ExcludePaths, options.ExcludeTests, exact);
                var effectiveSqlGraphSignal = NarrowSqlGraphContractSignal(
                    baseSqlGraphSignal,
                    counts.IncludesSql || DbReader.IsSqlLanguage(options.Lang) || DbReader.IsSqlLanguage(exactGraphLanguage));
                var exactSignalForCount = reader.GetReferencesExactQuerySignal(options.Lang, options.PathPatterns, options.ExcludePaths, options.ExcludeTests, includeSqlGraphContractSignal: effectiveSqlGraphSignal.Relevant);
                var exactZeroHintForCount = BuildExactZeroHint(
                    exact && reader._hasReferencesTable,
                    () => reader.CountSearchReferences(options.Query, ExactZeroHintProbeLimit, options.Lang, options.Kind, options.PathPatterns, options.ExcludePaths, options.ExcludeTests, exact: false) > 0,
                    () => reader.CountSearchReferences(options.Query, options.Limit, options.Lang, options.Kind, options.PathPatterns, options.ExcludePaths, options.ExcludeTests, exact: false),
                    () => reader.SearchReferences(options.Query, Math.Min(options.Limit, ExactZeroHintSampleLimit), options.Lang, options.Kind, options.PathPatterns, options.ExcludePaths, options.ExcludeTests, exact: false),
                    r => r.SymbolName);
                WriteExactGraphWarningIfNeeded(exact, options.Json, exactSignalForCount, reader, options);
                WriteSqlGraphContractWarningIfNeeded(options.Json, effectiveSqlGraphSignal, reader, options);
                if (counts.Count == 0)
                {
                    WriteGraphCountResult(reader, 0, 0, options, jsonOptions, reader._hasReferencesTable, exactSignalForCount, exactZeroHintForCount, extraFields: payload => AddSqlGraphContractJsonFields(payload, effectiveSqlGraphSignal));
                    return CommandExitCodes.Success;
                }

                WriteGraphCountResult(reader, counts.Count, counts.FileCount, options, jsonOptions, reader._hasReferencesTable, exactSignalForCount, extraFields: payload => AddSqlGraphContractJsonFields(payload, effectiveSqlGraphSignal));
                return CommandExitCodes.Success;
            }

            var results = reader.SearchReferences(options.Query, options.Limit, options.Lang, options.Kind, options.PathPatterns, options.ExcludePaths, options.ExcludeTests, exact, options.MaxLineWidth);
            if (options.IncludeBody)
                AttachBodyExcerpts(reader, results, options.SnippetLines, options.MaxLineWidth);
            ApplyBodyRecoveryCommands(results, options.DbPath);
            var sqlGraphSignal = NarrowSqlGraphContractSignalByLanguages(baseSqlGraphSignal, results.Select(result => result.Lang), options.Lang, exactGraphLanguage);
            var exactSignal = reader.GetReferencesExactQuerySignal(options.Lang, options.PathPatterns, options.ExcludePaths, options.ExcludeTests, includeSqlGraphContractSignal: sqlGraphSignal.Relevant);
            var exactZeroHint = BuildExactZeroHint(
                exact && reader._hasReferencesTable,
                () => reader.CountSearchReferences(options.Query, ExactZeroHintProbeLimit, options.Lang, options.Kind, options.PathPatterns, options.ExcludePaths, options.ExcludeTests, exact: false) > 0,
                () => reader.CountSearchReferences(options.Query, options.Limit, options.Lang, options.Kind, options.PathPatterns, options.ExcludePaths, options.ExcludeTests, exact: false),
                () => reader.SearchReferences(options.Query, Math.Min(options.Limit, ExactZeroHintSampleLimit), options.Lang, options.Kind, options.PathPatterns, options.ExcludePaths, options.ExcludeTests, exact: false),
                r => r.SymbolName);
            WriteExactGraphWarningIfNeeded(exact, options.Json, exactSignal, reader, options);
            WriteSqlGraphContractWarningIfNeeded(options.Json, sqlGraphSignal, reader, options);
            if (results.Count == 0)
            {
                if (options.Json && TryWriteEmptyFormattedResult(options, jsonOptions))
                    return ZeroResultExitCode(options);
                if (options.Json)
                    WriteGraphZeroJsonResult(reader, "references", jsonOptions, graphAvailable: reader._hasReferencesTable, exact ? exactSignal : (ExactQuerySignal?)null, exactZeroHint, queryOptions: options, extraFields: payload => AddSqlGraphContractJsonFields(payload, sqlGraphSignal));
                else if (!options.Json)
                {
                    CommandErrorWriter.WriteStderr(BuildZeroResultLine("No references found", options));
                    WriteExactZeroHint(exactZeroHint);
                    WriteGraphSupportHint(options.Lang);
                    WriteLangHint(options.Lang, reader);
                    WriteDegradedGraphZeroResult(reader, "references", json: false, graphAvailable: reader._hasReferencesTable, jsonOptions);
                }
                return ZeroResultExitCode(options);
            }

            if (options.Json)
            {
                if (TryWriteFormattedLocations(
                    options,
                    results.Select(r => new FormattedLocation(r.Path, r.Line, r.Column, $"{r.ReferenceKind} {r.SymbolName}")),
                    jsonOptions))
                    return CommandExitCodes.Success;
                if (options.OutputFormat == OutputFormatLsp)
                {
                    WriteLspLocations(results.Select(ToLspLocation), jsonOptions);
                    return CommandExitCodes.Success;
                }
                if (options.OutputFormat == OutputFormatQf)
                {
                    WriteQuickfix(results.Select(r => (r.Path, r.Line, r.Column, $"{r.ReferenceKind} {r.SymbolName}")));
                    return CommandExitCodes.Success;
                }
                if (options.OutputFormat == OutputFormatSarif)
                {
                    WriteSarif(results.Select(r => (r.Path, r.Line, r.Column, $"{r.ReferenceKind} {r.SymbolName}", r.ReferenceKind)), jsonOptions);
                    return CommandExitCodes.Success;
                }
                foreach (var r in results)
                {
                    if (exact)
                        WriteGraphJsonResult(r, CliJsonSerializerContextFactory.Create(jsonOptions).ReferenceResult, exactSignal, jsonOptions, extraFields: payload => AddSqlGraphContractJsonFields(payload, sqlGraphSignal));
                    else
                        WriteJsonResult(r, CliJsonSerializerContextFactory.Create(jsonOptions).ReferenceResult, jsonOptions, extraFields: payload => AddSqlGraphContractJsonFields(payload, sqlGraphSignal));
                }
            }
            else
            {
                foreach (var r in results)
                {
                    var owner = r.ContainerName != null ? $"  in {r.ContainerName}" : "";
                    Console.WriteLine($"{r.ReferenceKind,-12} {r.SymbolName,-32} {r.Path}:{r.Line}:{r.Column}{owner}");
                    Console.WriteLine($"  {r.Context}");
                    WriteOptionalBodyExcerpt(r.BodyStartLine, r.BodyContent);
                }
                var refFileCount = results.Select(r => r.Path).Distinct().Count();
                CommandErrorWriter.WriteStderr($"({results.Count} references in {refFileCount} files)");
            }
            return CommandExitCodes.Success;
        });
    }

    public static int RunCallers(string[] cmdArgs, JsonSerializerOptions jsonOptions)
    {
        var previewOptionError = ValidatePreviewOptions("callers", cmdArgs, allowMaxLineWidth: true, allowFocusOptions: false);
        if (previewOptionError != null)
        {
            CommandErrorWriter.WriteStderr(previewOptionError);
            return CommandExitCodes.UsageError;
        }
        var options = ParseArgs(cmdArgs, jsonDefault: false, allowNamedQuery: true);
        if (TryWriteUnsupportedOptionError("callers", cmdArgs, CliFlagSchema.GetAcceptedFlagNamesForCommand("callers"), options.Query))
            return CommandExitCodes.UsageError;
        if (TryWriteParseError(options, "callers"))
            return CommandExitCodes.UsageError;
        if (TryRejectNonCallGraphKindForGraphCommand("callers", options.Kind))
            return CommandExitCodes.UsageError;
        if (TryWriteInvalidKindFilterError(options, "callers", CallGraphOnlyReferenceKinds, AllValidReferenceKinds, AllValidKinds))
            return CommandExitCodes.InvalidArgument;
        if (!TryResolveNameExactMode(options, "callers", out var exact, out var exactError))
        {
            CommandErrorWriter.WriteStderr(exactError);
            return CommandExitCodes.UsageError;
        }
        if (TryWriteBlankQueryError(options, "callers"))
            return CommandExitCodes.UsageError;
        if (string.IsNullOrWhiteSpace(options.Query))
        {
            WriteUsageError(
                "callers requires a symbol query argument",
                GetUsageLineOrThrow("callers"),
                "Add the callee symbol name after the command, for example: `cdidx callers QueryCommandRunner`.");
            return CommandExitCodes.UsageError;
        }
        if (IsBareVerbatimQueryToken(options.Query))
        {
            WriteUsageError(
                "callers requires a symbol query argument",
                GetUsageLineOrThrow("callers"),
                "Add a real symbol name after the command; bare verbatim prefixes like `@` are not valid queries.");
            return CommandExitCodes.UsageError;
        }
        if (TryWriteUnexpectedExtraPositionals("callers", options))
            return CommandExitCodes.UsageError;

        return WithDb(options, jsonOptions, reader =>
        {
            WriteGraphReferenceKindHint("callers", options.Kind, options.Json);
            var baseSqlGraphSignal = reader.GetSqlGraphContractSignal(options.Lang, options.PathPatterns, options.ExcludePaths, options.ExcludeTests);
            var exactGraphLanguage = exact
                ? reader.GetExactGraphSupportedDefinitionLanguage(options.Query, options.Lang, options.PathPatterns, options.ExcludePaths, options.ExcludeTests)
                : null;
            if (options.CountOnly)
            {
                var counts = reader.CountCallersTotal(options.Query, options.Lang, options.Kind, options.PathPatterns, options.ExcludePaths, options.ExcludeTests, exact, options.RawKinds);
                var effectiveSqlGraphSignal = NarrowSqlGraphContractSignal(
                    baseSqlGraphSignal,
                    counts.IncludesSql || DbReader.IsSqlLanguage(options.Lang) || DbReader.IsSqlLanguage(exactGraphLanguage));
                var exactSignalForCount = reader.GetCallersExactQuerySignal(options.Lang, options.PathPatterns, options.ExcludePaths, options.ExcludeTests, includeSqlGraphContractSignal: effectiveSqlGraphSignal.Relevant);
                var exactZeroHintForCount = BuildExactZeroHint(
                    exact && reader._hasReferencesTable,
                    () => reader.CountCallers(options.Query, ExactZeroHintProbeLimit, options.Lang, options.Kind, options.PathPatterns, options.ExcludePaths, options.ExcludeTests, exact: false, rawKinds: options.RawKinds) > 0,
                    () => reader.CountCallers(options.Query, options.Limit, options.Lang, options.Kind, options.PathPatterns, options.ExcludePaths, options.ExcludeTests, exact: false, rawKinds: options.RawKinds),
                    () => reader.GetCallers(options.Query, Math.Min(options.Limit, ExactZeroHintSampleLimit), options.Lang, options.Kind, options.PathPatterns, options.ExcludePaths, options.ExcludeTests, exact: false, rawKinds: options.RawKinds, rankMode: options.RankMode),
                    r => r.CalleeName);
                WriteExactGraphWarningIfNeeded(exact, options.Json, exactSignalForCount, reader, options);
                WriteSqlGraphContractWarningIfNeeded(options.Json, effectiveSqlGraphSignal, reader, options);
                if (counts.Count == 0)
                {
                    WriteGraphCountResult(reader, 0, 0, options, jsonOptions, reader._hasReferencesTable, exactSignalForCount, exactZeroHintForCount, extraFields: payload => AddSqlGraphContractJsonFields(payload, effectiveSqlGraphSignal));
                    return CommandExitCodes.Success;
                }

                WriteGraphCountResult(reader, counts.Count, counts.FileCount, options, jsonOptions, reader._hasReferencesTable, exactSignalForCount, extraFields: payload => AddSqlGraphContractJsonFields(payload, effectiveSqlGraphSignal));
                return CommandExitCodes.Success;
            }

            var results = reader.GetCallers(options.Query, options.Limit, options.Lang, options.Kind, options.PathPatterns, options.ExcludePaths, options.ExcludeTests, exact, options.RawKinds, options.RankMode);
            if (options.IncludeBody)
                AttachBodyExcerpts(reader, results, options.SnippetLines, options.MaxLineWidth);
            ApplyBodyRecoveryCommands(results, options.DbPath);
            var sqlGraphSignal = NarrowSqlGraphContractSignalByLanguages(baseSqlGraphSignal, results.Select(result => result.Lang), options.Lang, exactGraphLanguage);
            var exactSignal = reader.GetCallersExactQuerySignal(options.Lang, options.PathPatterns, options.ExcludePaths, options.ExcludeTests, includeSqlGraphContractSignal: sqlGraphSignal.Relevant);
            var exactZeroHint = BuildExactZeroHint(
                exact && reader._hasReferencesTable,
                () => reader.CountCallers(options.Query, ExactZeroHintProbeLimit, options.Lang, options.Kind, options.PathPatterns, options.ExcludePaths, options.ExcludeTests, exact: false, rawKinds: options.RawKinds) > 0,
                () => reader.CountCallers(options.Query, options.Limit, options.Lang, options.Kind, options.PathPatterns, options.ExcludePaths, options.ExcludeTests, exact: false, rawKinds: options.RawKinds),
                () => reader.GetCallers(options.Query, Math.Min(options.Limit, ExactZeroHintSampleLimit), options.Lang, options.Kind, options.PathPatterns, options.ExcludePaths, options.ExcludeTests, exact: false, rawKinds: options.RawKinds, rankMode: options.RankMode),
                r => r.CalleeName);
            WriteExactGraphWarningIfNeeded(exact, options.Json, exactSignal, reader, options);
            WriteSqlGraphContractWarningIfNeeded(options.Json, sqlGraphSignal, reader, options);
            if (results.Count == 0)
            {
                if (options.Json && TryWriteEmptyFormattedResult(options, jsonOptions))
                    return ZeroResultExitCode(options);
                if (options.Json)
                    WriteGraphZeroJsonResult(reader, "callers", jsonOptions, graphAvailable: reader._hasReferencesTable, exact ? exactSignal : (ExactQuerySignal?)null, exactZeroHint, queryOptions: options, extraFields: payload => AddSqlGraphContractJsonFields(payload, sqlGraphSignal));
                else if (!options.Json)
                {
                    CommandErrorWriter.WriteStderr(BuildZeroResultLine("No callers found", options));
                    WriteExactZeroHint(exactZeroHint);
                    WriteGraphSupportHint(options.Lang);
                    WriteLangHint(options.Lang, reader);
                    WriteDegradedGraphZeroResult(reader, "callers", json: false, graphAvailable: reader._hasReferencesTable, jsonOptions);
                }
                return ZeroResultExitCode(options);
            }

            if (options.Json)
            {
                if (TryWriteFormattedLocations(
                    options,
                    results.Select(r => new FormattedLocation(r.Path, r.FirstLine, null, $"{r.CallerName ?? "<top-level>"} -> {r.CalleeName}")),
                    jsonOptions))
                    return CommandExitCodes.Success;
                if (options.OutputFormat == OutputFormatLsp)
                {
                    WriteLspLocations(results.Select(ToLspLocation), jsonOptions);
                    return CommandExitCodes.Success;
                }
                if (options.OutputFormat == OutputFormatQf)
                {
                    WriteQuickfix(results.Select(r => (r.Path, r.FirstLine, 1, $"{r.CallerName ?? "<top-level>"} -> {r.CalleeName}")));
                    return CommandExitCodes.Success;
                }
                if (options.OutputFormat == OutputFormatSarif)
                {
                    WriteSarif(results.Select(r => (r.Path, r.FirstLine, 1, $"{r.CallerName ?? "<top-level>"} -> {r.CalleeName}", r.ReferenceKind)), jsonOptions);
                    return CommandExitCodes.Success;
                }
                foreach (var r in results)
                {
                    if (exact)
                        WriteGraphJsonResult(r, CliJsonSerializerContextFactory.Create(jsonOptions).CallerResult, exactSignal, jsonOptions, extraFields: payload => AddSqlGraphContractJsonFields(payload, sqlGraphSignal));
                    else
                        WriteJsonResult(r, CliJsonSerializerContextFactory.Create(jsonOptions).CallerResult, jsonOptions, extraFields: payload => AddSqlGraphContractJsonFields(payload, sqlGraphSignal));
                }
            }
            else
            {
                var kindColumnWidth = ComputeReferenceKindColumnWidth(results, r => FormatReferenceKindLabel(r.ReferenceKind, r.ReferenceKinds, r.HasMixedReferenceKinds, r.ReferenceKindCounts));
                foreach (var r in results)
                {
                    var kindLabel = FormatReferenceKindLabel(r.ReferenceKind, r.ReferenceKinds, r.HasMixedReferenceKinds, r.ReferenceKindCounts);
                    Console.WriteLine($"{kindLabel.PadRight(kindColumnWidth)} {r.CallerKind ?? "?",-10} {r.CallerName ?? "<top-level>",-32} {r.Path}:{r.FirstLine}  -> {r.CalleeName} ({r.ReferenceCount} refs)");
                    WriteOptionalBodyExcerpt(r.BodyStartLine, r.BodyContent);
                }
                var callerFileCount = results.Select(r => r.Path).Distinct().Count();
                CommandErrorWriter.WriteStderr($"({results.Count} callers in {callerFileCount} files)");
            }
            return CommandExitCodes.Success;
        });
    }

    public static int RunCallees(string[] cmdArgs, JsonSerializerOptions jsonOptions)
    {
        var previewOptionError = ValidatePreviewOptions("callees", cmdArgs, allowMaxLineWidth: true, allowFocusOptions: false);
        if (previewOptionError != null)
        {
            CommandErrorWriter.WriteStderr(previewOptionError);
            return CommandExitCodes.UsageError;
        }
        var options = ParseArgs(cmdArgs, jsonDefault: false, allowNamedQuery: true);
        if (TryWriteUnsupportedOptionError("callees", cmdArgs, CliFlagSchema.GetAcceptedFlagNamesForCommand("callees"), options.Query))
            return CommandExitCodes.UsageError;
        if (TryWriteParseError(options, "callees"))
            return CommandExitCodes.UsageError;
        if (TryRejectNonCallGraphKindForGraphCommand("callees", options.Kind))
            return CommandExitCodes.UsageError;
        if (TryWriteInvalidKindFilterError(options, "callees", CallGraphOnlyReferenceKinds, AllValidReferenceKinds, AllValidKinds))
            return CommandExitCodes.InvalidArgument;
        if (!TryResolveNameExactMode(options, "callees", out var exact, out var exactError))
        {
            CommandErrorWriter.WriteStderr(exactError);
            return CommandExitCodes.UsageError;
        }
        if (TryWriteBlankQueryError(options, "callees"))
            return CommandExitCodes.UsageError;
        if (string.IsNullOrWhiteSpace(options.Query))
        {
            WriteUsageError(
                "callees requires a caller query argument",
                GetUsageLineOrThrow("callees"),
                "Add the caller symbol name after the command, for example: `cdidx callees RunIndex`.");
            return CommandExitCodes.UsageError;
        }
        if (IsBareVerbatimQueryToken(options.Query))
        {
            WriteUsageError(
                "callees requires a caller query argument",
                GetUsageLineOrThrow("callees"),
                "Add a real symbol name after the command; bare verbatim prefixes like `@` are not valid queries.");
            return CommandExitCodes.UsageError;
        }
        if (TryWriteUnexpectedExtraPositionals("callees", options))
            return CommandExitCodes.UsageError;

        return WithDb(options, jsonOptions, reader =>
        {
            WriteGraphReferenceKindHint("callees", options.Kind, options.Json);
            var baseSqlGraphSignal = reader.GetSqlGraphContractSignal(options.Lang, options.PathPatterns, options.ExcludePaths, options.ExcludeTests);
            var exactGraphLanguage = exact
                ? reader.GetExactGraphSupportedDefinitionLanguage(options.Query, options.Lang, options.PathPatterns, options.ExcludePaths, options.ExcludeTests)
                : null;
            if (options.CountOnly)
            {
                var counts = reader.CountCalleesTotal(options.Query, options.Lang, options.Kind, options.PathPatterns, options.ExcludePaths, options.ExcludeTests, exact, options.RawKinds);
                var effectiveSqlGraphSignal = NarrowSqlGraphContractSignal(
                    baseSqlGraphSignal,
                    counts.IncludesSql || DbReader.IsSqlLanguage(options.Lang) || DbReader.IsSqlLanguage(exactGraphLanguage));
                var exactSignalForCount = reader.GetCalleesExactQuerySignal(options.Lang, options.PathPatterns, options.ExcludePaths, options.ExcludeTests, includeSqlGraphContractSignal: effectiveSqlGraphSignal.Relevant);
                var exactZeroHintForCount = BuildExactZeroHint(
                    exact && reader._hasReferencesTable,
                    () => reader.CountCallees(options.Query, ExactZeroHintProbeLimit, options.Lang, options.Kind, options.PathPatterns, options.ExcludePaths, options.ExcludeTests, exact: false, rawKinds: options.RawKinds) > 0,
                    () => reader.CountCallees(options.Query, options.Limit, options.Lang, options.Kind, options.PathPatterns, options.ExcludePaths, options.ExcludeTests, exact: false, rawKinds: options.RawKinds),
                    () => reader.GetCallees(options.Query, Math.Min(options.Limit, ExactZeroHintSampleLimit), options.Lang, options.Kind, options.PathPatterns, options.ExcludePaths, options.ExcludeTests, exact: false, rawKinds: options.RawKinds, rankMode: options.RankMode),
                    r => r.CallerName);
                WriteExactGraphWarningIfNeeded(exact, options.Json, exactSignalForCount, reader, options);
                WriteSqlGraphContractWarningIfNeeded(options.Json, effectiveSqlGraphSignal, reader, options);
                if (counts.Count == 0)
                {
                    WriteGraphCountResult(reader, 0, 0, options, jsonOptions, reader._hasReferencesTable, exactSignalForCount, exactZeroHintForCount, extraFields: payload => AddSqlGraphContractJsonFields(payload, effectiveSqlGraphSignal));
                    return CommandExitCodes.Success;
                }

                WriteGraphCountResult(reader, counts.Count, counts.FileCount, options, jsonOptions, reader._hasReferencesTable, exactSignalForCount, extraFields: payload => AddSqlGraphContractJsonFields(payload, effectiveSqlGraphSignal));
                return CommandExitCodes.Success;
            }

            var results = reader.GetCallees(options.Query, options.Limit, options.Lang, options.Kind, options.PathPatterns, options.ExcludePaths, options.ExcludeTests, exact, options.RawKinds, options.RankMode);
            if (options.IncludeBody)
                AttachBodyExcerpts(reader, results, options.SnippetLines, options.MaxLineWidth);
            ApplyBodyRecoveryCommands(results, options.DbPath);
            var sqlGraphSignal = NarrowSqlGraphContractSignalByLanguages(baseSqlGraphSignal, results.Select(result => result.Lang), options.Lang, exactGraphLanguage);
            var exactSignal = reader.GetCalleesExactQuerySignal(options.Lang, options.PathPatterns, options.ExcludePaths, options.ExcludeTests, includeSqlGraphContractSignal: sqlGraphSignal.Relevant);
            var exactZeroHint = BuildExactZeroHint(
                exact && reader._hasReferencesTable,
                () => reader.CountCallees(options.Query, ExactZeroHintProbeLimit, options.Lang, options.Kind, options.PathPatterns, options.ExcludePaths, options.ExcludeTests, exact: false, rawKinds: options.RawKinds) > 0,
                () => reader.CountCallees(options.Query, options.Limit, options.Lang, options.Kind, options.PathPatterns, options.ExcludePaths, options.ExcludeTests, exact: false, rawKinds: options.RawKinds),
                () => reader.GetCallees(options.Query, Math.Min(options.Limit, ExactZeroHintSampleLimit), options.Lang, options.Kind, options.PathPatterns, options.ExcludePaths, options.ExcludeTests, exact: false, rawKinds: options.RawKinds, rankMode: options.RankMode),
                r => r.CallerName);
            WriteExactGraphWarningIfNeeded(exact, options.Json, exactSignal, reader, options);
            WriteSqlGraphContractWarningIfNeeded(options.Json, sqlGraphSignal, reader, options);
            if (results.Count == 0)
            {
                if (options.Json)
                    WriteGraphZeroJsonResult(reader, "callees", jsonOptions, graphAvailable: reader._hasReferencesTable, exact ? exactSignal : (ExactQuerySignal?)null, exactZeroHint, queryOptions: options, extraFields: payload => AddSqlGraphContractJsonFields(payload, sqlGraphSignal));
                else if (!options.Json)
                {
                    CommandErrorWriter.WriteStderr(BuildZeroResultLine("No callees found", options));
                    WriteExactZeroHint(exactZeroHint);
                    WriteGraphSupportHint(options.Lang);
                    WriteLangHint(options.Lang, reader);
                    WriteDegradedGraphZeroResult(reader, "callees", json: false, graphAvailable: reader._hasReferencesTable, jsonOptions);
                }
                return ZeroResultExitCode(options);
            }

            if (options.Json)
            {
                if (TryWriteFormattedLocations(
                    options,
                    results.Select(r => new FormattedLocation(r.Path, r.FirstLine, null, $"{r.CallerName ?? "<top-level>"} -> {r.CalleeName}")),
                    jsonOptions))
                    return CommandExitCodes.Success;
                if (options.OutputFormat == OutputFormatLsp)
                {
                    WriteLspLocations(results.Select(ToLspLocation), jsonOptions);
                    return CommandExitCodes.Success;
                }
                if (options.OutputFormat == OutputFormatQf)
                {
                    WriteQuickfix(results.Select(r => (r.Path, r.FirstLine, 1, $"{r.CallerName ?? "<top-level>"} -> {r.CalleeName}")));
                    return CommandExitCodes.Success;
                }
                if (options.OutputFormat == OutputFormatSarif)
                {
                    WriteSarif(results.Select(r => (r.Path, r.FirstLine, 1, $"{r.CallerName ?? "<top-level>"} -> {r.CalleeName}", r.ReferenceKind)), jsonOptions);
                    return CommandExitCodes.Success;
                }
                foreach (var r in results)
                {
                    if (exact)
                        WriteGraphJsonResult(r, CliJsonSerializerContextFactory.Create(jsonOptions).CalleeResult, exactSignal, jsonOptions, extraFields: payload => AddSqlGraphContractJsonFields(payload, sqlGraphSignal));
                    else
                        WriteJsonResult(r, CliJsonSerializerContextFactory.Create(jsonOptions).CalleeResult, jsonOptions, extraFields: payload => AddSqlGraphContractJsonFields(payload, sqlGraphSignal));
                }
            }
            else
            {
                var kindColumnWidth = ComputeReferenceKindColumnWidth(results, r => FormatReferenceKindLabel(r.ReferenceKind, r.ReferenceKinds, r.HasMixedReferenceKinds, r.ReferenceKindCounts));
                foreach (var r in results)
                {
                    var kindLabel = FormatReferenceKindLabel(r.ReferenceKind, r.ReferenceKinds, r.HasMixedReferenceKinds, r.ReferenceKindCounts);
                    Console.WriteLine($"{kindLabel.PadRight(kindColumnWidth)} {r.CalleeName,-32} {r.Path}:{r.FirstLine}  <- {r.CallerName ?? "<top-level>"} ({r.ReferenceCount} refs)");
                    WriteOptionalBodyExcerpt(r.BodyStartLine, r.BodyContent);
                }
                var calleeFileCount = results.Select(r => r.Path).Distinct().Count();
                CommandErrorWriter.WriteStderr($"({results.Count} callees in {calleeFileCount} files)");
            }
            return CommandExitCodes.Success;
        });
    }

    private static void AttachBodyExcerpts(DbReader reader, IEnumerable<ReferenceResult> results, int snippetLines, int maxLineWidth)
    {
        foreach (var result in results)
        {
            var excerpt = result.ContainerName != null
                ? BuildSymbolBodyExcerpt(reader, result.Path, result.Lang, result.ContainerName, snippetLines, maxLineWidth)
                : null;
            excerpt ??= BuildBodyExcerpt(reader, result.Path, result.Line, snippetLines, maxLineWidth, focusColumn: result.Column, focusLength: Math.Max(1, result.SymbolName.Length));
            ApplyBodyExcerpt(result, excerpt);
        }
    }

    private static void AttachBodyExcerpts(DbReader reader, IEnumerable<CallerResult> results, int snippetLines, int maxLineWidth)
    {
        foreach (var result in results)
        {
            var excerpt = result.CallerName != null
                ? BuildSymbolBodyExcerpt(reader, result.Path, result.Lang, result.CallerName, snippetLines, maxLineWidth)
                : null;
            excerpt ??= BuildBodyExcerpt(reader, result.Path, result.FirstLine, snippetLines, maxLineWidth);
            ApplyBodyExcerpt(result, excerpt);
        }
    }

    private static void AttachBodyExcerpts(DbReader reader, IEnumerable<CalleeResult> results, int snippetLines, int maxLineWidth)
    {
        foreach (var result in results)
        {
            var excerpt = BuildSymbolBodyExcerpt(reader, result.Path, result.Lang, result.CalleeName, snippetLines, maxLineWidth)
                ?? BuildBodyExcerpt(reader, result.Path, result.FirstLine, snippetLines, maxLineWidth);
            ApplyBodyExcerpt(result, excerpt);
        }
    }

    private static void AttachBodyExcerpts(DbReader reader, IEnumerable<ImpactResult> results, int snippetLines, int maxLineWidth)
    {
        foreach (var result in results)
        {
            var excerpt = result.CallerName != null
                ? BuildSymbolBodyExcerpt(reader, result.Path, result.Lang, result.CallerName, snippetLines, maxLineWidth)
                : null;
            excerpt ??= BuildBodyExcerpt(reader, result.Path, result.FirstLine, snippetLines, maxLineWidth);
            ApplyBodyExcerpt(result, excerpt);
        }
    }

    private static FileExcerptResult? BuildSymbolBodyExcerpt(DbReader reader, string path, string? lang, string symbolName, int snippetLines, int maxLineWidth)
    {
        var symbols = reader.SearchSymbols(
            symbolName,
            limit: 1,
            kind: null,
            lang: lang,
            pathPatterns: [path],
            excludePathPatterns: null,
            excludeTests: false,
            since: null,
            exact: true);
        var symbol = symbols.FirstOrDefault();
        if (symbol == null)
            return null;

        var startLine = symbol.StartLine;
        var naturalEndLine = symbol.BodyEndLine ?? symbol.EndLine;
        var cappedLines = SearchSnippetFormatter.ClampSnippetLines(snippetLines);
        var cappedEndLine = (int)Math.Min(naturalEndLine, (long)startLine + cappedLines - 1);
        var excerpt = reader.GetExcerpt(path, startLine, cappedEndLine, maxLineWidth: maxLineWidth, focusLine: startLine);
        if (excerpt != null && cappedEndLine < naturalEndLine)
        {
            excerpt.RequestedStartLine = startLine;
            excerpt.RequestedEndLine = naturalEndLine;
            excerpt.EffectiveStartLine = excerpt.StartLine;
            excerpt.EffectiveEndLine = excerpt.EndLine;
            var recoveryStartLine = cappedEndLine + 1;
            var recoveryEndLine = (int)Math.Min(naturalEndLine, (long)recoveryStartLine + cappedLines - 1);
            AddExcerptTruncation(excerpt, "body_line_cap", recoveryStartLine, recoveryEndLine);
        }
        return excerpt;
    }

    private static FileExcerptResult? BuildBodyExcerpt(DbReader reader, string path, int line, int snippetLines, int maxLineWidth, int? focusColumn = null, int focusLength = 1)
    {
        var cappedLines = SearchSnippetFormatter.ClampSnippetLines(snippetLines);
        var endLine = (int)Math.Min(int.MaxValue, (long)line + cappedLines - 1);
        return reader.GetExcerpt(
            path,
            line,
            endLine,
            maxLineWidth: maxLineWidth,
            focusLine: line,
            focusColumn: focusColumn,
            focusLength: focusLength);
    }

    private static void ApplyBodyExcerpt(ReferenceResult result, FileExcerptResult? excerpt)
    {
        if (excerpt == null)
            return;
        result.BodyContent = excerpt.Content;
        result.BodyStartLine = excerpt.StartLine;
        result.BodyEndLine = excerpt.EndLine;
        result.BodyContentTruncated = excerpt.ContentTruncated;
        result.BodyRequestedStartLine = excerpt.RequestedStartLine;
        result.BodyRequestedEndLine = excerpt.RequestedEndLine;
        result.BodyEffectiveStartLine = excerpt.EffectiveStartLine;
        result.BodyEffectiveEndLine = excerpt.EffectiveEndLine;
        result.BodyContentTruncationReasons = CopyTruncationReasons(excerpt);
        result.BodyContentRecovery = excerpt.ContentRecovery;
    }

    private static void ApplyBodyExcerpt(CallerResult result, FileExcerptResult? excerpt)
    {
        if (excerpt == null)
            return;
        result.BodyContent = excerpt.Content;
        result.BodyStartLine = excerpt.StartLine;
        result.BodyEndLine = excerpt.EndLine;
        result.BodyContentTruncated = excerpt.ContentTruncated;
        result.BodyRequestedStartLine = excerpt.RequestedStartLine;
        result.BodyRequestedEndLine = excerpt.RequestedEndLine;
        result.BodyEffectiveStartLine = excerpt.EffectiveStartLine;
        result.BodyEffectiveEndLine = excerpt.EffectiveEndLine;
        result.BodyContentTruncationReasons = CopyTruncationReasons(excerpt);
        result.BodyContentRecovery = excerpt.ContentRecovery;
    }

    private static void ApplyBodyExcerpt(CalleeResult result, FileExcerptResult? excerpt)
    {
        if (excerpt == null)
            return;
        result.BodyContent = excerpt.Content;
        result.BodyStartLine = excerpt.StartLine;
        result.BodyEndLine = excerpt.EndLine;
        result.BodyContentTruncated = excerpt.ContentTruncated;
        result.BodyRequestedStartLine = excerpt.RequestedStartLine;
        result.BodyRequestedEndLine = excerpt.RequestedEndLine;
        result.BodyEffectiveStartLine = excerpt.EffectiveStartLine;
        result.BodyEffectiveEndLine = excerpt.EffectiveEndLine;
        result.BodyContentTruncationReasons = CopyTruncationReasons(excerpt);
        result.BodyContentRecovery = excerpt.ContentRecovery;
    }

    private static void ApplyBodyExcerpt(ImpactResult result, FileExcerptResult? excerpt)
    {
        if (excerpt == null)
            return;
        result.BodyContent = excerpt.Content;
        result.BodyStartLine = excerpt.StartLine;
        result.BodyEndLine = excerpt.EndLine;
        result.BodyContentTruncated = excerpt.ContentTruncated;
        result.BodyRequestedStartLine = excerpt.RequestedStartLine;
        result.BodyRequestedEndLine = excerpt.RequestedEndLine;
        result.BodyEffectiveStartLine = excerpt.EffectiveStartLine;
        result.BodyEffectiveEndLine = excerpt.EffectiveEndLine;
        result.BodyContentTruncationReasons = CopyTruncationReasons(excerpt);
        result.BodyContentRecovery = excerpt.ContentRecovery;
    }

    private static void AddExcerptTruncation(FileExcerptResult excerpt, string reason, int recoveryStartLine, int recoveryEndLine)
    {
        excerpt.ContentTruncated = true;
        if (!excerpt.ContentTruncationReasons.Any(existing => string.Equals(existing, reason, StringComparison.Ordinal)))
            excerpt.ContentTruncationReasons.Add(reason);
        excerpt.ContentRecovery ??= FileExcerptResult.CreateRecoveryHint(excerpt.Path, recoveryStartLine, recoveryEndLine);
    }

    private static List<string>? CopyTruncationReasons(FileExcerptResult excerpt)
        => excerpt.ContentTruncationReasons.Count > 0 ? [.. excerpt.ContentTruncationReasons] : null;

    private static void ApplyBodyRecoveryCommands(IEnumerable<DefinitionResult> results, string dbPath)
    {
        foreach (var result in results)
            ExcerptRecoveryCommandFormatter.ApplyDbPath(result.BodyContentRecovery, result.Path, dbPath);
    }

    private static void ApplyBodyRecoveryCommands(IEnumerable<ReferenceResult> results, string dbPath)
    {
        foreach (var result in results)
            ExcerptRecoveryCommandFormatter.ApplyDbPath(result.BodyContentRecovery, result.Path, dbPath);
    }

    private static void ApplyBodyRecoveryCommands(IEnumerable<CallerResult> results, string dbPath)
    {
        foreach (var result in results)
            ExcerptRecoveryCommandFormatter.ApplyDbPath(result.BodyContentRecovery, result.Path, dbPath);
    }

    private static void ApplyBodyRecoveryCommands(IEnumerable<CalleeResult> results, string dbPath)
    {
        foreach (var result in results)
            ExcerptRecoveryCommandFormatter.ApplyDbPath(result.BodyContentRecovery, result.Path, dbPath);
    }

    private static void ApplyBodyRecoveryCommands(IEnumerable<ImpactResult> results, string dbPath)
    {
        foreach (var result in results)
            ExcerptRecoveryCommandFormatter.ApplyDbPath(result.BodyContentRecovery, result.Path, dbPath);
    }

    private static void ApplyBodyRecoveryCommands(SymbolAnalysisResult result, string dbPath)
    {
        ApplyBodyRecoveryCommands(result.Definitions, dbPath);
        ApplyBodyRecoveryCommands(result.References, dbPath);
        ApplyBodyRecoveryCommands(result.Callers, dbPath);
        ApplyBodyRecoveryCommands(result.Callees, dbPath);
    }

    private static void WriteOptionalBodyExcerpt(int? startLine, string? content, string indent = "")
    {
        if (startLine == null || content == null)
            return;

        Console.WriteLine($"{indent}  Body:");
        WriteNumberedExcerpt(startLine.Value, content, indent + "  ");
    }

    /// <summary>
    /// Build the OR-joined name list for `symbols`: first positional + extra positionals + --name values.
    /// Pipe characters are treated as literal name characters so operator symbols like `operator |` remain searchable.
    /// Multi-name queries must use repeated positional args or `--name` flags.
    /// `symbols` コマンド用の名前リストを組み立て（最初の positional + 追加 positional + --name）。
    /// `|` は名前文字として扱うので `operator |` などの演算子シンボルも検索可能。複数名指定は繰り返し positional か `--name` で行う。
    /// </summary>
    internal static (List<string>? Queries, bool HadExplicitInput) BuildSymbolQueryList(QueryCommandOptions options)
    {
        var raw = new List<string>();
        if (options.Query != null)
            raw.Add(options.Query);
        raw.AddRange(options.ExtraNames);
        var hadExplicitInput = raw.Count > 0;
        if (!hadExplicitInput)
            return (null, false);
        var deduped = raw.Where(s => !string.IsNullOrEmpty(s)).Distinct().ToList();
        if (deduped.Any(IsBareVerbatimQueryToken))
            return (null, hadExplicitInput);
        return (deduped.Count == 0 ? null : deduped, hadExplicitInput);
    }

    public static int RunSymbols(string[] cmdArgs, JsonSerializerOptions jsonOptions)
    {
        var previewOptionError = ValidatePreviewOptions("symbols", cmdArgs, allowMaxLineWidth: false, allowFocusOptions: false);
        if (previewOptionError != null)
        {
            CommandErrorWriter.WriteStderr(previewOptionError);
            return CommandExitCodes.UsageError;
        }
        var options = ParseArgs(
            cmdArgs,
            jsonDefault: false,
            allowNamedQuery: true,
            validateDefaultSnippetLines: false,
            validateDefaultMaxLineWidth: false);
        if (TryWriteUnsupportedOptionError("symbols", cmdArgs, CliFlagSchema.GetAcceptedFlagNamesForCommand("symbols"), options.Query))
            return CommandExitCodes.UsageError;
        if (TryWriteUnsupportedOutputFormat("symbols", options, SymbolOutputFormats, "Use `--format json` for symbol rows, `--format count` for symbol totals, or `--format lsp|qf|sarif` for editor/diagnostic locations; compact symbol rows are not currently defined."))
            return CommandExitCodes.UsageError;
        if (TryWriteInvalidKindFilterError(options, "symbols", KnownSymbolKindFilters))
            return CommandExitCodes.InvalidArgument;
        if (TryWriteParseError(options, "symbols"))
            return CommandExitCodes.UsageError;
        if (TryWriteBlankQueryError(options, "symbols"))
            return CommandExitCodes.UsageError;
        if (!TryResolveNameExactMode(options, "symbols", out var exact, out var exactError))
        {
            CommandErrorWriter.WriteStderr(exactError);
            return CommandExitCodes.UsageError;
        }
        var exactBareVerbatimOnly = exact && string.Equals(options.Lang, "csharp", StringComparison.OrdinalIgnoreCase) && (
            (options.Query is not null && IsBareVerbatimQueryToken(options.Query) && options.ExtraNames.Count == 0) ||
            (options.Query is null && options.ExtraNames.Count > 0 && options.ExtraNames.All(IsBareVerbatimQueryToken)));
        var (symbolQueries, hadExplicitInput) = BuildSymbolQueryList(options);
        if (hadExplicitInput && symbolQueries == null)
        {
            if (exactBareVerbatimOnly && options.CountOnly)
            {
                var countQuery = options.Query ?? string.Join(" ", options.ExtraNames);
                Console.WriteLine(options.Json
                    ? JsonSerializer.Serialize(new QueryCountFilesJsonResult(0, 0, countQuery), CliJsonSerializerContextFactory.Create(jsonOptions).QueryCountFilesJsonResult)
                    : "0");
                return CommandExitCodes.Success;
            }
            // Fail closed: an explicit name/query was provided but normalized to empty or a bare
            // verbatim prefix (e.g. `|`, `@`, `--name ""`). Returning null here would broaden into
            // an unfiltered symbol dump. /
            // 明示入力が正規化で空、または verbatim 接頭辞単独（`|`、`@`、`--name ""` など）になった場合は必ず拒否する。
            CommandErrorWriter.WriteStderr("Error: symbol name list is empty after normalization. Check for empty --name values, bare verbatim prefixes like `@`, or bare `|` separators. / シンボル名リストが正規化の結果空です。--name の空値、`@` のような verbatim 接頭辞単独、単独の `|` を確認してください。");
            return CommandExitCodes.UsageError;
        }
        if (symbolQueries != null && symbolQueries.Count > MaxSymbolQueryNames)
        {
            CommandErrorWriter.WriteStderr($"Error: too many symbol names ({symbolQueries.Count}); maximum is {MaxSymbolQueryNames}. Split the request into smaller batches. / シンボル名が多すぎます（{symbolQueries.Count}件、上限は {MaxSymbolQueryNames} 件）。分割してください。");
            return CommandExitCodes.UsageError;
        }

        return WithDb(options, jsonOptions, reader =>
        {
            if (options.CountOnly)
            {
                var counts = reader.CountSearchSymbolsTotal(symbolQueries, options.Kind, options.Lang, options.PathPatterns, options.ExcludePaths, options.ExcludeTests, options.Since, exact, visibilityFilters: options.VisibilityFilters, excludeVisibilityFilters: options.ExcludeVisibilityFilters);
                var hasExactPredicateForCount = exact && symbolQueries is { Count: > 0 };
                var exactSignalForCount = reader.GetSymbolsExactQuerySignal(options.Lang, options.PathPatterns, options.ExcludePaths, options.ExcludeTests, options.Since);
                var multiNameExactHintForCount = symbolQueries != null && symbolQueries.Count > 1;
                var exactZeroHintForCount = multiNameExactHintForCount
                    ? BuildExactZeroHint(
                        exact,
                        () => reader.AnySearchSymbols(symbolQueries, options.Kind, options.Lang, options.PathPatterns, options.ExcludePaths, options.ExcludeTests, options.Since, exact: false, visibilityFilters: options.VisibilityFilters, excludeVisibilityFilters: options.ExcludeVisibilityFilters),
                        () => reader.SearchSymbols(symbolQueries, Math.Min(options.Limit, ExactZeroHintSampleLimit), options.Kind, options.Lang, options.PathPatterns, options.ExcludePaths, options.ExcludeTests, options.Since, exact: false, visibilityFilters: options.VisibilityFilters, excludeVisibilityFilters: options.ExcludeVisibilityFilters),
                        r => r.Name)
                    : BuildExactZeroHint(
                        exact && symbolQueries != null && symbolQueries.Count > 0,
                        () => reader.CountSearchSymbols(symbolQueries, ExactZeroHintProbeLimit, options.Kind, options.Lang, options.PathPatterns, options.ExcludePaths, options.ExcludeTests, options.Since, exact: false, visibilityFilters: options.VisibilityFilters, excludeVisibilityFilters: options.ExcludeVisibilityFilters) > 0,
                        () => reader.CountSearchSymbols(symbolQueries, options.Limit, options.Kind, options.Lang, options.PathPatterns, options.ExcludePaths, options.ExcludeTests, options.Since, exact: false, visibilityFilters: options.VisibilityFilters, excludeVisibilityFilters: options.ExcludeVisibilityFilters),
                        () => reader.SearchSymbols(symbolQueries, Math.Min(options.Limit, ExactZeroHintSampleLimit), options.Kind, options.Lang, options.PathPatterns, options.ExcludePaths, options.ExcludeTests, options.Since, exact: false, visibilityFilters: options.VisibilityFilters, excludeVisibilityFilters: options.ExcludeVisibilityFilters),
                        r => r.Name);
                WriteExactSymbolWarningIfNeeded(hasExactPredicateForCount, options.Json, exactSignalForCount, reader, options);
                if (counts.Count == 0)
                {
                    Console.WriteLine(options.Json
                        ? BuildCountJsonPayload(reader, jsonOptions, count: 0, files: 0, query: options.Query, exactZeroHint: exactZeroHintForCount, exactSignal: hasExactPredicateForCount ? exactSignalForCount : null, queryOptions: options).ToJsonString(jsonOptions)
                        : "0");
                    return CommandExitCodes.Success;
                }

                if (options.Json)
                {
                    var payload = BuildCountJsonPayload(reader, jsonOptions, counts.Count, counts.FileCount, query: options.Query, exactSignal: hasExactPredicateForCount ? exactSignalForCount : null, queryOptions: options);
                    Console.WriteLine(payload.ToJsonString(jsonOptions));
                }
                else
                {
                    Console.WriteLine($"{counts.Count}");
                }
                return CommandExitCodes.Success;
            }

            var results = reader.SearchSymbols(symbolQueries, options.Limit, options.Kind, options.Lang, options.PathPatterns, options.ExcludePaths, options.ExcludeTests, options.Since, exact, visibilityFilters: options.VisibilityFilters, excludeVisibilityFilters: options.ExcludeVisibilityFilters, sortMode: options.SymbolSortMode);
            var hasExactPredicate = exact && symbolQueries is { Count: > 0 };
            var exactSignal = reader.GetSymbolsExactQuerySignal(options.Lang, options.PathPatterns, options.ExcludePaths, options.ExcludeTests, options.Since);
            var multiNameExactHint = symbolQueries != null && symbolQueries.Count > 1;
            var exactZeroHint = multiNameExactHint
                ? BuildExactZeroHint(
                    exact,
                    () => reader.AnySearchSymbols(symbolQueries, options.Kind, options.Lang, options.PathPatterns, options.ExcludePaths, options.ExcludeTests, options.Since, exact: false, visibilityFilters: options.VisibilityFilters, excludeVisibilityFilters: options.ExcludeVisibilityFilters),
                    () => reader.SearchSymbols(symbolQueries, Math.Min(options.Limit, ExactZeroHintSampleLimit), options.Kind, options.Lang, options.PathPatterns, options.ExcludePaths, options.ExcludeTests, options.Since, exact: false, visibilityFilters: options.VisibilityFilters, excludeVisibilityFilters: options.ExcludeVisibilityFilters),
                    r => r.Name)
                : BuildExactZeroHint(
                    exact && symbolQueries != null && symbolQueries.Count > 0,
                    () => reader.CountSearchSymbols(symbolQueries, ExactZeroHintProbeLimit, options.Kind, options.Lang, options.PathPatterns, options.ExcludePaths, options.ExcludeTests, options.Since, exact: false, visibilityFilters: options.VisibilityFilters, excludeVisibilityFilters: options.ExcludeVisibilityFilters) > 0,
                    () => reader.CountSearchSymbols(symbolQueries, options.Limit, options.Kind, options.Lang, options.PathPatterns, options.ExcludePaths, options.ExcludeTests, options.Since, exact: false, visibilityFilters: options.VisibilityFilters, excludeVisibilityFilters: options.ExcludeVisibilityFilters),
                    () => reader.SearchSymbols(symbolQueries, Math.Min(options.Limit, ExactZeroHintSampleLimit), options.Kind, options.Lang, options.PathPatterns, options.ExcludePaths, options.ExcludeTests, options.Since, exact: false, visibilityFilters: options.VisibilityFilters, excludeVisibilityFilters: options.ExcludeVisibilityFilters),
                    r => r.Name);
            WriteExactSymbolWarningIfNeeded(hasExactPredicate, options.Json, exactSignal, reader, options);
            if (results.Count == 0)
            {
                if (options.OutputFormat == OutputFormatJson && options.JsonOutputFormat == JsonOutputFormatArray)
                {
                    Console.WriteLine(JsonSerializer.Serialize(results, CliJsonSerializerContextFactory.Create(jsonOptions).ListSymbolResult));
                    return ZeroResultExitCode(options);
                }
                if (TryWriteEmptyFormattedResult(options, jsonOptions))
                    return ZeroResultExitCode(options);
                if (!options.Json)
                {
                    CommandErrorWriter.WriteStderr(BuildZeroResultLine("No symbols found", options));
                    WriteExactZeroHint(exactZeroHint);
                    WriteKindHint(options.Kind, reader);
                    WriteLangHint(options.Lang, reader);
                    WriteSymbolExtractionCapabilityHint(options.Lang, reader);
                    WriteZeroResultHints(options, reader);
                }
                return ZeroResultExitCode(options);
            }

            if (options.OutputFormat == OutputFormatLsp)
            {
                WriteLspLocations(results.Select(ToLspLocation), jsonOptions);
                return CommandExitCodes.Success;
            }
            if (options.OutputFormat == OutputFormatQf)
            {
                WriteQuickfix(results.Select(ToSymbolQuickfixItem));
                return CommandExitCodes.Success;
            }
            if (options.OutputFormat == OutputFormatSarif)
            {
                WriteSarif(results.Select(ToSymbolSarifItem), jsonOptions);
                return CommandExitCodes.Success;
            }
            if (options.Json)
            {
                if (options.JsonOutputFormat == JsonOutputFormatArray)
                {
                    Console.WriteLine(JsonSerializer.Serialize(results, CliJsonSerializerContextFactory.Create(jsonOptions).ListSymbolResult));
                }
                else
                {
                    foreach (var r in results)
                    {
                        if (hasExactPredicate)
                            WriteJsonResultWithExactSignal(r, CliJsonSerializerContextFactory.Create(jsonOptions).SymbolResult, exactSignal, jsonOptions);
                        else
                            Console.WriteLine(JsonSerializer.Serialize(r, CliJsonSerializerContextFactory.Create(jsonOptions).SymbolResult));
                    }
                }
            }
            else
            {
                foreach (var r in results)
                {
                    var lineRange = r.EndLine > r.StartLine
                        ? $"{r.StartLine}-{r.EndLine}"
                        : r.StartLine.ToString();
                    Console.WriteLine($"{ConsoleUi.ColorizeKind(r.Kind, 10)} {r.Name,-40} {r.Path}:{lineRange}{FormatSymbolRankSuffix(r)}");
                }
                var symFileCount = results.Select(r => r.Path).Distinct().Count();
                var sortSummary = options.SymbolSortMode == SymbolSortMode.Name ? string.Empty : $"; sort={options.SymbolSortMode.ToString().ToLowerInvariant()}";
                CommandErrorWriter.WriteStderr($"({results.Count} symbols in {symFileCount} files{sortSummary})");
            }
            return CommandExitCodes.Success;
        });
    }

    private static string FormatSymbolRankSuffix(SymbolResult result)
    {
        if (result.SortMode == null)
            return string.Empty;

        var parts = new List<string>();
        if (result.ReferenceCount.HasValue)
            parts.Add($"refs={result.ReferenceCount.Value}");
        if (result.HotspotScore.HasValue)
            parts.Add($"hotspot={result.HotspotScore.Value.ToString("0.###", CultureInfo.InvariantCulture)}");
        if (result.SizeLines.HasValue)
            parts.Add($"size={result.SizeLines.Value}");
        if (result.ComplexityScore.HasValue)
            parts.Add($"complexity={result.ComplexityScore.Value.ToString("0.###", CultureInfo.InvariantCulture)}");

        return parts.Count == 0 ? string.Empty : $" [{string.Join(", ", parts)}]";
    }

    public static int RunFiles(string[] cmdArgs, JsonSerializerOptions jsonOptions)
    {
        var previewOptionError = ValidatePreviewOptions("files", cmdArgs, allowMaxLineWidth: false, allowFocusOptions: false);
        if (previewOptionError != null)
        {
            CommandErrorWriter.WriteStderr(previewOptionError);
            return CommandExitCodes.UsageError;
        }
        var options = ParseArgs(
            cmdArgs,
            jsonDefault: false,
            allowNamedQuery: true,
            validateDefaultSnippetLines: false,
            validateDefaultMaxLineWidth: false);
        if (TryWriteUnsupportedOptionError("files", cmdArgs, CliFlagSchema.GetAcceptedFlagNamesForCommand("files"), options.Query))
            return CommandExitCodes.UsageError;
        if (TryWriteParseError(options, "files"))
            return CommandExitCodes.UsageError;
        if (TryWriteUnexpectedExtraPositionals("files", options))
            return CommandExitCodes.UsageError;

        return WithDb(options, jsonOptions, reader =>
        {
            if (options.CountOnly)
            {
                var counts = reader.CountListFiles(options.Query, options.Lang, options.PathPatterns, options.ExcludePaths, options.ExcludeTests, options.Since);
                if (counts.Count == 0)
                {
                    Console.WriteLine(options.Json
                        ? BuildCountJsonPayload(reader, jsonOptions, count: 0, files: 0, query: options.Query, queryOptions: options).ToJsonString(jsonOptions)
                        : "0");
                    return CommandExitCodes.Success;
                }

                Console.WriteLine(options.Json
                    ? BuildCountJsonPayload(reader, jsonOptions, counts.Count, counts.Count, query: options.Query, queryOptions: options).ToJsonString(jsonOptions)
                    : $"{counts.Count}");
                return CommandExitCodes.Success;
            }

            var results = reader.ListFiles(options.Query, options.Limit, options.Lang, options.PathPatterns, options.ExcludePaths, options.ExcludeTests, options.Since, orderBySize: options.RawBytes);
            if (results.Count == 0)
            {
                if (options.Json)
                {
                    Console.WriteLine(options.JsonOutputFormat == JsonOutputFormatArray
                        ? "[]"
                        : BuildJsonZeroResultPayload(reader, jsonOptions, resultsKey: "files", queryOptions: options).ToJsonString(jsonOptions));
                }
                else if (!options.Json)
                {
                    CommandErrorWriter.WriteStderr(BuildZeroResultLine("No files found", options));
                    WriteLangHint(options.Lang, reader);
                    WriteZeroResultHints(options, reader);
                }
                return ZeroResultExitCode(options);
            }

            if (options.Json)
            {
                var context = CliJsonSerializerContextFactory.Create(jsonOptions);
                if (options.JsonOutputFormat == JsonOutputFormatArray)
                {
                    Console.WriteLine(JsonSerializer.Serialize(results, context.ListFileResult));
                }
                else
                {
                    foreach (var r in results)
                        Console.WriteLine(JsonSerializer.Serialize(r, context.FileResult));
                }
            }
            else
            {
                foreach (var r in results)
                {
                    var size = options.RawBytes ? $"{r.Size.ToString(CultureInfo.InvariantCulture)} bytes" : ConsoleUi.FormatBytes(r.Size);
                    Console.WriteLine($"{r.Lang ?? "?",-12} {r.Lines,6} lines  {size,12}  {r.Path}");
                }
                CommandErrorWriter.WriteStderr($"({results.Count} files)");
            }
            return CommandExitCodes.Success;
        });
    }

    public static int RunExcerpt(string[] cmdArgs, JsonSerializerOptions jsonOptions)
    {
        var previewOptionError = ValidatePreviewOptions("excerpt", cmdArgs, allowMaxLineWidth: true, allowFocusOptions: true);
        if (previewOptionError != null)
        {
            CommandErrorWriter.WriteStderr(previewOptionError);
            return CommandExitCodes.UsageError;
        }
        var options = ParseArgs(
            cmdArgs,
            jsonDefault: false,
            validateDefaultLimit: false,
            validateDefaultSnippetLines: false);
        if (TryWriteUnsupportedOptionError("excerpt", cmdArgs, CliFlagSchema.GetAcceptedFlagNamesForCommand("excerpt")))
            return CommandExitCodes.UsageError;
        if (TryWriteParseError(options, "excerpt"))
            return CommandExitCodes.UsageError;
        if (options.Query == null)
        {
            WriteUsageError(
                "excerpt requires a path argument",
                GetUsageLineOrThrow("excerpt"),
                "Pass the indexed file path after `excerpt`, for example: `cdidx excerpt src/CodeIndex/Program.cs --start 20`.");
            return CommandExitCodes.UsageError;
        }
        if (TryWriteUnexpectedExtraPositionals("excerpt", options))
            return CommandExitCodes.UsageError;
        if (options.FocusColumn == null && (options.FocusLine.HasValue || cmdArgs.Any(arg => arg == "--focus-length" || arg.StartsWith("--focus-length=", StringComparison.Ordinal))))
        {
            WriteValidationError(
                "--focus-line and --focus-length require --focus-column.",
                "Add `--focus-column <n>` so excerpt knows which token to keep visible inside the clamped line.");
            return CommandExitCodes.UsageError;
        }

        if (options.StartLine == null)
        {
            WriteValidationError(
                "excerpt requires --start <line>",
                "Add a starting line number, for example: `cdidx excerpt src/CodeIndex/Program.cs --start 20`.");
            return CommandExitCodes.UsageError;
        }

        var endLine = options.EndLine ?? options.StartLine.Value;
        if (endLine < options.StartLine.Value)
        {
            WriteValidationError(
                $"--start ({options.StartLine.Value}) must be less than or equal to --end ({endLine}).",
                "Use `--start` less than or equal to `--end`, or omit `--end` to read a single line.");
            return CommandExitCodes.UsageError;
        }

        var filePath = DbPathResolver.ResolveQueryFilePath(options.DbPath, options.Query, options.DbPathExplicit);
        return WithDb(options, jsonOptions, reader =>
        {
            if (options.FocusLine.HasValue)
            {
                var file = reader.GetFileByPath(filePath);
                if (file != null)
                {
                    var requestedStart = Math.Max(1, options.StartLine.Value - options.ContextBefore);
                    var requestedEnd = Math.Min(file.Lines, endLine + options.ContextAfter);
                    if (options.FocusLine.Value < requestedStart || options.FocusLine.Value > requestedEnd)
                    {
                        CommandErrorWriter.WriteStderr($"Error: --focus-line ({options.FocusLine.Value}) must be within the returned excerpt range ({requestedStart}-{requestedEnd}).");
                        return CommandExitCodes.UsageError;
                    }
                }
            }
            if (options.FocusColumn.HasValue)
            {
                var focusLineLength = reader.GetExcerptFocusLineLength(
                    filePath,
                    options.StartLine.Value,
                    endLine,
                    options.ContextBefore,
                    options.ContextAfter,
                    options.FocusLine ?? options.StartLine.Value);
                if (focusLineLength.HasValue && options.FocusColumn.Value > focusLineLength.Value)
                {
                    CommandErrorWriter.WriteStderr($"Error: --focus-column ({options.FocusColumn.Value}) must be within the focused line length ({focusLineLength.Value}).");
                    return CommandExitCodes.UsageError;
                }
            }

            var excerpt = reader.GetExcerpt(
                filePath,
                options.StartLine.Value,
                endLine,
                options.ContextBefore,
                options.ContextAfter,
                options.MaxLineWidth,
                options.FocusLine ?? options.StartLine.Value,
                options.FocusColumn,
                options.FocusLength);
            if (excerpt == null)
            {
                if (!options.Json)
                    CommandErrorWriter.WriteStderr("No excerpt found.");
                return ZeroResultExitCode(options);
            }
            if (options.Json)
            {
                ExcerptRecoveryCommandFormatter.ApplyDbPath(excerpt, options.DbPath);
                excerpt.SemanticTokens = BuildExcerptSemanticTokens(excerpt);
            }

            if (options.Json)
            {
                Console.WriteLine(JsonSerializer.Serialize(excerpt, CliJsonSerializerContextFactory.Create(jsonOptions).FileExcerptResult));
            }
            else
            {
                Console.WriteLine($"{excerpt.Path}:{excerpt.StartLine}-{excerpt.EndLine}");
                WriteNumberedExcerpt(excerpt.StartLine, excerpt.Content);
            }
            return CommandExitCodes.Success;
        });
    }

    private static List<ExcerptSemanticToken> BuildExcerptSemanticTokens(FileExcerptResult excerpt)
    {
        var tokens = new List<ExcerptSemanticToken>();
        var lines = excerpt.Content.Replace("\r\n", "\n").Split('\n');
        var spans = excerpt.ContentLineSpans.Count == 0
            ? BuildIdentityExcerptContentLineSpans(excerpt, lines)
            : excerpt.ContentLineSpans;
        foreach (var span in spans)
        {
            if (span.ContentLine <= 0 || span.ContentLine > lines.Length)
                continue;

            var line = lines[span.ContentLine - 1];
            var startColumn = Math.Clamp(span.ContentStartColumn - 1, 0, line.Length);
            var endColumn = Math.Clamp(span.ContentEndColumn - 1, startColumn, line.Length);
            var column = startColumn;
            while (column < endColumn)
            {
                if (!IsSemanticTokenStart(line[column]))
                {
                    column++;
                    continue;
                }

                var start = column;
                column++;
                while (column < endColumn && IsSemanticTokenPart(line[column]))
                    column++;

                var tokenText = line[start..column];
                var sourceStartColumn = span.SourceStartColumn + ((start + 1) - span.ContentStartColumn);
                var sourceEndColumn = span.SourceStartColumn + ((column + 1) - span.ContentStartColumn);
                tokens.Add(new ExcerptSemanticToken
                {
                    StartLine = span.SourceLine,
                    StartColumn = sourceStartColumn,
                    EndLine = span.SourceLine,
                    EndColumn = sourceEndColumn,
                    Type = ClassifySemanticToken(tokenText),
                });
            }
        }

        return tokens;
    }

    private static List<ExcerptContentLineSpan> BuildIdentityExcerptContentLineSpans(FileExcerptResult excerpt, string[] lines)
    {
        var spans = new List<ExcerptContentLineSpan>(lines.Length);
        for (var i = 0; i < lines.Length; i++)
        {
            spans.Add(new ExcerptContentLineSpan
            {
                ContentLine = i + 1,
                SourceLine = excerpt.StartLine + i,
                ContentStartColumn = 1,
                ContentEndColumn = lines[i].Length + 1,
                SourceStartColumn = 1,
                SourceEndColumn = lines[i].Length + 1,
            });
        }

        return spans;
    }

    private static bool IsSemanticTokenStart(char value) =>
        char.IsLetter(value) || value == '_' || char.IsDigit(value);

    private static bool IsSemanticTokenPart(char value) =>
        char.IsLetterOrDigit(value) || value == '_';

    private static string ClassifySemanticToken(string token)
    {
        if (token.All(char.IsDigit))
            return "number";
        if (char.IsUpper(token[0]))
            return "type";
        return "variable";
    }

    public static int RunFind(string[] cmdArgs, JsonSerializerOptions jsonOptions)
    {
        var preparedFindArgs = PrepareFindArgs(cmdArgs, out var preparationError);
        if (preparationError != null)
        {
            CommandErrorWriter.WriteStderr(preparationError);
            CommandErrorWriter.WriteStderr(FindUsage);
            return CommandExitCodes.UsageError;
        }

        var findValidationError = ValidateFindArgs(preparedFindArgs);
        if (findValidationError != null)
        {
            CommandErrorWriter.WriteStderr(findValidationError);
            CommandErrorWriter.WriteStderr(FindUsage);
            return CommandExitCodes.UsageError;
        }

        var options = ParseArgs(
            preparedFindArgs,
            jsonDefault: false,
            allowNamedQuery: true,
            validateDefaultSnippetLines: false);
        if (options.ParseError != null)
        {
            CommandErrorWriter.WriteStderr(options.ParseError);
            CommandErrorWriter.WriteStderr(FindUsage);
            return CommandExitCodes.UsageError;
        }
        if (options.Query is not null && string.IsNullOrWhiteSpace(options.Query))
        {
            CommandErrorWriter.WriteStderr("Error: find query cannot be empty or whitespace-only");
            CommandErrorWriter.WriteStderr("Hint: Pass a non-empty value after `find`; empty or whitespace-only arguments (e.g. `\"\"` or `\"   \"`) are rejected.");
            CommandErrorWriter.WriteStderr(FindUsage);
            return CommandExitCodes.UsageError;
        }
        if (string.IsNullOrWhiteSpace(options.Query))
        {
            CommandErrorWriter.WriteStderr("Error: find requires a query argument");
            CommandErrorWriter.WriteStderr(FindUsage);
            return CommandExitCodes.UsageError;
        }
        if (options.Query.Length > QueryLimits.MaxQueryLength)
        {
            CommandErrorWriter.WriteStderr($"Error: {QueryLimits.FormatQueryTooLongError()}");
            CommandErrorWriter.WriteStderr("Hint: Shorten the find text or split generated input into smaller queries before running `cdidx find`.");
            CommandErrorWriter.WriteStderr(FindUsage);
            return CommandExitCodes.UsageError;
        }

        if (options.PathPatterns.Count == 0 && !options.All)
        {
            CommandErrorWriter.WriteStderr("Error: find requires at least one --path <glob> or explicit --all to scope the search");
            CommandErrorWriter.WriteStderr("Hint: use --path <glob> for a bounded file set, or --all to scan all indexed files with safety caps.");
            CommandErrorWriter.WriteStderr(FindUsage);
            return CommandExitCodes.UsageError;
        }
        if (options.PathPatterns.Count > 0 && options.All)
        {
            CommandErrorWriter.WriteStderr("Error: find accepts either --path <glob> or --all, not both");
            CommandErrorWriter.WriteStderr("Hint: remove --all when using explicit path filters, or remove --path to scan all indexed files with caps.");
            CommandErrorWriter.WriteStderr(FindUsage);
            return CommandExitCodes.UsageError;
        }

        return WithDb(options, jsonOptions, reader =>
        {
            var pathPatterns = options.All ? null : options.PathPatterns;
            var candidateFileLimit = options.All ? FindAllCandidateFileLimit : (int?)null;
            var lineLimit = options.All ? FindAllLineScanLimit : (int?)null;
            if (options.CountOnly)
            {
                FindCountResult counts;
                try
                {
                    counts = reader.CountFindInFiles(options.Query, options.Lang, pathPatterns, options.ExcludePaths, options.ExcludeTests, options.Exact, options.FocusLine, options.FocusColumn, options.Regex, candidateFileLimit, lineLimit);
                }
                catch (Exception ex) when (options.Regex && (ex is ArgumentException || ex is RegexMatchTimeoutException))
                {
                    return ex is RegexMatchTimeoutException timeout
                        ? WriteFindRegexTimeoutError(timeout, jsonOptions, options.Json)
                        : WriteFindInvalidRegexError(ex);
                }
                if (counts.Count == 0)
                {
                    if (options.Json)
                    {
                        var payload = BuildCountJsonPayload(
                            reader,
                            jsonOptions,
                            count: 0,
                            files: 0,
                            query: options.Query,
                            queryOptions: options,
                            extraFields: payload => AddFindScanJsonFields(payload, counts.Scan));
                        Console.WriteLine(payload.ToJsonString(jsonOptions));
                    }
                    else
                    {
                        Console.WriteLine("0");
                        WriteFindScanSummary(counts.Scan);
                    }
                    return CommandExitCodes.Success;
                }

                if (options.Json)
                {
                    var payload = BuildCountJsonPayload(
                        reader,
                        jsonOptions,
                        counts.Count,
                        counts.FileCount,
                        query: options.Query,
                        queryOptions: options,
                        extraFields: payload => AddFindScanJsonFields(payload, counts.Scan));
                    Console.WriteLine(payload.ToJsonString(jsonOptions));
                }
                else
                {
                    Console.WriteLine($"{counts.Count}");
                    WriteFindScanSummary(counts.Scan);
                }
                return CommandExitCodes.Success;
            }

            var (contextBefore, contextAfter, snippetLines) = ResolveFindContext(options, preparedFindArgs);
            FindResults findResults;
            try
            {
                findResults = reader.FindInFiles(options.Query, options.Limit, options.Lang, pathPatterns, options.ExcludePaths, options.ExcludeTests, contextBefore, contextAfter, options.Exact, options.MaxLineWidth, options.FocusLine, options.FocusColumn, options.Regex, candidateFileLimit, lineLimit);
            }
            catch (ArgumentException ex) when (options.Regex)
            {
                return WriteFindInvalidRegexError(ex);
            }
            catch (RegexMatchTimeoutException ex) when (options.Regex)
            {
                return WriteFindRegexTimeoutError(ex, jsonOptions, options.Json);
            }
            var results = findResults.Results;
            if (results.Count == 0)
            {
                var candidateFileCount = findResults.Scan.CandidateFiles;
                if (options.Json)
                {
                    if (TryWriteEmptyFormattedResult(options, jsonOptions))
                        return ZeroResultExitCode(options);
                    var payload = BuildJsonZeroResultPayload(reader, jsonOptions, resultsKey: "results", queryOptions: options, extraFields: payload =>
                    {
                        payload["query"] = options.Query;
                        payload["path"] = JsonSerializer.SerializeToNode(options.PathPatterns, CliJsonSerializerContextFactory.Create(jsonOptions).ListString);
                        payload["exclude_tests"] = options.ExcludeTests;
                        payload["before"] = contextBefore;
                        payload["after"] = contextAfter;
                        if (snippetLines.HasValue)
                            payload["snippet_lines"] = snippetLines.Value;
                        payload["exact"] = options.Exact;
                        payload["regex"] = options.Regex;
                        payload["file_count"] = candidateFileCount;
                    });
                    AddFindScanJsonFields(payload, findResults.Scan);
                    Console.WriteLine(payload.ToJsonString(jsonOptions));
                }
                else
                {
                    CommandErrorWriter.WriteStderr(BuildZeroResultLine("No matches found", options));
                    if (candidateFileCount > 0)
                    {
                        var fileText = ConsoleUi.Counted(candidateFileCount, "file");
                        WriteZeroResultHints(options, reader, filterHint: $"--path matched {fileText}, but the query did not match their contents. Try a broader query or check the query syntax.");
                    }
                    else
                    {
                        WriteZeroResultHints(options, reader, filterHint: "try broadening --path or adding another --path value; --path is required for find.");
                    }
                }
                return ZeroResultExitCode(options);
            }

            if (options.Json)
            {
                if (TryWriteFormattedLocations(
                    options,
                    results.Select(r => new FormattedLocation(r.Path, r.Line, r.Column, $"find match: {options.Query}")),
                    jsonOptions))
                    return CommandExitCodes.Success;
                if (options.OutputFormat == OutputFormatLsp)
                {
                    WriteLspLocations(results.Select(ToLspLocation), jsonOptions);
                    return CommandExitCodes.Success;
                }
                if (options.OutputFormat == OutputFormatQf)
                {
                    WriteQuickfix(results.Select(r => (r.Path, r.Line, r.Column, $"find match: {options.Query}")));
                    return CommandExitCodes.Success;
                }
                if (options.OutputFormat == OutputFormatSarif)
                {
                    WriteSarif(results.Select(r => (r.Path, r.Line, r.Column, $"find match: {options.Query}", "find")), jsonOptions);
                    return CommandExitCodes.Success;
                }
                foreach (var r in results)
                    Console.WriteLine(JsonSerializer.Serialize(r, CliJsonSerializerContextFactory.Create(jsonOptions).FileFindResult));
            }
            else
            {
                foreach (var r in results)
                {
                    Console.WriteLine($"{r.Path}:{r.Line}:{r.Column}");
                    WriteNumberedExcerpt(r.StartLine, r.Snippet);
                    Console.WriteLine();
                }
                var fileCount = results.Select(r => r.Path).Distinct().Count();
                CommandErrorWriter.WriteStderr($"({results.Count} matches in {fileCount} files)");
                WriteFindScanSummary(findResults.Scan);
            }
            return CommandExitCodes.Success;
        });
    }

    private static int WriteFindInvalidRegexError(Exception ex)
    {
        CommandErrorWriter.WriteStderr($"Error: invalid regular expression: {ex.Message}");
        return CommandExitCodes.UsageError;
    }

    internal static int WriteFindRegexTimeoutError(RegexMatchTimeoutException ex, JsonSerializerOptions jsonOptions, bool json)
    {
        var timeout = FormatRegexMatchTimeout(ex.MatchTimeout);
        return CommandErrorWriter.WriteJsonOrHuman(
            json,
            jsonOptions,
            $"regular expression timed out after {timeout} while scanning indexed file contents.",
            CommandExitCodes.RuntimeError,
            hint: "Simplify the pattern, narrow the scan with --path/--lang, or omit --regex for literal text.",
            errorCode: CommandErrorCodes.RegexMatchTimeout,
            category: "regex_timeout");
    }

    internal static string FormatRegexMatchTimeout(TimeSpan timeout)
    {
        if (timeout.TotalMilliseconds < 1000)
            return timeout.TotalMilliseconds.ToString("0.###", CultureInfo.InvariantCulture) + "ms";
        return timeout.TotalSeconds.ToString("0.###", CultureInfo.InvariantCulture) + "s";
    }

    private static string? ValidateFindArgs(string[] args)
    {
        var (allowedWithValues, allowedFlags) = CliFlagSchema.GetParserFlagsPartitionedByValueBearing("find");

        var queryCount = 0;
        for (int i = 0; i < args.Length; i++)
        {
            var rawArg = args[i];
            // Accept both `--opt value` and `--opt=value` so ValidateFindArgs and ParseArgs
            // agree on inline-`=` shape; splitting the token in PrepareFindArgs would
            // destroy legitimate inline values that start with `--` (e.g. `--path=--literal.txt`).
            // ParseArgs と同じく `--opt value` と `--opt=value` の両形を受け入れる。
            // PrepareFindArgs でトークンを分解すると `--path=--literal.txt` のような `--` 始まりの合法な
            // inline 値が壊れるため、validation 側で inline 値を解決する。
            string arg;
            string? inlineValue;
            if (TrySplitInlineOptionValue(rawArg, out var inlineOptionName))
            {
                arg = inlineOptionName!;
                inlineValue = rawArg[(inlineOptionName!.Length + 1)..];
            }
            else
            {
                arg = rawArg;
                inlineValue = null;
            }

            if (allowedWithValues.Contains(arg))
            {
                string value;
                if (inlineValue != null)
                {
                    value = inlineValue;
                }
                else
                {
                    if (i + 1 >= args.Length)
                        return BuildMissingOptionValueError(arg);
                    value = args[i + 1];
                    i++;
                }
                if ((arg == "--limit" || arg == "--top") && (!int.TryParse(value, NumberStyles.Integer, CultureInfo.InvariantCulture, out var limit) || limit <= 0))
                    return BuildPositiveIntegerError("--limit", ConsoleUi.FormatBoundedValue(value), arg);
                if ((arg == "--limit" || arg == "--top")
                    && int.TryParse(value, NumberStyles.Integer, CultureInfo.InvariantCulture, out var limitCeil)
                    && NumericFlagUpperBounds.TryGetValue("--limit", out var limitMax)
                    && limitCeil > limitMax)
                    return BuildPositiveIntegerUpperBoundError("--limit", ConsoleUi.FormatBoundedValue(value), limitMax);
                if (arg == "--max-line-width" && (!int.TryParse(value, NumberStyles.Integer, CultureInfo.InvariantCulture, out var widthValue) || widthValue < 0))
                    return BuildNonNegativeIntegerError(arg, ConsoleUi.FormatBoundedValue(value));
                if (arg == "--max-line-width" && int.TryParse(value, NumberStyles.Integer, CultureInfo.InvariantCulture, out var widthCeil) && widthCeil > LineWidthFormatter.MaxAllowedLineWidth)
                    return BuildNonNegativeIntegerUpperBoundError("--max-line-width", ConsoleUi.FormatBoundedValue(value), LineWidthFormatter.MaxAllowedLineWidth);
                if ((arg == "--before" || arg == "--after") && (!int.TryParse(value, NumberStyles.Integer, CultureInfo.InvariantCulture, out var context) || context < 0))
                    return BuildNonNegativeIntegerError(arg, ConsoleUi.FormatBoundedValue(value));
                if ((arg == "--before" || arg == "--after")
                    && int.TryParse(value, NumberStyles.Integer, CultureInfo.InvariantCulture, out var contextCeil)
                    && NumericFlagUpperBounds.TryGetValue(arg, out var contextMax)
                    && contextCeil > contextMax)
                    return BuildNonNegativeIntegerUpperBoundError(arg, ConsoleUi.FormatBoundedValue(value), contextMax);
                if (arg == "--snippet-lines" && (!int.TryParse(value, NumberStyles.Integer, CultureInfo.InvariantCulture, out var snippetLines) || snippetLines <= 0))
                    return BuildPositiveIntegerError(arg, ConsoleUi.FormatBoundedValue(value), arg);
                if (arg == "--snippet-lines"
                    && int.TryParse(value, NumberStyles.Integer, CultureInfo.InvariantCulture, out var snippetLinesCeil)
                    && NumericFlagUpperBounds.TryGetValue(arg, out var snippetLinesMax)
                    && snippetLinesCeil > snippetLinesMax)
                    return BuildPositiveIntegerUpperBoundError(arg, ConsoleUi.FormatBoundedValue(value), snippetLinesMax);
                if ((arg == "--focus-line" || arg == "--focus-column") && (!int.TryParse(value, NumberStyles.Integer, CultureInfo.InvariantCulture, out var focus) || focus <= 0))
                    return BuildPositiveIntegerError(arg, ConsoleUi.FormatBoundedValue(value), arg);
                if (arg == "--query")
                {
                    queryCount++;
                    if (queryCount > 1)
                        return "Error: find accepts exactly one query argument";
                }
                continue;
            }

            if (allowedFlags.Contains(arg))
                continue;

            if (rawArg.StartsWith('-'))
            {
                var error = $"Error: unsupported option for find: {ConsoleUi.FormatBoundedValue(rawArg)}";
                // Suggest the closest accepted find flag for typos like `--paht` → `--path`
                // (#1582). Strip any inline `=value` portion before matching, since the prefix
                // might not have been a recognized value-taking option (TrySplitInlineOptionValue
                // only splits on known options).
                // `--paht` → `--path` のようなタイプミスから回復させるため、find が受理する
                // フラグの中で最も近いものを提案する (#1582)。`--foo=bar` 形では prefix が未知
                // value-taking option の場合 TrySplitInlineOptionValue が分解しないので、
                // suggester 用に `=` 前の部分を独自に切り出して照合する。
                var nameForSuggestion = arg;
                var eq = nameForSuggestion.IndexOf('=');
                if (eq > 0)
                    nameForSuggestion = nameForSuggestion[..eq];
                var suggestion = ConsoleUi.FindClosestMatch(nameForSuggestion, allowedWithValues.Concat(allowedFlags).Where(o => o != "--"));
                if (suggestion != null)
                    error += $"\nDid you mean: {suggestion}?";
                return error;
            }

            queryCount++;
            if (queryCount > 1)
                return "Error: find accepts exactly one query argument";
        }

        return null;
    }

    private static (int Before, int After, int? SnippetLines) ResolveFindContext(QueryCommandOptions options, string[] preparedFindArgs)
    {
        if (!HasOption(preparedFindArgs, "--snippet-lines"))
            return (options.ContextBefore, options.ContextAfter, null);

        var explicitBefore = HasOption(preparedFindArgs, "--before");
        var explicitAfter = HasOption(preparedFindArgs, "--after");
        var surroundingLines = Math.Max(0, options.SnippetLines - 1);
        var before = explicitBefore ? options.ContextBefore : surroundingLines / 2;
        var after = explicitAfter ? options.ContextAfter : surroundingLines - before;
        return (before, after, options.SnippetLines);
    }

    private static string[] PrepareFindArgs(string[] args, out string? error)
    {
        var normalized = new List<string>(args.Length);
        error = null;
        for (int i = 0; i < args.Length; i++)
        {
            if (args[i] == "--")
            {
                if (i + 1 >= args.Length)
                {
                    error = "Error: -- requires a following literal query for find";
                    return args;
                }

                if (i + 2 < args.Length)
                {
                    error = "Error: find accepts exactly one query argument after --";
                    return args;
                }

                normalized.Add("--query");
                normalized.Add(args[i + 1]);
                return [.. normalized];
            }

            normalized.Add(args[i]);
        }

        return [.. normalized];
    }

    public static int RunMap(string[] cmdArgs, JsonSerializerOptions jsonOptions)
    {
        var previewOptionError = ValidatePreviewOptions("map", cmdArgs, allowMaxLineWidth: false, allowFocusOptions: false);
        if (previewOptionError != null)
        {
            CommandErrorWriter.WriteStderr(previewOptionError);
            return CommandExitCodes.UsageError;
        }
        var options = ParseArgs(
            cmdArgs,
            jsonDefault: false,
            validateDefaultSnippetLines: false,
            validateDefaultMaxLineWidth: false);
        if (TryWriteUnsupportedOptionError("map", cmdArgs, CliFlagSchema.GetAcceptedFlagNamesForCommand("map")))
            return CommandExitCodes.UsageError;
        if (TryWriteParseError(options, "map"))
            return CommandExitCodes.UsageError;
        if (TryWriteUnsupportedOutputFormat("map", options, RepoMapOutputFormats, "Use `--format json` or `--format compact` for map output; use `cdidx files --count` when you need only a file count."))
            return CommandExitCodes.UsageError;
        if (TryWriteUnexpectedPositionals("map", options))
            return CommandExitCodes.UsageError;
        if (options.MapSummaryOnly && options.MapSections != null)
            return CommandErrorWriter.Write(
                "--summary-only cannot be combined with --sections.",
                CommandExitCodes.UsageError,
                "choose --summary-only for aggregate fields only, or --sections <tree,languages,hotspots,metrics> for selected detail sections.",
                ConsoleUi.GetUsageLine("map"));

        return WithDb(options, jsonOptions, reader =>
        {
            var compactLimit = GetCompactSectionLimit(options);
            var mapLimit = options.Compact ? GetCompactSourceLimit(compactLimit) : options.Limit;
            var map = reader.GetRepoMap(mapLimit, options.Lang, options.PathPatterns, options.ExcludePaths, options.ExcludeTests, options.MinEntrypointConfidence);
            WorkspaceMetadataEnricher.Enrich(map, options.DbPath, options.DbPathExplicit);
            if (options.ContextAfterExplicit)
                ApplyRepoMapDepth(map, options.ContextAfter);
            var compactTruncation = options.Compact ? ApplyRepoMapCompactCaps(map, compactLimit, options) : null;

            // Return not-found only when a narrowing filter is active and produces zero files.
            // Unfiltered empty indexes return success (valid state for health probes).
            // フィルタ指定時に該当0件なら未検出を返す。フィルタなしの空DBは正常（ヘルスチェック用途）。
            var hasFilter = options.PathPatterns.Count > 0 || options.ExcludePaths.Count > 0
                || options.ExcludeTests || options.Lang != null;
            if (map.FileCount == 0 && hasFilter)
            {
                if (options.Json)
                {
                    var payload = BuildRepoMapJsonPayload(map, options, jsonOptions, compactTruncation);
                    Console.WriteLine(payload.ToJsonString(jsonOptions));
                }
                else
                {
                    CommandErrorWriter.WriteStderr("No files found matching the given filters.");
                }
                return ZeroResultExitCode(options);
            }

            if (options.Json)
            {
                var payload = BuildRepoMapJsonPayload(map, options, jsonOptions, compactTruncation);
                Console.WriteLine(payload.ToJsonString(jsonOptions));
            }
            else
            {
                Console.WriteLine($"Files      : {map.FileCount:N0}");
                Console.WriteLine($"Lines      : {map.TotalLines:N0}");
                Console.WriteLine($"Symbols    : {map.TotalSymbols:N0}");
                Console.WriteLine($"References : {map.TotalReferences:N0}");
                if (map.IndexedAt != null)
                    Console.WriteLine($"Scope Indexed At     : {map.IndexedAt:O}");
                if (map.LatestModified != null)
                    Console.WriteLine($"Scope Modified       : {map.LatestModified:O}");
                if (map.WorkspaceIndexedAt != null)
                    Console.WriteLine($"Workspace Indexed At : {map.WorkspaceIndexedAt:O}");
                if (map.WorkspaceLatestModified != null)
                    Console.WriteLine($"Workspace Modified   : {map.WorkspaceLatestModified:O}");
                if (map.GitHead != null)
                    Console.WriteLine($"Git HEAD   : {map.GitHead}");
                if (map.GitIsDirty != null)
                    Console.WriteLine($"Git Dirty  : {map.GitIsDirty}");
                if (!map.GraphTableAvailable)
                    Console.WriteLine("WARN       : symbol_references table missing — reference counts are synthesized 0. Do not use ReferenceRich / reference-derived ranking as authoritative.");
                if (MapSectionEnabled(options, "languages"))
                    WriteRepoMapSection("Languages", map.Languages.Select(item => $"{item.Lang,-12} {item.Files,4} files  {item.Symbols,5} syms  {item.References,5} refs"));
                if (MapSectionEnabled(options, "tree"))
                    WriteRepoMapSection("Modules", map.Modules.Select(item => $"{item.Module,-24} {item.Files,4} files  {item.Symbols,5} syms  {item.References,5} refs"));
                if (MapSectionEnabled(options, "hotspots"))
                {
                    WriteRepoMapSection("Top files", map.TopFiles.Select(item => $"{item.Path}  [score {item.Score}, {item.SymbolCount} syms, {item.ReferenceCount} refs]"));
                    WriteRepoMapSection("Symbol-rich files", map.SymbolRichFiles.Select(item => $"{item.Path}  [{item.SymbolCount} syms, {item.ReferenceCount} refs]"));
                    WriteRepoMapSection("Reference-rich files", map.ReferenceRichFiles.Select(item => $"{item.Path}  [{item.ReferenceCount} refs, {item.SymbolCount} syms]"));
                    WriteRepoMapSection("Entrypoints", map.Entrypoints.Select(item => $"{item.Kind,-10} {item.Name,-24} {item.Path}:{item.Line}  [score {item.Score}, confidence {item.Confidence:0.###}, {item.MatchType}, hint #{item.HintRank}]"));
                }
                if (MapSectionEnabled(options, "metrics"))
                    WriteRepoMapSection("Largest files", map.LargestFiles.Select(item =>
                {
                    var size = options.RawBytes ? $"{item.Size.ToString(CultureInfo.InvariantCulture)} bytes" : ConsoleUi.FormatBytes(item.Size);
                    return $"{item.Path}  [{item.Lines} lines, {size}]";
                }));
            }

            return CommandExitCodes.Success;
        });
    }

    private static bool MapSectionEnabled(QueryCommandOptions options, string section)
        => !options.MapSummaryOnly && (options.MapSections == null || options.MapSections.Contains(section, StringComparer.Ordinal));

    private static void ApplyRepoMapDepth(RepoMapResult map, int depth)
    {
        map.Modules = map.Modules
            .Where(module => GetPathDepth(module.Module) <= depth)
            .ToList();
    }

    private static int GetPathDepth(string path)
        => string.IsNullOrEmpty(path) ? 0 : path.Split('/', StringSplitOptions.RemoveEmptyEntries).Length;

    private static JsonObject BuildRepoMapJsonPayload(RepoMapResult map, QueryCommandOptions options, JsonSerializerOptions jsonOptions, JsonObject? compactTruncation = null)
    {
        var payload = JsonSerializer.SerializeToNode(map, CliJsonSerializerContextFactory.Create(jsonOptions).RepoMapResult)!.AsObject();
        if (options.MapSummaryOnly)
        {
            KeepRepoMapJsonProperties(payload, RepoMapSummaryJsonProperties);
            payload["summary_only"] = true;
            payload["sections"] = new JsonArray();
            return payload;
        }

        if (options.MapSections == null)
        {
            if (options.ContextAfterExplicit)
                payload["depth"] = options.ContextAfter;
            if (options.Compact && compactTruncation != null)
                AddCompactJsonFields(payload, GetCompactSectionLimit(options), compactTruncation);
            return payload;
        }

        var keep = new HashSet<string>(RepoMapSummaryJsonProperties, StringComparer.Ordinal);
        if (MapSectionEnabled(options, "languages"))
            keep.Add("languages");
        if (MapSectionEnabled(options, "tree"))
            keep.Add("modules");
        if (MapSectionEnabled(options, "hotspots"))
        {
            keep.Add("topFiles");
            keep.Add("symbolRichFiles");
            keep.Add("referenceRichFiles");
            keep.Add("entrypoints");
        }
        if (MapSectionEnabled(options, "metrics"))
            keep.Add("largestFiles");

        KeepRepoMapJsonProperties(payload, keep);
        payload["sections"] = new JsonArray(options.MapSections.Select(section => JsonValue.Create(section)).ToArray<JsonNode?>());
        if (options.ContextAfterExplicit)
            payload["depth"] = options.ContextAfter;
        if (options.Compact && compactTruncation != null)
            AddCompactJsonFields(payload, GetCompactSectionLimit(options), compactTruncation);
        return payload;
    }

    private static readonly HashSet<string> RepoMapSummaryJsonProperties = new(StringComparer.Ordinal)
    {
        "api_version",
        "file_count",
        "total_lines",
        "total_symbols",
        "total_references",
        "indexed_at",
        "latest_modified",
        "workspace_indexed_at",
        "workspace_latest_modified",
        "project_root",
        "git_head",
        "git_is_dirty",
        "indexed_head_commit",
        "worktree_head_changed",
        "graph_table_available",
    };

    private static void KeepRepoMapJsonProperties(JsonObject payload, IReadOnlySet<string> keep)
    {
        foreach (var propertyName in payload.Select(property => property.Key).Where(key => !keep.Contains(key)).ToList())
            payload.Remove(propertyName);
    }

    private static int GetCompactSectionLimit(QueryCommandOptions options)
        => options.LimitExplicit ? options.Limit : DefaultCompactSectionLimit;

    private static int GetCompactSourceLimit(int compactLimit)
    {
        var sourceLimit = compactLimit + 1;
        return NumericFlagUpperBounds.TryGetValue("--limit", out var maxLimit)
            ? Math.Min(sourceLimit, maxLimit)
            : sourceLimit;
    }

    private static JsonObject ApplyRepoMapCompactCaps(RepoMapResult map, int sectionLimit, QueryCommandOptions options)
    {
        var sections = new JsonObject();
        if (MapSectionEnabled(options, "languages"))
            TruncateCompactSection(map.Languages, sectionLimit, sections, "languages");
        if (MapSectionEnabled(options, "tree"))
            TruncateCompactSection(map.Modules, sectionLimit, sections, "modules");
        if (MapSectionEnabled(options, "hotspots"))
        {
            TruncateCompactSection(map.TopFiles, sectionLimit, sections, "top_files");
            TruncateCompactSection(map.SymbolRichFiles, sectionLimit, sections, "symbol_rich_files");
            TruncateCompactSection(map.ReferenceRichFiles, sectionLimit, sections, "reference_rich_files");
            TruncateCompactSection(map.Entrypoints, sectionLimit, sections, "entrypoints");
        }
        if (MapSectionEnabled(options, "metrics"))
            TruncateCompactSection(map.LargestFiles, sectionLimit, sections, "largest_files");
        return BuildCompactTruncationMetadata(sectionLimit, sections);
    }

    private static JsonObject ApplySymbolAnalysisCompactCaps(SymbolAnalysisResult analysis, int sectionLimit)
    {
        var sections = new JsonObject();
        TruncateCompactSection(analysis.Definitions, sectionLimit, sections, "definitions");
        TruncateCompactSection(analysis.NearbySymbols, sectionLimit, sections, "nearby_symbols");
        TruncateCompactSection(analysis.References, sectionLimit, sections, "references");
        TruncateCompactSection(analysis.Callers, sectionLimit, sections, "callers");
        TruncateCompactSection(analysis.Callees, sectionLimit, sections, "callees");
        return BuildCompactTruncationMetadata(sectionLimit, sections);
    }

    private static JsonObject ApplyOutlineCompactCaps(OutlineResult outline, int sectionLimit)
    {
        var sections = new JsonObject();
        TruncateCompactSection(outline.Symbols, sectionLimit, sections, "symbols");
        return BuildCompactTruncationMetadata(sectionLimit, sections);
    }

    private static bool HasOutlineJsonControls(QueryCommandOptions options, IReadOnlyList<string> kindFilters)
        => options.OutlineFieldsExplicit
           || kindFilters.Count > 0
           || options.LimitExplicit
           || options.OutlineCursorOffset.HasValue;

    private static List<string> BuildOutlineKindFilters(string? rawKind)
    {
        if (string.IsNullOrWhiteSpace(rawKind))
            return [];

        return rawKind
            .Split(',', StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries)
            .Select(kind => kind.ToLowerInvariant())
            .Distinct(StringComparer.Ordinal)
            .ToList();
    }

    private static List<OutlineSymbol> ApplyOutlineKindFilters(IReadOnlyList<OutlineSymbol> symbols, IReadOnlyList<string> kindFilters)
    {
        if (kindFilters.Count == 0)
            return symbols.ToList();

        var filterSet = kindFilters.ToHashSet(StringComparer.Ordinal);
        return symbols.Where(symbol => filterSet.Contains(symbol.Kind.ToLowerInvariant())).ToList();
    }

    private static List<OutlineSymbol> ApplyOutlineHumanPaging(IReadOnlyList<OutlineSymbol> symbols, QueryCommandOptions options)
    {
        if (!options.LimitExplicit && !options.OutlineCursorOffset.HasValue)
            return symbols.ToList();

        var offset = Math.Min(options.OutlineCursorOffset ?? 0, symbols.Count);
        return symbols.Skip(offset).Take(options.Limit).ToList();
    }

    private static JsonObject BuildOutlineJsonPayload(
        OutlineResult outline,
        IReadOnlyList<OutlineSymbol> filteredSymbols,
        IReadOnlyList<string> kindFilters,
        QueryCommandOptions options,
        JsonSerializerOptions jsonOptions,
        bool compact)
    {
        var totalMatchingSymbols = filteredSymbols.Count;
        var offset = Math.Min(options.OutlineCursorOffset ?? 0, totalMatchingSymbols);
        var remainingSymbols = offset == 0
            ? filteredSymbols.ToList()
            : filteredSymbols.Skip(offset).ToList();

        if (compact)
        {
            var compactLimit = GetCompactSectionLimit(options);
            var compactOutline = BuildOutlineView(outline, remainingSymbols, totalMatchingSymbols);
            var compactTruncation = ApplyOutlineCompactCaps(compactOutline, compactLimit);
            var payload = JsonSerializer.SerializeToNode(compactOutline, CliJsonSerializerContextFactory.Create(jsonOptions).OutlineResult)!.AsObject();
            AddOutlinePagingJsonFields(payload, kindFilters, totalMatchingSymbols, offset, compactOutline.Symbols.Count, jsonOptions);
            ApplyOutlineFieldSelection(payload, compactOutline.Symbols, options, jsonOptions);
            AddCompactJsonFields(payload, compactLimit, compactTruncation);
            return payload;
        }

        var shouldPage = options.LimitExplicit || options.OutlineCursorOffset.HasValue;
        var pageSymbols = shouldPage
            ? remainingSymbols.Take(options.Limit).ToList()
            : remainingSymbols;
        var pagedOutline = BuildOutlineView(outline, pageSymbols, totalMatchingSymbols);
        var pagedPayload = JsonSerializer.SerializeToNode(pagedOutline, CliJsonSerializerContextFactory.Create(jsonOptions).OutlineResult)!.AsObject();
        AddOutlinePagingJsonFields(pagedPayload, kindFilters, totalMatchingSymbols, offset, pageSymbols.Count, jsonOptions);
        ApplyOutlineFieldSelection(pagedPayload, pageSymbols, options, jsonOptions);
        return pagedPayload;
    }

    private static OutlineResult BuildOutlineView(OutlineResult outline, List<OutlineSymbol> symbols, int symbolCount)
        => new()
        {
            Path = outline.Path,
            Lang = outline.Lang,
            TotalLines = outline.TotalLines,
            SymbolCount = symbolCount,
            Symbols = symbols,
        };

    private static void AddOutlinePagingJsonFields(
        JsonObject payload,
        IReadOnlyList<string> kindFilters,
        int totalSymbolCount,
        int offset,
        int returnedSymbolCount,
        JsonSerializerOptions jsonOptions)
    {
        var nextOffset = offset + returnedSymbolCount;
        var hasMore = nextOffset < totalSymbolCount;
        payload["total_symbol_count"] = totalSymbolCount;
        payload["returned_symbol_count"] = returnedSymbolCount;
        payload["cursor_offset"] = offset;
        payload["next_cursor"] = hasMore ? JsonValue.Create(FormatOutlineCursor(nextOffset)) : null;
        payload["has_more"] = hasMore;
        if (kindFilters.Count > 0)
            payload["kind_filter"] = JsonSerializer.SerializeToNode(kindFilters.ToList(), CliJsonSerializerContextFactory.Create(jsonOptions).ListString);
    }

    private static void ApplyOutlineFieldSelection(
        JsonObject payload,
        IReadOnlyList<OutlineSymbol> symbols,
        QueryCommandOptions options,
        JsonSerializerOptions jsonOptions)
    {
        if (!options.OutlineFieldsExplicit)
            return;

        if (options.OutlineFields == null)
        {
            payload["selected_fields"] = JsonSerializer.SerializeToNode(new List<string> { "all" }, CliJsonSerializerContextFactory.Create(jsonOptions).ListString);
            return;
        }

        payload["selected_fields"] = JsonSerializer.SerializeToNode(options.OutlineFields, CliJsonSerializerContextFactory.Create(jsonOptions).ListString);
        var projectedSymbols = new JsonArray();
        foreach (var symbol in symbols)
            projectedSymbols.Add(BuildProjectedOutlineSymbol(symbol, options.OutlineFields));
        payload["symbols"] = projectedSymbols;
    }

    private static JsonObject BuildProjectedOutlineSymbol(OutlineSymbol symbol, IReadOnlyList<string> fields)
    {
        var payload = new JsonObject();
        foreach (var field in fields)
        {
            switch (field)
            {
                case "kind":
                    payload["kind"] = symbol.Kind;
                    break;
                case "name":
                    payload["name"] = symbol.Name;
                    break;
                case "display_name":
                    payload["display_name"] = symbol.DisplayName;
                    break;
                case "path":
                    payload["path"] = symbol.Path;
                    break;
                case "line":
                    payload["line"] = symbol.Line;
                    break;
                case "start_line":
                    payload["start_line"] = symbol.StartLine;
                    break;
                case "end_line":
                    payload["end_line"] = symbol.EndLine;
                    break;
                case "depth":
                    payload["depth"] = symbol.Depth;
                    break;
                case "body_start_line":
                    payload["body_start_line"] = symbol.BodyStartLine;
                    break;
                case "body_end_line":
                    payload["body_end_line"] = symbol.BodyEndLine;
                    break;
                case "signature":
                    payload["signature"] = symbol.Signature;
                    break;
                case "signature_truncated":
                    payload["signature_truncated"] = symbol.SignatureTruncated;
                    break;
                case "signature_original_length":
                    payload["signature_original_length"] = symbol.SignatureOriginalLength;
                    break;
                case "container_kind":
                    payload["container_kind"] = symbol.ContainerKind;
                    break;
                case "container_name":
                    payload["container_name"] = symbol.ContainerName;
                    break;
                case "visibility":
                    payload["visibility"] = symbol.Visibility;
                    break;
                case "return_type":
                    payload["return_type"] = symbol.ReturnType;
                    break;
            }
        }
        return payload;
    }

    private static JsonObject BuildCompactTruncationMetadata(int sectionLimit, JsonObject sections)
        => new()
        {
            ["section_limit"] = sectionLimit,
            ["sections"] = sections,
        };

    private static void AddCompactJsonFields(JsonObject payload, int compactLimit, JsonObject truncation)
    {
        payload["compact"] = true;
        payload["compact_limit"] = compactLimit;
        payload["truncation"] = truncation;
    }

    private static void TruncateCompactSection<T>(List<T> items, int sectionLimit, JsonObject sections, string sectionName)
    {
        var sourceCount = items.Count;
        if (sourceCount > sectionLimit)
            items.RemoveRange(sectionLimit, sourceCount - sectionLimit);

        sections[sectionName] = new JsonObject
        {
            ["returned"] = items.Count,
            ["source_count"] = sourceCount,
            ["truncated"] = sourceCount > sectionLimit,
        };
    }

    private static void ApplyInspectFieldSelection(JsonObject payload, QueryCommandOptions options, JsonSerializerOptions jsonOptions)
    {
        if (options.InspectFields == null)
            return;

        payload["selected_fields"] = JsonSerializer.SerializeToNode(options.InspectFields, CliJsonSerializerContextFactory.Create(jsonOptions).ListString);
        var keep = new HashSet<string>(StringComparer.Ordinal)
        {
            "api_version",
            "query",
            "selected_fields",
        };

        foreach (var field in options.InspectFields)
            AddInspectFieldProperties(keep, field);

        if (options.Compact)
        {
            keep.Add("compact");
            keep.Add("compact_limit");
            keep.Add("truncation");
            FilterInspectCompactTruncationSections(payload, options.InspectFields);
        }

        foreach (var propertyName in payload.Select(property => property.Key).Where(key => !keep.Contains(key)).ToList())
            payload.Remove(propertyName);
    }

    private static void AddInspectFieldProperties(HashSet<string> keep, string field)
    {
        switch (field)
        {
            case "file":
                keep.Add("file");
                break;
            case "workspace":
                keep.Add("workspace_indexed_at");
                keep.Add("workspace_latest_modified");
                keep.Add("project_root");
                keep.Add("git_head");
                keep.Add("git_is_dirty");
                keep.Add("indexed_head_commit");
                keep.Add("worktree_head_changed");
                break;
            case "graph":
                keep.Add("graph_language");
                keep.Add("graph_supported");
                keep.Add("graph_support_reason");
                keep.Add("graph_degraded");
                keep.Add("unsupported_symbol_kind");
                keep.Add("graph_table_available");
                keep.Add("sql_graph_contract_ready");
                keep.Add("sql_graph_contract_degraded_reason");
                keep.Add("exact_zero_hint");
                keep.Add("exact_index_available");
                keep.Add("degraded");
                keep.Add("degraded_reason");
                break;
            case "definitions":
                keep.Add("definitions");
                break;
            case "source_excerpt":
                keep.Add("source_excerpt");
                break;
            case "nearby_symbols":
                keep.Add("nearby_symbols");
                break;
            case "references":
                keep.Add("references");
                break;
            case "callers":
                keep.Add("callers");
                break;
            case "callees":
                keep.Add("callees");
                break;
        }
    }

    private static void FilterInspectCompactTruncationSections(JsonObject payload, IReadOnlyCollection<string> inspectFields)
    {
        if (!payload.TryGetPropertyValue("truncation", out var truncationNode)
            || truncationNode is not JsonObject truncation
            || !truncation.TryGetPropertyValue("sections", out var sectionsNode)
            || sectionsNode is not JsonObject sections)
        {
            return;
        }

        var keepSections = inspectFields
            .Where(IsInspectListField)
            .ToHashSet(StringComparer.Ordinal);
        foreach (var sectionName in sections.Select(section => section.Key).Where(section => !keepSections.Contains(section)).ToList())
            sections.Remove(sectionName);
    }

    private static bool IsInspectListField(string field)
        => field is "definitions" or "nearby_symbols" or "references" or "callers" or "callees";

    private static void AddInspectBodyModeJsonFields(JsonObject payload, QueryCommandOptions options, SymbolAnalysisResult analysis)
    {
        var bodyContentPresent = analysis.Definitions.Any(definition => definition.BodyContent != null);
        var bodyContentTruncated = analysis.Definitions.Any(definition => definition.BodyContentTruncated);
        var nextStartLine = analysis.Definitions
            .Where(definition => definition.BodyContentNextStartLine.HasValue)
            .Select(definition => definition.BodyContentNextStartLine!.Value)
            .DefaultIfEmpty()
            .Min();

        var bodyMode = new JsonObject
        {
            ["include_body"] = options.IncludeBody,
            ["definitions_only"] = IsInspectDefinitionsOnlyMode(options),
            ["body_content_present"] = bodyContentPresent,
            ["body_content_truncated"] = bodyContentTruncated,
            ["default_body_lines"] = DbReader.DefinitionBodyMaxLines,
            ["max_body_lines"] = DbReader.DefinitionBodyMaxRequestedLines,
            ["hint"] = BuildInspectBodyModeHint(options, bodyContentPresent, bodyContentTruncated),
        };
        if (options.BodyStartLine.HasValue)
            bodyMode["body_start_line"] = options.BodyStartLine.Value;
        if (options.BodyLines.HasValue)
            bodyMode["body_lines"] = options.BodyLines.Value;
        else if (options.IncludeBody)
            bodyMode["body_lines"] = DbReader.DefinitionBodyMaxLines;
        if (nextStartLine > 0)
            bodyMode["next_body_start_line"] = nextStartLine;

        payload["body_mode"] = bodyMode;
    }

    private static void WriteInspectBodyModeHint(SymbolAnalysisResult analysis, QueryCommandOptions options)
    {
        if (analysis.Definitions.Count == 0)
            return;

        var bodyContentPresent = analysis.Definitions.Any(definition => definition.BodyContent != null);
        var bodyContentTruncated = analysis.Definitions.Any(definition => definition.BodyContentTruncated);
        Console.WriteLine($"Body Hint           : {BuildInspectBodyModeHint(options, bodyContentPresent, bodyContentTruncated)}");
    }

    private static bool IsInspectDefinitionsOnlyMode(QueryCommandOptions options)
        => options.IncludeBody
            && options.InspectFields is { Count: 1 } fields
            && string.Equals(fields[0], "definitions", StringComparison.Ordinal);

    private static string BuildInspectBodyModeHint(QueryCommandOptions options, bool bodyContentPresent, bool bodyContentTruncated)
    {
        if (!options.IncludeBody)
            return "Add `--body` for definition body snippets in JSON, or use `--body-only` for body-focused JSON. Page long bodies with `--body-start <line> --body-lines <n>`.";

        if (!options.Json)
            return "Body content was requested, but human inspect output stays summary-only; use `--json --fields body` or `--body-only` to show `body_content`.";

        if (bodyContentTruncated)
            return "Use each definition's `body_content_next_start_line` with `--body-start <line>` and optionally `--body-lines <n>` to fetch the next body slice.";

        if (bodyContentPresent)
            return "Body content is present under each definition's `body_content` field.";

        return "No definition body content is available for the matched definitions.";
    }

    public static int RunInspect(string[] cmdArgs, JsonSerializerOptions jsonOptions)
    {
        var previewOptionError = ValidatePreviewOptions("inspect", cmdArgs, allowMaxLineWidth: true, allowFocusOptions: false);
        if (previewOptionError != null)
        {
            CommandErrorWriter.WriteStderr(previewOptionError);
            return CommandExitCodes.UsageError;
        }
        var options = ParseArgs(
            cmdArgs,
            jsonDefault: false,
            allowNamedQuery: true,
            validateDefaultSnippetLines: false);
        if (TryWriteUnsupportedOptionError("inspect", cmdArgs, CliFlagSchema.GetAcceptedFlagNamesForCommand("inspect"), options.Query))
            return CommandExitCodes.UsageError;
        if (TryWriteParseError(options, "inspect"))
            return CommandExitCodes.UsageError;
        if (TryWriteUnsupportedOutputFormat("inspect", options, InspectOutputFormats, "Use `--format json` or `--format compact` for inspect bundles; count output is not meaningful for one inspect bundle."))
            return CommandExitCodes.UsageError;
        if (!TryResolveNameExactMode(options, "inspect", out var exact, out var exactError))
        {
            CommandErrorWriter.WriteStderr(exactError);
            return CommandExitCodes.UsageError;
        }
        var pathLineInspectMode = IsInspectPathLineMode(options);
        if (!pathLineInspectMode && TryWriteBlankQueryError(options, "inspect"))
            return CommandExitCodes.UsageError;
        if (!pathLineInspectMode && string.IsNullOrWhiteSpace(options.Query))
        {
            WriteUsageError(
                "inspect requires a symbol query argument",
                GetUsageLineOrThrow("inspect"),
                "Add the symbol you want to inspect, for example: `cdidx inspect QueryCommandRunner`, or pass `--path <file> --line <line>` for a source excerpt.");
            return CommandExitCodes.UsageError;
        }
        if (options.Query != null && IsBareVerbatimQueryToken(options.Query))
        {
            WriteUsageError(
                "inspect requires a symbol query argument",
                GetUsageLineOrThrow("inspect"),
                "Add a real symbol name after the command; bare verbatim prefixes like `@` are not valid queries.");
            return CommandExitCodes.UsageError;
        }
        if (TryWriteUnexpectedExtraPositionals("inspect", options))
            return CommandExitCodes.UsageError;
        if (options.StartLine.HasValue && options.EndLine.HasValue && options.EndLine.Value < options.StartLine.Value)
        {
            WriteValidationError(
                $"--start-line ({options.StartLine.Value}) must be less than or equal to --end-line ({options.EndLine.Value}).",
                "Use `--start-line` less than or equal to `--end-line`, or omit `--end-line` to read one line.");
            return CommandExitCodes.UsageError;
        }

        return WithDb(options, jsonOptions, reader =>
        {
            var compactLimit = GetCompactSectionLimit(options);
            var inspectLimit = options.Compact ? GetCompactSourceLimit(compactLimit) : options.Limit;
            var inspectPath = pathLineInspectMode ? GetSingleSpecificPathPattern(options.PathPatterns) : null;
            var inspectQuery = pathLineInspectMode
                ? $"{inspectPath}:{options.StartLine!.Value}"
                : options.Query!;
            var analysis = reader.AnalyzeSymbol(
                inspectQuery,
                inspectLimit,
                options.Lang,
                options.IncludeBody,
                options.PathPatterns,
                options.ExcludePaths,
                options.ExcludeTests,
                exact,
                options.MaxLineWidth,
                options.BodyStartLine,
                options.BodyLines,
                kind: options.Kind);
            var sourceExcerpt = BuildInspectSourceExcerpt(reader, options, analysis, inspectPath);
            var sqlGraphSignal = NarrowSqlGraphContractSignal(
                reader.GetSqlGraphContractSignal(options.Lang, options.PathPatterns, options.ExcludePaths, options.ExcludeTests),
                DbReader.IsSqlLanguage(options.Lang)
                    || DbReader.IsSqlLanguage(analysis.GraphLanguage)
                    || DbReader.IsSqlLanguage(analysis.File?.Lang)
                    || DbReader.ContainsSqlLanguage(analysis.Definitions.Select(definition => definition.Lang))
                    || DbReader.ContainsSqlLanguage(analysis.References.Select(reference => reference.Lang))
                    || DbReader.ContainsSqlLanguage(analysis.Callers.Select(caller => caller.Lang))
                    || DbReader.ContainsSqlLanguage(analysis.Callees.Select(callee => callee.Lang)));
            var exactSignal = exact && analysis.ExactIndexAvailable.HasValue
                ? new ExactQuerySignal(
                    analysis.ExactIndexAvailable.Value,
                    analysis.ExactHasMissingIndex ?? false,
                    analysis.ExactHasMissingTable ?? false,
                    analysis.DegradedReason)
                : (ExactQuerySignal?)null;
            analysis.SqlGraphContractReady = sqlGraphSignal.Relevant ? sqlGraphSignal.Ready : null;
            analysis.SqlGraphContractDegradedReason = sqlGraphSignal.Relevant ? sqlGraphSignal.DegradedReason : null;
            WorkspaceMetadataEnricher.Enrich(analysis, options.DbPath, options.DbPathExplicit);
            if (exactSignal.HasValue)
                WriteExactBundleWarningIfNeeded(exact, options.Json, exactSignal.Value, reader, options);
            WriteSqlGraphContractWarningIfNeeded(options.Json, sqlGraphSignal, reader, options);
            if (options.Json)
            {
                var compactTruncation = options.Compact ? ApplySymbolAnalysisCompactCaps(analysis, compactLimit) : null;
                ApplyBodyRecoveryCommands(analysis, options.DbPath);
                var payload = JsonSerializer.SerializeToNode(analysis, CliJsonSerializerContextFactory.Create(jsonOptions).SymbolAnalysisResult)!.AsObject();
                AddSqlGraphContractJsonFields(payload, sqlGraphSignal);
                if (compactTruncation != null)
                    AddCompactJsonFields(payload, compactLimit, compactTruncation);
                if (sourceExcerpt != null)
                {
                    ExcerptRecoveryCommandFormatter.ApplyDbPath(sourceExcerpt, options.DbPath);
                    sourceExcerpt.SemanticTokens = BuildExcerptSemanticTokens(sourceExcerpt);
                    payload["source_excerpt"] = JsonSerializer.SerializeToNode(sourceExcerpt, CliJsonSerializerContextFactory.Create(jsonOptions).FileExcerptResult);
                }
                ApplyInspectFieldSelection(payload, options, jsonOptions);
                AddInspectBodyModeJsonFields(payload, options, analysis);
                Console.WriteLine(payload.ToJsonString(jsonOptions));
            }
            else
            {
                Console.WriteLine($"Query: {analysis.Query}");
                if (analysis.File != null)
                    Console.WriteLine($"File : {analysis.File.Path} ({analysis.File.Lang ?? "?"}, {analysis.File.Lines} lines)");
                if (analysis.WorkspaceIndexedAt != null)
                    Console.WriteLine($"Workspace Indexed At : {analysis.WorkspaceIndexedAt:O}");
                if (analysis.WorkspaceLatestModified != null)
                    Console.WriteLine($"Workspace Modified   : {analysis.WorkspaceLatestModified:O}");
                if (analysis.GitHead != null)
                    Console.WriteLine($"Git HEAD             : {analysis.GitHead}");
                if (analysis.GitIsDirty != null)
                    Console.WriteLine($"Git Dirty            : {analysis.GitIsDirty}");
                if (analysis.GraphLanguage != null)
                    Console.WriteLine($"Graph Language       : {analysis.GraphLanguage}");
                if (analysis.GraphSupported != null)
                    Console.WriteLine($"Graph Supported      : {analysis.GraphSupported}");
                if (analysis.GraphSupportReason != null)
                    Console.WriteLine($"Graph Note           : {analysis.GraphSupportReason}");
                if (analysis.UnsupportedSymbolKind != null)
                    Console.WriteLine($"Graph Limitation     : unsupported symbol kind '{analysis.UnsupportedSymbolKind}'");
                if (!analysis.GraphTableAvailable)
                    Console.WriteLine("Graph Table          : MISSING — empty References/Callers/Callees are degraded, NOT real zero-hit results.");
                if (exactSignal is ExactQuerySignal signal && !signal.ExactIndexAvailable && signal.DegradedReason != null)
                {
                    if (signal.HasMissingIndex)
                        Console.WriteLine($"Exact Index          : DEGRADED — {signal.DegradedReason}. Results are correct but may be slow.");
                    else if (IsCSharpCanonicalNameSignal(signal))
                    {
                        Console.WriteLine($"Exact Index          : DEGRADED — {signal.DegradedReason}. Exact-name C# operator / indexer matches may be incomplete.");
                        Console.WriteLine($"Hint                 : Run `{BuildCSharpCanonicalNameRepairCommand(reader, options)}`.");
                    }
                }
                WriteExactZeroHint(analysis.ExactZeroHint);
                WriteInspectBodyModeHint(analysis, options);
                if (sourceExcerpt != null)
                {
                    Console.WriteLine($"Source Excerpt      : {sourceExcerpt.Path}:{sourceExcerpt.StartLine}-{sourceExcerpt.EndLine}");
                    WriteNumberedExcerpt(sourceExcerpt.StartLine, sourceExcerpt.Content);
                }
                WriteRepoMapSection("Definitions", analysis.Definitions.Select(item => $"{item.Kind,-10} {item.Name,-24} {item.Path}:{item.StartLine}-{item.EndLine}"));
                WriteRepoMapSection("Nearby symbols", analysis.NearbySymbols.Select(item => $"{item.Kind,-10} {item.Name,-24} {item.Path}:{item.StartLine}-{item.EndLine}"));
                WriteRepoMapSection("References", analysis.References.Select(item => $"{item.Path}:{item.Line}:{item.Column}  {item.Context}"));
                WriteRepoMapSection("Callers", analysis.Callers.Select(item => $"{item.CallerName ?? "<top-level>"} -> {item.CalleeName}  ({item.ReferenceCount} refs)"));
                WriteRepoMapSection("Callees", analysis.Callees.Select(item => $"{item.CallerName ?? "<top-level>"} -> {item.CalleeName}  ({item.ReferenceCount} refs)"));
            }

            return IsEmptySymbolAnalysis(analysis) && sourceExcerpt == null ? ZeroResultExitCode(options) : CommandExitCodes.Success;
        });
    }

    private static bool IsInspectPathLineMode(QueryCommandOptions options)
        => options.Query == null
            && options.StartLine.HasValue
            && GetSingleSpecificPathPattern(options.PathPatterns) != null;

    private static bool IsInspectSourceExcerptRequested(QueryCommandOptions options)
        => options.StartLine.HasValue
            || options.EndLine.HasValue
            || options.ContextBefore > 0
            || options.ContextAfter > 0;

    private static FileExcerptResult? BuildInspectSourceExcerpt(
        DbReader reader,
        QueryCommandOptions options,
        SymbolAnalysisResult analysis,
        string? inspectPath)
    {
        if (!IsInspectSourceExcerptRequested(options))
            return null;

        var definition = analysis.Definitions.FirstOrDefault();
        var path = inspectPath
            ?? GetSingleSpecificPathPattern(options.PathPatterns)
            ?? definition?.Path
            ?? analysis.File?.Path;
        if (path == null)
            return null;

        var startLine = options.StartLine ?? definition?.StartLine ?? 1;
        var endLine = options.EndLine ?? options.StartLine ?? definition?.EndLine ?? startLine;
        return reader.GetExcerpt(
            path,
            startLine,
            endLine,
            options.ContextBefore,
            options.ContextAfter,
            options.MaxLineWidth,
            options.StartLine ?? startLine);
    }

    private static string? GetSingleSpecificPathPattern(IReadOnlyList<string> pathPatterns)
    {
        if (pathPatterns.Count != 1)
            return null;

        var path = pathPatterns[0];
        return ContainsGlobWildcard(path) ? null : path;
    }

    private static bool ContainsGlobWildcard(string value)
        => value.IndexOfAny(['*', '?', '[', ']']) >= 0;

    public static int RunOutline(string[] cmdArgs, JsonSerializerOptions jsonOptions)
    {
        if (cmdArgs.Length == 0 || cmdArgs[0].StartsWith('-'))
        {
            WriteUsageError(
                "outline requires a file path.",
                GetUsageLineOrThrow("outline"),
                "Pass the indexed file path, for example: `cdidx outline src/CodeIndex/Program.cs`.");
            return CommandExitCodes.UsageError;
        }

        var previewOptionError = ValidatePreviewOptions("outline", cmdArgs[1..], allowMaxLineWidth: false, allowFocusOptions: false);
        if (previewOptionError != null)
        {
            CommandErrorWriter.WriteStderr(previewOptionError);
            return CommandExitCodes.UsageError;
        }
        var options = ParseArgs(
            cmdArgs[1..],
            jsonDefault: false,
            validateDefaultLimit: false,
            validateDefaultSnippetLines: false,
            validateDefaultMaxLineWidth: false);
        if (TryWriteUnsupportedOptionError("outline", cmdArgs[1..], CliFlagSchema.GetAcceptedFlagNamesForCommand("outline")))
            return CommandExitCodes.UsageError;
        if (TryWriteParseError(options, "outline"))
            return CommandExitCodes.UsageError;
        if (TryWriteInvalidOutlineKindFilterError(options))
            return CommandExitCodes.InvalidArgument;
        if (TryWriteUnexpectedPositionals("outline", options))
            return CommandExitCodes.UsageError;
        if (options.SearchCursor.HasValue || options.UnusedCursorOffset.HasValue)
        {
            WriteUsageError(
                "outline --cursor must use an outline pagination cursor.",
                GetUsageLineOrThrow("outline"),
                "Use the `next_cursor` value returned by `cdidx outline <path> --json --limit <n>`.");
            return CommandExitCodes.UsageError;
        }

        var filePath = DbPathResolver.ResolveQueryFilePath(options.DbPath, cmdArgs[0], options.DbPathExplicit);
        return WithDb(options, jsonOptions, reader =>
        {
            var outline = reader.GetOutline(filePath);
            if (outline == null)
            {
                if (options.Json)
                    Console.WriteLine(JsonSerializer.Serialize(new QueryPathErrorJsonResult(filePath, "file not found in index"), CliJsonSerializerContextFactory.Create(jsonOptions).QueryPathErrorJsonResult));
                else
                    CommandErrorWriter.WriteStderr($"Error: '{filePath}' not found in index.");
                return CommandExitCodes.NotFound;
            }

            var kindFilters = BuildOutlineKindFilters(options.Kind);
            var filteredSymbols = ApplyOutlineKindFilters(outline.Symbols, kindFilters);
            if (options.Json)
            {
                if (options.Compact)
                {
                    var payload = BuildOutlineJsonPayload(outline, filteredSymbols, kindFilters, options, jsonOptions, compact: true);
                    Console.WriteLine(payload.ToJsonString(jsonOptions));
                }
                else if (HasOutlineJsonControls(options, kindFilters))
                {
                    var payload = BuildOutlineJsonPayload(outline, filteredSymbols, kindFilters, options, jsonOptions, compact: false);
                    Console.WriteLine(payload.ToJsonString(jsonOptions));
                }
                else
                {
                    Console.WriteLine(JsonSerializer.Serialize(outline, CliJsonSerializerContextFactory.Create(jsonOptions).OutlineResult));
                }
            }
            else
            {
                var outlineContent = reader.GetExcerpt(filePath, 1, outline.TotalLines)?.Content;

                Console.WriteLine($"# {outline.Path}  ({outline.Lang ?? "unknown"}, {outline.TotalLines} lines, {filteredSymbols.Count} symbols)");
                Console.WriteLine();
                var duplicateNames = filteredSymbols
                    .GroupBy(sym => sym.Name, StringComparer.Ordinal)
                    .Where(group => group.Count() > 1)
                    .Select(group => group.Key)
                    .ToHashSet(StringComparer.Ordinal);
                var displaySymbols = ApplyOutlineHumanPaging(filteredSymbols, options);
                foreach (var sym in displaySymbols)
                {
                    // Indent nested symbols by computed tree depth / コンテナ連鎖の深さでインデント
                    var indent = sym.Depth > 0 ? new string(' ', 4 * sym.Depth) : "";
                    var useDisplayName = sym.Kind is "function" or "method" or "constructor"
                        && duplicateNames.Contains(sym.Name)
                        && !string.IsNullOrWhiteSpace(sym.DisplayName);
                    var ret = !useDisplayName && sym.ReturnType != null ? $": {sym.ReturnType} " : "";
                    var sig = useDisplayName ? sym.DisplayName : sym.Signature ?? $"{sym.Kind} {sym.Name}";
                    // Avoid duplicating visibility when signature already contains it
                    // シグネチャに既に visibility が含まれている場合は重複を避ける
                    var vis = !useDisplayName && sym.Visibility != null && !sig.TrimStart().StartsWith(sym.Visibility, StringComparison.Ordinal)
                        ? $"{sym.Visibility} "
                        : "";
                    Console.WriteLine($"  {sym.Line,5}  {indent}{vis}{sig} {ret}");
                }

                // AI-orientation hint for C# files that look like top-level-statements programs:
                // no class / struct / interface / enum / namespace / record / delegate at all
                // means the executable body lives between the imports and local functions and
                // will not appear in outline at all. Emitting a short note on stderr keeps the
                // main human-readable block clean while giving AI consumers a reason for the gap.
                // AI向けヒント: C# のトップレベルステートメント想定のファイル
                // （class / struct / interface / enum / namespace / record / delegate が一切無い）は、
                // 実行本体が import と local function の間に書かれるため outline に現れない。
                // 人間向け本体を汚さないよう、理由を短く stderr に出す。
                if (LooksLikeCsharpTopLevelStatements(outline, outlineContent))
                {
                    CommandErrorWriter.WriteStderr();
                    CommandErrorWriter.WriteStderr("Note: no type/namespace declarations found; this file likely uses C# top-level statements.");
                    CommandErrorWriter.WriteStderr("      Outline lists imports and local functions only; the executable body is not indexed as symbols.");
                }
            }
            return CommandExitCodes.Success;
        });
    }

    /// <summary>
    /// Heuristic: hint only when a non-trivial C# file has no type/namespace declarations and
    /// its reconstructed content still contains uncovered file-scope executable code after
    /// skipping symbol-covered lines, imports, metadata-only attribute lines, comments, and
    /// preprocessor directives. This keeps the note off common files such as GlobalUsings.cs,
    /// AssemblyInfo.cs, and local-function-only files while preserving statement-only Program.cs
    /// files.
    /// Tiny files (snippets, partials under ~20 lines) are excluded to avoid noise.
    /// ヒューリスティック: 20 行以上の C# ファイルで型/名前空間宣言が無く、かつ
    /// import 行、metadata-only 属性行、コメント、プリプロセッサ行を除いても
    /// file-scope の実行コードが残る場合だけヒントを出す。これにより GlobalUsings.cs や
    /// AssemblyInfo.cs の誤検出を避けつつ、
    /// statement-only の Program.cs は拾い続ける。小さい断片はノイズ回避のため除外。
    /// </summary>
    private static bool LooksLikeCsharpTopLevelStatements(OutlineResult outline, string? content)
    {
        if (outline.Lang != "csharp") return false;
        if (outline.TotalLines < 20) return false;
        foreach (var sym in outline.Symbols)
        {
            if (sym.Kind is "class" or "struct" or "interface" or "enum" or "namespace" or "delegate" or "record")
                return false;
        }

        if (string.IsNullOrWhiteSpace(content))
            return false;

        var coveredLines = new bool[Math.Max(outline.TotalLines, 0) + 1];
        foreach (var sym in outline.Symbols)
        {
            var startLine = sym.StartLine > 0 ? sym.StartLine : sym.Line;
            var endLine = sym.EndLine >= startLine ? sym.EndLine : startLine;
            startLine = Math.Max(1, startLine);
            endLine = Math.Min(outline.TotalLines, endLine);
            for (var lineNumber = startLine; lineNumber <= endLine; lineNumber++)
                coveredLines[lineNumber] = true;
        }

        var inBlockComment = false;
        var currentLineNumber = 0;
        foreach (var rawLine in content.Split('\n'))
        {
            currentLineNumber++;
            var line = rawLine.Trim();
            if (line.Length == 0)
                continue;
            if (currentLineNumber < coveredLines.Length && coveredLines[currentLineNumber])
                continue;

            if (inBlockComment)
            {
                if (line.Contains("*/", StringComparison.Ordinal))
                    inBlockComment = false;
                continue;
            }

            if (line.StartsWith("/*", StringComparison.Ordinal))
            {
                if (!line.Contains("*/", StringComparison.Ordinal))
                    inBlockComment = true;
                continue;
            }

            if (line.StartsWith("using ", StringComparison.Ordinal))
            {
                if (line.StartsWith("using var ", StringComparison.Ordinal))
                    return true;
                if (line.StartsWith("using (", StringComparison.Ordinal))
                    return true;
                continue;
            }
            if (line.StartsWith("global using ", StringComparison.Ordinal))
                continue;
            if (line.StartsWith("extern alias ", StringComparison.Ordinal))
                continue;
            if (line.StartsWith("[assembly:", StringComparison.Ordinal))
                continue;
            if (line.StartsWith("[module:", StringComparison.Ordinal))
                continue;
            if (line.StartsWith("//", StringComparison.Ordinal))
                continue;
            if (line.StartsWith("*", StringComparison.Ordinal))
                continue;
            if (line.StartsWith("*/", StringComparison.Ordinal))
                continue;
            if (line.StartsWith("#", StringComparison.Ordinal))
                continue;
            return true;
        }

        return false;
    }

    public static int RunStatus(
        string[] cmdArgs,
        JsonSerializerOptions jsonOptions,
        string? appVersion = null,
        CancellationToken cancellationToken = default)
    {
        var checkUpdates = cmdArgs.Contains("--check-updates", StringComparer.Ordinal);
        if (checkUpdates)
            cmdArgs = cmdArgs.Where(arg => !string.Equals(arg, "--check-updates", StringComparison.Ordinal)).ToArray();
        var previewOptionError = ValidatePreviewOptions("status", cmdArgs, allowMaxLineWidth: false, allowFocusOptions: false);
        if (previewOptionError != null)
        {
            CommandErrorWriter.WriteStderr(previewOptionError);
            return CommandExitCodes.UsageError;
        }
        var options = ParseArgs(
            cmdArgs,
            jsonDefault: false,
            allowStatusCheck: true,
            validateDefaultLimit: false,
            validateDefaultSnippetLines: false,
            validateDefaultMaxLineWidth: false);
        if (TryWriteUnsupportedOptionError("status", cmdArgs, CliFlagSchema.GetAcceptedFlagNamesForCommand("status")))
            return CommandExitCodes.UsageError;
        if (TryWriteParseError(options, "status"))
            return CommandExitCodes.UsageError;
        if (TryWriteUnexpectedPositionals("status", options))
            return CommandExitCodes.UsageError;
        if (options.StatusConfig)
        {
            if (options.CheckWorkspace || options.StatusLogPath || options.StatusExplainField != null)
            {
                CommandErrorWriter.WriteStderr("Error: status --config cannot be combined with --check, --log-path, or --explain.");
                return CommandExitCodes.UsageError;
            }

            Console.WriteLine(BuildEffectiveConfigJson(options, cmdArgs, appVersion).ToJsonString(jsonOptions));
            return CommandExitCodes.Success;
        }
        if (options.StatusLogPath)
        {
            if (options.CheckWorkspace)
            {
                CommandErrorWriter.WriteStderr("Error: status --log-path cannot be combined with --check.");
                return CommandExitCodes.UsageError;
            }

            var logPath = GlobalToolLog.ResolveLogDirectoryForStatus();
            if (options.Json)
                Console.WriteLine(JsonSerializer.Serialize(new Dictionary<string, string> { ["log_path"] = logPath }, jsonOptions));
            else
                Console.WriteLine(logPath);
            return CommandExitCodes.Success;
        }
        if (options.StatusExplainField != null)
        {
            if (options.Json)
                return WriteStatusReadinessExplanationJson(options.StatusExplainField);
            return WriteStatusReadinessExplanation(options.StatusExplainField);
        }

        return WithDb(options, jsonOptions, reader =>
        {
            var staleAfter = (Value: DefaultStaleAfter, Error: (string?)null);
            if (options.CheckWorkspace || options.StaleAfter.HasValue)
            {
                staleAfter = ResolveStaleAfter(options, CdidxEnvironment.GetEnvironmentVariable(StaleAfterEnvironmentVariable));
                if (staleAfter.Error != null)
                {
                    CommandErrorWriter.WriteStderr(staleAfter.Error);
                    return CommandExitCodes.UsageError;
                }
            }

            var status = reader.GetStatus();
            WorkspaceMetadataEnricher.Enrich(status, options.DbPath, options.DbPathExplicit, cancellationToken);
            status.DataDir = options.DataDir;
            status.DataDirSource = options.DataDirSource;
            status.DataDirMode = DataDirectorySecurity.GetUnixModeString(GetDataDirectoryPath(options.DbPath));
            status.DbFileMode = DbContext.GetUnixFileModeString(options.DbPath);
            var macProfile = MacProfileDetector.DetectCurrentWithDiagnostics();
            status.MacProfile = macProfile.Profile;
            if (macProfile.Diagnostics.Count > 0)
                status.MacProfileDiagnostics = macProfile.Diagnostics.ToList();
            if (options.CheckWorkspace)
            {
                status.WorkspaceCheck = IndexFreshnessChecker.Check(reader, status.ProjectRoot, cancellationToken);
                status.IndexMatchesWorkspace = status.WorkspaceCheck.Checked
                    ? status.WorkspaceCheck.MatchesWorkspace
                    : null;
                status.StaleAfterSeconds = (long)Math.Round(staleAfter.Value.TotalSeconds, MidpointRounding.AwayFromZero);
                if (status.IndexedAt.HasValue)
                    status.IndexAgeSeconds = Math.Max(0, (long)Math.Round((GetUtcNow() - status.IndexedAt.Value).TotalSeconds, MidpointRounding.AwayFromZero));
            }
            // Attach runtime metadata / ランタイムメタデータを付加
            ApplyStatusSymbolKindLimits(status, reader.GetSymbolKindCounts());
            ExtractorPluginRegistry.LoadPatternConfigsForProjectRoot(status.ProjectRoot);
            status.GraphSupportedLanguages = ReferenceExtractor.GetSupportedLanguages().OrderBy(l => l).ToList();
            status.Extractors = ExtractorPluginRegistry.GetStatusSnapshot();
            var postExtractionHookSnapshot = PostExtractionHookRunner.DiscoverDefaultMetadata();
            var postExtractionHooks = postExtractionHookSnapshot.Hooks;
            if (postExtractionHookSnapshot.Diagnostics.Count > 0)
                status.HookDiagnostics = postExtractionHookSnapshot.Diagnostics.ToList();
            var trustOverrides = ExtractorPluginRegistry.GetAcceptedTrustOverrides(status.ProjectRoot)
                .Concat(postExtractionHookSnapshot.TrustOverrides)
                .ToList();
            if (trustOverrides.Count > 0)
                status.TrustOverrides = trustOverrides;
            if (postExtractionHooks.Count > 0)
            {
                status.Hooks = postExtractionHooks
                    .Select(hook => new PostExtractionHookStatus
                    {
                        Name = hook.Name,
                        AssemblyPath = hook.AssemblyPath,
                        TypeName = hook.TypeName,
                        CallbackBudgetMs = (long)Math.Round(postExtractionHookSnapshot.CallbackBudget.TotalMilliseconds, MidpointRounding.AwayFromZero),
                    })
                    .ToList();
            }
            if (appVersion != null)
                status.Version = appVersion;
            var updateResult = checkUpdates && appVersion != null
                ? UpdateChecker.Check(appVersion, cancellationToken)
                : null;
            status.UpdateCheck = updateResult;

            // Build one-line summary for AI orientation / AI向けの1行サマリーを構築
            var topLangs = status.Languages.OrderByDescending(kv => kv.Value).Take(3).Select(kv => kv.Key);
            var freshness = BuildStatusFreshnessLabel(status);
            var dirty = status.GitIsDirty == true ? ", dirty" : "";
            ApplyStatusDegradationGuidance(status, options);

            var degraded = IsStatusDegraded(status)
                ? ", DEGRADED"
                : "";
            status.Summary = $"{status.Files} files, {status.Symbols} symbols, {status.References} refs across {status.Languages.Count} languages ({string.Join(", ", topLangs)}); index {freshness}{dirty}{degraded}";

            IReadOnlyList<StatusCheckFailure> checkFailures = options.CheckWorkspace
                ? BuildStatusCheckFailures(status, options.StatusCheckScopes)
                : Array.Empty<StatusCheckFailure>();
            if (options.CheckWorkspace)
            {
                status.FailedChecks = checkFailures.Select(f => f.Name).ToList();
                status.RepairCommands = BuildStatusRepairCommands(status, checkFailures, options);
            }

            if (options.Json)
            {
                Console.WriteLine(JsonSerializer.Serialize(
                    status,
                    CliJsonSerializerContextFactory.Create(jsonOptions).StatusResult));
            }
            else if (options.CheckWorkspace)
            {
                if (options.StaleAfter.HasValue)
                    WriteStatusAge(status, staleAfter.Value);
                if (checkFailures.Count > 0)
                    WriteStatusCheckDiagnostics(checkFailures);
            }
            else
            {
                if (status.Summary != null)
                    Console.WriteLine(status.Summary);
                Console.WriteLine();
                if (status.Version != null)
                    Console.WriteLine(ConsoleUi.FormatSummaryLine("Version", $"cdidx v{status.Version}"));
                if (updateResult?.UpdateAvailable == true && updateResult.LatestVersion != null)
                    Console.WriteLine(ConsoleUi.FormatSummaryLine("Update", $"cdidx v{updateResult.LatestVersion} is available."));
                Console.WriteLine(ConsoleUi.FormatSummaryLine("Files", $"{status.Files:N0}"));
                Console.WriteLine(ConsoleUi.FormatSummaryLine("Chunks", $"{status.Chunks:N0}"));
                Console.WriteLine(ConsoleUi.FormatSummaryLine("Symbols", $"{status.Symbols:N0}"));
                Console.WriteLine(ConsoleUi.FormatSummaryLine("Refs", $"{status.References:N0}"));
                if (status.IndexedAt != null)
                    Console.WriteLine(ConsoleUi.FormatSummaryLine("Indexed", $"{status.IndexedAt:O}"));
                if (status.LastWorkspaceFreshenedAt != null)
                    Console.WriteLine(ConsoleUi.FormatSummaryLine("Freshened", $"{status.LastWorkspaceFreshenedAt:O}"));
                if (status.LatestModified != null)
                    Console.WriteLine(ConsoleUi.FormatSummaryLine("Source", $"{status.LatestModified:O}"));
                if (status.GitHead != null)
                    Console.WriteLine(ConsoleUi.FormatSummaryLine("Git HEAD", status.GitHead));
                if (status.GitIsDirty != null)
                    Console.WriteLine(ConsoleUi.FormatSummaryLine("Git Dirty", status.GitIsDirty));
                if (status.MacProfile != null)
                    Console.WriteLine(ConsoleUi.FormatSummaryLine("MAC", status.MacProfile));
                // #1509 surface: SHA / branch / timestamp / drift come from the per-success
                // stamp (indexed_head_sha / _branch / _timestamp) and reflect last-touched HEAD
                // regardless of update mode. #1508/#1512's IndexedHeadCommit (full-scan only)
                // is rendered separately below when it disagrees with the runtime GitHead.
                if (status.IndexedHeadSha != null)
                {
                    var branchSuffix = string.IsNullOrWhiteSpace(status.IndexedHeadBranch)
                        ? string.Empty
                        : $" (branch {status.IndexedHeadBranch})";
                    Console.WriteLine(ConsoleUi.FormatSummaryLine("Idx HEAD", $"{status.IndexedHeadSha}{branchSuffix}"));
                }
                else if (status.IndexedHeadCommit != null && !string.Equals(status.IndexedHeadCommit, status.GitHead, StringComparison.OrdinalIgnoreCase))
                {
                    Console.WriteLine(ConsoleUi.FormatSummaryLine("Idx HEAD", status.IndexedHeadCommit));
                }
                if (status.IndexedHeadTimestamp != null)
                    Console.WriteLine(ConsoleUi.FormatSummaryLine("Idx Stamp", $"{status.IndexedHeadTimestamp:O}"));
                if (status.CommitsAheadOfIndexedHead is { } ahead && ahead > 0)
                    Console.WriteLine(ConsoleUi.FormatSummaryLine("Idx Drift", $"workspace is {ConsoleUi.Counted(ahead, "commit")} ahead of indexed HEAD — rerun `cdidx index .` to refresh."));
                if (status.WorkspaceCheck != null)
                {
                    WriteStatusAge(status, staleAfter.Value);
                    WriteWorkspaceCheck(status.WorkspaceCheck);
                }
                if (status.Languages.Count > 0)
                {
                    Console.WriteLine("Languages:");
                    foreach (var (lang, count) in status.Languages)
                        Console.WriteLine($"  {lang,-12} {count,6}");
                }
                if (status.SymbolKinds is { Count: > 0 })
                {
                    Console.WriteLine("Kinds:");
                    foreach (var (kind, count) in status.SymbolKinds)
                        Console.WriteLine($"  {kind,-12} {count,6}");
                    if (status.SymbolKindOmittedCount is > 0)
                    {
                        Console.WriteLine(
                            $"  ... {ConsoleUi.Counted(status.SymbolKindOmittedCount.Value, "kind")} omitted (limit {status.SymbolKindLimit}, names capped at {status.SymbolKindNameLimit} chars)");
                    }
                    else if (status.SymbolKindNamesTruncated == true)
                    {
                        Console.WriteLine($"  ... kind names capped at {status.SymbolKindNameLimit} chars");
                    }
                }
                if (status.GraphSupportedLanguages is { Count: > 0 })
                    Console.WriteLine(ConsoleUi.FormatSummaryLine("Graph", $"{status.GraphSupportedLanguages.Count} languages ({string.Join(", ", status.GraphSupportedLanguages)})"));
                if (status.TrustOverrides is { Count: > 0 })
                {
                    foreach (var trustOverride in status.TrustOverrides)
                    {
                        var pathSuffix = string.IsNullOrWhiteSpace(trustOverride.Path)
                            ? string.Empty
                            : $" ({trustOverride.Path})";
                        Console.WriteLine(ConsoleUi.FormatSummaryLine("Trust", $"{trustOverride.Kind} via {trustOverride.EnvironmentVariable}{pathSuffix}"));
                    }
                }
                // #1546: surface the persisted filesystem case-sensitivity so operators can
                // diagnose phantom path collapses on case-sensitive APFS / WSL / ReFS volumes.
                // #1546: case-sensitivity を診断用に明示する。
                if (status.PathCaseSensitive != null)
                    Console.WriteLine(ConsoleUi.FormatSummaryLine("FS Case", status.PathCaseSensitive == true ? "case-sensitive" : "case-insensitive"));
                WriteStatusReadinessSummary(status, options);
                if (status.WorktreeHeadChanged == true)
                    Console.WriteLine(ConsoleUi.FormatSummaryLine("WARN", $"worktree HEAD changed since the index was built ({ShortSha(status.IndexedHeadCommit)} -> {ShortSha(status.GitHead)}). Run `{BuildReindexRepairCommand(status.ProjectRoot, options.DbPath, options.DbPathExplicit)}` to refresh the index for the current branch."));
                if (status.IndexNewerThanReader)
                {
                    var reason = status.IndexNewerThanReaderReason ?? "DB was written by a newer cdidx than this binary.";
                    var writerLabel = status.IndexWriterVersion is { Length: > 0 } writerVersion
                        ? $" (DB writer: cdidx v{writerVersion}; reader: cdidx v{status.Version ?? "unknown"})"
                        : status.Version is { Length: > 0 } readerVersion
                            ? $" (reader: cdidx v{readerVersion})"
                            : "";
                    Console.WriteLine(ConsoleUi.FormatSummaryLine("WARN", $"{reason}{writerLabel}"));
                }
                if (!status.GraphTableAvailable)
                    Console.WriteLine(ConsoleUi.FormatSummaryLine("WARN", "symbol_references table missing — reference / caller / callee / unused counts are degraded to 0."));
                if (!status.IssuesTableAvailable)
                    Console.WriteLine(ConsoleUi.FormatSummaryLine("WARN", "file_issues table missing — validate output is degraded to empty."));
                else if (!status.FileIssuesDataCurrent)
                    Console.WriteLine(ConsoleUi.FormatSummaryLine("WARN", "file_issues table exists but its rows are not stamped current for this index generation."));
                if (!status.SqlGraphContractReady)
                    Console.WriteLine(ConsoleUi.FormatSummaryLine("WARN", $"SQL graph/dependency results may be stale. Run `{BuildSqlGraphContractRepairCommand(status.ProjectRoot, options.DbPath, options.DbPathExplicit)}` before trusting SQL references/callers/deps/unused/hotspots."));
                if (!status.HotspotFamilyReady && status.HotspotFamilyDegradedReason != null)
                {
                    Console.WriteLine(ConsoleUi.FormatSummaryLine("WARN", status.HotspotFamilyDegradedReason));
                    Console.WriteLine(ConsoleUi.FormatSummaryLine("Hint", "rerun `cdidx index <projectPath>` to restore authoritative cross-file hotspot families."));
                }
                if (!status.CSharpSymbolNameReady)
                    Console.WriteLine(ConsoleUi.FormatSummaryLine("WARN", $"C# exact-name for operators / conversion operators / indexers is degraded. Run `{BuildCSharpCanonicalNameRepairCommand(status.ProjectRoot, options.DbPath, options.DbPathExplicit)}` to upgrade canonical symbol names in place."));
                // #435: tell the user when deps / impact metadata-attribute edges fall back
                // to the legacy signature / name-suffix heuristic (impostor classes may be
                // silently promoted or demoted until the authoritative resolver is re-run).
                // #435: deps / impact の metadata-attribute edge が legacy heuristic に
                // 縮退しているときは明示する。
                if (!status.CSharpMetadataTargetReady)
                    Console.WriteLine(ConsoleUi.FormatSummaryLine("WARN", "C# deps / impact metadata-attribute edges fall back to the signature / name-suffix heuristic. Run `cdidx index .` to re-stamp authoritative is_metadata_target values."));
                // #86: tell the user when `--exact` is running on the ASCII NOCASE fallback.
                // #86: --exact が ASCII NOCASE fallback で動いているときは明示する。
                if (!status.FoldReady)
                {
                    if (IsFoldOnlyReadinessDegraded(status) && status.DegradedReason != null && status.RecommendedAction != null && status.AlternativeAction != null)
                    {
                        Console.WriteLine(ConsoleUi.FormatSummaryLine("WARN", status.DegradedReason));
                        Console.WriteLine(ConsoleUi.FormatSummaryLine("Hint", $"run `{status.RecommendedAction}` to restamp folded-name columns in place."));
                        Console.WriteLine(ConsoleUi.FormatSummaryLine("Hint", $"or run `{status.AlternativeAction}` for a full rebuild."));
                    }
                    else
                    {
                        Console.WriteLine(ConsoleUi.FormatSummaryLine("WARN", BuildFoldNotReadyWarning(status.FoldReadyReason, BuildFoldBackfillCommand(options.DbPath, options.DbPathExplicit), BuildFoldRebuildRepairCommand(status.ProjectRoot, options.DbPath, options.DbPathExplicit))));
                    }
                }
                var totalLangs = FileIndexer.GetLanguageExtensions().Values.Distinct().Count();
                var symbolLangs = SymbolExtractor.GetSupportedLanguages().Count;
                Console.WriteLine(ConsoleUi.FormatSummaryLine("Support", $"{totalLangs} detected, {symbolLangs} with symbols, {status.GraphSupportedLanguages?.Count ?? 0} with graph"));
            }

            if (!options.CheckWorkspace)
                return CommandExitCodes.Success;
            return GetStatusCheckExitCode(checkFailures);
        }, cancellationToken: cancellationToken);
    }

    private readonly record struct LimitedStatusKindCounts(
        Dictionary<string, long> Counts,
        int TotalCount,
        int OmittedCount,
        bool NamesTruncated);

    private static void ApplyStatusSymbolKindLimits(StatusResult status, Dictionary<string, long> symbolKinds)
    {
        var limitedSymbolKinds = LimitStatusKindCounts(symbolKinds);
        status.SymbolKinds = limitedSymbolKinds.Counts;
        if (limitedSymbolKinds.OmittedCount > 0 || limitedSymbolKinds.NamesTruncated)
        {
            status.SymbolKindLimit = MaxStatusSymbolKindEntries;
            status.SymbolKindNameLimit = MaxStatusSymbolKindNameLength;
            status.SymbolKindTotalCount = limitedSymbolKinds.TotalCount;
            status.SymbolKindOmittedCount = limitedSymbolKinds.OmittedCount;
            status.SymbolKindNamesTruncated = limitedSymbolKinds.NamesTruncated;
        }

        if (status.SymbolsByLanguage is not { Count: > 0 })
            return;

        Dictionary<string, int>? totalCounts = null;
        Dictionary<string, int>? omittedCounts = null;
        List<string>? truncatedLanguages = null;
        foreach (var (language, kinds) in status.SymbolsByLanguage.ToArray())
        {
            var limited = LimitStatusKindCounts(kinds);
            status.SymbolsByLanguage[language] = limited.Counts;
            if (limited.OmittedCount == 0 && !limited.NamesTruncated)
                continue;

            totalCounts ??= new Dictionary<string, int>(StringComparer.Ordinal);
            totalCounts[language] = limited.TotalCount;
            if (limited.OmittedCount > 0)
            {
                omittedCounts ??= new Dictionary<string, int>(StringComparer.Ordinal);
                omittedCounts[language] = limited.OmittedCount;
            }

            if (limited.NamesTruncated)
            {
                truncatedLanguages ??= [];
                truncatedLanguages.Add(language);
            }
        }

        status.SymbolsByLanguageKindTotalCounts = totalCounts;
        status.SymbolsByLanguageKindOmittedCounts = omittedCounts;
        status.SymbolsByLanguageKindNamesTruncated = truncatedLanguages;
    }

    private static LimitedStatusKindCounts LimitStatusKindCounts(IReadOnlyDictionary<string, long> counts)
    {
        var limited = new Dictionary<string, long>(StringComparer.Ordinal);
        var consumed = 0;
        var namesTruncated = false;
        foreach (var (kind, count) in counts
                     .OrderByDescending(kv => kv.Value)
                     .ThenBy(kv => kv.Key, StringComparer.Ordinal)
                     .Take(MaxStatusSymbolKindEntries))
        {
            consumed++;
            var displayKind = LimitStatusSymbolKindName(kind, ref namesTruncated);
            if (limited.TryGetValue(displayKind, out var existing))
                limited[displayKind] = existing + count;
            else
                limited[displayKind] = count;
        }

        return new LimitedStatusKindCounts(
            limited,
            counts.Count,
            Math.Max(0, counts.Count - consumed),
            namesTruncated);
    }

    private static string LimitStatusSymbolKindName(string kind, ref bool namesTruncated)
    {
        if (kind.Length <= MaxStatusSymbolKindNameLength)
            return kind;

        namesTruncated = true;
        return kind[..(MaxStatusSymbolKindNameLength - 3)] + "...";
    }

    private static JsonObject BuildEffectiveConfigJson(QueryCommandOptions options, string[] cmdArgs, string? appVersion)
    {
        JsonObject Entry<T>(T? value, string source)
        {
            var entry = new JsonObject
            {
                ["value"] = JsonSerializer.SerializeToNode(value),
                ["source"] = source,
            };
            AddEffectiveConfigSourceSummary(entry, source);
            return entry;
        }

        var staleAfterEnvValue = CdidxEnvironment.GetEnvironmentVariable(StaleAfterEnvironmentVariable);

        var payload = new JsonObject
        {
            ["api_version"] = "1",
            ["effective_config"] = new JsonObject
            {
                ["db_path"] = Entry(options.DbPath, ResolveDbPathConfigSource(options)),
                ["data_dir"] = Entry(options.DataDir, options.DataDirSource ?? "flag"),
                ["limit"] = Entry(options.Limit, ResolveNumericConfigSource(cmdArgs, "--limit", "--top", DefaultLimitEnvironmentVariable)),
                ["snippet_lines"] = Entry(options.SnippetLines, ResolveNumericConfigSource(cmdArgs, "--snippet-lines", null, DefaultSnippetLinesEnvironmentVariable)),
                ["max_line_width"] = Entry(options.MaxLineWidth, ResolveNumericConfigSource(cmdArgs, "--max-line-width", null, DefaultMaxLineWidthEnvironmentVariable)),
                ["json"] = Entry(options.Json, HasOption(cmdArgs, "--json") ? "flag" : "default"),
                ["stale_after"] = Entry(options.StaleAfter?.ToString() ?? staleAfterEnvValue, options.StaleAfter.HasValue ? "flag" : ResolveEnvSource(StaleAfterEnvironmentVariable)),
                ["global_tool_log_dir"] = Entry(GlobalToolLog.ResolveLogDirectoryForStatus(), ResolveEnvSource("CDIDX_GLOBAL_TOOL_LOG_DIR")),
                ["version"] = Entry(appVersion ?? ConsoleUi.LoadVersion(), "build"),
            },
        };
        return payload;
    }

    private static void AddEffectiveConfigSourceSummary(JsonObject entry, string source)
    {
        var sourceKind = source;
        string? sourceDetail = null;
        if (source.StartsWith("config:", StringComparison.Ordinal))
        {
            sourceKind = "config_file";
            sourceDetail = Path.GetFileName(source["config:".Length..]);
        }
        else if (source.StartsWith("env:", StringComparison.Ordinal))
        {
            sourceKind = "environment";
            sourceDetail = source["env:".Length..];
        }

        entry["source_kind"] = sourceKind;
        if (string.IsNullOrWhiteSpace(sourceDetail))
        {
            if (sourceKind == "config_file")
                entry["source"] = sourceKind;
            return;
        }

        var bounded = CdidxConfigFile.FormatConfigSourceDetail(sourceDetail);
        if (sourceKind == "config_file")
            entry["source"] = $"config:{bounded.Text}";
        entry["source_detail"] = bounded.Text;
        if (bounded.Truncated)
        {
            entry["source_detail_length"] = bounded.OriginalLength;
            entry["source_detail_truncated"] = true;
        }
    }

    private static string ResolveDbPathConfigSource(QueryCommandOptions options)
    {
        if (options.DbPathExplicit)
            return "flag";
        return options.DataDirSource switch
        {
            DbPathResolver.DataDirSourceFlag => "flag",
            DbPathResolver.DataDirSourceEnv => $"env:{DbPathResolver.DataDirEnvironmentVariable}",
            DbPathResolver.DataDirSourceXdg => "env:XDG_DATA_HOME",
            DbPathResolver.DataDirSourceWorkspace => "workspace",
            _ => "default",
        };
    }

    private static string ResolveNumericConfigSource(string[] args, string primaryFlag, string? aliasFlag, string envName)
    {
        if (HasOption(args, primaryFlag) || (aliasFlag != null && HasOption(args, aliasFlag)))
            return "flag";
        if (CdidxEnvironment.GetEnvironmentVariable(envName) is null)
            return "default";
        var configSource = CdidxEnvironment.GetConfigSource(envName);
        if (!string.IsNullOrWhiteSpace(configSource))
            return $"config:{configSource}";
        return $"env:{envName}";
    }

    private static string ResolveEnvSource(string envName)
    {
        if (CdidxEnvironment.GetEnvironmentVariable(envName) is null)
            return "default";
        var configSource = CdidxEnvironment.GetConfigSource(envName);
        if (!string.IsNullOrWhiteSpace(configSource))
            return $"config:{configSource}";
        return $"env:{envName}";
    }

    private static bool HasOption(string[] args, string optionName)
    {
        foreach (var arg in args)
        {
            if (string.Equals(arg, optionName, StringComparison.Ordinal))
                return true;
            if (arg.StartsWith(optionName + "=", StringComparison.Ordinal))
                return true;
        }
        return false;
    }

    public static int RunVacuum(string[] cmdArgs, JsonSerializerOptions jsonOptions)
        => RunVacuum(cmdArgs, jsonOptions, CancellationToken.None);

    public static int RunVacuum(string[] cmdArgs, JsonSerializerOptions jsonOptions, CancellationToken cancellationToken)
    {
        var options = ParseArgs(
            cmdArgs,
            jsonDefault: false,
            validateDefaultLimit: false,
            validateDefaultSnippetLines: false,
            validateDefaultMaxLineWidth: false);
        if (TryWriteUnsupportedOptionError("vacuum", cmdArgs, CliFlagSchema.GetAcceptedFlagNamesForCommand("vacuum")))
            return CommandExitCodes.UsageError;
        var explicitDbPathError = BuildExplicitDbPathParseError(options);
        if (explicitDbPathError != null && explicitDbPathError.Contains(CommandErrorCodes.DbNotFound, StringComparison.Ordinal))
        {
            CommandErrorWriter.WriteStderr(explicitDbPathError);
            CommandErrorWriter.WriteStderr("Hint: point `--db` at an existing `codeindex.db`, or run `cdidx index <projectPath>` first to create one.");
            return CommandExitCodes.NotFound;
        }
        if (TryWriteParseError(options, "vacuum"))
            return CommandExitCodes.UsageError;
        if (TryWriteUnexpectedPositionals("vacuum", options))
            return CommandExitCodes.UsageError;

        try
        {
            cancellationToken.ThrowIfCancellationRequested();
            if (!DbContext.TryValidateExistingCodeIndexDb(options.DbPath, out var validationMessage, out var isNotFound, cancellationToken: cancellationToken))
            {
                CommandErrorWriter.WriteStderr($"Error [{(isNotFound ? CommandErrorCodes.DbNotFound : CommandErrorCodes.DbError)}]: {validationMessage}");
                CommandErrorWriter.WriteStderr(isNotFound
                    ? "Hint: point `--db` at an existing `codeindex.db`, or run `cdidx index <projectPath>` first to create one."
                    : "Hint: point `--db` at an existing CodeIndex database created by `cdidx index`, then retry `cdidx vacuum`.");
                return isNotFound ? CommandExitCodes.NotFound : CommandExitCodes.DatabaseError;
            }

            using var db = new DbContext(options.DbPath, cancellationToken);
            var result = db.RunIncrementalVacuum(options.DryRun, cancellationToken);
            if (options.Json)
            {
                Console.WriteLine(JsonSerializer.Serialize(
                    result,
                    CliJsonSerializerContextFactory.Create(jsonOptions).VacuumResult));
            }
            else
            {
                Console.WriteLine(result.DryRun
                    ? $"Vacuum dry run: estimated reclaimable {result.EstimatedPagesReclaimable:N0} page(s) ({result.EstimatedBytesReclaimable:N0} bytes)."
                    : $"Vacuum complete: reclaimed {result.PagesReclaimed:N0} page(s) ({result.BytesReclaimed:N0} bytes).");
                Console.WriteLine(ConsoleUi.FormatSummaryLine("Page size", $"{result.PageSize:N0} bytes"));
                Console.WriteLine(ConsoleUi.FormatSummaryLine("Pages", $"{result.PageCountBefore:N0} -> {result.PageCountAfter:N0}"));
                Console.WriteLine(ConsoleUi.FormatSummaryLine("Freelist", $"{result.FreelistCountBefore:N0} -> {result.FreelistCountAfter:N0}"));
                Console.WriteLine(ConsoleUi.FormatSummaryLine("AutoVac", $"{result.AutoVacuumModeBeforeName} -> {result.AutoVacuumModeAfterName}"));
                if (result.MaintenanceGuidance.RecommendedCommand != "none")
                    Console.WriteLine(ConsoleUi.FormatSummaryLine("Recommend", result.MaintenanceGuidance.RecommendedCommand));
                if (!string.IsNullOrWhiteSpace(result.MaintenanceGuidance.PostMaintenanceFollowUp))
                    Console.WriteLine(ConsoleUi.FormatSummaryLine("Follow-up", result.MaintenanceGuidance.PostMaintenanceFollowUp));
            }

            return CommandExitCodes.Success;
        }
        catch (OperationCanceledException)
        {
            return CommandErrorWriter.WriteJsonOrHuman(
                options.Json,
                jsonOptions,
                "vacuum cancelled before it could complete",
                CommandExitCodes.CancelledBySignal,
                "Retry `cdidx vacuum` after the cancelling operation completes.",
                errorCode: CommandErrorCodes.Interrupted);
        }
    }

    public static int RunImpact(string[] cmdArgs, JsonSerializerOptions jsonOptions)
    {
        var previewOptionError = ValidatePreviewOptions("impact", cmdArgs, allowMaxLineWidth: true, allowFocusOptions: false);
        if (previewOptionError != null)
        {
            CommandErrorWriter.WriteStderr(previewOptionError);
            return CommandExitCodes.UsageError;
        }
        var options = ParseArgs(cmdArgs, jsonDefault: false, allowNamedQuery: true);
        if (TryWriteUnsupportedOptionError("impact", cmdArgs, CliFlagSchema.GetAcceptedFlagNamesForCommand("impact"), options.Query))
            return CommandExitCodes.UsageError;
        if (TryWriteParseError(options, "impact"))
            return CommandExitCodes.UsageError;
        if (TryWriteBlankQueryError(options, "impact"))
            return CommandExitCodes.UsageError;
        if (string.IsNullOrWhiteSpace(options.Query))
        {
            WriteUsageError(
                "impact requires a symbol query argument",
                GetUsageLineOrThrow("impact"),
                "Add the symbol whose callers you want to inspect, for example: `cdidx impact QueryCommandRunner`.");
            return CommandExitCodes.UsageError;
        }
        if (IsBareVerbatimQueryToken(options.Query))
        {
            WriteUsageError(
                "impact requires a symbol query argument",
                GetUsageLineOrThrow("impact"),
                "Add a real symbol name after the command; bare verbatim prefixes like `@` are not valid queries.");
            return CommandExitCodes.UsageError;
        }
        if (TryWriteUnexpectedExtraPositionals("impact", options))
            return CommandExitCodes.UsageError;

        return WithDb(options, jsonOptions, reader =>
        {
            var maxDepth = options.ContextAfterExplicit ? options.ContextAfter : 5; // --max-hops/--depth is parsed into ContextAfter; 0 means resolve-only
            if (!options.Json && options.ImpactDeprecatedDepthUsed)
                CommandErrorWriter.WriteStderr("Warning: --depth is deprecated for impact; use --max-hops instead.");
            var analysis = reader.AnalyzeImpact(options.Query, maxDepth, options.Limit, options.Lang, options.PathPatterns, options.ExcludePaths, options.ExcludeTests, options.WithPaths);
            if (options.IncludeBody)
                AttachBodyExcerpts(reader, analysis.Callers, options.SnippetLines, options.MaxLineWidth);
            ApplyBodyRecoveryCommands(analysis.Callers, options.DbPath);
            var sqlGraphSignal = NarrowSqlGraphContractSignal(
                reader.GetSqlGraphContractSignal(options.Lang, options.PathPatterns, options.ExcludePaths, options.ExcludeTests),
                DbReader.IsSqlLanguage(options.Lang)
                    || DbReader.ContainsSqlLanguage(analysis.Definitions.Select(definition => definition.Lang))
                    || DbReader.ContainsSqlLanguage(analysis.Callers.Select(caller => caller.Lang))
                    || reader.AnyFilePathHasLanguage(analysis.FileImpacts.SelectMany(impact => new[] { impact.SourcePath, impact.TargetPath }), "sql"));
            var confirmedCount = analysis.Callers.Count;
            var confirmedFileCount = analysis.Callers.Select(r => r.Path).Distinct().Count();
            var hintCount = analysis.FileImpacts.Count;
            var hintFileCount = analysis.FileImpacts.Select(r => r.SourcePath).Distinct().Count();
            var hasHeuristicHints = analysis.ImpactMode == "file_dependency_hints";
            var visibleCount = hasHeuristicHints ? hintCount : confirmedCount;
            var visibleFileCount = hasHeuristicHints ? hintFileCount : confirmedFileCount;
            var depthZeroResolved = maxDepth == 0 && analysis.DefinitionCount > 0;

            WriteSqlGraphContractWarningIfNeeded(options.Json, sqlGraphSignal, reader, options);

            if (confirmedCount == 0 && !hasHeuristicHints)
            {
                if (!options.CountOnly && depthZeroResolved)
                {
                    if (options.Json)
                    {
                        var payload = BuildJsonZeroResultPayload(
                            reader,
                            jsonOptions,
                            resultsKey: "callers",
                            graphTableAvailable: analysis.GraphTableAvailable,
                            degraded: false,
                            extraFields: zeroPayload =>
                            {
                                zeroPayload["query"] = options.Query;
                                zeroPayload["resolved_name"] = analysis.ResolvedName;
                                zeroPayload["file_count"] = 0;
                                zeroPayload["confirmed_count"] = 0;
                                zeroPayload["confirmed_file_count"] = 0;
                                zeroPayload["hint_count"] = 0;
                                zeroPayload["hint_file_count"] = 0;
                                zeroPayload["max_hops"] = maxDepth;
                                zeroPayload["max_depth"] = maxDepth;
                                zeroPayload["actual_depth"] = 0;
                                zeroPayload["truncated"] = analysis.Truncated;
                                if (analysis.TruncatedReason != null)
                                    zeroPayload["truncated_reason"] = analysis.TruncatedReason;
                                AddImpactTerminationJsonFields(zeroPayload, analysis, jsonOptions);
                                zeroPayload["impact_mode"] = analysis.ImpactMode;
                                zeroPayload["heuristic"] = analysis.Heuristic;
                                zeroPayload["file_impacts"] = new JsonArray();
                                zeroPayload["definition_count"] = analysis.DefinitionCount;
                                zeroPayload["definition_file_count"] = analysis.DefinitionFileCount;
                                zeroPayload["has_multiple_definitions"] = analysis.HasMultipleDefinitions;
                                zeroPayload["has_class_like_definitions"] = analysis.HasClassLikeDefinitions;
                                zeroPayload["has_multiple_definition_files"] = analysis.HasMultipleDefinitionFiles;
                                zeroPayload["definitions"] = JsonSerializer.SerializeToNode(analysis.Definitions, CliJsonSerializerContextFactory.Create(jsonOptions).ListSymbolResult);
                                if (analysis.ZeroResultReason != null)
                                    zeroPayload["zero_result_reason"] = analysis.ZeroResultReason;
                                AddImpactFailureJsonFields(zeroPayload, analysis, jsonOptions);
                                if (analysis.Suggestion != null)
                                    zeroPayload["suggestion"] = analysis.Suggestion;
                                AddSqlGraphContractJsonFields(zeroPayload, sqlGraphSignal);
                                AddImpactOptionWarnings(zeroPayload, options);
                            });
                        Console.WriteLine(payload.ToJsonString(jsonOptions));
                    }
                    else
                    {
                        CommandErrorWriter.WriteStderr("Depth 0 requested: resolved the symbol only; callers were not traversed.");
                        WriteImpactResolutionHint(analysis);
                        WriteGraphSupportHint(options.Lang);
                    }
                    return StrictImpactExitCode(options, analysis, CommandExitCodes.Success);
                }

                if (options.CountOnly)
                {
                    if (options.Json)
                    {
                        var payload = new JsonObject
                        {
                            ["query"] = options.Query,
                            ["resolved_name"] = analysis.ResolvedName,
                            ["count"] = 0,
                            ["files"] = 0,
                            ["file_count"] = 0,
                            ["confirmed_count"] = 0,
                            ["confirmed_file_count"] = 0,
                            ["impact_mode"] = analysis.ImpactMode,
                            ["heuristic"] = analysis.Heuristic,
                            ["hint_count"] = analysis.HintCount,
                            ["hint_file_count"] = 0,
                            ["definition_count"] = analysis.DefinitionCount,
                            ["definition_file_count"] = analysis.DefinitionFileCount,
                            ["has_multiple_definitions"] = analysis.HasMultipleDefinitions,
                            ["has_class_like_definitions"] = analysis.HasClassLikeDefinitions,
                            ["has_multiple_definition_files"] = analysis.HasMultipleDefinitionFiles,
                            ["graph_table_available"] = analysis.GraphTableAvailable,
                            ["degraded"] = !analysis.GraphTableAvailable,
                        };
                        AddImpactTerminationJsonFields(payload, analysis, jsonOptions);
                        if (analysis.ZeroResultReason != null)
                            payload["zero_result_reason"] = analysis.ZeroResultReason;
                        AddImpactFailureJsonFields(payload, analysis, jsonOptions);
                        if (analysis.Suggestion != null)
                            payload["suggestion"] = analysis.Suggestion;
                        if (!analysis.GraphTableAvailable)
                            payload["note"] = "symbol_references table is missing in this index (legacy or read-only DB). Zero result is degraded, not authoritative.";
                        AddSqlGraphContractJsonFields(payload, sqlGraphSignal);
                        AddImpactOptionWarnings(payload, options);
                        AddCountEnvelopeJsonFields(payload, reader, jsonOptions, options);
                        Console.WriteLine(payload.ToJsonString(jsonOptions));
                    }
                    else
                    {
                        Console.WriteLine("0");
                        if (!analysis.GraphTableAvailable)
                            CommandErrorWriter.WriteStderr("WARN: symbol_references table missing — this count result is degraded, not authoritative.");
                    }
                }
                else if (options.Json)
                {
                    var payload = BuildJsonZeroResultPayload(
                        reader,
                        jsonOptions,
                        resultsKey: "callers",
                        graphTableAvailable: analysis.GraphTableAvailable,
                        degraded: !analysis.GraphTableAvailable,
                        extraFields: zeroPayload =>
                        {
                            zeroPayload["query"] = options.Query;
                            zeroPayload["resolved_name"] = analysis.ResolvedName;
                            zeroPayload["file_count"] = 0;
                            zeroPayload["confirmed_count"] = 0;
                            zeroPayload["confirmed_file_count"] = 0;
                            zeroPayload["hint_count"] = 0;
                            zeroPayload["hint_file_count"] = 0;
                            zeroPayload["max_hops"] = maxDepth;
                            zeroPayload["max_depth"] = maxDepth;
                            zeroPayload["actual_depth"] = 0;
                            zeroPayload["truncated"] = analysis.Truncated;
                            if (analysis.TruncatedReason != null)
                                zeroPayload["truncated_reason"] = analysis.TruncatedReason;
                            AddImpactTerminationJsonFields(zeroPayload, analysis, jsonOptions);
                            zeroPayload["impact_mode"] = analysis.ImpactMode;
                            zeroPayload["heuristic"] = analysis.Heuristic;
                            zeroPayload["file_impacts"] = new JsonArray();
                            zeroPayload["definition_count"] = analysis.DefinitionCount;
                            zeroPayload["definition_file_count"] = analysis.DefinitionFileCount;
                            zeroPayload["has_multiple_definitions"] = analysis.HasMultipleDefinitions;
                            zeroPayload["has_class_like_definitions"] = analysis.HasClassLikeDefinitions;
                            zeroPayload["has_multiple_definition_files"] = analysis.HasMultipleDefinitionFiles;
                            zeroPayload["definitions"] = JsonSerializer.SerializeToNode(analysis.Definitions, CliJsonSerializerContextFactory.Create(jsonOptions).ListSymbolResult);
                            if (analysis.ZeroResultReason != null)
                                zeroPayload["zero_result_reason"] = analysis.ZeroResultReason;
                            AddImpactFailureJsonFields(zeroPayload, analysis, jsonOptions);
                            if (analysis.Suggestion != null)
                                zeroPayload["suggestion"] = analysis.Suggestion;
                            AddSqlGraphContractJsonFields(zeroPayload, sqlGraphSignal);
                            AddImpactOptionWarnings(zeroPayload, options);
                        });
                    if (!analysis.GraphTableAvailable)
                        payload["note"] = "symbol_references table is missing in this index (legacy or read-only DB). Zero result is degraded, not authoritative.";
                    Console.WriteLine(payload.ToJsonString(jsonOptions));
                }
                else if (!options.Json)
                {
                    CommandErrorWriter.WriteStderr($"No impact found for '{analysis.Query}'.");
                    WriteImpactResolutionHint(analysis);
                    WriteGraphSupportHint(options.Lang);
                    WriteDegradedGraphZeroResult(reader, "callers", json: false, graphAvailable: reader._hasReferencesTable, jsonOptions);
                }
                return StrictImpactExitCode(options, analysis, ZeroResultExitCode(options));
            }

            if (options.CountOnly)
            {
                if (options.Json)
                {
                    var payload = new JsonObject
                    {
                        ["query"] = options.Query,
                        ["resolved_name"] = analysis.ResolvedName,
                        ["count"] = visibleCount,
                        ["files"] = visibleFileCount,
                        ["file_count"] = visibleFileCount,
                        ["confirmed_count"] = confirmedCount,
                        ["confirmed_file_count"] = confirmedFileCount,
                        ["impact_mode"] = analysis.ImpactMode,
                        ["heuristic"] = analysis.Heuristic,
                        ["hint_count"] = hintCount,
                        ["hint_file_count"] = hintFileCount,
                        ["truncated"] = analysis.Truncated,
                    };
                    AddImpactTerminationJsonFields(payload, analysis, jsonOptions);
                    if (analysis.TruncatedReason != null)
                        payload["truncated_reason"] = analysis.TruncatedReason;
                    AddSqlGraphContractJsonFields(payload, sqlGraphSignal);
                    AddImpactOptionWarnings(payload, options);
                    AddCountEnvelopeJsonFields(payload, reader, jsonOptions, options);
                    Console.WriteLine(payload.ToJsonString(jsonOptions));
                }
                else
                {
                    Console.WriteLine($"{visibleCount}");
                }
                return CommandExitCodes.Success;
            }

            if (options.Json)
            {
                var payload = new JsonObject
                {
                    ["query"] = options.Query,
                    ["resolved_name"] = analysis.ResolvedName,
                    ["count"] = visibleCount,
                    ["file_count"] = visibleFileCount,
                    ["confirmed_count"] = confirmedCount,
                    ["confirmed_file_count"] = confirmedFileCount,
                    ["hint_count"] = hintCount,
                    ["hint_file_count"] = hintFileCount,
                    ["max_hops"] = maxDepth,
                    ["max_depth"] = maxDepth,
                    ["actual_depth"] = analysis.Callers.Count > 0 ? analysis.Callers.Max(r => r.Depth) : 0,
                    ["truncated"] = analysis.Truncated,
                    ["impact_mode"] = analysis.ImpactMode,
                    ["heuristic"] = analysis.Heuristic,
                    ["callers"] = JsonSerializer.SerializeToNode(analysis.Callers, CliJsonSerializerContextFactory.Create(jsonOptions).ListImpactResult),
                    ["file_impacts"] = JsonSerializer.SerializeToNode(analysis.FileImpacts, CliJsonSerializerContextFactory.Create(jsonOptions).ListFileDependencyResult),
                    ["definition_count"] = analysis.DefinitionCount,
                    ["definition_file_count"] = analysis.DefinitionFileCount,
                    ["has_multiple_definitions"] = analysis.HasMultipleDefinitions,
                    ["has_class_like_definitions"] = analysis.HasClassLikeDefinitions,
                    ["has_multiple_definition_files"] = analysis.HasMultipleDefinitionFiles,
                    ["definitions"] = JsonSerializer.SerializeToNode(analysis.Definitions, CliJsonSerializerContextFactory.Create(jsonOptions).ListSymbolResult),
                };
                AddImpactTerminationJsonFields(payload, analysis, jsonOptions);
                if (analysis.TruncatedReason != null)
                    payload["truncated_reason"] = analysis.TruncatedReason;
                if (analysis.Suggestion != null)
                    payload["suggestion"] = analysis.Suggestion;
                AddImpactFailureJsonFields(payload, analysis, jsonOptions);
                AddSqlGraphContractJsonFields(payload, sqlGraphSignal);
                AddImpactOptionWarnings(payload, options);
                Console.WriteLine(payload.ToJsonString(jsonOptions));
            }
            else
            {
                if (hasHeuristicHints)
                {
                    CommandErrorWriter.WriteStderr($"No symbol-level callers found for '{analysis.ResolvedName}'. Possible file-level dependents follow.");
                    WriteImpactResolutionHint(analysis);
                    CommandErrorWriter.WriteStderr("WARN: these file-level dependents are heuristic only; the current graph does not record resolved target file/type for each call.");
                    if (analysis.Truncated)
                        CommandErrorWriter.WriteStderr("WARN: heuristic file-level dependents were truncated by the current limit.");
                    foreach (var edge in analysis.FileImpacts)
                        Console.WriteLine($"  {edge.SourcePath,-40} -> {edge.TargetPath} ({edge.ReferenceCount} refs: {edge.Symbols})");
                }
                else
                {
                    var grouped = analysis.Callers.GroupBy(r => r.Depth).OrderBy(g => g.Key);
                    foreach (var group in grouped)
                    {
                        CommandErrorWriter.WriteStderr($"--- Depth {group.Key} ---");
                        foreach (var r in group)
                        {
                            var indent = new string(' ', (r.Depth - 1) * 2);
                            Console.WriteLine($"  {indent}{r.CallerKind ?? "?",-10} {r.CallerName ?? "<top-level>",-32} {r.Path}:{r.FirstLine}  -> {r.CalleeName} ({r.ReferenceCount} refs)");
                            WriteOptionalBodyExcerpt(r.BodyStartLine, r.BodyContent, $"  {indent}");
                            if (options.WithPaths && r.Paths != null)
                            {
                                foreach (var p in r.Paths)
                                    Console.WriteLine($"  {indent}  via: {string.Join(" -> ", p)}");
                                if (r.PathsTruncated)
                                    Console.WriteLine($"  {indent}  via: ... (more paths exist, truncated by per-row cap)");
                            }
                        }
                    }
                }

                var truncNote = analysis.Truncated
                    ? analysis.TruncatedReason != null
                        ? $" [TRUNCATED: {analysis.TruncatedReason}]"
                        : " [TRUNCATED]"
                    : "";
                if (hasHeuristicHints)
                    CommandErrorWriter.WriteStderr($"\n({hintCount} heuristic dependency hints across {hintFileCount} files{truncNote})");
                else
                    CommandErrorWriter.WriteStderr($"\n({confirmedCount} callers across {confirmedFileCount} files, max depth {maxDepth}{truncNote})");
            }
            return StrictImpactExitCode(options, analysis, CommandExitCodes.Success);
        });
    }

    private static void AddImpactFailureJsonFields(JsonObject payload, ImpactAnalysisResult analysis, JsonSerializerOptions jsonOptions)
    {
        if (analysis.ImpactFailureChain is { Count: > 0 })
            payload["impact_failure_chain"] = JsonSerializer.SerializeToNode(analysis.ImpactFailureChain, CliJsonSerializerContextFactory.Create(jsonOptions).ListString);
        if (analysis.SuggestionType != null)
            payload["suggestion_type"] = analysis.SuggestionType;
    }

    private static int StrictImpactExitCode(QueryCommandOptions options, ImpactAnalysisResult analysis, int defaultExitCode)
    {
        if (!options.Strict || analysis.ImpactFailureChain is not { Count: > 0 })
            return defaultExitCode;
        return analysis.ImpactFailureChain.Any(code => code != "no_callers")
            ? CommandExitCodes.FeatureUnavailable
            : defaultExitCode;
    }

    private static void AddImpactTerminationJsonFields(JsonObject payload, ImpactAnalysisResult analysis, JsonSerializerOptions jsonOptions)
    {
        payload["termination_reason"] = analysis.TerminationReason;
        payload["cycle_detected"] = analysis.CycleDetected;
        if (analysis.Cycles is { Count: > 0 })
            payload["cycles"] = JsonSerializer.SerializeToNode(analysis.Cycles, CliJsonSerializerContextFactory.Create(jsonOptions).ListImpactCycleResult);
    }

    public static int RunDeps(string[] cmdArgs, JsonSerializerOptions jsonOptions)
    {
        var previewOptionError = ValidatePreviewOptions("deps", cmdArgs, allowMaxLineWidth: false, allowFocusOptions: false);
        if (previewOptionError != null)
        {
            CommandErrorWriter.WriteStderr(previewOptionError);
            return CommandExitCodes.UsageError;
        }
        if (!TryExtractDepsFormat(cmdArgs, out var depsFormat, out var parseArgs, out var depsFormatError))
        {
            CommandErrorWriter.WriteStderr(depsFormatError);
            return CommandExitCodes.UsageError;
        }

        var options = ParseArgs(
            parseArgs,
            jsonDefault: false,
            validateDefaultSnippetLines: false,
            validateDefaultMaxLineWidth: false);
        if (TryWriteUnsupportedOptionError("deps", cmdArgs, CliFlagSchema.GetAcceptedFlagNamesForCommand("deps")))
            return CommandExitCodes.UsageError;
        if (TryWriteParseError(options, "deps"))
            return CommandExitCodes.UsageError;
        if (TryWriteUnexpectedPositionals("deps", options))
            return CommandExitCodes.UsageError;
        if (TryWriteWorkspaceDependencyFanOutError(options))
            return CommandExitCodes.UsageError;

        return WithDb(options, jsonOptions, reader =>
        {
            if (TryWriteInvalidWorkspaceDependencyDatabaseError(options, out var workspaceDbExitCode))
                return workspaceDbExitCode;

            var reverse = cmdArgs.Any(a => a == "--reverse");
            var results = GetWorkspaceFileDependencies(reader, options, reverse, options.Limit);
            var cycleCandidates = options.DependencyCycles
                ? GetWorkspaceFileDependencies(reader, options, reverse, GetDependencyCycleGraphLimit(options.Limit))
                : results;
            var baseSqlGraphSignal = reader.GetSqlGraphContractSignal(options.Lang, options.PathPatterns, options.ExcludePaths, options.ExcludeTests);
            if (results.Count == 0)
            {
                var zeroSqlGraphSignal = baseSqlGraphSignal;
                if (options.Json && !reader._hasReferencesTable)
                    WriteDegradedGraphZeroResult(reader, "edges", json: true, graphAvailable: false, jsonOptions, queryOptions: options, extraFields: payload => AddSqlGraphContractJsonFields(payload, zeroSqlGraphSignal));
                else if (options.Json)
                    Console.WriteLine(BuildJsonZeroResultPayload(reader, jsonOptions, resultsKey: "edges", graphTableAvailable: true, degraded: !zeroSqlGraphSignal.Ready, queryOptions: options, extraFields: payload => AddSqlGraphContractJsonFields(payload, zeroSqlGraphSignal)).ToJsonString(jsonOptions));
                else
                {
                    CommandErrorWriter.WriteStderr(BuildZeroResultLine("No file dependencies found", options));
                    WriteSqlGraphContractWarningIfNeeded(json: false, zeroSqlGraphSignal, reader, options);
                    WriteDegradedGraphZeroResult(reader, "edges", json: false, graphAvailable: reader._hasReferencesTable, jsonOptions);
                }
                return ZeroResultExitCode(options);
            }

            List<List<string>> cycles = [];
            var outputEdges = options.DependencyCycles
                ? FilterCycleEdges(cycleCandidates, out cycles).Take(options.Limit).ToList()
                : results;
            if (options.DependencyCycles)
                cycles = cycles.Take(options.Limit).ToList();
            var sqlGraphSignalPaths = options.DependencyCycles
                ? cycles.Count > 0
                    ? cycles.SelectMany(static cycle => cycle)
                    : cycleCandidates.SelectMany(static result => new[] { result.SourcePath, result.TargetPath })
                : results.SelectMany(static result => new[] { result.SourcePath, result.TargetPath });
            var sqlGraphSignal = NarrowSqlGraphContractSignalByPaths(
                reader,
                baseSqlGraphSignal,
                sqlGraphSignalPaths,
                options.Lang);
            if (options.DependencyCycles && cycles.Count == 0)
            {
                if (options.Json)
                {
                    var payload = new JsonObject { ["count"] = 0, ["cycles"] = new JsonArray() };
                    AddSqlGraphContractJsonFields(payload, sqlGraphSignal);
                    Console.WriteLine(payload.ToJsonString(jsonOptions));
                }
                else
                {
                    CommandErrorWriter.WriteStderr(BuildZeroResultLine("No dependency cycles found", options));
                    WriteSqlGraphContractWarningIfNeeded(json: false, sqlGraphSignal, reader, options);
                }
                return ZeroResultExitCode(options);
            }

            if (depsFormat is OutputFormatDot or OutputFormatGraphMl or OutputFormatJsonGraph)
            {
                WriteDependencyGraph(outputEdges, depsFormat, jsonOptions);
                return CommandExitCodes.Success;
            }

            if (options.Json)
            {
                var payload = new JsonObject
                {
                    ["count"] = options.DependencyCycles ? cycles.Count : results.Count,
                };
                if (options.DependencyCycles)
                    payload["cycles"] = BuildDependencyCyclesJson(cycles);
                else
                    payload["edges"] = JsonSerializer.SerializeToNode(results, CliJsonSerializerContextFactory.Create(jsonOptions).ListFileDependencyResult);
                AddSqlGraphContractJsonFields(payload, sqlGraphSignal);
                Console.WriteLine(payload.ToJsonString(jsonOptions));
            }
            else
            {
                if (options.DependencyCycles)
                {
                    foreach (var cycle in cycles)
                        Console.WriteLine(string.Join(" -> ", cycle.Concat([cycle[0]])));
                    CommandErrorWriter.WriteStderr($"({cycles.Count} dependency cycles)");
                    WriteSqlGraphContractWarningIfNeeded(json: false, sqlGraphSignal, reader, options);
                    return CommandExitCodes.Success;
                }

                foreach (var r in results)
                {
                    var syms = r.Symbols.Length > 60 ? r.Symbols[..57] + "..." : r.Symbols;
                    Console.WriteLine($"{r.SourcePath,-45} -> {r.TargetPath,-45} ({r.ReferenceCount} refs: {syms})");
                }
                CommandErrorWriter.WriteStderr($"({results.Count} dependency edges)");
                WriteSqlGraphContractWarningIfNeeded(json: false, sqlGraphSignal, reader, options);
            }
            return CommandExitCodes.Success;
        });
    }

    private static bool TryExtractDepsFormat(string[] args, out string format, out string[] parseArgs, out string? error)
    {
        format = OutputFormatEdgeList;
        error = null;
        var rewritten = new List<string>(args.Length);
        for (var i = 0; i < args.Length; i++)
        {
            var arg = args[i];
            if (arg.StartsWith("--format=", StringComparison.Ordinal))
            {
                var rawFormat = arg["--format=".Length..];
                if (!TryNormalizeDepsFormat(rawFormat, out format, out error))
                {
                    parseArgs = args;
                    return false;
                }
                rewritten.Add(format == OutputFormatJsonGraph ? "--format=json" : "--format=text");
                continue;
            }

            if (arg == "--format" && i + 1 < args.Length)
            {
                var rawFormat = args[++i];
                if (!TryNormalizeDepsFormat(rawFormat, out format, out error))
                {
                    parseArgs = args;
                    return false;
                }
                rewritten.Add("--format");
                rewritten.Add(format == OutputFormatJsonGraph ? "json" : "text");
                continue;
            }

            rewritten.Add(arg);
        }

        parseArgs = rewritten.ToArray();
        return true;
    }

    private static bool TryNormalizeDepsFormat(string rawFormat, out string format, out string? error)
    {
        format = rawFormat.ToLowerInvariant();
        error = null;
        switch (format)
        {
            case OutputFormatText:
            case OutputFormatJson:
            case OutputFormatEdgeList:
                format = OutputFormatEdgeList;
                return true;
            case OutputFormatDot:
            case OutputFormatGraphMl:
            case OutputFormatJsonGraph:
                return true;
            default:
                error = $"Error: deps --format must be one of edgelist, dot, graphml, or json-graph; got '{ConsoleUi.FormatBoundedValue(rawFormat)}'.";
                return false;
        }
    }

    internal static List<FileDependencyResult> FilterCycleEdges(List<FileDependencyResult> results, out List<List<string>> cycles)
    {
        cycles = FindDependencyCycles(results);
        if (cycles.Count == 0)
            return [];
        var cycleNodes = cycles.SelectMany(cycle => cycle).ToHashSet(StringComparer.Ordinal);
        return results
            .Where(edge => cycleNodes.Contains(edge.SourcePath) && cycleNodes.Contains(edge.TargetPath))
            .ToList();
    }

    internal static List<List<string>> FindDependencyCycles(IReadOnlyList<FileDependencyResult> edges)
    {
        var adjacency = new Dictionary<string, List<string>>(StringComparer.Ordinal);
        foreach (var edge in edges)
        {
            if (!adjacency.TryGetValue(edge.SourcePath, out var targets))
                adjacency[edge.SourcePath] = targets = [];
            targets.Add(edge.TargetPath);
            adjacency.TryAdd(edge.TargetPath, []);
        }

        var index = 0;
        var stack = new Stack<string>();
        var onStack = new HashSet<string>(StringComparer.Ordinal);
        var indexes = new Dictionary<string, int>(StringComparer.Ordinal);
        var lowLinks = new Dictionary<string, int>(StringComparer.Ordinal);
        var cycles = new List<List<string>>();

        void Visit(string node)
        {
            indexes[node] = index;
            lowLinks[node] = index;
            index++;
            stack.Push(node);
            onStack.Add(node);

            foreach (var target in adjacency[node])
            {
                if (!indexes.ContainsKey(target))
                {
                    Visit(target);
                    lowLinks[node] = Math.Min(lowLinks[node], lowLinks[target]);
                }
                else if (onStack.Contains(target))
                {
                    lowLinks[node] = Math.Min(lowLinks[node], indexes[target]);
                }
            }

            if (lowLinks[node] != indexes[node])
                return;

            var component = new List<string>();
            string popped;
            do
            {
                popped = stack.Pop();
                onStack.Remove(popped);
                component.Add(popped);
            } while (!string.Equals(popped, node, StringComparison.Ordinal));

            var selfCycle = component.Count == 1 && adjacency[component[0]].Contains(component[0], StringComparer.Ordinal);
            if (component.Count > 1 || selfCycle)
                cycles.Add(component.OrderBy(path => path, StringComparer.Ordinal).ToList());
        }

        foreach (var node in adjacency.Keys.OrderBy(path => path, StringComparer.Ordinal).ToList())
            if (!indexes.ContainsKey(node))
                Visit(node);

        return cycles;
    }

    internal static JsonArray BuildDependencyCyclesJson(IReadOnlyList<List<string>> cycles)
    {
        var array = new JsonArray();
        foreach (var cycle in cycles)
        {
            array.Add(new JsonObject
            {
                ["length"] = cycle.Count,
                ["nodes"] = new JsonArray(cycle.Select(node => JsonValue.Create(node)).ToArray<JsonNode?>())
            });
        }
        return array;
    }

    private static void WriteDependencyGraph(IReadOnlyList<FileDependencyResult> edges, string format, JsonSerializerOptions jsonOptions)
    {
        switch (format)
        {
            case OutputFormatDot:
                Console.WriteLine("digraph deps {");
                foreach (var edge in edges)
                    Console.WriteLine($"  \"{EscapeDot(edge.SourcePath)}\" -> \"{EscapeDot(edge.TargetPath)}\" [label=\"{edge.ReferenceCount}\"];");
                Console.WriteLine("}");
                break;
            case OutputFormatGraphMl:
                Console.WriteLine("<?xml version=\"1.0\" encoding=\"UTF-8\"?>");
                Console.WriteLine("<graphml xmlns=\"http://graphml.graphdrawing.org/xmlns\"><graph edgedefault=\"directed\">");
                foreach (var node in edges.SelectMany(edge => new[] { edge.SourcePath, edge.TargetPath }).Distinct(StringComparer.Ordinal))
                    Console.WriteLine($"<node id=\"{System.Security.SecurityElement.Escape(node)}\" />");
                foreach (var edge in edges)
                    Console.WriteLine($"<edge source=\"{System.Security.SecurityElement.Escape(edge.SourcePath)}\" target=\"{System.Security.SecurityElement.Escape(edge.TargetPath)}\"><data key=\"references\">{edge.ReferenceCount}</data></edge>");
                Console.WriteLine("</graph></graphml>");
                break;
            case OutputFormatJsonGraph:
                WriteDependencyJsonGraph(edges, jsonOptions);
                break;
        }
    }

    private static void WriteDependencyJsonGraph(IReadOnlyList<FileDependencyResult> edges, JsonSerializerOptions jsonOptions)
    {
        var seenNodes = new HashSet<string>(StringComparer.Ordinal);
        var nodes = new List<string>();
        foreach (var edge in edges)
        {
            if (seenNodes.Add(edge.SourcePath))
                nodes.Add(edge.SourcePath);
            if (seenNodes.Add(edge.TargetPath))
                nodes.Add(edge.TargetPath);
        }

        var writer = Console.Out;
        if (!jsonOptions.WriteIndented)
        {
            writer.Write("{\"nodes\":[");
            for (var i = 0; i < nodes.Count; i++)
            {
                if (i > 0)
                    writer.Write(',');
                writer.Write("{\"id\":");
                writer.Write(JsonSerializer.Serialize(nodes[i], jsonOptions));
                writer.Write('}');
            }

            writer.Write("],\"edges\":[");
            for (var i = 0; i < edges.Count; i++)
            {
                if (i > 0)
                    writer.Write(',');
                var edge = edges[i];
                writer.Write("{\"source\":");
                writer.Write(JsonSerializer.Serialize(edge.SourcePath, jsonOptions));
                writer.Write(",\"target\":");
                writer.Write(JsonSerializer.Serialize(edge.TargetPath, jsonOptions));
                writer.Write(",\"reference_count\":");
                writer.Write(edge.ReferenceCount.ToString(CultureInfo.InvariantCulture));
                writer.Write('}');
            }

            writer.WriteLine("]}");
            return;
        }

        writer.WriteLine("{");
        writer.WriteLine("  \"nodes\": [");
        for (var i = 0; i < nodes.Count; i++)
        {
            writer.Write("    { \"id\": ");
            writer.Write(JsonSerializer.Serialize(nodes[i], jsonOptions));
            writer.Write(" }");
            writer.WriteLine(i + 1 < nodes.Count ? "," : string.Empty);
        }

        writer.WriteLine("  ],");
        writer.WriteLine("  \"edges\": [");
        for (var i = 0; i < edges.Count; i++)
        {
            var edge = edges[i];
            writer.Write("    { \"source\": ");
            writer.Write(JsonSerializer.Serialize(edge.SourcePath, jsonOptions));
            writer.Write(", \"target\": ");
            writer.Write(JsonSerializer.Serialize(edge.TargetPath, jsonOptions));
            writer.Write(", \"reference_count\": ");
            writer.Write(edge.ReferenceCount.ToString(CultureInfo.InvariantCulture));
            writer.Write(" }");
            writer.WriteLine(i + 1 < edges.Count ? "," : string.Empty);
        }

        writer.WriteLine("  ]");
        writer.WriteLine("}");
    }

    private static string EscapeDot(string value) => value.Replace("\\", "\\\\", StringComparison.Ordinal).Replace("\"", "\\\"", StringComparison.Ordinal);

    internal static int GetDependencyCycleGraphLimit(int displayLimit)
    {
        var requestedLimit = Math.Max(displayLimit, DefaultDependencyCycleGraphLimit);
        return NumericFlagUpperBounds.TryGetValue("--limit", out var maxLimit)
            ? Math.Min(requestedLimit, maxLimit)
            : requestedLimit;
    }

    private static List<FileDependencyResult> GetWorkspaceFileDependencies(DbReader primaryReader, QueryCommandOptions options, bool reverse, int limit)
    {
        var results = primaryReader.GetFileDependencies(limit, options.Lang, options.PathPatterns, options.ExcludePaths, options.ExcludeTests, reverse);
        if (options.WorkspaceDbPaths.Count == 0)
            return results;

        var memberDbs = BuildWorkspaceDependencyDatabaseList(options);
        var primaryDb = memberDbs[0];
        TagFileDependencyResults(results, primaryDb);
        foreach (var normalizedDbPath in memberDbs.Skip(1))
        {
            using var db = new DbContext(normalizedDbPath);
            db.TryMigrateForRead();
            var reader = new DbReader(db) { IncludeGenerated = primaryReader.IncludeGenerated };
            var memberResults = reader.GetFileDependencies(limit, options.Lang, options.PathPatterns, options.ExcludePaths, options.ExcludeTests, reverse);
            TagFileDependencyResults(memberResults, normalizedDbPath);
            results.AddRange(memberResults);
        }

        foreach (var sourceDb in memberDbs)
            foreach (var targetDb in memberDbs)
            {
                if (string.Equals(sourceDb, targetDb, StringComparison.Ordinal))
                    continue;
                results.AddRange(GetCrossDatabaseFileDependencies(sourceDb, targetDb, options, reverse, limit));
            }

        return results
            .OrderByDescending(result => result.ReferenceCount)
            .ThenBy(result => result.SourceDb, StringComparer.Ordinal)
            .ThenBy(result => result.SourcePath, StringComparer.Ordinal)
            .ThenBy(result => result.TargetDb, StringComparer.Ordinal)
            .ThenBy(result => result.TargetPath, StringComparer.Ordinal)
            .Take(limit)
            .ToList();
    }

    internal static List<string> BuildWorkspaceDependencyDatabaseList(QueryCommandOptions options)
    {
        var primaryDb = Path.GetFullPath(DbPathResolver.NormalizeDbPath(options.DbPath));
        var comparer = PathCasing.IsIgnoreCase(primaryDb)
            ? StringComparer.OrdinalIgnoreCase
            : StringComparer.Ordinal;
        return options.WorkspaceDbPaths
            .Select(path => Path.GetFullPath(DbPathResolver.NormalizeDbPath(path)))
            .Prepend(primaryDb)
            .Distinct(comparer)
            .ToList();
    }

    private static bool TryWriteWorkspaceDependencyFanOutError(QueryCommandOptions options)
    {
        if (options.WorkspaceDbPaths.Count == 0)
            return false;

        var memberDbs = BuildWorkspaceDependencyDatabaseList(options);
        var pairCount = memberDbs.Count * (memberDbs.Count - 1);
        if (memberDbs.Count <= MaxWorkspaceDependencyDatabaseCount &&
            pairCount <= MaxWorkspaceDependencyDatabasePairCount)
            return false;

        var maxAdditional = MaxWorkspaceDependencyDatabaseCount - 1;
        var additionalCount = Math.Max(0, memberDbs.Count - 1);
        CommandErrorWriter.WriteStderr($"Error: deps --workspace-db accepts at most {maxAdditional} distinct additional databases ({MaxWorkspaceDependencyDatabaseCount} total including --db), which is {MaxWorkspaceDependencyDatabasePairCount} ordered cross-database pairs; got {additionalCount} additional ({memberDbs.Count} total, {pairCount} pairs).");
        CommandErrorWriter.WriteStderr("Hint: pass fewer --workspace-db values or run deps separately for smaller workspace member groups.");
        return true;
    }

    private static bool TryWriteInvalidWorkspaceDependencyDatabaseError(QueryCommandOptions options, out int exitCode)
    {
        exitCode = CommandExitCodes.Success;
        if (options.WorkspaceDbPaths.Count == 0)
            return false;

        foreach (var dbPath in BuildWorkspaceDependencyDatabaseList(options).Skip(1))
        {
            if (DbContext.TryValidateExistingCodeIndexDb(
                    dbPath,
                    requireWritable: false,
                    requireSupportedUserVersion: true,
                    out var validationMessage,
                    out var isNotFound,
                    out var isSchemaTooNew))
                continue;

            var errorCode = isNotFound
                ? CommandErrorCodes.DbNotFound
                : isSchemaTooNew
                    ? CommandErrorCodes.SchemaTooNew
                    : CommandErrorCodes.DbError;
            CommandErrorWriter.WriteStderr($"Error [{errorCode}]: attached workspace database cannot be used for cross-database dependency query: {validationMessage}");
            CommandErrorWriter.WriteStderr(isNotFound
                ? "Hint: pass an existing CodeIndex database to `--workspace-db`, or run `cdidx index <workspacePath>` for that workspace member first."
                : isSchemaTooNew
                    ? "Hint: run the query with a current cdidx binary, or rebuild that workspace member database with this cdidx version before using `--workspace-db`."
                    : "Hint: pass only CodeIndex databases created by `cdidx index` to `--workspace-db`; remove stale, empty, or unrelated SQLite files from the workspace database list.");
            exitCode = CommandExitCodes.DatabaseError;
            return true;
        }

        return false;
    }

    private static List<FileDependencyResult> GetCrossDatabaseFileDependencies(string sourceDbPath, string targetDbPath, QueryCommandOptions options, bool reverse, int limit)
    {
        using var sourceDb = new DbContext(sourceDbPath);
        sourceDb.TryMigrateForRead();
        var connection = sourceDb.Connection;
        AttachCrossDatabaseTarget(connection, targetDbPath);

        using var cmd = connection.CreateCommand();
        var sourcePathExpr = reverse ? "dst.path" : "src.path";
        var targetPathExpr = reverse ? "src.path" : "dst.path";
        cmd.CommandText = $@"
            WITH edges AS (
            SELECT {sourcePathExpr} AS source_path,
                   {targetPathExpr} AS target_path,
                   r.symbol_name
            FROM symbol_references r
            JOIN files src ON src.id = r.file_id
            JOIN targetdb.symbols s ON s.name = r.symbol_name
            JOIN targetdb.files dst ON dst.id = s.file_id
            WHERE 1 = 1";
        if (options.Lang != null)
        {
            cmd.CommandText += " AND src.lang = @lang AND dst.lang = @lang";
            SqliteCommandPolicy.Add(cmd, "@lang", options.Lang);
        }
        AddCrossDatabasePathFilters(cmd, "src", options.PathPatterns, include: !reverse);
        AddCrossDatabasePathFilters(cmd, "dst", options.PathPatterns, include: reverse);
        AddCrossDatabaseExcludeFilters(cmd, "src", options.ExcludePaths, include: !reverse);
        AddCrossDatabaseExcludeFilters(cmd, "dst", options.ExcludePaths, include: reverse);
        if (options.ExcludeTests)
            cmd.CommandText += reverse
                ? $" AND NOT {BuildCrossDatabaseTestPathCondition("dst")}"
                : $" AND NOT {BuildCrossDatabaseTestPathCondition("src")}";
        cmd.CommandText += @"
            ),
            edge_totals AS (
                SELECT source_path,
                       target_path,
                       COUNT(*) AS reference_count
                FROM edges
                GROUP BY source_path, target_path
            ),
            distinct_edge_symbols AS (
                SELECT DISTINCT source_path, target_path, symbol_name
                FROM edges
            ),
            ranked_edge_symbols AS (
                SELECT source_path,
                       target_path,
                       symbol_name,
                       ROW_NUMBER() OVER (PARTITION BY source_path, target_path ORDER BY symbol_name) AS symbol_rank
                FROM distinct_edge_symbols
            )
            SELECT edge_totals.source_path,
                   edge_totals.target_path,
                   edge_totals.reference_count,
                   COALESCE(GROUP_CONCAT(CASE WHEN ranked_edge_symbols.symbol_rank <= @symbolSampleLimit THEN ranked_edge_symbols.symbol_name END), '') AS symbols
            FROM edge_totals
            LEFT JOIN ranked_edge_symbols
              ON ranked_edge_symbols.source_path = edge_totals.source_path
             AND ranked_edge_symbols.target_path = edge_totals.target_path
            GROUP BY edge_totals.source_path, edge_totals.target_path, edge_totals.reference_count
            ORDER BY edge_totals.reference_count DESC, edge_totals.source_path, edge_totals.target_path
            LIMIT @limit";
        SqliteCommandPolicy.Add(cmd, "@limit", limit);
        SqliteCommandPolicy.Add(cmd, "@symbolSampleLimit", DbReader.DependencySymbolSampleLimit);

        var results = new List<FileDependencyResult>();
        using var reader = cmd.ExecuteReader();
        while (reader.Read())
        {
            results.Add(new FileDependencyResult
            {
                SourcePath = reader.GetString(0),
                TargetPath = reader.GetString(1),
                SourceDb = reverse ? targetDbPath : sourceDbPath,
                TargetDb = reverse ? sourceDbPath : targetDbPath,
                ReferenceCount = reader.GetInt32(2),
                Symbols = reader.IsDBNull(3) ? string.Empty : reader.GetString(3),
            });
        }
        return results;
    }

    private static void AttachCrossDatabaseTarget(SqliteConnection connection, string targetDbPath)
    {
        try
        {
            AttachCrossDatabaseTargetCore(connection, targetDbPath);
        }
        catch (SqliteException) when (!SqliteFileUri.StartsWithFileScheme(targetDbPath) && File.Exists(LongPath.EnsureWindowsPrefix(targetDbPath)))
        {
            AttachCrossDatabaseTargetCore(connection, DbContext.ToReadOnlyUri(targetDbPath));
        }
    }

    private static void AttachCrossDatabaseTargetCore(SqliteConnection connection, string targetDbPath)
    {
        using var attach = connection.CreateCommand();
        attach.CommandText = "ATTACH DATABASE @targetDb AS targetdb";
        attach.Parameters.Add("@targetDb", SqliteType.Text).Value = targetDbPath;
        attach.ExecuteNonQuery();
    }

    private static void AddCrossDatabasePathFilters(SqliteCommand cmd, string alias, IReadOnlyList<string> patterns, bool include)
    {
        if (!include || patterns.Count == 0)
            return;
        SqliteDynamicSql.EnsureParameterBudget(patterns.Count, "cross-database path filters");
        var parts = new List<string>(patterns.Count);
        for (var i = 0; i < patterns.Count; i++)
        {
            var name = SqliteDynamicSql.BuildParameterName($"crossPath{alias}", i);
            parts.Add($"{alias}.path LIKE {name} ESCAPE '\\'");
            cmd.Parameters.Add(name, SqliteType.Text).Value = CrossDatabaseGlobToLikePattern(patterns[i]);
        }
        cmd.CommandText += " AND (" + string.Join(" OR ", parts) + ")";
    }

    private static void AddCrossDatabaseExcludeFilters(SqliteCommand cmd, string alias, IReadOnlyList<string> patterns, bool include)
    {
        if (!include || patterns.Count == 0)
            return;
        SqliteDynamicSql.EnsureParameterBudget(patterns.Count, "cross-database exclude path filters");
        for (var i = 0; i < patterns.Count; i++)
        {
            var name = SqliteDynamicSql.BuildParameterName($"crossExclude{alias}", i);
            cmd.CommandText += $" AND {alias}.path NOT LIKE {name} ESCAPE '\\'";
            cmd.Parameters.Add(name, SqliteType.Text).Value = CrossDatabaseGlobToLikePattern(patterns[i]);
        }
    }

    internal static string BuildCrossDatabaseTestPathConditionForTesting(string alias)
        => BuildCrossDatabaseTestPathCondition(alias);

    private static string BuildCrossDatabaseTestPathCondition(string alias)
        => DbReader.TestPathCondition.Replace("f.path", $"{alias}.path", StringComparison.Ordinal);

    private static string CrossDatabaseGlobToLikePattern(string pattern)
    {
        var builder = new System.Text.StringBuilder(pattern.Length);
        foreach (var ch in pattern)
        {
            builder.Append(ch switch
            {
                '*' => '%',
                '?' => '_',
                '%' => "\\%",
                '_' => "\\_",
                '\\' => "\\\\",
                _ => ch,
            });
        }
        return builder.ToString();
    }

    private static void TagFileDependencyResults(IEnumerable<FileDependencyResult> results, string dbPath)
    {
        foreach (var result in results)
        {
            result.SourceDb = dbPath;
            result.TargetDb = dbPath;
        }
    }

    public static int RunHotspots(string[] cmdArgs, JsonSerializerOptions jsonOptions)
    {
        bool groupByName = cmdArgs.Any(a => a == "--group-by-name");
        var previewOptionError = ValidatePreviewOptions("hotspots", cmdArgs, allowMaxLineWidth: false, allowFocusOptions: false);
        if (previewOptionError != null)
        {
            CommandErrorWriter.WriteStderr(previewOptionError);
            return CommandExitCodes.UsageError;
        }
        var options = ParseArgs(
            cmdArgs,
            jsonDefault: false,
            validateDefaultSnippetLines: false,
            validateDefaultMaxLineWidth: false);
        if (TryWriteUnsupportedOptionError("hotspots", cmdArgs, CliFlagSchema.GetAcceptedFlagNamesForCommand("hotspots")))
            return CommandExitCodes.UsageError;
        if (TryWriteParseError(options, "hotspots"))
            return CommandExitCodes.UsageError;
        if (TryWriteInvalidKindFilterError(options, "hotspots", KnownSymbolKindFilters))
            return CommandExitCodes.InvalidArgument;
        if (TryWriteUnexpectedPositionals("hotspots", options))
            return CommandExitCodes.UsageError;
        if (!TryResolveHotspotsGroupBy(options.GroupBy, options.Lang, groupByName, out var groupBy, out var groupByError))
        {
            CommandErrorWriter.WriteStderr(groupByError);
            CommandErrorWriter.WriteStderr("Usage: cdidx hotspots [--db <path>] [--json] [--limit <n>] [--kind <kind>] [--lang <lang>] [--path <glob>] [--exclude-path <glob>] [--exclude-tests] [--count] [--group-by <symbol|file|statement>] [--group-by-name]");
            return CommandExitCodes.UsageError;
        }

        return WithDb(options, jsonOptions, reader =>
        {
            var baseSqlGraphSignal = reader.GetSqlGraphContractSignal(options.Lang, options.PathPatterns, options.ExcludePaths, options.ExcludeTests);
            var zeroResultSqlGraphSignal = NarrowSqlGraphContractSignal(
                baseSqlGraphSignal,
                reader.ScopeMayIncludeSqlSymbols(options.Kind, options.Lang, options.PathPatterns, options.ExcludePaths, options.ExcludeTests));
            if (groupBy == HotspotsGroupedByNameKind)
            {
                if (options.CountOnly)
                {
                    var countSummary = reader.CountGroupedSymbolHotspots(options.Kind, options.Lang, options.PathPatterns, options.ExcludePaths, options.ExcludeTests, visibilityFilters: options.VisibilityFilters, excludeVisibilityFilters: options.ExcludeVisibilityFilters);
                    var countSqlGraphSignal = countSummary.Count == 0
                        ? zeroResultSqlGraphSignal
                        : NarrowSqlGraphContractSignal(
                            baseSqlGraphSignal,
                            reader.ScopeMayIncludeSqlSymbols(options.Kind, options.Lang, options.PathPatterns, options.ExcludePaths, options.ExcludeTests));
                    if (options.Json)
                    {
                        var payload = countSummary.Count == 0
                            ? BuildGroupedHotspotsZeroJsonPayload(reader, jsonOptions, countOnly: true, graphAvailable: reader._hasReferencesTable, queryOptions: options)
                            : new JsonObject
                            {
                                ["count"] = countSummary.Count,
                                ["files"] = countSummary.FileCount,
                                ["definition_site_total"] = countSummary.DefinitionSiteTotal,
                                ["grouped_by"] = HotspotsGroupedByNameKind,
                            };
                        AddSqlGraphContractJsonFields(payload, countSqlGraphSignal);
                        Console.WriteLine(payload.ToJsonString(jsonOptions));
                    }
                    else
                    {
                        Console.WriteLine($"{countSummary.Count}");
                        WriteSqlGraphContractWarningIfNeeded(json: false, countSqlGraphSignal, reader, options);
                    }
                    return countSummary.Count == 0 ? ZeroResultExitCode(options) : CommandExitCodes.Success;
                }

                var groupedResults = reader.GetGroupedSymbolHotspots(options.Limit, options.Kind, options.Lang, options.PathPatterns, options.ExcludePaths, options.ExcludeTests, visibilityFilters: options.VisibilityFilters, excludeVisibilityFilters: options.ExcludeVisibilityFilters);
                var effectiveSqlGraphSignal = groupedResults.Count == 0
                    ? zeroResultSqlGraphSignal
                    : NarrowSqlGraphContractSignalByLanguages(baseSqlGraphSignal, groupedResults.Select(result => result.Symbol.Lang), options.Lang);
                if (groupedResults.Count == 0)
                {
                    if (options.CountOnly)
                    {
                        if (options.Json)
                        {
                            var payload = BuildGroupedHotspotsZeroJsonPayload(reader, jsonOptions, countOnly: true, graphAvailable: reader._hasReferencesTable, queryOptions: options);
                            AddSqlGraphContractJsonFields(payload, effectiveSqlGraphSignal);
                            Console.WriteLine(payload.ToJsonString(jsonOptions));
                        }
                        else
                            WriteGraphCountResult(reader, 0, 0, options, jsonOptions, reader._hasReferencesTable, new ExactQuerySignal(true, HasMissingIndex: false, HasMissingTable: false, null));
                    }
                    else if (options.Json)
                    {
                        var payload = BuildGroupedHotspotsZeroJsonPayload(reader, jsonOptions, countOnly: false, graphAvailable: reader._hasReferencesTable, queryOptions: options);
                        AddSqlGraphContractJsonFields(payload, effectiveSqlGraphSignal);
                        Console.WriteLine(payload.ToJsonString(jsonOptions));
                    }
                    else
                    {
                        CommandErrorWriter.WriteStderr(BuildZeroResultLine("No symbol hotspots found", options));
                        WriteZeroResultHints(options, reader);
                        WriteKindHint(options.Kind, reader);
                        WriteLangHint(options.Lang, reader);
                        WriteSqlGraphContractWarningIfNeeded(json: false, effectiveSqlGraphSignal, reader, options);
                        WriteDegradedGraphZeroResult(reader, "hotspots", json: false, graphAvailable: reader._hasReferencesTable, jsonOptions);
                    }
                    return ZeroResultExitCode(options);
                }

                var definitionSiteTotal = groupedResults.Sum(g => g.DefinitionSites);

                if (options.Json)
                {
                    var items = groupedResults
                        .Select(g => new GroupedSymbolHotspotJsonResult(
                            g.Symbol.Name,
                            g.Symbol.Kind,
                            g.Symbol.Path,
                            g.Symbol.Line,
                            g.ReferenceCount,
                            g.ReferenceScore,
                            g.RankingScore,
                            g.GenericNamePenalty,
                            g.Symbol.Visibility,
                            g.Symbol.ContainerName,
                            g.DefinitionSites,
                            g.Paths,
                            g.PathsTruncated,
                            BuildGroupedHotspotRepresentative(g),
                            g.DefinitionSiteDetails.Select(ToGroupedHotspotSiteJson).ToList()))
                        .ToList();
                    var payload = new JsonObject
                    {
                        ["count"] = groupedResults.Count,
                        ["definition_site_total"] = definitionSiteTotal,
                        ["grouped_by"] = HotspotsGroupedByNameKind,
                        ["hotspots"] = JsonSerializer.SerializeToNode(items, CliJsonSerializerContextFactory.Create(jsonOptions).ListGroupedSymbolHotspotJsonResult)
                    };
                    AddSqlGraphContractJsonFields(payload, effectiveSqlGraphSignal);
                    Console.WriteLine(payload.ToJsonString(jsonOptions));
                }
                else
                {
                    foreach (var g in groupedResults)
                    {
                        var s = g.Symbol;
                        var vis = s.Visibility != null ? $" [{s.Visibility}]" : "";
                        var multi = g.DefinitionSites > 1 ? $" (×{g.DefinitionSites} sites)" : "";
                        Console.WriteLine($"{FormatHotspotScore(g.ReferenceScore),5} score {g.ReferenceCount,5} refs  {ConsoleUi.ColorizeKind(s.Kind, 12)} {s.Name,-40} {s.Path}:{s.Line}{vis}{multi}");
                    }
                    CommandErrorWriter.WriteStderr($"({groupedResults.Count} unique name/kind groups, {definitionSiteTotal} definition sites)");
                    WriteSqlGraphContractWarningIfNeeded(json: false, effectiveSqlGraphSignal, reader, options);
                }
                return CommandExitCodes.Success;
            }

            if (groupBy == HotspotsGroupedByFile)
            {
                var fileHotspotSignal = reader.GetHotspotFamilySignal(options.Lang);
                if (options.CountOnly)
                {
                    var countSummary = reader.CountFileSymbolHotspots(options.Kind, options.Lang, options.PathPatterns, options.ExcludePaths, options.ExcludeTests, visibilityFilters: options.VisibilityFilters, excludeVisibilityFilters: options.ExcludeVisibilityFilters);
                    var countSqlGraphSignal = countSummary.Count == 0
                        ? zeroResultSqlGraphSignal
                        : NarrowSqlGraphContractSignal(
                            baseSqlGraphSignal,
                            reader.ScopeMayIncludeSqlSymbols(options.Kind, options.Lang, options.PathPatterns, options.ExcludePaths, options.ExcludeTests));
                    if (options.Json)
                    {
                        var payload = new JsonObject
                        {
                            ["count"] = countSummary.Count,
                            ["files"] = countSummary.FileCount,
                            ["graph_table_available"] = reader._hasReferencesTable,
                            ["grouped_by"] = groupBy,
                        };
                        AddHotspotFamilyJsonFields(payload, fileHotspotSignal);
                        AddSqlGraphContractJsonFields(payload, countSqlGraphSignal);
                        if (countSummary.Count == 0)
                            AddFreshnessHint(payload, reader);
                        Console.WriteLine(payload.ToJsonString(jsonOptions));
                    }
                    else
                    {
                        Console.WriteLine($"{countSummary.Count}");
                        WriteHotspotFamilyWarningIfNeeded(json: false, fileHotspotSignal);
                        WriteSqlGraphContractWarningIfNeeded(json: false, countSqlGraphSignal, reader, options);
                    }
                    return countSummary.Count == 0 ? ZeroResultExitCode(options) : CommandExitCodes.Success;
                }

                var fileResults = reader.GetFileSymbolHotspots(options.Limit, options.Kind, options.Lang, options.PathPatterns, options.ExcludePaths, options.ExcludeTests, visibilityFilters: options.VisibilityFilters, excludeVisibilityFilters: options.ExcludeVisibilityFilters);
                var effectiveSqlGraphSignal = fileResults.Count == 0
                    ? zeroResultSqlGraphSignal
                    : NarrowSqlGraphContractSignalByLanguages(baseSqlGraphSignal, fileResults.Select(result => result.Lang), options.Lang);

                if (fileResults.Count == 0)
                {
                    if (options.CountOnly)
                    {
                        if (options.Json)
                        {
                            var payload = new JsonObject
                            {
                                ["count"] = 0,
                                ["files"] = 0,
                                ["graph_table_available"] = reader._hasReferencesTable,
                                ["grouped_by"] = groupBy,
                            };
                            AddHotspotFamilyJsonFields(payload, fileHotspotSignal);
                            AddSqlGraphContractJsonFields(payload, effectiveSqlGraphSignal);
                            AddFreshnessHint(payload, reader);
                            Console.WriteLine(payload.ToJsonString(jsonOptions));
                        }
                        else
                        {
                            Console.WriteLine("0");
                            WriteHotspotFamilyWarningIfNeeded(json: false, fileHotspotSignal);
                            WriteSqlGraphContractWarningIfNeeded(json: false, effectiveSqlGraphSignal, reader, options);
                        }
                    }
                    else if (options.Json)
                    {
                        Console.WriteLine(BuildJsonZeroResultPayload(
                            reader,
                            jsonOptions,
                            resultsKey: "hotspots",
                            graphTableAvailable: reader._hasReferencesTable,
                            degraded: !reader._hasReferencesTable || !fileHotspotSignal.Ready,
                            extraFields: payload =>
                            {
                                payload["grouped_by"] = groupBy;
                                AddHotspotFamilyJsonFields(payload, fileHotspotSignal);
                                AddSqlGraphContractJsonFields(payload, effectiveSqlGraphSignal);
                            }).ToJsonString(jsonOptions));
                    }
                    else
                    {
                        CommandErrorWriter.WriteStderr("No symbol hotspots found.");
                        WriteZeroResultHints(options, reader);
                        WriteKindHint(options.Kind, reader);
                        WriteLangHint(options.Lang, reader);
                        WriteHotspotFamilyWarningIfNeeded(json: false, fileHotspotSignal);
                        WriteSqlGraphContractWarningIfNeeded(json: false, effectiveSqlGraphSignal, reader, options);
                        WriteDegradedGraphZeroResult(reader, "hotspots", json: false, graphAvailable: reader._hasReferencesTable, jsonOptions);
                    }
                    return ZeroResultExitCode(options);
                }

                if (options.Json)
                {
                    var hotspots = new JsonArray();
                    foreach (var result in fileResults)
                    {
                        hotspots.Add(new JsonObject
                        {
                            ["path"] = result.Path,
                            ["lang"] = result.Lang,
                            ["reference_count"] = result.ReferenceCount,
                            ["symbol_count"] = result.SymbolCount,
                        });
                    }
                    var payload = new JsonObject
                    {
                        ["count"] = fileResults.Count,
                        ["files"] = fileResults.Count,
                        ["grouped_by"] = groupBy,
                        ["hotspots"] = hotspots,
                    };
                    AddHotspotFamilyJsonFields(payload, fileHotspotSignal);
                    AddSqlGraphContractJsonFields(payload, effectiveSqlGraphSignal);
                    payload["query_context"] = BuildQueryContextJson(options, jsonOptions);
                    if (options.Compact)
                    {
                        payload["compact"] = true;
                        payload["omitted_sections"] = new JsonArray();
                    }
                    Console.WriteLine(payload.ToJsonString(jsonOptions));
                }
                else
                {
                    foreach (var result in fileResults)
                    {
                        Console.WriteLine($"{result.ReferenceCount,5} refs  {result.SymbolCount,5} symbols  {result.Path}");
                    }
                    CommandErrorWriter.WriteStderr($"({fileResults.Count} file hotspots; grouped_by={groupBy})");
                    WriteHotspotFamilyWarningIfNeeded(json: false, fileHotspotSignal);
                    WriteSqlGraphContractWarningIfNeeded(json: false, effectiveSqlGraphSignal, reader, options);
                }
                return CommandExitCodes.Success;
            }

            var hotspotSignal = reader.GetHotspotFamilySignal(options.Lang);
            if (options.CountOnly)
            {
                var countSummary = reader.CountSymbolHotspots(options.Kind, options.Lang, options.PathPatterns, options.ExcludePaths, options.ExcludeTests, visibilityFilters: options.VisibilityFilters, excludeVisibilityFilters: options.ExcludeVisibilityFilters);
                var countSqlGraphSignal = countSummary.Count == 0
                    ? zeroResultSqlGraphSignal
                    : NarrowSqlGraphContractSignal(
                        baseSqlGraphSignal,
                        reader.ScopeMayIncludeSqlSymbols(options.Kind, options.Lang, options.PathPatterns, options.ExcludePaths, options.ExcludeTests));
                if (options.Json)
                {
                    var payload = new JsonObject
                    {
                        ["count"] = countSummary.Count,
                        ["files"] = countSummary.FileCount,
                        ["graph_table_available"] = reader._hasReferencesTable,
                        ["grouped_by"] = groupBy,
                    };
                    if (!reader._hasReferencesTable)
                        payload["degraded"] = true;
                    AddHotspotFamilyJsonFields(payload, hotspotSignal);
                    AddSqlGraphContractJsonFields(payload, countSqlGraphSignal);
                    if (countSummary.Count == 0)
                        AddFreshnessHint(payload, reader);
                    Console.WriteLine(payload.ToJsonString(jsonOptions));
                }
                else
                {
                    Console.WriteLine($"{countSummary.Count}");
                    if (!reader._hasReferencesTable)
                        CommandErrorWriter.WriteStderr("WARN: symbol_references table missing — this count result is degraded, not authoritative.");
                    WriteHotspotFamilyWarningIfNeeded(json: false, hotspotSignal);
                    WriteSqlGraphContractWarningIfNeeded(json: false, countSqlGraphSignal, reader, options);
                }
                return countSummary.Count == 0 ? ZeroResultExitCode(options) : CommandExitCodes.Success;
            }

            var results = reader.GetSymbolHotspots(options.Limit, options.Kind, options.Lang, options.PathPatterns, options.ExcludePaths, options.ExcludeTests, visibilityFilters: options.VisibilityFilters, excludeVisibilityFilters: options.ExcludeVisibilityFilters);
            var sqlGraphSignal = results.Count == 0
                ? zeroResultSqlGraphSignal
                : NarrowSqlGraphContractSignalByLanguages(baseSqlGraphSignal, results.Select(result => result.Symbol.Lang), options.Lang);
            if (results.Count == 0)
            {
                if (options.CountOnly)
                {
                    if (!options.Json)
                    {
                        Console.WriteLine("0");
                        if (!reader._hasReferencesTable)
                            CommandErrorWriter.WriteStderr("WARN: symbol_references table missing — this count result is degraded, not authoritative.");
                        WriteHotspotFamilyWarningIfNeeded(json: false, hotspotSignal);
                    }
                    else
                    {
                        var payload = new JsonObject
                        {
                            ["count"] = 0,
                            ["files"] = 0,
                            ["graph_table_available"] = reader._hasReferencesTable,
                            ["grouped_by"] = groupBy,
                        };
                        if (!reader._hasReferencesTable)
                            payload["degraded"] = true;
                        AddHotspotFamilyJsonFields(payload, hotspotSignal);
                        AddSqlGraphContractJsonFields(payload, sqlGraphSignal);
                        AddFreshnessHint(payload, reader);
                        Console.WriteLine(payload.ToJsonString(jsonOptions));
                    }
                }
                else if (options.Json && !reader._hasReferencesTable)
                    WriteDegradedGraphZeroResult(reader, "hotspots", json: true, graphAvailable: false, jsonOptions, queryOptions: options, extraFields: payload =>
                    {
                        payload["grouped_by"] = groupBy;
                        AddHotspotFamilyJsonFields(payload, hotspotSignal);
                        AddSqlGraphContractJsonFields(payload, sqlGraphSignal);
                    });
                else if (options.Json)
                    Console.WriteLine(BuildJsonZeroResultPayload(
                        reader,
                        jsonOptions,
                        resultsKey: "hotspots",
                        graphTableAvailable: true,
                        degraded: !hotspotSignal.Ready,
                        extraFields: payload =>
                        {
                            payload["grouped_by"] = groupBy;
                            AddHotspotFamilyJsonFields(payload, hotspotSignal);
                            AddSqlGraphContractJsonFields(payload, sqlGraphSignal);
                        }).ToJsonString(jsonOptions));
                else if (!options.Json)
                {
                    CommandErrorWriter.WriteStderr(BuildZeroResultLine("No symbol hotspots found", options));
                    WriteZeroResultHints(options, reader);
                    WriteKindHint(options.Kind, reader);
                    WriteLangHint(options.Lang, reader);
                    WriteHotspotFamilyWarningIfNeeded(json: false, hotspotSignal);
                    WriteSqlGraphContractWarningIfNeeded(json: false, sqlGraphSignal, reader, options);
                    WriteDegradedGraphZeroResult(reader, "hotspots", json: false, graphAvailable: reader._hasReferencesTable, jsonOptions);
                }
                return ZeroResultExitCode(options);
            }

            if (options.Json)
            {
                var items = results
                    .Select(r => new SymbolHotspotJsonResult(
                        r.Symbol.Name,
                        r.Symbol.Kind,
                        r.Symbol.Path,
                        r.Symbol.Line,
                        r.ReferenceCount,
                        r.ReferenceScore,
                        r.RankingScore,
                        r.GenericNamePenalty,
                        r.Symbol.Visibility,
                        r.Symbol.ContainerName))
                    .ToList();
                var payload = new JsonObject
                {
                    ["count"] = results.Count,
                    ["grouped_by"] = groupBy,
                    ["hotspots"] = JsonSerializer.SerializeToNode(items, CliJsonSerializerContextFactory.Create(jsonOptions).ListSymbolHotspotJsonResult)
                };
                AddHotspotFamilyJsonFields(payload, hotspotSignal);
                AddSqlGraphContractJsonFields(payload, sqlGraphSignal);
                Console.WriteLine(payload.ToJsonString(jsonOptions));
            }
            else
            {
                foreach (var r in results)
                {
                    var s = r.Symbol;
                    var vis = s.Visibility != null ? $" [{s.Visibility}]" : "";
                    Console.WriteLine($"{FormatHotspotScore(r.ReferenceScore),5} score {r.ReferenceCount,5} refs  {ConsoleUi.ColorizeKind(s.Kind, 12)} {s.Name,-40} {s.Path}:{s.Line}{vis}");
                }
                CommandErrorWriter.WriteStderr($"({results.Count} symbol hotspots; grouped_by={groupBy})");
                WriteHotspotFamilyWarningIfNeeded(json: false, hotspotSignal);
                WriteSqlGraphContractWarningIfNeeded(json: false, sqlGraphSignal, reader, options);
            }
            return CommandExitCodes.Success;
        });
    }

    private static GroupedSymbolHotspotSiteJsonResult BuildGroupedHotspotRepresentative(GroupedHotspotResult result)
    {
        var representative = result.DefinitionSiteDetails.FirstOrDefault(site =>
            string.Equals(site.Path, result.Symbol.Path, StringComparison.Ordinal)
            && site.Line == result.Symbol.Line);
        if (representative != null)
            return ToGroupedHotspotSiteJson(representative);

        return new GroupedSymbolHotspotSiteJsonResult(
            result.Symbol.Path,
            result.Symbol.Lang,
            result.Symbol.Line,
            result.Symbol.Visibility,
            result.Symbol.ContainerName,
            LogicalTargetKey: null);
    }

    private static GroupedSymbolHotspotSiteJsonResult ToGroupedHotspotSiteJson(GroupedHotspotDefinitionSite site)
        => new(
            site.Path,
            site.Lang,
            site.Line,
            site.Visibility,
            site.Container,
            site.LogicalTargetKey);

    public static int RunUnused(string[] cmdArgs, JsonSerializerOptions jsonOptions)
    {
        var byBucket = cmdArgs.Any(arg => arg == "--by-bucket");
        var previewOptionError = ValidatePreviewOptions("unused", cmdArgs, allowMaxLineWidth: false, allowFocusOptions: false);
        if (previewOptionError != null)
        {
            CommandErrorWriter.WriteStderr(previewOptionError);
            return CommandExitCodes.UsageError;
        }
        var options = ParseArgs(
            cmdArgs,
            jsonDefault: false,
            validateDefaultSnippetLines: false,
            validateDefaultMaxLineWidth: false);
        if (TryWriteUnsupportedOptionError("unused", cmdArgs, CliFlagSchema.GetAcceptedFlagNamesForCommand("unused")))
            return CommandExitCodes.UsageError;
        if (TryWriteParseError(options, "unused"))
            return CommandExitCodes.UsageError;
        if (TryWriteInvalidKindFilterError(options, "unused", KnownSymbolKindFilters))
            return CommandExitCodes.InvalidArgument;
        if (TryWriteInvalidUnusedFilterError(options))
            return CommandExitCodes.InvalidArgument;
        if (TryWriteUnexpectedPositionals("unused", options))
            return CommandExitCodes.UsageError;
        if (options.SearchCursor.HasValue)
        {
            WriteUsageError(
                "--cursor for unused must use the `unused:<offset>` cursor returned by a previous unused response.",
                GetUsageLineOrThrow("unused"),
                "Use the `next_cursor` value from `cdidx unused --json`.");
            return CommandExitCodes.UsageError;
        }
        if (options.OutlineCursorOffset.HasValue)
        {
            WriteUsageError(
                "--cursor for unused must use the `unused:<offset>` cursor returned by a previous unused response.",
                GetUsageLineOrThrow("unused"),
                "`outline:<offset>` cursors are for `cdidx outline <path>`.");
            return CommandExitCodes.UsageError;
        }
        if (options.UnusedCursorOffset.HasValue && options.CountOnly)
        {
            WriteUsageError(
                "--cursor cannot be used with `unused --count`.",
                GetUsageLineOrThrow("unused"),
                "Remove `--count` to page unused results.");
            return CommandExitCodes.UsageError;
        }
        var unusedScope = BuildUnusedAuditScopeFilters(options);

        return WithDb(options, jsonOptions, reader =>
        {
            // Warn if user specified an unsupported language / 未対応言語の場合は警告
            if (options.Lang != null && !ReferenceExtractor.SupportsLanguage(options.Lang) && !options.Json)
                CommandErrorWriter.WriteStderr($"Warning: '{options.Lang}' does not support reference extraction. Unused results are unavailable for this language.");

            bool? graphSupported = options.Lang != null ? ReferenceExtractor.SupportsLanguage(options.Lang) : null;
            var graphSupportReason = ReferenceExtractor.BuildGraphSupportReason(options.Lang, graphSupported);
            var baseSqlGraphSignal = reader.GetSqlGraphContractSignal(options.Lang, unusedScope.PathPatterns, unusedScope.ExcludePaths, unusedScope.ExcludeTests);
            var zeroResultSqlGraphSignal = NarrowSqlGraphContractSignal(
                baseSqlGraphSignal,
                reader.ScopeMayIncludeSqlSymbols(options.Kind, options.Lang, unusedScope.PathPatterns, unusedScope.ExcludePaths, unusedScope.ExcludeTests));
            if (options.CountOnly)
            {
                if (options.Json)
                {
                    var countSummary = reader.CountUnusedSymbolsDetailed(
                        options.Kind,
                        options.Lang,
                        unusedScope.PathPatterns,
                        unusedScope.ExcludePaths,
                        unusedScope.ExcludeTests,
                        visibilityFilters: unusedScope.VisibilityFilters,
                        excludeVisibilityFilters: unusedScope.ExcludeVisibilityFilters,
                        bucketFilter: options.UnusedBucket,
                        minConfidence: options.MinUnusedConfidence);
                    var effectiveSqlGraphSignal = countSummary.Count == 0
                        ? zeroResultSqlGraphSignal
                        : NarrowSqlGraphContractSignal(
                            baseSqlGraphSignal,
                            countSummary.IncludesSql || DbReader.IsSqlLanguage(options.Lang));
                    var payload = new JsonObject
                    {
                        ["count"] = countSummary.Count,
                        ["files"] = countSummary.FileCount,
                        ["returned_bucket_counts"] = JsonSerializer.SerializeToNode(ToUnusedCountDictionary(countSummary.BucketCounts), CliJsonSerializerContextFactory.Create(jsonOptions).DictionaryStringInt32),
                        ["summary"] = BuildUnusedCountSummaryJson(countSummary, jsonOptions),
                        ["bucket_taxonomy"] = BuildUnusedBucketTaxonomyJson(),
                        ["graph_supported"] = graphSupported,
                        ["graph_support_reason"] = graphSupportReason,
                        ["graph_table_available"] = reader._hasReferencesTable,
                        ["degraded"] = !reader._hasReferencesTable
                    };
                    AddSqlGraphContractJsonFields(payload, effectiveSqlGraphSignal);
                    payload["query_context"] = BuildUnusedQueryContextJson(options, unusedScope, jsonOptions);
                    if (options.Compact)
                    {
                        payload["compact"] = true;
                        payload["omitted_sections"] = new JsonArray();
                    }
                    Console.WriteLine(payload.ToJsonString(jsonOptions));
                }
                else
                {
                    var countSummary = reader.CountUnusedSymbols(
                        options.Kind,
                        options.Lang,
                        unusedScope.PathPatterns,
                        unusedScope.ExcludePaths,
                        unusedScope.ExcludeTests,
                        visibilityFilters: unusedScope.VisibilityFilters,
                        excludeVisibilityFilters: unusedScope.ExcludeVisibilityFilters,
                        bucketFilter: options.UnusedBucket,
                        minConfidence: options.MinUnusedConfidence);
                    var effectiveSqlGraphSignal = countSummary.Count == 0
                        ? zeroResultSqlGraphSignal
                        : NarrowSqlGraphContractSignal(
                            baseSqlGraphSignal,
                            countSummary.IncludesSql || DbReader.IsSqlLanguage(options.Lang));
                    Console.WriteLine($"{countSummary.Count}");
                    WriteSqlGraphContractWarningIfNeeded(json: false, effectiveSqlGraphSignal, reader, options);
                    WriteDegradedGraphZeroResult(reader, "unused", json: false, graphAvailable: reader._hasReferencesTable, jsonOptions);
                }
                return CommandExitCodes.Success;
            }

            var pageOffset = options.UnusedCursorOffset ?? 0;
            var fetchLimit = GetUnusedFetchLimit(options.Limit, pageOffset);
            var fetchedResults = reader.GetUnusedSymbols(
                fetchLimit,
                options.Kind,
                options.Lang,
                unusedScope.PathPatterns,
                unusedScope.ExcludePaths,
                unusedScope.ExcludeTests,
                visibilityFilters: unusedScope.VisibilityFilters,
                excludeVisibilityFilters: unusedScope.ExcludeVisibilityFilters,
                bucketFilter: options.UnusedBucket,
                minConfidence: options.MinUnusedConfidence);
            var results = fetchedResults
                .Skip(pageOffset)
                .Take(options.Limit)
                .ToList();
            var nextCursor = fetchedResults.Count > pageOffset + options.Limit
                ? FormatUnusedCursor(pageOffset + options.Limit)
                : null;
            var sqlGraphSignal = results.Count == 0
                ? zeroResultSqlGraphSignal
                : NarrowSqlGraphContractSignalByLanguages(
                    baseSqlGraphSignal,
                    results.Select(result => result.Lang),
                    options.Lang);
            if (results.Count == 0)
            {
                if (options.Json)
                {
                    Console.WriteLine(BuildUnusedJsonPayload(
                        Array.Empty<UnusedSymbolResult>(),
                        graphSupported,
                        graphSupportReason,
                        sqlGraphSignal,
                        reader._hasReferencesTable,
                        jsonOptions,
                        options,
                        unusedScope,
                        nextCursor: nextCursor));
                }
                else
                {
                    CommandErrorWriter.WriteStderr(BuildZeroResultLine("No unused symbols found", options));
                    WriteZeroResultHints(options, reader);
                    WriteKindHint(options.Kind, reader);
                    WriteLangHint(options.Lang, reader);
                    WriteSqlGraphContractWarningIfNeeded(json: false, sqlGraphSignal, reader, options);
                    WriteDegradedGraphZeroResult(reader, "symbols", json: false, graphAvailable: reader._hasReferencesTable, jsonOptions);
                }
                return ZeroResultExitCode(options);
            }

            if (options.Json)
            {
                Console.WriteLine(BuildUnusedJsonPayload(results, graphSupported, graphSupportReason, sqlGraphSignal, reader._hasReferencesTable, jsonOptions, options, unusedScope, byBucket: byBucket, nextCursor: nextCursor));
            }
            else
            {
                var bucketCounts = BuildUnusedBucketCounts(results);
                foreach (var bucket in OrderedUnusedBuckets)
                {
                    var bucketResults = results.Where(s => s.UnusedBucket == bucket).ToList();
                    if (bucketResults.Count == 0)
                        continue;

                    Console.WriteLine($"{GetUnusedBucketHeading(bucket)} ({bucketResults.Count})");
                    foreach (var s in bucketResults)
                    {
                        var vis = s.Visibility != null ? $" [{s.Visibility}]" : "";
                        var container = s.ContainerName != null ? $" in {s.ContainerName}" : "";
                        Console.WriteLine($"{ConsoleUi.ColorizeKind(s.Kind, 12)} {s.Name,-40} {s.Path}:{s.Line}{vis}{container}");
                        Console.WriteLine($"             confidence={s.UnusedConfidence} reason={s.UnusedReason}");
                    }
                    Console.WriteLine();
                }
                var summaryBuckets = OrderedUnusedBuckets
                    .Where(bucketCounts.ContainsKey)
                    .Select(bucket => $"{GetUnusedBucketHeading(bucket)}: {bucketCounts[bucket]}");
                CommandErrorWriter.WriteStderr($"({results.Count} returned potentially unused symbols; returned buckets: {string.Join(", ", summaryBuckets)})");
                if (nextCursor != null)
                    CommandErrorWriter.WriteStderr($"next_cursor={nextCursor}");
                WriteSqlGraphContractWarningIfNeeded(json: false, sqlGraphSignal, reader, options);
            }
            return CommandExitCodes.Success;
        });
    }

    internal static readonly string[] OrderedUnusedBuckets =
    [
        "likely_unused_private",
        "maybe_unused_nonpublic",
        "public_or_exported_no_refs",
        "reflection_or_config_suspect",
    ];

    private static readonly string[] UnusedSourceAuditExcludePaths =
    [
        "*.md",
        "docs/*",
        "doc/*",
        "CHANGELOG.md",
        "changelog.d/*",
        "README.md",
        "USER_GUIDE.md",
        "DEVELOPER_GUIDE.md",
        "TESTING_GUIDE.md",
        "AGENT_GUIDE.md",
        ".codex/*",
        ".github/*",
    ];

    private static readonly string[] UnusedSourceAuditVisibilityFilters =
    [
        "private",
        "internal",
    ];

    private sealed record UnusedAuditScopeFilters(
        IReadOnlyList<string> PathPatterns,
        IReadOnlyList<string> ExcludePaths,
        bool ExcludeTests,
        IReadOnlyList<string> VisibilityFilters,
        IReadOnlyList<string> ExcludeVisibilityFilters,
        bool AppliedSourceDefaults);

    private static UnusedAuditScopeFilters BuildUnusedAuditScopeFilters(QueryCommandOptions options)
    {
        if (!options.AuditScopeExplicit
            || !string.Equals(options.AuditScope, SearchAuditRecipes.DefaultAuditScope, StringComparison.OrdinalIgnoreCase))
        {
            return new(
                options.PathPatterns,
                options.ExcludePaths,
                options.ExcludeTests,
                options.VisibilityFilters,
                options.ExcludeVisibilityFilters,
                AppliedSourceDefaults: false);
        }

        var excludePaths = new List<string>(options.ExcludePaths);
        AddDistinct(excludePaths, UnusedSourceAuditExcludePaths);
        var visibilityFilters = options.VisibilityFilters.Count > 0
            ? options.VisibilityFilters
            : [.. UnusedSourceAuditVisibilityFilters];
        return new(
            options.PathPatterns,
            excludePaths,
            ExcludeTests: true,
            visibilityFilters,
            options.ExcludeVisibilityFilters,
            AppliedSourceDefaults: true);
    }

    internal static Dictionary<string, int> BuildUnusedBucketCounts(IEnumerable<UnusedSymbolResult> results)
    {
        var grouped = results
            .GroupBy(result => result.UnusedBucket, StringComparer.Ordinal)
            .ToDictionary(group => group.Key, group => group.Count(), StringComparer.Ordinal);
        var ordered = new Dictionary<string, int>(StringComparer.Ordinal);
        foreach (var bucket in OrderedUnusedBuckets)
        {
            if (grouped.TryGetValue(bucket, out var count))
                ordered[bucket] = count;
        }
        return ordered;
    }

    internal static Dictionary<string, int> BuildUnusedConfidenceCounts(IEnumerable<UnusedSymbolResult> results)
        => results
            .GroupBy(result => result.UnusedConfidence, StringComparer.Ordinal)
            .OrderBy(group => GetUnusedConfidenceOrder(group.Key))
            .ToDictionary(group => group.Key, group => group.Count(), StringComparer.Ordinal);

    internal static JsonObject BuildUnusedSummaryJson(IEnumerable<UnusedSymbolResult> results, JsonSerializerOptions jsonOptions)
    {
        var resultList = results as List<UnusedSymbolResult> ?? results.ToList();
        return new JsonObject
        {
            ["by_bucket"] = JsonSerializer.SerializeToNode(BuildUnusedBucketCounts(resultList), CliJsonSerializerContextFactory.Create(jsonOptions).DictionaryStringInt32),
            ["by_confidence"] = JsonSerializer.SerializeToNode(BuildUnusedConfidenceCounts(resultList), CliJsonSerializerContextFactory.Create(jsonOptions).DictionaryStringInt32),
        };
    }

    internal static JsonObject BuildUnusedCountSummaryJson(UnusedCountResult result, JsonSerializerOptions jsonOptions)
    {
        var context = CliJsonSerializerContextFactory.Create(jsonOptions);
        return new JsonObject
        {
            ["by_bucket"] = JsonSerializer.SerializeToNode(ToUnusedCountDictionary(result.BucketCounts), context.DictionaryStringInt32),
            ["by_confidence"] = JsonSerializer.SerializeToNode(ToUnusedCountDictionary(result.ConfidenceCounts), context.DictionaryStringInt32),
        };
    }

    private static Dictionary<string, int> ToUnusedCountDictionary(IReadOnlyDictionary<string, int> counts)
        => counts.ToDictionary(pair => pair.Key, pair => pair.Value, StringComparer.Ordinal);

    private static int GetUnusedFetchLimit(int pageLimit, int pageOffset)
    {
        var requested = (long)Math.Max(pageLimit, 1) + Math.Max(pageOffset, 0) + 1;
        return requested > int.MaxValue ? int.MaxValue : (int)requested;
    }

    internal static JsonObject BuildUnusedRepresentativeSymbolsJson(IEnumerable<UnusedSymbolResult> results)
    {
        var grouped = results
            .GroupBy(result => result.UnusedBucket, StringComparer.Ordinal)
            .ToDictionary(group => group.Key, group => group.Take(3).ToList(), StringComparer.Ordinal);
        var representatives = new JsonObject();
        foreach (var bucket in OrderedUnusedBuckets)
        {
            if (!grouped.TryGetValue(bucket, out var bucketResults) || bucketResults.Count == 0)
                continue;

            var samples = new JsonArray();
            foreach (var result in bucketResults)
            {
                samples.Add(new JsonObject
                {
                    ["name"] = result.Name,
                    ["kind"] = result.Kind,
                    ["path"] = result.Path,
                    ["line"] = result.Line,
                    ["confidence"] = result.UnusedConfidence,
                });
            }

            representatives[bucket] = samples;
        }

        return representatives;
    }

    internal static JsonObject BuildUnusedBucketTaxonomyJson()
    {
        var taxonomy = new JsonObject();
        foreach (var bucket in OrderedUnusedBuckets)
            taxonomy[bucket] = new JsonObject
            {
                ["confidence"] = GetUnusedBucketConfidence(bucket),
                ["description"] = GetUnusedBucketDescription(bucket),
            };
        return taxonomy;
    }

    private static int GetUnusedConfidenceOrder(string confidence) => confidence switch
    {
        "medium" => 0,
        "low" => 1,
        _ => 2,
    };

    private static string GetUnusedBucketConfidence(string bucket) => bucket switch
    {
        "likely_unused_private" => "medium",
        "maybe_unused_nonpublic" => "low",
        "public_or_exported_no_refs" => "low",
        "reflection_or_config_suspect" => "low",
        _ => "unknown",
    };

    private static string GetUnusedBucketDescription(string bucket) => bucket switch
    {
        "likely_unused_private" => "Private symbols with no indexed references; usually the highest-signal unused candidates.",
        "maybe_unused_nonpublic" => "Internal, protected, or otherwise non-public symbols with no indexed references; review call paths and framework entry points before removal.",
        "public_or_exported_no_refs" => "Public or exported symbols with no indexed references; may still be external API surface.",
        "reflection_or_config_suspect" => "Symbols with no indexed references that look reachable through reflection, serialization, contracts, config, metadata, generated code, documentation headings, test hooks, or binding conventions.",
        _ => "Unknown unused-symbol bucket.",
    };

    private static string BuildUnusedJsonPayload(IEnumerable<UnusedSymbolResult> results, bool? graphSupported, string? graphSupportReason, SqlGraphContractSignal sqlGraphSignal, bool hasReferencesTable, JsonSerializerOptions jsonOptions, QueryCommandOptions? queryOptions = null, UnusedAuditScopeFilters? unusedScope = null, bool byBucket = false, string? nextCursor = null)
    {
        var resultList = results as List<UnusedSymbolResult> ?? results.ToList();
        var payload = new JsonObject
        {
            ["count"] = resultList.Count,
            ["graph_supported"] = graphSupported,
            ["graph_support_reason"] = graphSupportReason,
            ["returned_bucket_counts"] = JsonSerializer.SerializeToNode(BuildUnusedBucketCounts(resultList), CliJsonSerializerContextFactory.Create(jsonOptions).DictionaryStringInt32),
            ["summary"] = BuildUnusedSummaryJson(resultList, jsonOptions),
            ["bucket_taxonomy"] = BuildUnusedBucketTaxonomyJson(),
        };
        if (nextCursor != null)
            payload["next_cursor"] = nextCursor;
        if (queryOptions?.Compact == true)
        {
            payload["compact"] = true;
            payload["representative_symbols"] = BuildUnusedRepresentativeSymbolsJson(resultList);
            payload["omitted_sections"] = new JsonArray(JsonValue.Create("symbols"));
        }
        else
        {
            payload["symbols"] = JsonSerializer.SerializeToNode(resultList, CliJsonSerializerContextFactory.Create(jsonOptions).ListUnusedSymbolResult);
        }
        if (byBucket)
            payload["by_bucket"] = BuildUnusedResultsByBucketJson(resultList, jsonOptions);

        if (!hasReferencesTable)
        {
            payload["graph_table_available"] = false;
            payload["degraded"] = true;
            payload["note"] = "symbol_references table is missing in this index (legacy or read-only DB). Zero result is degraded, not authoritative.";
        }

        AddSqlGraphContractJsonFields(payload, sqlGraphSignal);
        if (queryOptions != null)
            payload["query_context"] = unusedScope != null
                ? BuildUnusedQueryContextJson(queryOptions, unusedScope, jsonOptions)
                : BuildQueryContextJson(queryOptions, jsonOptions);
        return payload.ToJsonString(jsonOptions);
    }

    private static JsonObject BuildUnusedQueryContextJson(QueryCommandOptions options, UnusedAuditScopeFilters unusedScope, JsonSerializerOptions jsonOptions)
    {
        var query = BuildQueryContextJson(options, jsonOptions);
        if (!unusedScope.AppliedSourceDefaults)
            return query;

        var context = CliJsonSerializerContextFactory.Create(jsonOptions);
        if (!options.ExcludeTests && unusedScope.ExcludeTests)
            query["effective_exclude_tests"] = true;
        if (!options.ExcludePaths.SequenceEqual(unusedScope.ExcludePaths, StringComparer.Ordinal))
            query["effective_exclude_path"] = JsonSerializer.SerializeToNode(unusedScope.ExcludePaths.ToList(), context.ListString);
        if (options.VisibilityFilters.Count == 0 && unusedScope.VisibilityFilters.Count > 0)
            query["effective_visibility"] = JsonSerializer.SerializeToNode(unusedScope.VisibilityFilters.ToList(), context.ListString);
        return query;
    }

    private static JsonObject BuildUnusedResultsByBucketJson(IEnumerable<UnusedSymbolResult> results, JsonSerializerOptions jsonOptions)
    {
        var grouped = results
            .GroupBy(result => result.UnusedBucket, StringComparer.Ordinal)
            .ToDictionary(group => group.Key, group => group.ToList(), StringComparer.Ordinal);
        var byBucket = new JsonObject();
        foreach (var bucket in OrderedUnusedBuckets)
        {
            if (grouped.TryGetValue(bucket, out var bucketResults))
                byBucket[bucket] = JsonSerializer.SerializeToNode(bucketResults, CliJsonSerializerContextFactory.Create(jsonOptions).ListUnusedSymbolResult);
            else
                byBucket[bucket] = new JsonArray();
        }
        return byBucket;
    }

    private static string GetUnusedBucketHeading(string bucket) => bucket switch
    {
        "likely_unused_private" => "Likely unused private",
        "maybe_unused_nonpublic" => "Maybe unused non-public",
        "public_or_exported_no_refs" => "Public/exported with no refs",
        "reflection_or_config_suspect" => "Intentional-surface suspects",
        _ => bucket,
    };

    // Issue kinds emitted by FileIndexer.ValidateFileContent for `validate --kind` filtering.
    // Keep in sync with `Kind = "..."` assignments in FileIndexer.cs so typos like
    // `--kind replacement_chra` produce a did-you-mean hint instead of silently filtering
    // to zero results (#1582).
    // FileIndexer.ValidateFileContent が出力する file_issues 行の Kind 一覧。
    // `--kind replacement_chra` のようなタイプミスを did-you-mean で救うため、
    // FileIndexer.cs 内の `Kind = "..."` 代入と同期させる (#1582)。
    private static readonly string[] AllValidValidateKinds =
        ["bom", "cr_only_line_endings", "file_too_large", "fts_token_too_long", "line_too_long", "mixed_line_endings", "mixed_line_endings_three_way", "non_utf8_likely", "null_byte", "replacement_char", "utf16_bom"];
    private static readonly string[] AllValidValidateSeverities =
        ["error", FileIssue.SeverityInfo, FileIssue.SeverityWarning];

    public static int RunValidate(string[] cmdArgs, JsonSerializerOptions jsonOptions)
    {
        var previewOptionError = ValidatePreviewOptions("validate", cmdArgs, allowMaxLineWidth: false, allowFocusOptions: false);
        if (previewOptionError != null)
        {
            CommandErrorWriter.WriteStderr(previewOptionError);
            return CommandExitCodes.UsageError;
        }
        var options = ParseArgs(
            cmdArgs,
            jsonDefault: false,
            validateDefaultLimit: false,
            validateDefaultSnippetLines: false,
            validateDefaultMaxLineWidth: false);
        if (TryWriteUnsupportedOptionError("validate", cmdArgs, CliFlagSchema.GetAcceptedFlagNamesForCommand("validate")))
            return CommandExitCodes.UsageError;
        if (TryWriteParseError(options, "validate"))
            return CommandExitCodes.UsageError;
        if (TryWriteUnexpectedPositionals("validate", options))
            return CommandExitCodes.UsageError;
        if (options.Severity != null && !AllValidValidateSeverities.Contains(options.Severity, StringComparer.Ordinal))
        {
            CommandErrorWriter.Write(
                $"unsupported validate severity '{options.Severity}'.",
                "use one of: info, warning, error.",
                "cdidx validate [--severity <info|warning|error>]");
            return CommandExitCodes.UsageError;
        }

        return WithDb(options, jsonOptions, reader =>
        {
            var issueLimit = HasOption(cmdArgs, "--limit") || HasOption(cmdArgs, "--top")
                ? options.Limit
                : (int?)null;
            var issues = reader.GetIssues(options.Kind, options.PathPatterns, issueLimit, options.Severity);
            var issuesAvailable = reader._hasIssuesTable;
            if (issues.Count == 0)
            {
                if (options.Json)
                {
                    if (TryWriteEmptyFormattedResult(options, jsonOptions))
                        return CommandExitCodes.Success;
                    if (options.OutputFormat == OutputFormatJson && options.JsonOutputFormat == JsonOutputFormatArray)
                    {
                        Console.WriteLine(JsonSerializer.Serialize(
                            new List<FileIssue>(),
                            CliJsonSerializerContextFactory.Create(jsonOptions).ListFileIssue));
                        return CommandExitCodes.Success;
                    }
                    Console.WriteLine(new JsonObject
                    {
                        ["count"] = 0,
                        ["issues"] = new JsonArray(),
                        ["issues_table_available"] = issuesAvailable,
                        ["degraded"] = !issuesAvailable,
                    }.ToJsonString(jsonOptions));
                }
                else if (!issuesAvailable)
                    CommandErrorWriter.WriteStderr("WARN: file_issues table missing in this index (legacy or read-only DB) — validate output is degraded, not a real clean signal.");
                else
                {
                    CommandErrorWriter.WriteStderr("No encoding issues found.");
                    WriteValidateKindHint(options.Kind);
                }
                return CommandExitCodes.Success;
            }

            if (options.Json)
            {
                if (TryWriteFormattedLocations(
                    options,
                    issues.Select(i => new FormattedLocation(i.Path, i.Line, null, $"{i.Kind}: {i.Message}")),
                    jsonOptions))
                    return CommandExitCodes.Success;
                if (options.OutputFormat == OutputFormatLsp)
                {
                    WriteLspLocations(issues.Select(ToLspLocation), jsonOptions);
                    return CommandExitCodes.Success;
                }
                if (options.OutputFormat == OutputFormatQf)
                {
                    WriteQuickfix(issues.Select(i => (i.Path, i.Line, 1, $"{i.Kind}: {i.Message}")));
                    return CommandExitCodes.Success;
                }
                if (options.OutputFormat == OutputFormatSarif)
                {
                    WriteSarif(issues.Select(i => (i.Path, i.Line, 1, i.Message, i.Kind)), jsonOptions);
                    return CommandExitCodes.Success;
                }
                if (options.OutputFormat == OutputFormatJson && options.JsonOutputFormat == JsonOutputFormatArray)
                {
                    Console.WriteLine(JsonSerializer.Serialize(
                        issues,
                        CliJsonSerializerContextFactory.Create(jsonOptions).ListFileIssue));
                    return CommandExitCodes.Success;
                }
                Console.WriteLine(new JsonObject
                {
                    ["count"] = issues.Count,
                    ["issues"] = JsonSerializer.SerializeToNode(issues, CliJsonSerializerContextFactory.Create(jsonOptions).ListFileIssue),
                }.ToJsonString(jsonOptions));
            }
            else
            {
                foreach (var issue in issues)
                {
                    var location = issue.Line > 0 ? $":{issue.Line}" : "";
                    Console.WriteLine($"  {issue.Kind,-20} {issue.Path}{location}  {issue.Message}");
                }
                var kindCounts = issues.GroupBy(i => i.Kind).Select(g => $"{g.Key}: {g.Count()}");
                CommandErrorWriter.WriteStderr($"\n({issues.Count} issues: {string.Join(", ", kindCounts)})");
            }
            return CommandExitCodes.Success;
        });
    }

    public static int RunLanguages(string[] cmdArgs, JsonSerializerOptions jsonOptions)
    {
        var options = ParseArgs(
            cmdArgs,
            jsonDefault: false,
            validateDefaultLimit: false,
            validateDefaultSnippetLines: false,
            validateDefaultMaxLineWidth: false);
        if (TryWriteUnsupportedOptionError("languages", cmdArgs, CliFlagSchema.GetAcceptedFlagNamesForCommand("languages")))
            return CommandExitCodes.UsageError;
        if (TryWriteParseError(options, "languages"))
            return CommandExitCodes.UsageError;
        if (TryWriteUnexpectedPositionals("languages", options))
            return CommandExitCodes.UsageError;
        var json = options.Json;

        var langExtensions = FileIndexer.GetLanguageExtensions();
        var symbolLangs = SymbolExtractor.GetSupportedLanguages();
        var graphLangs = ReferenceExtractor.GetSupportedLanguages();

        // Build a consolidated view: language -> capability flags and gaps.
        // 統合ビュー: 言語 -> capability flag と gap。
        var allLangs = new Dictionary<string, LanguageSupportInfo>(StringComparer.Ordinal);

        foreach (var (ext, lang) in langExtensions)
        {
            if (!allLangs.TryGetValue(lang, out var info))
            {
                var hasSymbols = symbolLangs.Contains(lang);
                var hasReferences = graphLangs.Contains(lang);
                info = new LanguageSupportInfo(
                    [],
                    GetLanguageAliases(lang).ToList(),
                    hasSymbols,
                    hasReferences,
                    hasReferences,
                    BuildLanguageCapabilityGaps(hasSymbols, hasReferences, hasReferences));
                allLangs[lang] = info;
            }
            info.Extensions.Add(ext);
        }

        // Sort by language name / 言語名でソート
        var sorted = allLangs.OrderBy(kv => kv.Key).ToList();

        if (options.LanguagesIndexedOnly || ShouldLoadLanguageIndexedCounts(options))
        {
            return WithDb(options, jsonOptions, reader =>
            {
                var indexedLanguageCounts = reader.GetStatus().Languages;
                return WriteLanguages(SelectLanguages(sorted, indexedLanguageCounts), indexedLanguageCounts);
            });
        }

        return WriteLanguages(SelectLanguages(sorted, indexedLanguageCounts: null), indexedLanguageCounts: null);

        IEnumerable<KeyValuePair<string, LanguageSupportInfo>> SelectLanguages(
            IEnumerable<KeyValuePair<string, LanguageSupportInfo>> languages,
            IReadOnlyDictionary<string, long>? indexedLanguageCounts)
        {
            var selected = languages;
            if (options.LanguagesIndexedOnly)
                selected = selected.Where(kv => indexedLanguageCounts?.ContainsKey(kv.Key) == true);
            if (HasLanguageLookup(options))
                selected = selected.Where(kv => LanguageMatchesLookup(kv.Key, kv.Value, options));
            return selected;
        }

        int WriteLanguages(
            IEnumerable<KeyValuePair<string, LanguageSupportInfo>> languages,
            IReadOnlyDictionary<string, long>? indexedLanguageCounts)
        {
            var filtered = languages
                .Where(kv => options.LanguageCapabilities.All(capability => LanguageMatchesCapability(kv.Value, capability)))
                .OrderBy(kv => kv.Key, StringComparer.Ordinal)
                .ToList();

            if (json)
            {
                var entries = filtered.Select(kv => new LanguageEntryJsonResult(
                    kv.Key,
                    kv.Value.Extensions.OrderBy(e => e).ToList(),
                    kv.Value.Aliases.OrderBy(a => a).ToList(),
                    kv.Value.Symbols,
                    kv.Value.References,
                    kv.Value.Graph,
                    kv.Value.CapabilityGaps,
                    GetIndexedLanguageCount(indexedLanguageCounts, kv.Key))).ToList();
                Console.WriteLine(JsonSerializer.Serialize(new LanguagesJsonResult(entries), CliJsonSerializerContextFactory.Create(jsonOptions).LanguagesJsonResult));
            }
            else
            {
                // Fixed-width Extensions column for short lists; spill long lists onto a continuation
                // line so the Symbols / Graph columns are never swallowed by a wide extension string.
                // 拡張子が短い場合は固定幅テーブル、長い場合は継続行に退避させることで、
                // Symbols / Graph 列が拡張子文字列に埋もれないようにする。
                const int ExtensionColumnWidth = 36;
                const int AliasColumnWidth = 12;
                var showIndexedCounts = indexedLanguageCounts != null;
                if (showIndexedCounts)
                {
                    Console.WriteLine($"{"Language",-14} {"Extensions",-36} {"Aliases",-12} {"Indexed",-7} {"Symbols",-9} {"Refs",-5} {"Graph",-7}");
                    Console.WriteLine(new string('-', 93));
                }
                else
                {
                    Console.WriteLine($"{"Language",-14} {"Extensions",-36} {"Aliases",-12} {"Symbols",-9} {"Refs",-5} {"Graph",-7}");
                    Console.WriteLine(new string('-', 85));
                }
                foreach (var (lang, info) in filtered)
                {
                    var exts = string.Join(" ", info.Extensions.OrderBy(e => e));
                    var aliases = string.Join(" ", info.Aliases.OrderBy(a => a));
                    var aliasCell = string.IsNullOrWhiteSpace(aliases) ? "-" : aliases;
                    var indexedCount = GetIndexedLanguageCount(indexedLanguageCounts, lang);
                    var indexedCell = indexedCount?.ToString(CultureInfo.InvariantCulture) ?? "-";
                    var sym = info.Symbols ? "yes" : "-";
                    var refs = info.References ? "yes" : "-";
                    var graph = info.Graph ? "yes" : "-";
                    if (exts.Length <= ExtensionColumnWidth && aliases.Length <= AliasColumnWidth)
                    {
                        if (showIndexedCounts)
                            Console.WriteLine($"{lang,-14} {exts,-36} {aliasCell,-12} {indexedCell,-7} {sym,-9} {refs,-5} {graph,-7}");
                        else
                            Console.WriteLine($"{lang,-14} {exts,-36} {aliasCell,-12} {sym,-9} {refs,-5} {graph,-7}");
                    }
                    else
                    {
                        if (showIndexedCounts)
                            Console.WriteLine($"{lang,-14} {"",-36} {"",-12} {indexedCell,-7} {sym,-9} {refs,-5} {graph,-7}");
                        else
                            Console.WriteLine($"{lang,-14} {"",-36} {"",-12} {sym,-9} {refs,-5} {graph,-7}");
                        Console.WriteLine($"  Extensions: {exts}");
                        if (!string.IsNullOrWhiteSpace(aliases))
                            Console.WriteLine($"  Aliases: {aliases}");
                        if (info.CapabilityGaps.Count > 0)
                            Console.WriteLine($"  Gaps: {string.Join(", ", info.CapabilityGaps)}");
                    }
                }
                CommandErrorWriter.WriteStderr($"\n({filtered.Count} languages)");
            }

            return CommandExitCodes.Success;
        }
    }

    private sealed record LanguageSupportInfo(List<string> Extensions, List<string> Aliases, bool Symbols, bool References, bool Graph, List<string> CapabilityGaps);

    private static bool HasLanguageLookup(QueryCommandOptions options)
        => options.LanguageLookups.Count > 0 || options.LanguageExtensionLookups.Count > 0 || options.LanguageAliasLookups.Count > 0;

    private static bool ShouldLoadLanguageIndexedCounts(QueryCommandOptions options)
    {
        if (!HasLanguageLookup(options))
            return false;
        if (options.DbPathExplicit)
            return true;
        if (options.DbPath.StartsWith("file:", StringComparison.OrdinalIgnoreCase))
            return true;
        return File.Exists(LongPath.EnsureWindowsPrefix(options.DbPath));
    }

    private static long? GetIndexedLanguageCount(IReadOnlyDictionary<string, long>? indexedLanguageCounts, string lang)
    {
        if (indexedLanguageCounts == null)
            return null;
        return indexedLanguageCounts.TryGetValue(lang, out var count) ? count : 0;
    }

    private static bool LanguageMatchesLookup(string lang, LanguageSupportInfo language, QueryCommandOptions options)
        => options.LanguageLookups.Any(lookup => string.Equals(DbReader.NormalizeQueryLanguage(lookup), lang, StringComparison.Ordinal))
           || options.LanguageExtensionLookups.Any(lookup => LanguageMatchesExtensionLookup(language, lookup))
           || options.LanguageAliasLookups.Any(lookup => LanguageMatchesAliasLookup(language, lookup));

    private static bool LanguageMatchesExtensionLookup(LanguageSupportInfo language, string lookup)
    {
        var normalized = NormalizeLanguageLookupKey(lookup);
        return language.Extensions.Any(ext => string.Equals(NormalizeLanguageLookupKey(ext), normalized, StringComparison.Ordinal));
    }

    private static bool LanguageMatchesAliasLookup(LanguageSupportInfo language, string lookup)
    {
        var normalized = NormalizeLanguageLookupKey(lookup);
        return language.Aliases.Any(alias => string.Equals(NormalizeLanguageLookupKey(alias), normalized, StringComparison.Ordinal));
    }

    private static string NormalizeLanguageLookupKey(string value)
    {
        var builder = new StringBuilder(value.Length);
        foreach (var ch in value.Trim())
        {
            if (char.IsWhiteSpace(ch) || ch is '-' or '_' or '.')
                continue;
            builder.Append(char.ToLowerInvariant(ch));
        }
        return builder.ToString();
    }

    private static bool LanguageMatchesCapability(LanguageSupportInfo language, string capability)
        => capability switch
        {
            LanguageCapabilitySymbols => language.Symbols,
            LanguageCapabilityReferences => language.References,
            LanguageCapabilityGraph => language.Graph,
            LanguageCapabilityMissingSymbols => !language.Symbols,
            LanguageCapabilityMissingReferences => !language.References,
            LanguageCapabilityMissingGraph => !language.Graph,
            LanguageCapabilitySearchOnly => !language.Symbols && !language.References && !language.Graph,
            _ => false,
        };

    private static bool TryNormalizeLanguageCapability(string value, out string capability)
    {
        capability = value.Trim().ToLowerInvariant();
        return capability is
            LanguageCapabilityGraph or
            LanguageCapabilityReferences or
            LanguageCapabilitySymbols or
            LanguageCapabilityMissingGraph or
            LanguageCapabilityMissingReferences or
            LanguageCapabilityMissingSymbols or
            LanguageCapabilitySearchOnly;
    }

    private static List<string> BuildLanguageCapabilityGaps(bool symbols, bool references, bool graph)
    {
        var gaps = new List<string>();
        if (!symbols)
            gaps.Add("missing-symbols");
        if (!references)
            gaps.Add("missing-references");
        if (!graph)
            gaps.Add("missing-graph");
        return gaps;
    }

    private static bool TryNormalizeSearchAuditScope(string value, out string scope)
    {
        scope = value.Trim().ToLowerInvariant();
        if (scope is SearchAuditRecipes.DefaultAuditScope or SearchAuditRecipes.AllAuditScope)
            return true;
        if (scope is "production" or "production-only")
        {
            scope = SearchAuditRecipes.DefaultAuditScope;
            return true;
        }

        return false;
    }

    private static bool TryNormalizeSearchGuardScope(string value, out SearchGuardScope scope)
    {
        switch (value.Trim().ToLowerInvariant().Replace("_", "-"))
        {
            case "window":
                scope = SearchGuardScope.Window;
                return true;
            case "same-line":
            case "sameline":
                scope = SearchGuardScope.SameLine;
                return true;
            default:
                scope = SearchGuardScope.Window;
                return false;
        }
    }

    private static string FormatSearchGuardScope(SearchGuardScope scope)
        => scope == SearchGuardScope.SameLine ? "same-line" : "window";


    public static QueryCommandOptions ParseArgs(
        string[] args,
        bool jsonDefault,
        bool allowNamedQuery = false,
        bool allowStatusCheck = false,
        bool allowIssueDraftsFormat = false,
        bool validateDefaultLimit = true,
        bool validateDefaultSnippetLines = true,
        bool validateDefaultMaxLineWidth = true)
    {
        string? dbPath = null;
        string? dataDir = null;
        bool? json = null;
        string jsonOutputFormat = JsonOutputFormatNdjson;
        int limit = ResolveDefaultPositiveInt(DefaultLimitEnvironmentVariable, DefaultQueryLimit, "--limit", out var defaultLimitError);
        string? lang = null;
        string? kind = null;
        string? unusedBucket = null;
        string? minUnusedConfidence = null;
        string? severity = null;
        string? query = null;
        bool rawFts = false;
        bool includeBody = false;
        int? bodyStartLine = null;
        int? bodyLines = null;
        bool countOnly = false;
        bool all = false;
        bool strictNotFound = false;
        int? startLine = null;
        int? endLine = null;
        int contextBefore = 0;
        int contextAfter = 0;
        int? focusLine = null;
        int? focusColumn = null;
        int focusLength = 1;
        int snippetLines = ResolveDefaultPositiveInt(DefaultSnippetLinesEnvironmentVariable, SearchSnippetFormatter.DefaultSnippetLines, "--snippet-lines", out var defaultSnippetLinesError);
        var snippetFocus = SearchSnippetFocusMode.Quality;
        int maxLineWidth = ResolveDefaultNonNegativeInt(DefaultMaxLineWidthEnvironmentVariable, LineWidthFormatter.DefaultMaxLineWidth, "--max-line-width", out var defaultMaxLineWidthError);
        bool contextAfterExplicit = false;
        var pathPatterns = new List<string>();
        var userPathPatterns = new List<string>();
        var workspaceDbPaths = new List<string>();
        var projectFilters = new List<string>();
        string? solutionFilter = null;
        var excludePaths = new List<string>();
        var visibilityFilters = new List<string>();
        var excludeVisibilityFilters = new List<string>();
        bool excludeTests = false;
        bool unusedActionable = false;
        bool includeGenerated = false;
        DateTime? since = null;
        bool noDedup = false;
        bool noVisibilityRank = false;
        bool exact = false;
        bool regex = false;
        bool prefix = false;
        var guardFilters = new List<SearchGuardFilter>();
        var guardWindow = DbReader.DefaultSearchGuardWindow;
        var guardScope = SearchGuardScope.Window;
        bool excludeComments = false;
        bool excludeStrings = false;
        bool excludeFixtures = false;
        List<string>? parseErrors = null;
        bool exactName = false;
        bool exactSubstring = false;
        bool dbPathExplicit = false;
        bool readOnly = false;
        bool dryRun = false;
        bool checkWorkspace = false;
        TimeSpan? staleAfter = null;
        HashSet<string>? statusCheckScopes = null;
        bool withPaths = false;
        string? groupBy = null;
        string? uniqueBy = null;
        string? countBy = null;
        var matchOrigins = new List<string>();
        var excludeOrigins = new List<string>();
        var resultKinds = new List<string>();
        List<string>? searchFields = null;
        List<string>? outlineFields = null;
        bool outlineFieldsExplicit = false;
        bool firstPerFile = false;
        bool resultsOnly = false;
        bool nextSteps = false;
        int groupedPerFileLimit = DefaultSearchGroupedPerFileLimit;
        int? sampleSize = null;
        int? maxJsonBytes = null;
        bool rawBytes = false;
        bool rawKinds = false;
        bool verbose = false;
        bool profile = false;
        int? slowQueryMs = null;
        bool compact = false;
        List<string>? inspectFields = null;
        double minEntrypointConfidence = 0;
        string? statusExplainField = null;
        bool statusLogPath = false;
        string outputFormat = OutputFormatText;
        bool statusConfig = false;
        bool limitExplicit = false;
        bool snippetLinesExplicit = false;
        bool maxLineWidthExplicit = false;
        bool strict = false;
        var rankMode = ReferenceRankMode.Weighted;
        var symbolSortMode = SymbolSortMode.Name;
        var extraNames = new List<string>();
        bool impactDeprecatedDepthUsed = false;
        List<string>? mapSections = null;
        bool mapSummaryOnly = false;
        bool dependencyCycles = false;
        string? recipeName = null;
        var includeRecipeQueries = new List<string>();
        var excludeRecipeQueries = new List<string>();
        bool showExcluded = false;
        bool listRecipes = false;
        string? openIssuesPath = null;
        string auditScope = SearchAuditRecipes.DefaultAuditScope;
        bool auditScopeExplicit = false;
        string? openIssuesRepository = null;
        string duplicateConfidence = IssueDuplicatePreflight.DefaultDuplicateConfidence;
        double duplicateThreshold = IssueDuplicatePreflight.DefaultDuplicateThreshold;
        bool duplicateConfidenceExplicit = false;
        bool duplicateThresholdExplicit = false;
        string? issueTitle = null;
        var issueLabels = new List<string>();
        SearchCursor? searchCursor = null;
        int? unusedCursorOffset = null;
        int? outlineCursorOffset = null;
        var namedSearchQueries = new List<SearchNamedQuery>();
        bool languagesIndexedOnly = false;
        var languageCapabilities = new List<string>();
        var languageLookups = new List<string>();
        var languageExtensionLookups = new List<string>();
        var languageAliasLookups = new List<string>();
        ProjectFilterRootResolution? projectFilterRootResolution = null;

        void AddParseError(string error)
        {
            parseErrors ??= [];
            parseErrors.Add(error);
        }

        void AddSearchGuardFilter(string optionName, SearchGuardRole role, SearchGuardDirection direction, string value)
        {
            if (string.IsNullOrWhiteSpace(value))
            {
                AddParseError(BuildMissingOptionValueError(optionName));
                return;
            }
            if (value.Length > QueryLimits.MaxQueryLength)
            {
                AddParseError($"Error: {optionName} query too long (max {QueryLimits.MaxQueryLength} characters).");
                return;
            }

            guardFilters.Add(new SearchGuardFilter(role, direction, value));
        }

        void AddIssueDraftLabels(string rawLabels)
        {
            if (string.IsNullOrWhiteSpace(rawLabels))
            {
                AddParseError("Error: --issue-label value cannot be empty.");
                return;
            }

            foreach (var label in rawLabels.Split(',', StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries))
            {
                if (issueLabels.Count >= MaxIssueDraftLabelCount)
                {
                    AddParseError($"Error: search issue drafts accept at most {MaxIssueDraftLabelCount} labels.");
                    return;
                }
                if (label.Length > IssueDuplicatePreflight.MaxOpenIssueLabelLength)
                {
                    AddParseError($"Error: --issue-label value too long (max {IssueDuplicatePreflight.MaxOpenIssueLabelLength} characters).");
                    return;
                }
                if (!issueLabels.Contains(label, StringComparer.OrdinalIgnoreCase))
                    issueLabels.Add(label);
            }
        }

        void AddRecipeQuerySelectors(string optionName, string rawSelectors, List<string> selectors)
        {
            if (string.IsNullOrWhiteSpace(rawSelectors))
            {
                AddParseError($"Error: {optionName} value cannot be empty.");
                return;
            }

            foreach (var selector in rawSelectors.Split(',', StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries))
            {
                if (selectors.Count >= MaxSearchRecipeQuerySelectorCount)
                {
                    AddParseError($"Error: search recipes accept at most {MaxSearchRecipeQuerySelectorCount} {optionName} values.");
                    return;
                }
                if (selector.Length > MaxSearchRecipeQuerySelectorLength)
                {
                    AddParseError($"Error: {optionName} value too long (max {MaxSearchRecipeQuerySelectorLength} characters).");
                    return;
                }
                if (!selectors.Contains(selector, StringComparer.OrdinalIgnoreCase))
                    selectors.Add(selector);
            }
        }

        void AddStatusCheckScopes(string rawScopes)
        {
            if (string.IsNullOrWhiteSpace(rawScopes))
            {
                AddParseError("Error: --check scope list cannot be empty. Use --check or --check=workspace,fold,graph,issues,hotspot,csharp,sql,newer.");
                return;
            }
            if (!ValidateCsvBounds("--check", rawScopes, MaxStatusCheckScopesCsvLength, MaxStatusCheckScopesCsvEntries, AddParseError))
                return;

            statusCheckScopes ??= new HashSet<string>(StringComparer.OrdinalIgnoreCase);
            foreach (var rawScope in rawScopes.Split(',', StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries))
            {
                var scope = rawScope.ToLowerInvariant();
                switch (scope)
                {
                    case "workspace":
                    case "fold":
                    case "graph":
                    case "issues":
                    case "hotspot":
                    case "csharp":
                    case "sql":
                    case "newer":
                        statusCheckScopes.Add(scope);
                        break;
                    default:
                        AddParseError($"Error: unsupported --check scope '{ConsoleUi.FormatBoundedValue(rawScope)}'. Use one or more of workspace, fold, graph, issues, hotspot, csharp, sql, newer.");
                        break;
                }
            }

            if (statusCheckScopes.Count == 0)
                AddParseError("Error: --check scope list cannot be empty. Use --check or --check=workspace,fold,graph,issues,hotspot,csharp,sql,newer.");
        }

        // Track non-repeatable value-taking options that have already been observed and warn on
        // subsequent occurrences. Previously `--db /A --db /B` silently used `/B`; this makes the
        // override explicit so users (and AI callers) can spot a copy/paste or scripted mistake.
        // 非 repeatable な value-taking オプションの初出を記録し、2 回目以降で警告する。以前は
        // `--db /A --db /B` が silent に `/B` を採用していたため、スクリプトやコピペのミスに
        // ユーザーや AI 呼び出し側が気付けるよう、上書きを明示化する。
        var seenSingleValueOptions = new HashSet<string>(StringComparer.Ordinal);
        void WarnIfDuplicateSingleValueOption(string canonicalName, string newValue)
        {
            if (seenSingleValueOptions.Add(canonicalName))
                return;
            var displayValue = ConsoleUi.FormatBoundedValue(newValue);
            CommandErrorWriter.WriteStderr($"Warning: {canonicalName} specified more than once; the rightmost CLI value '{displayValue}' takes precedence over earlier CLI values and any environment/config default.");
        }

        for (int i = 0; i < args.Length; i++)
        {
            var currentArg = args[i];
            if (allowStatusCheck && currentArg.StartsWith("--check=", StringComparison.Ordinal))
            {
                checkWorkspace = true;
                AddStatusCheckScopes(currentArg["--check=".Length..]);
                continue;
            }

            var inlineValue = TrySplitInlineOptionValue(currentArg, out var inlineOptionName)
                ? currentArg[(inlineOptionName!.Length + 1)..]
                : null;
            var normalizedArg = inlineOptionName ?? currentArg;

            switch (normalizedArg)
            {
                case "--":
                    if (i + 1 >= args.Length)
                    {
                        AddParseError("Error: -- requires a following literal query.");
                    }
                    else if (query == null)
                    {
                        query = args[++i];
                    }
                    else
                    {
                        extraNames.Add(args[++i]);
                    }
                    break;
                case "--db":
                    if (TryReadStringOptionValue(args, ref i, "--db", inlineValue, allowSeparatedDashPrefixedLiteralValue: true, out var dbPathValue, out var dbPathError))
                    {
                        WarnIfDuplicateSingleValueOption("--db", dbPathValue!);
                        dbPath = dbPathValue!;
                        dbPathExplicit = true;
                    }
                    else
                        AddParseError(dbPathError!);
                    break;
                case "--read-only":
                case "--immutable":
                    readOnly = true;
                    break;
                case "--dry-run":
                    dryRun = true;
                    break;
                case "--pretty":
                    break;
                case "--compact":
                    compact = true;
                    json = true;
                    outputFormat = OutputFormatJson;
                    break;
                case "--body-only":
                    includeBody = true;
                    inspectFields = ["definitions"];
                    json = true;
                    outputFormat = OutputFormatJson;
                    break;
                case "--workspace-db":
                    if (TryReadStringOptionValue(args, ref i, "--workspace-db", inlineValue, allowSeparatedDashPrefixedLiteralValue: true, out var workspaceDbPath, out var workspaceDbError))
                        workspaceDbPaths.Add(workspaceDbPath!);
                    else
                        AddParseError(workspaceDbError!);
                    break;
                case "--data-dir":
                    if (TryReadStringOptionValue(args, ref i, "--data-dir", inlineValue, allowSeparatedDashPrefixedLiteralValue: true, out var dataDirValue, out var dataDirError))
                    {
                        WarnIfDuplicateSingleValueOption("--data-dir", dataDirValue!);
                        dataDir = dataDirValue!;
                    }
                    else
                        AddParseError(dataDirError!);
                    break;
                case "--json":
                    if (inlineValue == null)
                    {
                        json = true;
                        outputFormat = OutputFormatJson;
                    }
                    else if (TryParseJsonOutputFormat(inlineValue, out var parsedJsonOutputFormat))
                    {
                        json = true;
                        jsonOutputFormat = parsedJsonOutputFormat;
                        outputFormat = OutputFormatJson;
                    }
                    else
                    {
                        AddParseError($"Error: --json format must be one of ndjson or array, got '{ConsoleUi.FormatBoundedValue(inlineValue)}'. Hint: use `--json` or `--json=ndjson` for newline-delimited JSON, or `--json=array` for a single JSON array.");
                    }
                    break;
                case "--indexed-only":
                    languagesIndexedOnly = true;
                    break;
                case "--capability":
                    if (!TryReadStringOptionValue(args, ref i, "--capability", inlineValue, allowSeparatedDashPrefixedLiteralValue: false, out var capabilityValue, out var capabilityError))
                    {
                        AddParseError(capabilityError!);
                    }
                    else if (TryNormalizeLanguageCapability(capabilityValue!, out var capability))
                    {
                        languageCapabilities.Add(capability);
                    }
                    else
                    {
                        AddParseError($"Error: unsupported --capability value '{ConsoleUi.FormatBoundedValue(capabilityValue)}'. Use graph, references, symbols, missing-graph, missing-references, missing-symbols, or search-only.");
                    }
                    break;
                case "--language":
                    if (TryReadStringOptionValue(args, ref i, "--language", inlineValue, allowSeparatedDashPrefixedLiteralValue: false, out var languageValue, out var languageError))
                    {
                        languageLookups.Add(languageValue!);
                        lang = NormalizeLangFilterValue(languageValue);
                    }
                    else
                    {
                        AddParseError(languageError!);
                    }
                    break;
                case "--extension":
                    if (TryReadStringOptionValue(args, ref i, "--extension", inlineValue, allowSeparatedDashPrefixedLiteralValue: false, out var languageExtensionValue, out var languageExtensionError))
                        languageExtensionLookups.Add(languageExtensionValue!);
                    else
                        AddParseError(languageExtensionError!);
                    break;
                case "--alias":
                    if (TryReadStringOptionValue(args, ref i, "--alias", inlineValue, allowSeparatedDashPrefixedLiteralValue: false, out var languageAliasValue, out var languageAliasError))
                        languageAliasLookups.Add(languageAliasValue!);
                    else
                        AddParseError(languageAliasError!);
                    break;
                case "--format":
                    if (TryReadStringOptionValue(args, ref i, "--format", inlineValue, allowSeparatedDashPrefixedLiteralValue: false, out var formatValue, out var formatError))
                    {
                        WarnIfDuplicateSingleValueOption("--format", formatValue!);
                        if (TryParseOutputFormat(formatValue!, out var parsedOutputFormat))
                        {
                            outputFormat = parsedOutputFormat;
                            if (parsedOutputFormat == OutputFormatCompact)
                                compact = true;
                            if (parsedOutputFormat == OutputFormatCount)
                                countOnly = true;
                            if (parsedOutputFormat != OutputFormatText &&
                                parsedOutputFormat != OutputFormatDot &&
                                parsedOutputFormat != OutputFormatGraphMl)
                                json = true;
                        }
                        else if (allowIssueDraftsFormat && string.Equals(formatValue, OutputFormatIssueDrafts, StringComparison.OrdinalIgnoreCase))
                        {
                            outputFormat = OutputFormatIssueDrafts;
                            json = true;
                        }
                        else
                        {
                            var allowedFormats = allowIssueDraftsFormat
                                ? "text, json, count, compact, csv, tsv, lsp, qf, sarif, or issue-drafts"
                                : "text, json, count, compact, csv, tsv, lsp, qf, or sarif";
                            AddParseError($"Error: --format must be one of {allowedFormats}; got '{ConsoleUi.FormatBoundedValue(formatValue)}'.");
                        }
                    }
                    else
                    {
                        AddParseError(formatError!);
                    }
                    break;
                case "--limit":
                case "--max-results":
                case "--top":
                    var limitOptionName = normalizedArg == "--top" ? "--limit" : normalizedArg;
                    if (!TryReadRawOptionValue(args, ref i, limitOptionName, inlineValue, out var limitValue, out var missingLimitError))
                        AddParseError(missingLimitError!);
                    else if (TryParsePositiveInt(limitValue!, limitOptionName, out var parsedLimit, out var limitError))
                    {
                        WarnIfDuplicateSingleValueOption("--limit", limitValue!);
                        limit = parsedLimit;
                        limitExplicit = true;
                    }
                    else
                        AddParseError(limitError!);
                    break;
                case "--lang":
                    if (TryReadStringOptionValue(args, ref i, "--lang", inlineValue, allowSeparatedDashPrefixedLiteralValue: false, out var langValue, out var langError))
                    {
                        WarnIfDuplicateSingleValueOption("--lang", langValue!);
                        // Normalize to lowercase so '--lang Python' == '--lang python' — every LangMap key and
                        // every DB 'files.lang' row is lowercase, so the SQL filter and WriteLangHint match.
                        // Also fold common short aliases (e.g. `py`) to canonical language names so Python-heavy
                        // workflows can use familiar shorthand without silently returning zero rows.
                        // '--lang Python' と '--lang python' を同一視するため lowercase 正規化する。LangMap の key と
                        // DB の `files.lang` はすべて lowercase なので、SQL filter と WriteLangHint が一致する。
                        // さらに `py` のような短縮エイリアスを正規名へ畳み込み、Python 利用時の慣用入力で
                        // 意図せず 0 件になる事故を避ける。
                        lang = NormalizeLangFilterValue(langValue);
                    }
                    else
                        AddParseError(langError!);
                    break;
                case "--query":
                    if (!allowNamedQuery)
                    {
                        AddParseError("Error: --query is not supported by this command.");
                        if (i + 1 < args.Length && !args[i + 1].StartsWith("-", StringComparison.Ordinal))
                            i++;
                    }
                    else if (TryReadStringOptionValue(args, ref i, "--query", inlineValue, allowSeparatedDashPrefixedLiteralValue: true, out var queryValue, out var queryError))
                    {
                        WarnIfDuplicateSingleValueOption("--query", queryValue!);
                        query = queryValue;
                    }
                    else
                        AddParseError(queryError!);
                    break;
                case "--recipe":
                    if (TryReadStringOptionValue(args, ref i, "--recipe", inlineValue, allowSeparatedDashPrefixedLiteralValue: false, out var recipeValue, out var recipeError))
                    {
                        WarnIfDuplicateSingleValueOption("--recipe", recipeValue!);
                        recipeName = recipeValue;
                    }
                    else
                        AddParseError(recipeError!);
                    break;
                case "--include-query":
                    if (TryReadStringOptionValue(args, ref i, "--include-query", inlineValue, allowSeparatedDashPrefixedLiteralValue: false, out var includeQueryValue, out var includeQueryError))
                        AddRecipeQuerySelectors("--include-query", includeQueryValue!, includeRecipeQueries);
                    else
                        AddParseError(includeQueryError!);
                    break;
                case "--exclude-query":
                    if (TryReadStringOptionValue(args, ref i, "--exclude-query", inlineValue, allowSeparatedDashPrefixedLiteralValue: false, out var excludeQueryValue, out var excludeQueryError))
                        AddRecipeQuerySelectors("--exclude-query", excludeQueryValue!, excludeRecipeQueries);
                    else
                        AddParseError(excludeQueryError!);
                    break;
                case "--show-excluded":
                    showExcluded = true;
                    break;
                case "--list-recipes":
                    listRecipes = true;
                    break;
                case "--open-issues":
                    if (TryReadStringOptionValue(args, ref i, "--open-issues", inlineValue, allowSeparatedDashPrefixedLiteralValue: true, out var openIssuesValue, out var openIssuesError))
                    {
                        WarnIfDuplicateSingleValueOption("--open-issues", openIssuesValue!);
                        openIssuesPath = openIssuesValue;
                    }
                    else
                        AddParseError(openIssuesError!);
                    break;
                case "--audit-scope":
                    if (!TryReadStringOptionValue(args, ref i, "--audit-scope", inlineValue, allowSeparatedDashPrefixedLiteralValue: false, out var auditScopeValue, out var auditScopeError))
                    {
                        AddParseError(auditScopeError!);
                    }
                    else if (TryNormalizeSearchAuditScope(auditScopeValue!, out var normalizedAuditScope))
                    {
                        WarnIfDuplicateSingleValueOption("--audit-scope", auditScopeValue!);
                        auditScope = normalizedAuditScope;
                        auditScopeExplicit = true;
                    }
                    else
                    {
                        AddParseError($"Error: unsupported --audit-scope value '{ConsoleUi.FormatBoundedValue(auditScopeValue)}'. Use source or all.");
                    }
                    break;
                case "--repo":
                    if (TryReadStringOptionValue(args, ref i, "--repo", inlineValue, allowSeparatedDashPrefixedLiteralValue: false, out var repoValue, out var repoError))
                    {
                        WarnIfDuplicateSingleValueOption("--repo", repoValue!);
                        openIssuesRepository = repoValue;
                    }
                    else
                        AddParseError(repoError!);
                    break;
                case "--duplicate-confidence":
                    if (TryReadStringOptionValue(args, ref i, "--duplicate-confidence", inlineValue, allowSeparatedDashPrefixedLiteralValue: false, out var duplicateConfidenceValue, out var duplicateConfidenceError))
                    {
                        WarnIfDuplicateSingleValueOption("--duplicate-confidence", duplicateConfidenceValue!);
                        if (IssueDuplicatePreflight.TryNormalizeDuplicateConfidence(duplicateConfidenceValue!, out var normalizedDuplicateConfidence))
                        {
                            duplicateConfidence = normalizedDuplicateConfidence;
                            duplicateThreshold = IssueDuplicatePreflight.ThresholdForDuplicateConfidence(normalizedDuplicateConfidence);
                            duplicateConfidenceExplicit = true;
                        }
                        else
                        {
                            AddParseError($"Error: --duplicate-confidence must be one of low, medium, high; got '{ConsoleUi.FormatBoundedValue(duplicateConfidenceValue)}'.");
                        }
                    }
                    else
                    {
                        AddParseError(duplicateConfidenceError!);
                    }
                    break;
                case "--duplicate-threshold":
                    if (!TryReadRawOptionValue(args, ref i, "--duplicate-threshold", inlineValue, out var duplicateThresholdValue, out var missingDuplicateThresholdError))
                    {
                        AddParseError(missingDuplicateThresholdError!);
                    }
                    else if (TryParseConfidence(duplicateThresholdValue!, out var parsedDuplicateThreshold))
                    {
                        WarnIfDuplicateSingleValueOption("--duplicate-threshold", duplicateThresholdValue!);
                        duplicateThreshold = parsedDuplicateThreshold;
                        duplicateThresholdExplicit = true;
                    }
                    else
                    {
                        AddParseError($"Error: --duplicate-threshold must be a number between 0 and 1; got '{ConsoleUi.FormatBoundedValue(duplicateThresholdValue)}'.");
                    }
                    break;
                case "--issue-title":
                    if (TryReadStringOptionValue(args, ref i, "--issue-title", inlineValue, allowSeparatedDashPrefixedLiteralValue: true, out var issueTitleValue, out var issueTitleError))
                    {
                        WarnIfDuplicateSingleValueOption("--issue-title", issueTitleValue!);
                        var trimmedTitle = issueTitleValue!.Trim();
                        if (trimmedTitle.Length == 0)
                            AddParseError("Error: --issue-title value cannot be empty.");
                        else if (trimmedTitle.Length > MaxIssueDraftTitleLength)
                            AddParseError($"Error: --issue-title value too long (max {MaxIssueDraftTitleLength} characters).");
                        else
                            issueTitle = trimmedTitle;
                    }
                    else
                        AddParseError(issueTitleError!);
                    break;
                case "--issue-label":
                    if (TryReadStringOptionValue(args, ref i, "--issue-label", inlineValue, allowSeparatedDashPrefixedLiteralValue: false, out var issueLabelValue, out var issueLabelError))
                        AddIssueDraftLabels(issueLabelValue!);
                    else
                        AddParseError(issueLabelError!);
                    break;
                case "--cursor":
                    if (TryReadStringOptionValue(args, ref i, "--cursor", inlineValue, allowSeparatedDashPrefixedLiteralValue: false, out var cursorValue, out var cursorError))
                    {
                        WarnIfDuplicateSingleValueOption("--cursor", cursorValue!);
                        if (TryParseSearchCursor(cursorValue!, out var parsedCursor))
                            searchCursor = parsedCursor;
                        else if (TryParseUnusedCursor(cursorValue!, out var parsedUnusedCursorOffset))
                            unusedCursorOffset = parsedUnusedCursorOffset;
                        else if (TryParseOutlineCursor(cursorValue!, out var parsedOutlineCursorOffset))
                            outlineCursorOffset = parsedOutlineCursorOffset;
                        else
                            AddParseError("Error: --cursor must be a search, unused, or outline pagination cursor returned as `next_cursor`.");
                    }
                    else
                    {
                        AddParseError(cursorError!);
                    }
                    break;
                case "--named-query":
                    if (!allowNamedQuery)
                    {
                        AddParseError("Error: --named-query is not supported by this command.");
                        if (i + 1 < args.Length && !args[i + 1].StartsWith("-", StringComparison.Ordinal))
                            i++;
                    }
                    else if (TryReadStringOptionValue(args, ref i, "--named-query", inlineValue, allowSeparatedDashPrefixedLiteralValue: true, out var namedQueryValue, out var namedQueryError))
                    {
                        if (TryParseNamedSearchQuery(namedQueryValue!, out var namedQuery, out var namedQueryParseError))
                            namedSearchQueries.Add(namedQuery);
                        else
                            AddParseError(namedQueryParseError!);
                    }
                    else
                    {
                        AddParseError(namedQueryError!);
                    }
                    break;
                case "--require-before":
                    if (TryReadStringOptionValue(args, ref i, "--require-before", inlineValue, allowSeparatedDashPrefixedLiteralValue: true, out var requireBeforeValue, out var requireBeforeError))
                        AddSearchGuardFilter("--require-before", SearchGuardRole.Require, SearchGuardDirection.Before, requireBeforeValue!);
                    else
                        AddParseError(requireBeforeError!);
                    break;
                case "--require-after":
                    if (TryReadStringOptionValue(args, ref i, "--require-after", inlineValue, allowSeparatedDashPrefixedLiteralValue: true, out var requireAfterValue, out var requireAfterError))
                        AddSearchGuardFilter("--require-after", SearchGuardRole.Require, SearchGuardDirection.After, requireAfterValue!);
                    else
                        AddParseError(requireAfterError!);
                    break;
                case "--reject-before":
                    if (TryReadStringOptionValue(args, ref i, "--reject-before", inlineValue, allowSeparatedDashPrefixedLiteralValue: true, out var rejectBeforeValue, out var rejectBeforeError))
                        AddSearchGuardFilter("--reject-before", SearchGuardRole.Reject, SearchGuardDirection.Before, rejectBeforeValue!);
                    else
                        AddParseError(rejectBeforeError!);
                    break;
                case "--reject-after":
                    if (TryReadStringOptionValue(args, ref i, "--reject-after", inlineValue, allowSeparatedDashPrefixedLiteralValue: true, out var rejectAfterValue, out var rejectAfterError))
                        AddSearchGuardFilter("--reject-after", SearchGuardRole.Reject, SearchGuardDirection.After, rejectAfterValue!);
                    else
                        AddParseError(rejectAfterError!);
                    break;
                case "--guard-window":
                    if (!TryReadRawOptionValue(args, ref i, "--guard-window", inlineValue, out var guardWindowValue, out var missingGuardWindowError))
                    {
                        AddParseError(missingGuardWindowError!);
                    }
                    else if (TryParseNonNegativeInt(guardWindowValue!, "--guard-window", out var parsedGuardWindow, out var guardWindowError))
                    {
                        WarnIfDuplicateSingleValueOption("--guard-window", guardWindowValue!);
                        if (parsedGuardWindow > DbReader.MaxSearchGuardWindow)
                            AddParseError($"Error: --guard-window must be between 0 and {DbReader.MaxSearchGuardWindow}; got {parsedGuardWindow}.");
                        else
                            guardWindow = parsedGuardWindow;
                    }
                    else
                    {
                        AddParseError(guardWindowError!);
                    }
                    break;
                case "--guard-scope":
                    if (TryReadStringOptionValue(args, ref i, "--guard-scope", inlineValue, allowSeparatedDashPrefixedLiteralValue: false, out var guardScopeValue, out var guardScopeError))
                    {
                        WarnIfDuplicateSingleValueOption("--guard-scope", guardScopeValue!);
                        if (TryNormalizeSearchGuardScope(guardScopeValue!, out var parsedGuardScope))
                            guardScope = parsedGuardScope;
                        else
                            AddParseError($"Error: unsupported --guard-scope value '{ConsoleUi.FormatBoundedValue(guardScopeValue!)}'. Use window or same-line.");
                    }
                    else
                        AddParseError(guardScopeError!);
                    break;
                case "--kind":
                    if (TryReadStringOptionValue(args, ref i, "--kind", inlineValue, allowSeparatedDashPrefixedLiteralValue: false, out var kindValue, out var kindError))
                    {
                        WarnIfDuplicateSingleValueOption("--kind", kindValue!);
                        // Normalize to lowercase so '--kind FUNCTION' == '--kind function'. AllValidKinds entries
                        // and every DB 'symbols.kind' row are lowercase.
                        // '--kind FUNCTION' と '--kind function' を同一視するため lowercase 正規化する。AllValidKinds
                        // と DB の `symbols.kind` はすべて lowercase。
                        kind = kindValue?.ToLowerInvariant();
                    }
                    else
                        AddParseError(kindError!);
                    break;
                case "--bucket":
                    if (TryReadStringOptionValue(args, ref i, "--bucket", inlineValue, allowSeparatedDashPrefixedLiteralValue: false, out var unusedBucketValue, out var unusedBucketError))
                    {
                        WarnIfDuplicateSingleValueOption("--bucket", unusedBucketValue!);
                        unusedBucket = unusedBucketValue?.ToLowerInvariant();
                    }
                    else
                        AddParseError(unusedBucketError!);
                    break;
                case "--confidence":
                case "--min-confidence":
                    var confidenceFlag = normalizedArg;
                    if (TryReadStringOptionValue(args, ref i, confidenceFlag, inlineValue, allowSeparatedDashPrefixedLiteralValue: false, out var minUnusedConfidenceValue, out var minUnusedConfidenceError))
                    {
                        WarnIfDuplicateSingleValueOption("--min-confidence", minUnusedConfidenceValue!);
                        minUnusedConfidence = minUnusedConfidenceValue?.ToLowerInvariant();
                    }
                    else
                        AddParseError(minUnusedConfidenceError!);
                    break;
                case "--severity":
                    if (TryReadStringOptionValue(args, ref i, "--severity", inlineValue, allowSeparatedDashPrefixedLiteralValue: false, out var severityValue, out var severityError))
                    {
                        WarnIfDuplicateSingleValueOption("--severity", severityValue!);
                        severity = severityValue?.ToLowerInvariant();
                    }
                    else
                    {
                        AddParseError(severityError!);
                    }
                    break;
                case "--visibility":
                    if (TryReadStringOptionValue(args, ref i, "--visibility", inlineValue, allowSeparatedDashPrefixedLiteralValue: false, out var visibilityValue, out var visibilityError))
                        AddVisibilityFilterValues("--visibility", visibilityValue!, visibilityFilters, AddParseError);
                    else
                        AddParseError(visibilityError!);
                    break;
                case "--exclude-visibility":
                    if (TryReadStringOptionValue(args, ref i, "--exclude-visibility", inlineValue, allowSeparatedDashPrefixedLiteralValue: false, out var excludeVisibilityValue, out var excludeVisibilityError))
                        AddVisibilityFilterValues("--exclude-visibility", excludeVisibilityValue!, excludeVisibilityFilters, AddParseError);
                    else
                        AddParseError(excludeVisibilityError!);
                    break;
                case "--rank-by":
                    if (TryReadStringOptionValue(args, ref i, "--rank-by", inlineValue, allowSeparatedDashPrefixedLiteralValue: false, out var rankByValue, out var rankByError))
                    {
                        WarnIfDuplicateSingleValueOption("--rank-by", rankByValue!);
                        if (TryParseReferenceRankMode(rankByValue!, out var parsedRankMode))
                            rankMode = parsedRankMode;
                        else
                            AddParseError($"Error: --rank-by must be one of weighted, count, kind; got '{rankByValue}'.");
                    }
                    else
                        AddParseError(rankByError!);
                    break;
                case "--sort":
                    if (TryReadStringOptionValue(args, ref i, "--sort", inlineValue, allowSeparatedDashPrefixedLiteralValue: false, out var sortValue, out var sortError))
                    {
                        WarnIfDuplicateSingleValueOption("--sort", sortValue!);
                        if (TryParseSymbolSortMode(sortValue!, out var parsedSortMode))
                            symbolSortMode = parsedSortMode;
                        else
                            AddParseError($"Error: --sort must be one of hotspot, references, size, complexity, path; got '{sortValue}'.");
                    }
                    else
                        AddParseError(sortError!);
                    break;
                case "--sections":
                    if (TryReadStringOptionValue(args, ref i, "--sections", inlineValue, allowSeparatedDashPrefixedLiteralValue: false, out var sectionsValue, out var sectionsError))
                    {
                        WarnIfDuplicateSingleValueOption("--sections", sectionsValue!);
                        mapSections = ParseMapSections(sectionsValue!, AddParseError);
                    }
                    else
                        AddParseError(sectionsError!);
                    break;
                case "--summary-only":
                    mapSummaryOnly = true;
                    break;
                case "--fields":
                    if (TryReadStringOptionValue(args, ref i, "--fields", inlineValue, allowSeparatedDashPrefixedLiteralValue: false, out var fieldsValue, out var fieldsError))
                    {
                        WarnIfDuplicateSingleValueOption("--fields", fieldsValue!);
                        inspectFields = ParseInspectFields(fieldsValue!, AddParseError, out var includeBodyFromFields);
                        includeBody |= includeBodyFromFields;
                        json = true;
                        outputFormat = OutputFormatJson;
                    }
                    else
                    {
                        AddParseError(fieldsError!);
                    }
                    break;
                case "--fts":
                    rawFts = true;
                    break;
                case "--body":
                    includeBody = true;
                    break;
                case "--body-start":
                    if (!TryReadRawOptionValue(args, ref i, "--body-start", inlineValue, out var bodyStartValue, out var missingBodyStartError))
                        AddParseError(missingBodyStartError!);
                    else if (TryParsePositiveInt(bodyStartValue!, "--body-start", out var parsedBodyStartLine, out var bodyStartError))
                    {
                        WarnIfDuplicateSingleValueOption("--body-start", bodyStartValue!);
                        bodyStartLine = parsedBodyStartLine;
                        includeBody = true;
                    }
                    else
                        AddParseError(bodyStartError!);
                    break;
                case "--body-lines":
                case "--body-line-count":
                    var bodyLinesFlag = normalizedArg;
                    if (!TryReadRawOptionValue(args, ref i, bodyLinesFlag, inlineValue, out var bodyLinesValue, out var missingBodyLinesError))
                        AddParseError(missingBodyLinesError!);
                    else if (TryParsePositiveInt(bodyLinesValue!, bodyLinesFlag, out var parsedBodyLines, out var bodyLinesError))
                    {
                        WarnIfDuplicateSingleValueOption("--body-lines", bodyLinesValue!);
                        bodyLines = parsedBodyLines;
                        includeBody = true;
                    }
                    else
                        AddParseError(bodyLinesError!);
                    break;
                case "--count":
                    countOnly = true;
                    break;
                case "--cycles":
                    dependencyCycles = true;
                    break;
                case "--strict-not-found":
                    strictNotFound = true;
                    break;
                case "--strict":
                    strict = true;
                    break;
                case "--by-bucket":
                    break;
                case "--all":
                    all = true;
                    break;
                case "--no-dedup":
                    noDedup = true;
                    break;
                case "--no-visibility-rank":
                    noVisibilityRank = true;
                    break;
                case "--exact":
                    exact = true;
                    break;
                case "--regex":
                    regex = true;
                    break;
                case "--exact-name":
                    exactName = true;
                    break;
                case "--exact-substring":
                    exactSubstring = true;
                    break;
                case "--prefix":
                    prefix = true;
                    break;
                case "--max-hops":
                case "--depth":
                    var depthOptionName = normalizedArg;
                    if (!TryReadRawOptionValue(args, ref i, depthOptionName, inlineValue, out var depthValue, out var missingDepthError))
                        AddParseError(missingDepthError!);
                    else if (TryParseNonNegativeInt(depthValue!, depthOptionName, out var parsedDepth, out var depthError))
                    {
                        WarnIfDuplicateSingleValueOption("--max-hops", depthValue!);
                        contextAfter = parsedDepth; // reused as depth for impact / impact用に再利用
                        contextAfterExplicit = true;
                        if (depthOptionName == "--depth")
                            impactDeprecatedDepthUsed = true;
                    }
                    else
                        AddParseError(depthError!);
                    break;
                case "--reverse":
                    break; // handled by specific commands / 特定コマンドで処理
                case "--group-by-name":
                    break;
                case "--group-by":
                    if (TryReadStringOptionValue(args, ref i, "--group-by", inlineValue, allowSeparatedDashPrefixedLiteralValue: false, out var groupByValue, out var groupByError))
                    {
                        WarnIfDuplicateSingleValueOption("--group-by", groupByValue!);
                        groupBy = groupByValue?.ToLowerInvariant();
                    }
                    else
                        AddParseError(groupByError!);
                    break;
                case "--unique":
                    if (TryReadStringOptionValue(args, ref i, "--unique", inlineValue, allowSeparatedDashPrefixedLiteralValue: false, out var uniqueValue, out var uniqueError))
                    {
                        WarnIfDuplicateSingleValueOption("--unique", uniqueValue!);
                        uniqueBy = uniqueValue?.ToLowerInvariant();
                    }
                    else
                        AddParseError(uniqueError!);
                    break;
                case "--count-by":
                    if (TryReadStringOptionValue(args, ref i, "--count-by", inlineValue, allowSeparatedDashPrefixedLiteralValue: false, out var countByValue, out var countByError))
                    {
                        WarnIfDuplicateSingleValueOption("--count-by", countByValue!);
                        countBy = countByValue?.ToLowerInvariant();
                    }
                    else
                        AddParseError(countByError!);
                    break;
                case "--origin":
                case "--match-origin":
                    var originOptionName = normalizedArg;
                    if (TryReadStringOptionValue(args, ref i, originOptionName, inlineValue, allowSeparatedDashPrefixedLiteralValue: false, out var originValue, out var originError))
                        AddSearchMatchOrigins(originOptionName, originValue!, matchOrigins, AddParseError);
                    else
                        AddParseError(originError!);
                    break;
                case "--exclude-origin":
                    if (TryReadStringOptionValue(args, ref i, "--exclude-origin", inlineValue, allowSeparatedDashPrefixedLiteralValue: false, out var excludedOriginValue, out var excludedOriginError))
                        AddSearchMatchOrigins("--exclude-origin", excludedOriginValue!, excludeOrigins, AddParseError);
                    else
                        AddParseError(excludedOriginError!);
                    break;
                case "--result-kind":
                    if (TryReadStringOptionValue(args, ref i, "--result-kind", inlineValue, allowSeparatedDashPrefixedLiteralValue: false, out var resultKindValue, out var resultKindError))
                        AddSearchResultKinds(resultKindValue!, resultKinds, AddParseError);
                    else
                        AddParseError(resultKindError!);
                    break;
                case "--search-fields":
                    if (TryReadStringOptionValue(args, ref i, "--search-fields", inlineValue, allowSeparatedDashPrefixedLiteralValue: false, out var searchFieldsValue, out var searchFieldsError))
                    {
                        WarnIfDuplicateSingleValueOption("--search-fields", searchFieldsValue!);
                        searchFields = ParseSearchProjectionFields(searchFieldsValue!, AddParseError);
                        json = true;
                        outputFormat = OutputFormatJson;
                    }
                    else
                        AddParseError(searchFieldsError!);
                    break;
                case "--first-per-file":
                    firstPerFile = true;
                    break;
                case "--results-only":
                    resultsOnly = true;
                    json = true;
                    jsonOutputFormat = JsonOutputFormatNdjson;
                    outputFormat = OutputFormatJson;
                    break;
                case "--next-steps":
                    nextSteps = true;
                    break;
                case "--sample":
                    if (!TryReadRawOptionValue(args, ref i, "--sample", inlineValue, out var sampleValue, out var missingSampleError))
                        AddParseError(missingSampleError!);
                    else if (TryParsePositiveInt(sampleValue!, "--sample", out var parsedSample, out var sampleError))
                    {
                        WarnIfDuplicateSingleValueOption("--sample", sampleValue!);
                        sampleSize = parsedSample;
                    }
                    else
                        AddParseError(sampleError!);
                    break;
                case "--per-file-limit":
                    if (!TryReadRawOptionValue(args, ref i, "--per-file-limit", inlineValue, out var perFileLimitValue, out var missingPerFileLimitError))
                        AddParseError(missingPerFileLimitError!);
                    else if (TryParsePositiveInt(perFileLimitValue!, "--per-file-limit", out var parsedPerFileLimit, out var perFileLimitError))
                    {
                        WarnIfDuplicateSingleValueOption("--per-file-limit", perFileLimitValue!);
                        groupedPerFileLimit = Math.Min(parsedPerFileLimit, MaxSearchGroupedPerFileLimit);
                    }
                    else
                        AddParseError(perFileLimitError!);
                    break;
                case "--max-json-bytes":
                    if (!TryReadRawOptionValue(args, ref i, "--max-json-bytes", inlineValue, out var maxJsonBytesValue, out var missingMaxJsonBytesError))
                        AddParseError(missingMaxJsonBytesError!);
                    else if (TryParsePositiveInt(maxJsonBytesValue!, "--max-json-bytes", out var parsedMaxJsonBytes, out var maxJsonBytesError))
                    {
                        WarnIfDuplicateSingleValueOption("--max-json-bytes", maxJsonBytesValue!);
                        maxJsonBytes = Math.Min(parsedMaxJsonBytes, MaxSearchJsonByteLimit);
                    }
                    else
                        AddParseError(maxJsonBytesError!);
                    break;
                case "--with-paths":
                    withPaths = true;
                    break;
                case "--bytes":
                    rawBytes = true;
                    break;
                case "--raw-kinds":
                    rawKinds = true;
                    break;
                case "--verbose":
                    verbose = true;
                    break;
                case "--profile":
                    profile = true;
                    break;
                case "--slow-query-ms":
                    if (!TryReadRawOptionValue(args, ref i, "--slow-query-ms", inlineValue, out var slowQueryValue, out var missingSlowQueryError))
                        AddParseError(missingSlowQueryError!);
                    else if (TryParseNonNegativeInt(slowQueryValue!, "--slow-query-ms", out var parsedSlowQueryMs, out var slowQueryError))
                    {
                        WarnIfDuplicateSingleValueOption("--slow-query-ms", slowQueryValue!);
                        slowQueryMs = parsedSlowQueryMs;
                    }
                    else
                        AddParseError(slowQueryError!);
                    break;
                case "--min-entrypoint-confidence":
                    if (!TryReadRawOptionValue(args, ref i, "--min-entrypoint-confidence", inlineValue, out var minEntrypointConfidenceValue, out var missingMinEntrypointConfidenceError))
                        AddParseError(missingMinEntrypointConfidenceError!);
                    else if (TryParseConfidence(minEntrypointConfidenceValue!, out var parsedMinEntrypointConfidence))
                    {
                        WarnIfDuplicateSingleValueOption("--min-entrypoint-confidence", minEntrypointConfidenceValue!);
                        minEntrypointConfidence = parsedMinEntrypointConfidence;
                    }
                    else
                        AddParseError($"Error: --min-entrypoint-confidence must be a number from 0.0 through 1.0; got '{ConsoleUi.FormatBoundedValue(minEntrypointConfidenceValue)}'.");
                    break;
                case "--check":
                    if (allowStatusCheck)
                    {
                        checkWorkspace = true;
                    }
                    else if (allowNamedQuery && query == null)
                    {
                        query = currentArg;
                    }
                    else
                    {
                        AddParseError("Error: --check is not supported by this command.");
                    }
                    break;
                case "--outline-fields":
                    if (TryReadStringOptionValue(args, ref i, "--outline-fields", inlineValue, allowSeparatedDashPrefixedLiteralValue: false, out var outlineFieldsValue, out var outlineFieldsError))
                    {
                        WarnIfDuplicateSingleValueOption("--outline-fields", outlineFieldsValue!);
                        outlineFields = ParseOutlineProjectionFields(outlineFieldsValue!, AddParseError);
                        outlineFieldsExplicit = true;
                        json = true;
                        outputFormat = OutputFormatJson;
                    }
                    else
                    {
                        AddParseError(outlineFieldsError!);
                    }
                    break;
                case "--stale-after":
                    if (allowStatusCheck)
                    {
                        if (TryReadStringOptionValue(args, ref i, "--stale-after", inlineValue, allowSeparatedDashPrefixedLiteralValue: false, out var staleAfterValue, out var staleAfterError))
                        {
                            WarnIfDuplicateSingleValueOption("--stale-after", staleAfterValue!);
                            if (TryParseStaleAfter(staleAfterValue!, out var parsedStaleAfter, out var parseStaleAfterError))
                                staleAfter = parsedStaleAfter;
                            else
                                AddParseError(parseStaleAfterError!);
                        }
                        else
                        {
                            AddParseError(staleAfterError!);
                        }
                    }
                    else
                    {
                        AddParseError("Error: --stale-after is not supported by this command.");
                    }
                    break;
                case "--explain":
                    if (allowStatusCheck)
                    {
                        if (TryReadStringOptionValue(args, ref i, "--explain", inlineValue, allowSeparatedDashPrefixedLiteralValue: false, out var explainValue, out var explainError))
                        {
                            WarnIfDuplicateSingleValueOption("--explain", explainValue!);
                            statusExplainField = explainValue;
                        }
                        else
                            AddParseError(explainError!);
                    }
                    else if (allowNamedQuery && query == null)
                    {
                        query = currentArg;
                    }
                    else
                    {
                        AddParseError("Error: --explain is not supported by this command.");
                    }
                    break;
                case "--log-path":
                    if (allowStatusCheck)
                    {
                        statusLogPath = true;
                    }
                    else
                    {
                        AddParseError("Error: --log-path is not supported by this command.");
                    }
                    break;
                case "--config":
                    if (allowStatusCheck)
                    {
                        statusConfig = true;
                    }
                    else
                    {
                        AddParseError("Error: --config is only supported by status.");
                    }
                    break;
                case "--log-format":
                case "--log-retain-count":
                case "--log-max-size-mb":
                    if (allowNamedQuery && query == null)
                    {
                        query = currentArg;
                    }
                    else
                    {
                        AddParseError($"Error: unsupported option: {ConsoleUi.FormatBoundedValue(currentArg)}. Use `--` before a query literal that starts with `-`.");
                    }
                    break;
                case "--path":
                    if (TryReadStringOptionValue(args, ref i, "--path", inlineValue, allowSeparatedDashPrefixedLiteralValue: true, out var pathPattern, out var pathError))
                    {
                        pathPatterns.Add(pathPattern!); // Repeatable; multiple values OR together / 繰り返し可、複数値は OR で結合
                        userPathPatterns.Add(pathPattern!);
                    }
                    else
                        AddParseError(pathError!);
                    break;
                case "--project":
                    if (TryReadStringOptionValue(args, ref i, "--project", inlineValue, allowSeparatedDashPrefixedLiteralValue: true, out var projectName, out var projectError))
                        projectFilters.Add(projectName!);
                    else
                        AddParseError(projectError!);
                    break;
                case "--solution":
                    if (TryReadStringOptionValue(args, ref i, "--solution", inlineValue, allowSeparatedDashPrefixedLiteralValue: true, out var solutionValue, out var solutionError))
                    {
                        WarnIfDuplicateSingleValueOption("--solution", solutionValue!);
                        solutionFilter = solutionValue;
                    }
                    else
                        AddParseError(solutionError!);
                    break;
                case "--exclude-path":
                    if (TryReadStringOptionValue(args, ref i, "--exclude-path", inlineValue, allowSeparatedDashPrefixedLiteralValue: true, out var excludePath, out var excludePathError))
                        excludePaths.Add(excludePath!);
                    else
                        AddParseError(excludePathError!);
                    break;
                case "--exclude-tests":
                    excludeTests = true;
                    break;
                case "--exclude-comments":
                    excludeComments = true;
                    break;
                case "--exclude-strings":
                    excludeStrings = true;
                    break;
                case "--exclude-fixtures":
                    excludeFixtures = true;
                    break;
                case "--actionable":
                    unusedActionable = true;
                    break;
                case "--include-generated":
                    includeGenerated = true;
                    break;
                case "--since":
                    if (!TryReadStringOptionValue(args, ref i, "--since", inlineValue, allowSeparatedDashPrefixedLiteralValue: false, out var sinceValue, out var sinceError))
                        AddParseError(sinceError!);
                    else if (TryParseIso8601Since(sinceValue!, out var parsedSince))
                    {
                        WarnIfDuplicateSingleValueOption("--since", sinceValue!);
                        since = parsedSince;
                    }
                    else
                        AddParseError($"Error: could not parse --since value '{ConsoleUi.FormatBoundedValue(sinceValue)}' as a date/time. Use ISO 8601 format (e.g. 2024-01-01 or 2024-01-01T00:00:00Z).");
                    break;
                case "--line":
                    if (!TryReadRawOptionValue(args, ref i, "--line", inlineValue, out var lineValue, out var missingLineError))
                        AddParseError(missingLineError!);
                    else if (TryParsePositiveInt(lineValue!, "--line", out var parsedLine, out var lineError))
                    {
                        WarnIfDuplicateSingleValueOption("--start", lineValue!);
                        WarnIfDuplicateSingleValueOption("--end", lineValue!);
                        startLine = parsedLine;
                        endLine = parsedLine;
                    }
                    else
                        AddParseError(lineError!);
                    break;
                case "--start":
                case "--start-line":
                    var startFlag = normalizedArg;
                    if (!TryReadRawOptionValue(args, ref i, startFlag, inlineValue, out var startValue, out var missingStartError))
                        AddParseError(missingStartError!);
                    else if (TryParsePositiveInt(startValue!, startFlag, out var parsedStart, out var startError))
                    {
                        WarnIfDuplicateSingleValueOption("--start", startValue!);
                        startLine = parsedStart;
                    }
                    else
                        AddParseError(startError!);
                    break;
                case "--end":
                case "--end-line":
                    var endFlag = normalizedArg;
                    if (!TryReadRawOptionValue(args, ref i, endFlag, inlineValue, out var endValue, out var missingEndError))
                        AddParseError(missingEndError!);
                    else if (TryParsePositiveInt(endValue!, endFlag, out var parsedEnd, out var endError))
                    {
                        WarnIfDuplicateSingleValueOption("--end", endValue!);
                        endLine = parsedEnd;
                    }
                    else
                        AddParseError(endError!);
                    break;
                case "--context":
                    if (!TryReadRawOptionValue(args, ref i, "--context", inlineValue, out var contextValue, out var missingContextError))
                        AddParseError(missingContextError!);
                    else if (TryParseNonNegativeInt(contextValue!, "--context", out var parsedContext, out var contextError))
                    {
                        WarnIfDuplicateSingleValueOption("--before", contextValue!);
                        WarnIfDuplicateSingleValueOption("--after", contextValue!);
                        contextBefore = parsedContext;
                        contextAfter = parsedContext;
                        contextAfterExplicit = true;
                    }
                    else
                        AddParseError(contextError!);
                    break;
                case "--before":
                    if (!TryReadRawOptionValue(args, ref i, "--before", inlineValue, out var beforeValue, out var missingBeforeError))
                        AddParseError(missingBeforeError!);
                    else if (TryParseNonNegativeInt(beforeValue!, "--before", out var parsedBefore, out var beforeError))
                    {
                        WarnIfDuplicateSingleValueOption("--before", beforeValue!);
                        contextBefore = parsedBefore;
                    }
                    else
                        AddParseError(beforeError!);
                    break;
                case "--after":
                    if (!TryReadRawOptionValue(args, ref i, "--after", inlineValue, out var afterValue, out var missingAfterError))
                        AddParseError(missingAfterError!);
                    else if (TryParseNonNegativeInt(afterValue!, "--after", out var parsedAfter, out var afterError))
                    {
                        WarnIfDuplicateSingleValueOption("--after", afterValue!);
                        contextAfter = parsedAfter;
                    }
                    else
                        AddParseError(afterError!);
                    break;
                case "--focus-line":
                    if (!TryReadRawOptionValue(args, ref i, "--focus-line", inlineValue, out var focusLineValue, out var missingFocusLineError))
                        AddParseError(missingFocusLineError!);
                    else if (TryParsePositiveInt(focusLineValue!, "--focus-line", out var parsedFocusLine, out var focusLineError))
                    {
                        WarnIfDuplicateSingleValueOption("--focus-line", focusLineValue!);
                        focusLine = parsedFocusLine;
                    }
                    else
                        AddParseError(focusLineError!);
                    break;
                case "--focus-column":
                    if (!TryReadRawOptionValue(args, ref i, "--focus-column", inlineValue, out var focusColumnValue, out var missingFocusColumnError))
                        AddParseError(missingFocusColumnError!);
                    else if (TryParsePositiveInt(focusColumnValue!, "--focus-column", out var parsedFocusColumn, out var focusColumnError))
                    {
                        WarnIfDuplicateSingleValueOption("--focus-column", focusColumnValue!);
                        focusColumn = parsedFocusColumn;
                    }
                    else
                        AddParseError(focusColumnError!);
                    break;
                case "--focus-length":
                    if (!TryReadRawOptionValue(args, ref i, "--focus-length", inlineValue, out var focusLengthValue, out var missingFocusLengthError))
                        AddParseError(missingFocusLengthError!);
                    else if (TryParsePositiveInt(focusLengthValue!, "--focus-length", out var parsedFocusLength, out var focusLengthError))
                    {
                        WarnIfDuplicateSingleValueOption("--focus-length", focusLengthValue!);
                        focusLength = parsedFocusLength;
                    }
                    else
                        AddParseError(focusLengthError!);
                    break;
                case "--name":
                    if (TryReadStringOptionValue(args, ref i, "--name", inlineValue, allowSeparatedDashPrefixedLiteralValue: false, out var extraName, out var nameError))
                        extraNames.Add(extraName!); // Repeatable; OR-joined with other --name values and extra positional names / 繰り返し可、他の --name や追加の positional 引数と OR 結合
                    else
                        AddParseError($"{nameError} / --name には値（シンボル名パターン）が必要です。");
                    break;
                case "--snippet-lines":
                    if (!TryReadRawOptionValue(args, ref i, "--snippet-lines", inlineValue, out var snippetLinesValue, out var missingSnippetLinesError))
                        AddParseError(missingSnippetLinesError!);
                    else if (TryParsePositiveInt(snippetLinesValue!, "--snippet-lines", out var parsedSnippetLines, out var snippetLinesError))
                    {
                        WarnIfDuplicateSingleValueOption("--snippet-lines", snippetLinesValue!);
                        snippetLines = parsedSnippetLines;
                        snippetLinesExplicit = true;
                    }
                    else
                        AddParseError(snippetLinesError!);
                    break;
                case "--snippet-focus":
                    if (!TryReadStringOptionValue(args, ref i, "--snippet-focus", inlineValue, allowSeparatedDashPrefixedLiteralValue: false, out var snippetFocusValue, out var snippetFocusError))
                    {
                        AddParseError(snippetFocusError!);
                    }
                    else if (TryParseSnippetFocusMode(snippetFocusValue!, out var parsedSnippetFocus))
                    {
                        WarnIfDuplicateSingleValueOption("--snippet-focus", snippetFocusValue!);
                        snippetFocus = parsedSnippetFocus;
                    }
                    else
                    {
                        AddParseError($"Error: invalid --snippet-focus value '{ConsoleUi.FormatBoundedValue(snippetFocusValue)}'. Use leftmost, quality, or proximity.");
                    }
                    break;
                case "--max-line-width":
                    if (!TryReadRawOptionValue(args, ref i, "--max-line-width", inlineValue, out var maxLineWidthValue, out var missingMaxLineWidthError))
                        AddParseError(missingMaxLineWidthError!);
                    else if (TryParseNonNegativeInt(maxLineWidthValue!, "--max-line-width", out var parsedMaxLineWidth, out var maxLineWidthError))
                    {
                        WarnIfDuplicateSingleValueOption("--max-line-width", maxLineWidthValue!);
                        maxLineWidth = parsedMaxLineWidth;
                        maxLineWidthExplicit = true;
                    }
                    else
                        AddParseError(maxLineWidthError!);
                    break;
                default:
                    if (args[i].StartsWith('-'))
                    {
                        AddParseError($"Error: unsupported option: {ConsoleUi.FormatBoundedValue(args[i])}. Use `--` before a query literal that starts with `-`.");
                        break;
                    }
                    else if (query == null)
                    {
                        query = args[i];
                    }
                    else
                    {
                        // Extra positional args become additional symbol names / 追加の positional 引数を追加の symbol name として扱う
                        extraNames.Add(args[i]);
                    }
                    break;
            }
        }

        if (unusedActionable)
        {
            unusedBucket ??= "likely_unused_private";
            minUnusedConfidence ??= "medium";
            if (visibilityFilters.Count == 0)
                visibilityFilters.Add("private");
            excludeTests = true;
        }

        var dbResolution = DbPathResolver.ResolveForQuery(Environment.CurrentDirectory, dbPath, dataDir);
        var resolvedDbPath = dbResolution.DbPath;

        if (parseErrors == null && projectFilters.Count > 0)
        {
            try
            {
                projectFilterRootResolution = ResolveProjectFilterRoot(resolvedDbPath, dbPathExplicit);
                foreach (var glob in SolutionProjectResolver.ResolveProjectDirectoryGlobs(projectFilterRootResolution.Value.Root, projectFilters, solutionFilter))
                    pathPatterns.Add(glob);
            }
            catch (Exception ex) when (ex is IOException or UnauthorizedAccessException or InvalidOperationException)
            {
                AddParseError($"Error: {ex.Message}");
            }
        }

        ValidateQueryPathOptionValues(userPathPatterns, excludePaths, AddParseError);
        if (guardFilters.Count > DbReader.MaxSearchGuardFilters)
            AddParseError($"Error: search accepts at most {DbReader.MaxSearchGuardFilters} guard filters; got {guardFilters.Count}.");
        var duplicateNamedQuery = namedSearchQueries
            .GroupBy(query => query.Name, StringComparer.OrdinalIgnoreCase)
            .FirstOrDefault(group => group.Count() > 1);
        if (duplicateNamedQuery != null)
            AddParseError($"Error: duplicate --named-query name '{ConsoleUi.FormatBoundedValue(duplicateNamedQuery.Key)}'. Use unique names so grouped results are unambiguous.");
        if (duplicateConfidenceExplicit && duplicateThresholdExplicit)
            AddParseError("Error: --duplicate-confidence and --duplicate-threshold cannot be combined; use the preset or the explicit score threshold.");

        if (validateDefaultLimit && !limitExplicit && defaultLimitError != null)
            AddParseError(defaultLimitError);
        if (validateDefaultSnippetLines && !snippetLinesExplicit && defaultSnippetLinesError != null)
            AddParseError(defaultSnippetLinesError);
        if (validateDefaultMaxLineWidth && !maxLineWidthExplicit && defaultMaxLineWidthError != null)
            AddParseError(defaultMaxLineWidthError);

        if (readOnly)
        {
            var canAppendReadOnlyFlags = !SqliteFileUri.StartsWithFileScheme(resolvedDbPath) ||
                SqliteFileUri.TryValidateBounds(resolvedDbPath, out _);
            if (canAppendReadOnlyFlags)
                resolvedDbPath = DbContext.ToReadOnlyUri(resolvedDbPath);
        }

        return new QueryCommandOptions
        {
            DbPath = resolvedDbPath,
            DbPathExplicit = dbPathExplicit,
            ReadOnly = readOnly,
            DryRun = dryRun,
            DataDir = dbResolution.DataDir,
            DataDirSource = dbResolution.DataDirSource,
            Json = json ?? jsonDefault,
            JsonOutputFormat = jsonOutputFormat,
            OutputFormat = outputFormat,
            Limit = limit,
            LimitExplicit = limitExplicit,
            Lang = lang,
            Kind = kind,
            UnusedBucket = unusedBucket,
            MinUnusedConfidence = minUnusedConfidence,
            UnusedActionable = unusedActionable,
            Severity = severity,
            Query = query,
            RawFts = rawFts,
            IncludeBody = includeBody,
            BodyStartLine = bodyStartLine,
            BodyLines = bodyLines,
            StartLine = startLine,
            EndLine = endLine,
            ContextBefore = contextBefore,
            ContextAfter = contextAfter,
            ContextAfterExplicit = contextAfterExplicit,
            ImpactDeprecatedDepthUsed = impactDeprecatedDepthUsed,
            FocusLine = focusLine,
            FocusColumn = focusColumn,
            FocusLength = focusLength,
            SnippetLines = snippetLines,
            SnippetFocus = snippetFocus,
            MaxLineWidth = maxLineWidth,
            PathPatterns = pathPatterns,
            WorkspaceDbPaths = workspaceDbPaths,
            ProjectFilters = projectFilters,
            ProjectFilterRoot = projectFilterRootResolution?.Root,
            ProjectFilterRootFallbackReason = projectFilterRootResolution?.FallbackReason,
            SolutionFilter = solutionFilter,
            ExcludePaths = excludePaths,
            VisibilityFilters = visibilityFilters,
            ExcludeVisibilityFilters = excludeVisibilityFilters,
            ExcludeTests = excludeTests,
            IncludeGenerated = includeGenerated,
            CountOnly = countOnly,
            All = all,
            StrictNotFound = strictNotFound,
            Strict = strict,
            Since = since,
            NoDedup = noDedup,
            NoVisibilityRank = noVisibilityRank,
            Exact = exact,
            Regex = regex,
            Prefix = prefix,
            GuardFilters = guardFilters,
            GuardWindow = guardWindow,
            GuardScope = guardScope,
            ExcludeComments = excludeComments,
            ExcludeStrings = excludeStrings,
            ExcludeFixtures = excludeFixtures,
            ExactName = exactName,
            ExactSubstring = exactSubstring,
            CheckWorkspace = checkWorkspace,
            StaleAfter = staleAfter,
            StatusCheckScopes = statusCheckScopes,
            WithPaths = withPaths,
            GroupBy = groupBy,
            UniqueBy = uniqueBy,
            CountBy = countBy,
            MatchOrigins = matchOrigins,
            ExcludeOrigins = excludeOrigins,
            ResultKinds = resultKinds,
            SearchFields = searchFields,
            OutlineFields = outlineFields,
            OutlineFieldsExplicit = outlineFieldsExplicit,
            FirstPerFile = firstPerFile,
            ResultsOnly = resultsOnly,
            NextSteps = nextSteps,
            GroupedPerFileLimit = groupedPerFileLimit,
            SampleSize = sampleSize,
            MaxJsonBytes = maxJsonBytes,
            RawBytes = rawBytes,
            RawKinds = rawKinds,
            Verbose = verbose,
            Profile = profile,
            SlowQueryMs = slowQueryMs,
            Compact = compact,
            InspectFields = inspectFields,
            MinEntrypointConfidence = minEntrypointConfidence,
            StatusExplainField = statusExplainField,
            StatusLogPath = statusLogPath,
            StatusConfig = statusConfig,
            RankMode = rankMode,
            SymbolSortMode = symbolSortMode,
            ExtraNames = extraNames,
            MapSections = mapSections,
            MapSummaryOnly = mapSummaryOnly,
            DependencyCycles = dependencyCycles,
            RecipeName = recipeName,
            IncludeRecipeQueries = includeRecipeQueries,
            ExcludeRecipeQueries = excludeRecipeQueries,
            ShowExcluded = showExcluded,
            ListRecipes = listRecipes,
            OpenIssuesPath = openIssuesPath,
            AuditScope = auditScope,
            AuditScopeExplicit = auditScopeExplicit,
            OpenIssuesRepository = openIssuesRepository,
            DuplicateConfidence = duplicateThresholdExplicit ? IssueDuplicatePreflight.CustomDuplicateConfidence : duplicateConfidence,
            DuplicateThreshold = duplicateThreshold,
            DuplicatePreflightTuningExplicit = duplicateConfidenceExplicit || duplicateThresholdExplicit,
            IssueTitle = issueTitle,
            IssueLabels = issueLabels,
            SearchCursor = searchCursor,
            UnusedCursorOffset = unusedCursorOffset,
            OutlineCursorOffset = outlineCursorOffset,
            NamedSearchQueries = namedSearchQueries,
            LanguagesIndexedOnly = languagesIndexedOnly,
            LanguageCapabilities = languageCapabilities,
            LanguageLookups = languageLookups,
            LanguageExtensionLookups = languageExtensionLookups,
            LanguageAliasLookups = languageAliasLookups,
            ParseError = parseErrors == null ? null : string.Join(Environment.NewLine, parseErrors),
        };
    }

    private static bool TryParseNamedSearchQuery(string value, out SearchNamedQuery namedQuery, out string? error)
    {
        namedQuery = new SearchNamedQuery(string.Empty, string.Empty);
        error = null;
        var separator = value.IndexOf('=');
        if (separator <= 0)
        {
            error = "Error: --named-query must use <name>=<query>.";
            return false;
        }

        var name = value[..separator].Trim();
        var query = value[(separator + 1)..].Trim();
        if (string.IsNullOrWhiteSpace(name))
        {
            error = "Error: --named-query name cannot be empty.";
            return false;
        }
        if (name.Length > MaxNamedSearchQueryNameLength)
        {
            error = $"Error: --named-query name '{ConsoleUi.FormatBoundedValue(name)}' exceeds the {MaxNamedSearchQueryNameLength} character limit.";
            return false;
        }
        if (string.IsNullOrWhiteSpace(query))
        {
            error = $"Error: --named-query '{ConsoleUi.FormatBoundedValue(name)}' query cannot be empty.";
            return false;
        }
        if (query.Length > QueryLimits.MaxQueryLength)
        {
            error = QueryLimits.FormatQueryTooLongError();
            return false;
        }

        namedQuery = new SearchNamedQuery(name, query);
        return true;
    }

    internal static ProjectFilterRootResolution ResolveProjectFilterRoot(string dbPath, bool dbPathExplicit)
    {
        var effectiveDbPath = s_batchReader != null && !string.IsNullOrWhiteSpace(s_batchDbPath)
            ? s_batchDbPath!
            : dbPath;
        var effectiveDbPathExplicit = s_batchReader != null && !string.IsNullOrWhiteSpace(s_batchDbPath)
            ? s_batchDbPathExplicit
            : dbPathExplicit;
        var projectRoot = DbPathResolver.ResolveProjectRootForQuery(effectiveDbPath, effectiveDbPathExplicit);
        if (!string.IsNullOrWhiteSpace(projectRoot))
            return new ProjectFilterRootResolution(Path.GetFullPath(projectRoot), null);

        return new ProjectFilterRootResolution(
            Path.GetFullPath(Environment.CurrentDirectory),
            ProjectFilterRootFallbackReasonCurrentDirectory);
    }

    private static List<string> ParseMapSections(string rawValue, Action<string> addParseError)
    {
        var sections = new List<string>();
        if (!ValidateCsvBounds("--sections", rawValue, MaxMapSectionsCsvLength, MaxMapSectionsCsvEntries, addParseError))
            return sections;

        foreach (var rawSection in rawValue.Split(',', StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries))
        {
            var section = rawSection.ToLowerInvariant();
            switch (section)
            {
                case "tree":
                case "modules":
                    sections.Add("tree");
                    break;
                case "languages":
                case "hotspots":
                case "metrics":
                    sections.Add(section);
                    break;
                default:
                    addParseError($"Error: --sections contains unsupported section '{ConsoleUi.FormatBoundedValue(rawSection)}'. Use one or more of tree, languages, hotspots, metrics.");
                    break;
            }
        }

        if (sections.Count == 0)
            addParseError("Error: --sections cannot be empty. Use one or more of tree, languages, hotspots, metrics.");
        return sections.Distinct(StringComparer.Ordinal).ToList();
    }

    private static List<string>? ParseInspectFields(string rawValue, Action<string> addParseError, out bool includeBody)
    {
        includeBody = false;
        var fields = new List<string>();
        var seen = new HashSet<string>(StringComparer.Ordinal);
        var all = false;

        if (!ValidateCsvBounds("--fields", rawValue, MaxInspectFieldsCsvLength, MaxInspectFieldsCsvEntries, addParseError))
            return fields;

        foreach (var rawField in rawValue.Split(',', StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries))
        {
            var field = rawField.ToLowerInvariant().Replace('-', '_');
            string canonical;
            switch (field)
            {
                case "all":
                    all = true;
                    continue;
                case "file":
                    canonical = "file";
                    break;
                case "metadata":
                case "workspace":
                    canonical = "workspace";
                    break;
                case "graph":
                case "trust":
                    canonical = "graph";
                    break;
                case "definition":
                case "definitions":
                case "defs":
                    canonical = "definitions";
                    break;
                case "body":
                    canonical = "definitions";
                    includeBody = true;
                    break;
                case "source":
                case "source_excerpt":
                case "excerpt":
                    canonical = "source_excerpt";
                    break;
                case "nearby":
                case "nearby_symbols":
                case "nearbysymbols":
                    canonical = "nearby_symbols";
                    break;
                case "reference":
                case "references":
                case "refs":
                    canonical = "references";
                    break;
                case "caller":
                case "callers":
                    canonical = "callers";
                    break;
                case "callee":
                case "callees":
                    canonical = "callees";
                    break;
                default:
                    addParseError($"Error: unsupported --fields value '{ConsoleUi.FormatBoundedValue(rawField)}'. Use one or more of all, file, workspace, graph, definitions, body, source_excerpt, nearby_symbols, references, callers, callees.");
                    continue;
            }

            if (seen.Add(canonical))
                fields.Add(canonical);
        }

        if (all && fields.Count > 0)
            addParseError("Error: --fields all cannot be combined with specific field names.");
        if (!all && fields.Count == 0)
            addParseError("Error: --fields requires at least one field name.");

        return all ? null : fields;
    }

    private static List<string>? ParseSearchProjectionFields(string rawValue, Action<string> addParseError)
    {
        var fields = new List<string>();
        var seen = new HashSet<string>(StringComparer.Ordinal);
        if (!ValidateCsvBounds("--search-fields", rawValue, MaxSearchProjectionFieldsCsvLength, MaxSearchProjectionFieldsCsvEntries, addParseError))
            return fields;

        foreach (var rawField in rawValue.Split(',', StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries))
        {
            var field = rawField.ToLowerInvariant().Replace('-', '_');
            string canonical;
            switch (field)
            {
                case "path":
                case "file":
                    canonical = "path";
                    break;
                case "line":
                case "start_line":
                    canonical = "line";
                    break;
                case "end_line":
                    canonical = "end_line";
                    break;
                case "lang":
                case "language":
                    canonical = "lang";
                    break;
                case "column":
                case "col":
                    canonical = "column";
                    break;
                case "symbol":
                case "symbol_name":
                    canonical = "symbol";
                    break;
                case "symbol_kind":
                    canonical = "symbol_kind";
                    break;
                case "origin":
                case "origins":
                case "match_origin":
                case "match_origins":
                    canonical = "origin";
                    break;
                case "kind":
                case "result_kind":
                case "result_kinds":
                    canonical = "kind";
                    break;
                case "score":
                    canonical = "score";
                    break;
                case "snippet":
                    canonical = "snippet";
                    break;
                default:
                    addParseError($"Error: unsupported --search-fields value '{ConsoleUi.FormatBoundedValue(rawField)}'. Use one or more of path,line,end_line,lang,column,symbol,symbol_kind,origin,kind,score,snippet.");
                    continue;
            }

            if (seen.Add(canonical))
                fields.Add(canonical);
        }

        if (fields.Count == 0)
            addParseError("Error: --search-fields requires at least one field name.");
        return fields;
    }

    private static List<string>? ParseOutlineProjectionFields(string rawValue, Action<string> addParseError)
    {
        var fields = new List<string>();
        var seen = new HashSet<string>(StringComparer.Ordinal);
        var all = false;
        if (!ValidateCsvBounds("--outline-fields", rawValue, MaxOutlineProjectionFieldsCsvLength, MaxOutlineProjectionFieldsCsvEntries, addParseError))
            return fields;

        void AddField(string field)
        {
            if (seen.Add(field))
                fields.Add(field);
        }

        foreach (var rawField in rawValue.Split(',', StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries))
        {
            var field = rawField.ToLowerInvariant().Replace('-', '_');
            switch (field)
            {
                case "all":
                    all = true;
                    continue;
                case "kind":
                case "name":
                case "display_name":
                case "path":
                case "line":
                case "start_line":
                case "end_line":
                case "depth":
                case "body_start_line":
                case "body_end_line":
                case "signature":
                case "signature_truncated":
                case "signature_original_length":
                case "container_kind":
                case "container_name":
                case "visibility":
                case "return_type":
                    AddField(field);
                    break;
                case "range":
                case "lines":
                    AddField("start_line");
                    AddField("end_line");
                    break;
                case "body":
                case "body_range":
                    AddField("body_start_line");
                    AddField("body_end_line");
                    break;
                case "container":
                    AddField("container_kind");
                    AddField("container_name");
                    break;
                default:
                    addParseError($"Error: unsupported --outline-fields value '{ConsoleUi.FormatBoundedValue(rawField)}'. Use one or more of all, kind, name, display_name, path, line, start_line, end_line, depth, body_start_line, body_end_line, signature, signature_truncated, signature_original_length, container_kind, container_name, visibility, return_type, or aliases range, lines, body, body_range, container.");
                    continue;
            }
        }

        if (all && fields.Count > 0)
            addParseError("Error: --outline-fields all cannot be combined with specific field names.");
        if (!all && fields.Count == 0)
            addParseError("Error: --outline-fields requires at least one field name.");
        return all ? null : fields;
    }

    private static void AddSearchMatchOrigins(string optionName, string rawValue, List<string> origins, Action<string> addParseError)
    {
        if (!ValidateCsvBounds(optionName, rawValue, MaxSearchProjectionFieldsCsvLength, MaxSearchProjectionFieldsCsvEntries, addParseError))
            return;
        foreach (var rawOrigin in rawValue.Split(',', StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries))
        {
            if (!TryNormalizeSearchMatchOrigin(rawOrigin, out var origin))
            {
                addParseError($"Error: unsupported {optionName} value '{ConsoleUi.FormatBoundedValue(rawOrigin)}'. Use code, comment, string_literal, regex_literal, help_text, or unknown.");
                continue;
            }
            if (!origins.Contains(origin, StringComparer.Ordinal))
                origins.Add(origin);
        }
    }

    private static bool TryNormalizeSearchMatchOrigin(string rawOrigin, out string origin)
    {
        switch (rawOrigin.ToLowerInvariant().Replace("-", "_"))
        {
            case SearchMatchClassifier.Code:
                origin = SearchMatchClassifier.Code;
                return true;
            case SearchMatchClassifier.Comment:
                origin = SearchMatchClassifier.Comment;
                return true;
            case "string":
            case SearchMatchClassifier.StringLiteral:
                origin = SearchMatchClassifier.StringLiteral;
                return true;
            case "regex":
            case SearchMatchClassifier.RegexLiteral:
                origin = SearchMatchClassifier.RegexLiteral;
                return true;
            case "help":
            case SearchMatchClassifier.HelpText:
                origin = SearchMatchClassifier.HelpText;
                return true;
            case SearchMatchClassifier.Unknown:
                origin = SearchMatchClassifier.Unknown;
                return true;
            default:
                origin = string.Empty;
                return false;
        }
    }

    private static void AddSearchResultKinds(string rawValue, List<string> resultKinds, Action<string> addParseError)
    {
        if (!ValidateCsvBounds("--result-kind", rawValue, MaxSearchProjectionFieldsCsvLength, MaxSearchProjectionFieldsCsvEntries, addParseError))
            return;
        foreach (var rawKind in rawValue.Split(',', StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries))
        {
            if (!TryNormalizeSearchResultKind(rawKind, out var kind))
            {
                addParseError($"Error: unsupported --result-kind value '{ConsoleUi.FormatBoundedValue(rawKind)}'. Use call_site, declaration, identifier, code, comment, string_literal, regex_literal, help_text, or unknown.");
                continue;
            }
            if (!resultKinds.Contains(kind, StringComparer.Ordinal))
                resultKinds.Add(kind);
        }
    }

    private static bool TryNormalizeSearchResultKind(string rawKind, out string kind)
    {
        switch (rawKind.ToLowerInvariant().Replace("-", "_"))
        {
            case "call":
            case "callsite":
            case "call_site":
                kind = "call_site";
                return true;
            case "decl":
            case "declaration":
                kind = "declaration";
                return true;
            case "identifier":
            case "ident":
                kind = "identifier";
                return true;
            case SearchMatchClassifier.Code:
                kind = SearchMatchClassifier.Code;
                return true;
            case SearchMatchClassifier.Comment:
                kind = SearchMatchClassifier.Comment;
                return true;
            case "string":
            case SearchMatchClassifier.StringLiteral:
                kind = SearchMatchClassifier.StringLiteral;
                return true;
            case "regex":
            case SearchMatchClassifier.RegexLiteral:
                kind = SearchMatchClassifier.RegexLiteral;
                return true;
            case "help":
            case SearchMatchClassifier.HelpText:
                kind = SearchMatchClassifier.HelpText;
                return true;
            case SearchMatchClassifier.Unknown:
                kind = SearchMatchClassifier.Unknown;
                return true;
            default:
                kind = string.Empty;
                return false;
        }
    }

    private static bool ValidateCsvBounds(
        string optionName,
        string rawValue,
        int maxLength,
        int maxEntries,
        Action<string> addParseError)
    {
        if (rawValue.Length > maxLength)
        {
            addParseError($"Error: {optionName} value is too long ({rawValue.Length} characters; max {maxLength}).");
            return false;
        }

        var entries = CountCsvEntries(rawValue);
        if (entries > maxEntries)
        {
            addParseError($"Error: {optionName} accepts at most {maxEntries} comma-separated entries.");
            return false;
        }

        return true;
    }

    private static int CountCsvEntries(string rawValue)
    {
        if (rawValue.Length == 0)
            return 0;

        var count = 1;
        foreach (var ch in rawValue)
        {
            if (ch == ',')
                count++;
        }

        return count;
    }

    private static void ValidateQueryPathOptionValues(
        IReadOnlyList<string> pathPatterns,
        IReadOnlyList<string> excludePaths,
        Action<string> addParseError)
    {
        ValidatePathOptionValues("--path", pathPatterns, addParseError);
        ValidatePathOptionValues("--exclude-path", excludePaths, addParseError);
    }

    private static void ValidatePathOptionValues(
        string optionName,
        IReadOnlyList<string> patterns,
        Action<string> addParseError)
    {
        if (patterns.Count > MaxQueryPathFilterCount)
            addParseError($"Error: {optionName} accepts at most {MaxQueryPathFilterCount} values.");

        foreach (var pattern in patterns)
        {
            if (pattern.Length > MaxQueryPathFilterLength)
            {
                addParseError($"Error: {optionName} value is too long ({pattern.Length} characters; max {MaxQueryPathFilterLength}).");
                continue;
            }

            ValidatePathGlobPattern(optionName, pattern, addParseError);
        }
    }

    private static bool TryParseJsonOutputFormat(string rawValue, out string format)
    {
        if (string.Equals(rawValue, JsonOutputFormatArray, StringComparison.OrdinalIgnoreCase))
        {
            format = JsonOutputFormatArray;
            return true;
        }
        if (string.Equals(rawValue, JsonOutputFormatNdjson, StringComparison.OrdinalIgnoreCase))
        {
            format = JsonOutputFormatNdjson;
            return true;
        }

        format = JsonOutputFormatNdjson;
        return false;
    }

    private static bool TryParseOutputFormat(string rawValue, out string format)
    {
        switch (rawValue.ToLowerInvariant())
        {
            case OutputFormatText:
            case OutputFormatJson:
            case OutputFormatCount:
            case OutputFormatCompact:
            case OutputFormatGrouped:
            case OutputFormatCsv:
            case OutputFormatTsv:
            case OutputFormatLsp:
            case OutputFormatQf:
            case OutputFormatSarif:
                format = rawValue.ToLowerInvariant();
                return true;
            default:
                format = OutputFormatText;
                return false;
        }
    }

    private static void ValidatePathGlobPattern(string optionName, string pattern, Action<string> addParseError)
    {
        if (TryFindUnsupportedBracketGlob(pattern, out var reason))
        {
            addParseError($"Error: {optionName} '{ConsoleUi.FormatBoundedValue(pattern)}' is not a valid glob: {reason}. Hint: escape '[' or ']' with a backslash when matching literal path characters, or use only '*' and '?' wildcards.");
        }
    }

    private static bool TryFindUnsupportedBracketGlob(string pattern, out string reason)
    {
        var escaped = false;
        for (var i = 0; i < pattern.Length; i++)
        {
            var ch = pattern[i];
            if (escaped)
            {
                escaped = false;
                continue;
            }

            if (ch == '\\')
            {
                escaped = true;
                continue;
            }

            if (ch == '[')
            {
                reason = "character classes are not supported";
                return true;
            }

            if (ch == ']')
            {
                reason = "unmatched ']'";
                return true;
            }
        }

        reason = string.Empty;
        return false;
    }

    internal static bool TryParseReferenceRankMode(string value, out ReferenceRankMode rankMode)
    {
        switch (value.Trim().ToLowerInvariant())
        {
            case "weighted":
                rankMode = ReferenceRankMode.Weighted;
                return true;
            case "count":
                rankMode = ReferenceRankMode.Count;
                return true;
            case "kind":
                rankMode = ReferenceRankMode.Kind;
                return true;
            default:
                rankMode = ReferenceRankMode.Weighted;
                return false;
        }
    }

    internal static bool TryParseSymbolSortMode(string value, out SymbolSortMode sortMode)
    {
        switch (value.Trim().ToLowerInvariant())
        {
            case "name":
                sortMode = SymbolSortMode.Name;
                return true;
            case "hotspot":
                sortMode = SymbolSortMode.Hotspot;
                return true;
            case "references":
            case "reference":
            case "refs":
                sortMode = SymbolSortMode.References;
                return true;
            case "size":
                sortMode = SymbolSortMode.Size;
                return true;
            case "complexity":
                sortMode = SymbolSortMode.Complexity;
                return true;
            case "path":
                sortMode = SymbolSortMode.Path;
                return true;
            default:
                sortMode = SymbolSortMode.Name;
                return false;
        }
    }

    private static bool TryParseConfidence(string value, out double confidence)
    {
        if (double.TryParse(value, NumberStyles.Float, CultureInfo.InvariantCulture, out confidence) &&
            !double.IsNaN(confidence) &&
            !double.IsInfinity(confidence) &&
            confidence >= 0 &&
            confidence <= 1)
        {
            return true;
        }

        confidence = 0;
        return false;
    }

    private static bool TryResolveHotspotsGroupBy(string? requestedGroupBy, string? lang, bool groupByName, out string groupBy, out string error)
    {
        groupBy = string.Empty;
        error = string.Empty;

        if (groupByName && requestedGroupBy != null)
        {
            error = "Error: --group-by-name cannot be combined with --group-by.";
            return false;
        }

        if (groupByName)
        {
            groupBy = HotspotsGroupedByNameKind;
            return true;
        }

        if (requestedGroupBy == null)
        {
            groupBy = IsSqlLanguageFilter(lang) ? HotspotsGroupedByStatement : HotspotsGroupedBySymbol;
            return true;
        }

        switch (requestedGroupBy)
        {
            case HotspotsGroupedBySymbol:
            case HotspotsGroupedByFile:
            case HotspotsGroupedByStatement:
                groupBy = requestedGroupBy;
                return true;
            case "name":
            case HotspotsGroupedByNameKind:
                groupBy = HotspotsGroupedByNameKind;
                return true;
            default:
                error = $"Error: unsupported hotspots --group-by value '{ConsoleUi.FormatBoundedValue(requestedGroupBy)}'. Use symbol, file, or statement.";
                return false;
        }
    }

    private static bool IsSqlLanguageFilter(string? lang) =>
        string.Equals(lang, "sql", StringComparison.Ordinal);

    internal static string? NormalizeLangFilterValue(string? langValue)
    {
        return DbReader.NormalizeQueryLanguage(langValue);
    }

    internal static IReadOnlyList<string> GetLanguageAliases(string lang)
        => LanguageDisplayAliases.TryGetValue(lang, out var aliases) ? aliases : [];

    internal static bool TryParseSnippetFocusMode(string value, out SearchSnippetFocusMode mode)
    {
        mode = value.Trim().ToLowerInvariant() switch
        {
            "leftmost" => SearchSnippetFocusMode.Leftmost,
            "quality" => SearchSnippetFocusMode.Quality,
            "proximity" => SearchSnippetFocusMode.Proximity,
            _ => default,
        };
        return value.Trim().Equals("leftmost", StringComparison.OrdinalIgnoreCase)
            || value.Trim().Equals("quality", StringComparison.OrdinalIgnoreCase)
            || value.Trim().Equals("proximity", StringComparison.OrdinalIgnoreCase);
    }

    internal static IReadOnlyCollection<string> GetCompletionLanguageAliases()
        => LanguageDisplayAliases.Values.SelectMany(aliases => aliases).ToArray();

    internal static bool TryParseStaleAfter(string value, out TimeSpan staleAfter, out string? error)
    {
        staleAfter = default;
        error = null;

        if (string.IsNullOrWhiteSpace(value))
        {
            error = "Error: --stale-after requires a duration like 30m, 2h, or 7d.";
            return false;
        }

        var trimmed = value.Trim();
        var suffix = trimmed[^1];
        var numberText = trimmed[..^1];
        TimeSpan unit;
        switch (suffix)
        {
            case 'm':
            case 'M':
                unit = TimeSpan.FromMinutes(1);
                break;
            case 'h':
            case 'H':
                unit = TimeSpan.FromHours(1);
                break;
            case 'd':
            case 'D':
                unit = TimeSpan.FromDays(1);
                break;
            default:
                error = $"Error: could not parse stale-after value '{ConsoleUi.FormatBoundedValue(value)}'. Use a positive duration with m, h, or d suffix (e.g. 30m, 2h, 7d).";
                return false;
        }

        if (!double.TryParse(numberText, NumberStyles.AllowDecimalPoint, CultureInfo.InvariantCulture, out var number) ||
            !double.IsFinite(number) ||
            number <= 0)
        {
            error = $"Error: could not parse stale-after value '{ConsoleUi.FormatBoundedValue(value)}'. Use a positive duration with m, h, or d suffix (e.g. 30m, 2h, 7d).";
            return false;
        }

        var ticks = number * unit.Ticks;
        if (ticks > TimeSpan.MaxValue.Ticks)
        {
            error = $"Error: stale-after value '{ConsoleUi.FormatBoundedValue(value)}' is too large.";
            return false;
        }

        if (ticks > MaxStaleAfter.Ticks)
        {
            error = $"Error: stale-after value '{ConsoleUi.FormatBoundedValue(value)}' exceeds the maximum {MaxStaleAfterDisplay}.";
            return false;
        }

        staleAfter = TimeSpan.FromTicks((long)Math.Round(ticks, MidpointRounding.AwayFromZero));
        return true;
    }

    private static (TimeSpan Value, string? Error) ResolveStaleAfter(QueryCommandOptions options, string? envValue)
    {
        if (options.StaleAfter.HasValue)
            return (options.StaleAfter.Value, null);

        if (!string.IsNullOrWhiteSpace(envValue))
        {
            if (TryParseStaleAfter(envValue, out var parsed, out var error))
                return (parsed, null);
            return (DefaultStaleAfter, error!.Replace("--stale-after", StaleAfterEnvironmentVariable, StringComparison.Ordinal));
        }

        return (DefaultStaleAfter, null);
    }

    private static bool TryResolveSearchExactMode(QueryCommandOptions options, out bool exact, out string? error)
    {
        if (!TryRejectMultipleExactFlags(options, out error))
        {
            exact = false;
            return false;
        }
        if (options.ExactName)
        {
            exact = false;
            error = "Error: --exact-name applies to name-based commands (symbols/definition/references/callers/callees/inspect), not search. Use --exact-substring for search, or keep --exact for backward compatibility.";
            return false;
        }

        exact = options.Exact || options.ExactSubstring;
        error = null;
        return true;
    }

    private static bool TryResolveNameExactMode(QueryCommandOptions options, string commandName, out bool exact, out string? error)
    {
        if (!TryRejectMultipleExactFlags(options, out error))
        {
            exact = false;
            return false;
        }
        if (options.ExactSubstring)
        {
            exact = false;
            error = $"Error: --exact-substring only applies to search. Use --exact-name for {commandName}, or keep --exact for backward compatibility.";
            return false;
        }

        exact = options.Exact || options.ExactName;
        error = null;
        return true;
    }

    private static bool TryRejectMultipleExactFlags(QueryCommandOptions options, out string? error)
    {
        var count = (options.Exact ? 1 : 0) + (options.ExactSubstring ? 1 : 0) + (options.ExactName ? 1 : 0);
        if (count > 1)
        {
            error = "Error: pass only one of --exact, --exact-substring, --exact-name.";
            return false;
        }

        error = null;
        return true;
    }

    // Preview option validation now lives in the command-specific unsupported-option allowlists.
    // Keep this shim so the existing call sites stay simple while the actual fail-closed logic
    // runs through ParseArgs() + TryWriteUnsupportedOptionError().
    // preview 系オプションの検証はコマンド別 allowlist に寄せたため、この shim は常に null を返す。
    private static string? ValidatePreviewOptions(string commandName, string[] args, bool allowMaxLineWidth, bool allowFocusOptions) => null;

    private static int ZeroResultExitCode(QueryCommandOptions options)
        => options.StrictNotFound ? CommandExitCodes.NotFound : CommandExitCodes.Success;

    private static bool IsEmptySymbolAnalysis(SymbolAnalysisResult analysis)
        => analysis.File == null
           && analysis.Definitions.Count == 0
           && analysis.NearbySymbols.Count == 0
           && analysis.References.Count == 0
           && analysis.Callers.Count == 0
           && analysis.Callees.Count == 0;

    private static int WithDb(
        QueryCommandOptions options,
        JsonSerializerOptions jsonOptions,
        Func<DbReader, int> action,
        Action<int>? afterProfile = null,
        CancellationToken cancellationToken = default)
    {
        var dbPath = options.DbPath;
        if (s_batchReader == null)
        {
            if (string.IsNullOrWhiteSpace(dbPath))
            {
                CommandErrorWriter.WriteStderr(BuildMissingOptionValueError("--db"));
                return CommandExitCodes.UsageError;
            }

            // Allow SQLite URI forms (file:///abs/path?immutable=1 etc.) so users and AI agents
            // on read-only mounts / sandboxes can opt into the immutable read-only escape hatch
            // explicitly when the automatic DbContext fallback cannot recover. File.Exists is
            // skipped for URI-shaped inputs because they may carry query params and schemes that
            // are meaningless to the filesystem API but are understood by SQLite.
            // URI 形式の --db を受け入れるため、file: で始まる値は File.Exists チェックをスキップ。
            var isUri = dbPath.StartsWith("file:", StringComparison.OrdinalIgnoreCase);
            var fileExistsPath = dbPath;
            if (isUri)
            {
                if (!DbPathResolver.TryNormalizeDbPath(dbPath, out fileExistsPath, out var parseError))
                {
                    var boundedDbPath = FormatDbDiagnosticValue(dbPath);
                    CommandErrorWriter.WriteStderr($"Error [{CommandErrorCodes.DbError}]: invalid --db file URI: {SqliteFileUri.FormatParseError(parseError)}");
                    CommandErrorWriter.WriteStderr($"Hint: pass a valid SQLite file URI such as `file:///absolute/path/to/codeindex.db?immutable=1`; the --db value resolved to: {boundedDbPath}");
                    GlobalToolLog.Error($"invalid_db_file_uri db={FormatLogValue(dbPath)} exception={FormatLogValue(parseError?.ToString() ?? "<unknown>")}");
                    return CommandExitCodes.DatabaseError;
                }
            }

            if (!fileExistsPath.StartsWith("file:", StringComparison.OrdinalIgnoreCase)
                && !File.Exists(LongPath.EnsureWindowsPrefix(fileExistsPath)))
            {
                var resolvedPath = Path.GetFullPath(fileExistsPath);
                var displayPath = FormatDbDiagnosticValue(resolvedPath);
                CommandErrorWriter.WriteStderr($"Error [{CommandErrorCodes.DbNotFound}]: database not found at {displayPath}");
                if (isUri)
                    CommandErrorWriter.WriteStderr($"Hint: the --db path resolved to: {displayPath}");
                CommandErrorWriter.WriteStderr("Hint: create or refresh the index with `cdidx index <projectPath>` (or `cdidx .`) and then rerun this command.");
                return CommandExitCodes.DatabaseError;
            }
        }

        Database.DbDebug.ResetContext();
        var profiling = options.Profile || options.Verbose || options.SlowQueryMs.HasValue;
        if (profiling)
            Database.DbDebug.BeginProfile(options.SlowQueryMs);
        DbContext? db = null;
        try
        {
            DbReader reader;
            if (s_batchReader != null)
            {
                reader = s_batchReader;
            }
            else
            {
                db = new DbContext(dbPath, cancellationToken);
                if (!db.TryValidateIsCodeIndexDb(out var validationReason))
                    return WriteInvalidCodeIndexDbError(dbPath, validationReason);
                db.TryMigrateForRead();
                reader = new DbReader(db);
            }

            reader.IncludeGenerated = options.IncludeGenerated;
            var previousProjectRoot = s_activeQueryProjectRoot;
            s_activeQueryProjectRoot = ResolveProjectFilterRoot(dbPath, options.DbPathExplicit).Root;
            int exitCode;
            try
            {
                exitCode = reader.RunWithGeneratedScope(() => action(reader));
            }
            finally
            {
                s_activeQueryProjectRoot = previousProjectRoot;
            }
            var profileEntries = profiling ? Database.DbDebug.EndProfile() : [];
            if (options.Profile)
                WriteProfilePayload(profileEntries, jsonOptions);
            if (options.Verbose)
                WriteVerboseQueryDebug(options, profileEntries, jsonOptions);
            afterProfile?.Invoke(exitCode);
            return exitCode;
        }
        catch (OperationCanceledException) when (cancellationToken.IsCancellationRequested)
        {
            throw;
        }
        catch (FtsQuerySyntaxException ex)
        {
            CommandErrorWriter.WriteStderr($"Error [{CommandErrorCodes.FtsQuerySyntax}]: FTS5 query syntax: {ex.Message}");
            if (ex.Message.Contains("no such column", StringComparison.OrdinalIgnoreCase))
            {
                CommandErrorWriter.WriteStderr("Hint: `--fts` passes raw FTS5 syntax, so `:` is treated as a column qualifier. Drop `--fts` if you want literal-safe search.");
            }
            else
            {
                CommandErrorWriter.WriteStderr("Hint: `--fts` passes raw FTS5 syntax. Fix the query or drop `--fts` to use literal-safe search.");
            }
            return CommandExitCodes.UsageError;
        }
        catch (SearchGuardCandidateLimitException ex)
        {
            CommandErrorWriter.WriteStderr($"Error [{CommandErrorCodes.UsageError}]: guarded search is too broad: {ex.Message}");
            CommandErrorWriter.WriteStderr("Hint: narrow the search with more specific query text, --lang, --path, or --exclude-tests, or reduce pagination offset before retrying guarded search.");
            return CommandExitCodes.UsageError;
        }
        catch (SearchQueryLimitException ex)
        {
            CommandErrorWriter.WriteStderr($"Error [{CommandErrorCodes.UsageError}]: {ex.Message}");
            CommandErrorWriter.WriteStderr("Hint: shorten the search text or split generated input into smaller literal queries.");
            return CommandExitCodes.UsageError;
        }
        catch (Exception ex)
        {
            if (JsonOutputFailure.TryHandle(ex, out var exitCode))
                return exitCode;

            if (ex is SqliteException sqliteEx)
            {
                if (sqliteEx.SqliteErrorCode == 13)
                {
                    CommandErrorWriter.WriteStderr($"Error [{CommandErrorCodes.TempStoreExhausted}]: SQLite temp-store exhausted while evaluating this query.");
                    CommandErrorWriter.WriteStderr("Hint: narrow the query with `--lang`, `--path`, or `--kind`, then retry with a freshly updated cdidx build if the problem persists.");
                    Database.DbDebug.DumpToStderr(ex);
                    return CommandExitCodes.DatabaseError;
                }

                // SQLITE_BUSY (5) and SQLITE_LOCKED (6) both mean a concurrent writer is
                // holding the database; surface E002_DB_LOCKED so scripts can implement
                // retry-with-backoff without substring-matching the prose message.
                // SQLITE_BUSY/LOCKED は別 writer によるロック競合なので、リトライ判断用に
                // E002_DB_LOCKED で機械可読に区別する。
                if (sqliteEx.SqliteErrorCode == 5 || sqliteEx.SqliteErrorCode == 6)
                {
                    CommandErrorWriter.WriteStderr($"Error [{CommandErrorCodes.DbLocked}]: SQLite reported the database is locked or busy: {ex.Message}");
                    CommandErrorWriter.WriteStderr("Hint: another process may be holding the database. Wait for it to finish, or retry with backoff.");
                    Database.DbDebug.DumpToStderr(ex);
                    return CommandExitCodes.DatabaseError;
                }
            }

            WriteDatabaseOpenFailure(ex, dbPath);
            Database.DbDebug.DumpToStderr(ex);
            return CommandExitCodes.DatabaseError;
        }
        finally
        {
            db?.Dispose();
            if (profiling)
                Database.DbDebug.EndProfile();
            Database.DbDebug.ResetContext();
        }
    }

    private static int WriteInvalidCodeIndexDbError(string dbPath, string? validationReason)
    {
        CommandErrorWriter.WriteStderr($"Error [{CommandErrorCodes.DbError}]: {FormatDbDiagnosticValue(dbPath)} does not appear to be a valid CodeIndex database ({validationReason}).");
        CommandErrorWriter.WriteStderr("Hint: rebuild with `cdidx index <projectPath> --db <path>` to create a fresh database.");
        return CommandExitCodes.DatabaseError;
    }

    private static string? GetDataDirectoryPath(string? dbPath)
    {
        if (string.IsNullOrWhiteSpace(dbPath) ||
            dbPath.StartsWith("file:", StringComparison.OrdinalIgnoreCase))
        {
            return null;
        }

        return Path.GetDirectoryName(Path.GetFullPath(dbPath));
    }

    private static void WriteDatabaseOpenFailure(Exception ex, string dbPath)
    {
        GlobalToolLog.Error($"database_open_failed db={FormatLogValue(dbPath)} exception={FormatLogValue(ex.ToString())}");

        var unauthorized = FindException<UnauthorizedAccessException>(ex);
        if (unauthorized != null)
        {
            CommandErrorWriter.WriteStderr($"Error [{CommandErrorCodes.DbError}]: database access denied: {unauthorized.Message}");
            CommandErrorWriter.WriteStderr(MacProfileDetector.BuildDatabaseHint(MacProfileDetector.DetectCurrent()));
            return;
        }

        var io = FindException<IOException>(ex);
        if (io != null)
        {
            CommandErrorWriter.WriteStderr($"Error [{CommandErrorCodes.DbError}]: database I/O error: {io.Message}");
            CommandErrorWriter.WriteStderr(MacProfileDetector.BuildDatabaseHint(MacProfileDetector.DetectCurrent()));
            return;
        }

        var sqlite = FindException<SqliteException>(ex);
        if (sqlite != null)
        {
            if (sqlite.SqliteErrorCode == 14)
            {
                CommandErrorWriter.WriteStderr($"Error [{CommandErrorCodes.DbError}]: database access/open denied: {sqlite.Message}");
                CommandErrorWriter.WriteStderr(MacProfileDetector.BuildDatabaseHint(MacProfileDetector.DetectCurrent()));
                return;
            }

            if (sqlite.SqliteErrorCode == 11)
            {
                CommandErrorWriter.WriteStderr($"Error [{CommandErrorCodes.DbError}]: SQLite reported database corruption: {sqlite.Message}");
                CommandErrorWriter.WriteStderr("Hint: rebuild the index with `cdidx index <projectPath> --rebuild`, or delete the broken `.cdidx/codeindex.db*` files and run `cdidx index <projectPath>` again.");
                return;
            }

            CommandErrorWriter.WriteStderr($"Error [{CommandErrorCodes.DbError}]: SQLite database error ({sqlite.SqliteErrorCode}): {sqlite.Message}");
            CommandErrorWriter.WriteStderr(MacProfileDetector.IsPermissionStyleSqliteError(sqlite)
                ? MacProfileDetector.BuildDatabaseHint(MacProfileDetector.DetectCurrent())
                : "Hint: check `--db`, verify the index was written by a compatible cdidx version, or rebuild it with `cdidx index <projectPath> --rebuild`.");
            return;
        }

        CommandErrorWriter.WriteStderr($"Error [{CommandErrorCodes.DbError}]: database error: {ex.Message}");
        CommandErrorWriter.WriteStderr("Hint: check `--db`, or rebuild the index with `cdidx index <projectPath>` if the DB may be stale or corrupted.");
    }

    private static T? FindException<T>(Exception ex)
        where T : Exception
    {
        for (Exception? current = ex; current != null; current = current.InnerException)
        {
            if (current is T typed)
                return typed;
        }

        return null;
    }

    private static string FormatLogValue(string? value)
    {
        if (string.IsNullOrEmpty(value))
            return "<empty>";

        return SqliteFileUri.TruncateDiagnosticValue(value)
            .Replace("\\", "/", StringComparison.Ordinal)
            .Replace("\r", " ", StringComparison.Ordinal)
            .Replace("\n", " ", StringComparison.Ordinal)
            .Replace("\t", " ", StringComparison.Ordinal);
    }

    private static string FormatDbDiagnosticValue(string? value)
    {
        if (string.IsNullOrEmpty(value))
            return "<empty>";

        return SqliteFileUri.TruncateDiagnosticValue(value)
            .Replace("\r", " ", StringComparison.Ordinal)
            .Replace("\n", " ", StringComparison.Ordinal)
            .Replace("\t", " ", StringComparison.Ordinal);
    }

    private static void WriteProfilePayload(IReadOnlyList<QueryProfileEntry> entries, JsonSerializerOptions jsonOptions)
    {
        var phases = new JsonArray();
        var queryPlan = new JsonArray();
        var queries = new JsonArray();
        for (var i = 0; i < entries.Count; i++)
        {
            var name = "sql_" + (i + 1).ToString(CultureInfo.InvariantCulture);
            var entry = entries[i];
            phases.Add(new JsonObject
            {
                ["name"] = name,
                ["elapsed_ms"] = Math.Round(entry.ElapsedMs, 3),
                ["rows_scanned"] = entry.RowsScanned,
            });
            queries.Add(new JsonObject
            {
                ["name"] = name,
                ["sql"] = entry.Sql,
            });
            foreach (var row in entry.QueryPlan)
            {
                queryPlan.Add(new JsonObject
                {
                    ["phase"] = name,
                    ["id"] = row.Id,
                    ["parent"] = row.Parent,
                    ["not_used"] = row.NotUsed,
                    ["detail"] = row.Detail,
                });
            }
        }

        Console.WriteLine(new JsonObject
        {
            ["profile"] = new JsonObject
            {
                ["phases"] = phases,
                ["query_plan"] = queryPlan,
                ["queries"] = queries,
            },
        }.ToJsonString(jsonOptions));
    }

    private static void WriteVerboseQueryDebug(QueryCommandOptions options, IReadOnlyList<QueryProfileEntry> entries, JsonSerializerOptions jsonOptions)
    {
        var elapsedMs = Math.Round(entries.Sum(entry => entry.ElapsedMs), 3);
        var rowsScanned = entries.Sum(entry => entry.RowsScanned);
        if (!options.Json)
        {
            CommandErrorWriter.WriteStderr($"DEBUG query: sql_statements={entries.Count} elapsed_ms={elapsedMs.ToString(CultureInfo.InvariantCulture)} rows_scanned={rowsScanned}");
            for (var i = 0; i < entries.Count; i++)
            {
                var entry = entries[i];
                CommandErrorWriter.WriteStderr(
                    $"DEBUG query sql_{i + 1}: elapsed_ms={Math.Round(entry.ElapsedMs, 3).ToString(CultureInfo.InvariantCulture)} rows_scanned={entry.RowsScanned}");
            }
            return;
        }

        var phases = new JsonArray();
        for (var i = 0; i < entries.Count; i++)
        {
            var entry = entries[i];
            phases.Add(new JsonObject
            {
                ["name"] = "sql_" + (i + 1).ToString(CultureInfo.InvariantCulture),
                ["elapsed_ms"] = Math.Round(entry.ElapsedMs, 3),
                ["rows_scanned"] = entry.RowsScanned,
            });
        }

        Console.WriteLine(new JsonObject
        {
            ["_debug"] = new JsonObject
            {
                ["sql_statement_count"] = entries.Count,
                ["elapsed_ms"] = elapsedMs,
                ["rows_scanned"] = rowsScanned,
                ["phases"] = phases,
                ["redaction"] = "SQL text and parameter values are omitted from --verbose debug output; use --profile for opt-in SQL diagnostics.",
            },
        }.ToJsonString(jsonOptions));
    }

    private static void WriteNumberedExcerpt(int startLine, string content, string indent = "")
    {
        var lines = content.Split('\n');
        for (int i = 0; i < lines.Length; i++)
            Console.WriteLine($"{indent}  {startLine + i,4}: {lines[i]}");
    }

    private static bool TryWriteParseError(QueryCommandOptions options, string commandName)
    {
        var dbPathError = BuildExplicitDbPathParseError(options);
        if (options.ParseError == null && dbPathError == null)
            return false;

        var primaryError = options.ParseError ?? dbPathError!;
        CommandErrorWriter.Write(
            StripErrorPrefix(primaryError),
            primaryError == dbPathError && options.ParseError == null
                ? "create or refresh the index with `cdidx index <projectPath>` (or `cdidx .`) and then rerun this command."
                : "fix the invalid or missing option value, then rerun with the command shape below.",
            GetUsageLineOrThrow(commandName),
            ExtractErrorCode(primaryError));
        if (options.ParseError != null && dbPathError != null)
            CommandErrorWriter.Write(
                StripErrorPrefix(dbPathError),
                "create or refresh the index with `cdidx index <projectPath>` (or `cdidx .`) and then rerun this command.",
                GetUsageLineOrThrow(commandName),
                ExtractErrorCode(dbPathError));
        return true;
    }

    private static string? BuildExplicitDbPathParseError(QueryCommandOptions options)
    {
        if (options.StatusConfig)
            return null;
        if (!options.DbPathExplicit)
            return null;
        if (string.IsNullOrWhiteSpace(options.DbPath))
            return BuildMissingOptionValueError("--db");
        if (options.DbPath.StartsWith("file:", StringComparison.OrdinalIgnoreCase))
            return null;
        if (File.Exists(LongPath.EnsureWindowsPrefix(options.DbPath)))
            return null;

        return $"Error [{CommandErrorCodes.DbNotFound}]: --db '{FormatDbDiagnosticValue(options.DbPath)}' does not point to an existing database file.";
    }

    private static readonly HashSet<string> KnownSymbolKindFilters = new(StringComparer.Ordinal)
    {
        "accessor",
        "associatedtype",
        "attribute",
        "class",
        "class_hook",
        "constant",
        "constructor",
        "delegate",
        "enum",
        "event",
        "field",
        "function",
        "heading",
        "hook",
        "impl",
        "implements",
        "import",
        "interface",
        "label",
        "lambda",
        "layout",
        "method",
        "module",
        "namespace",
        "object",
        "operator",
        "procedure",
        "property",
        "protocol",
        "record",
        "reference",
        "route",
        "specialization",
        "struct",
        "test.method",
        "trait",
        "type",
        "typealias",
        "union",
        "variable",
    };

    private static readonly HashSet<string> KnownVisibilityFilters = new(StringComparer.Ordinal)
    {
        "public",
        "protected",
        "internal",
        "private",
    };

    private static void AddVisibilityFilterValues(string optionName, string rawValue, List<string> target, Action<string> addParseError)
    {
        if (!ValidateCsvBounds(optionName, rawValue, MaxVisibilityFilterCsvLength, MaxVisibilityFilterCsvEntries, addParseError))
            return;

        var values = rawValue
            .Split(',', StringSplitOptions.TrimEntries | StringSplitOptions.RemoveEmptyEntries)
            .Select(value => value.ToLowerInvariant())
            .Distinct(StringComparer.Ordinal)
            .ToList();
        if (values.Count == 0)
        {
            addParseError($"Error: {optionName} requires one or more of public, protected, internal, private.");
            return;
        }

        foreach (var value in values)
        {
            if (!KnownVisibilityFilters.Contains(value))
            {
                addParseError($"Error: unsupported {optionName} value '{ConsoleUi.FormatBoundedValue(value)}'. Use one or more of public, protected, internal, private.");
                continue;
            }

            if (!target.Contains(value, StringComparer.Ordinal))
                target.Add(value);
        }
    }

    private static bool TryWriteInvalidKindFilterError(QueryCommandOptions options, string commandName, IReadOnlyCollection<string> acceptedKinds, params IReadOnlyCollection<string>[] alternateAcceptedKinds)
    {
        if (options.Kind != null
            && !acceptedKinds.Contains(options.Kind)
            && !alternateAcceptedKinds.Any(kinds => kinds.Contains(options.Kind)))
        {
            CommandErrorWriter.Write(
                $"invalid --kind value `{ConsoleUi.FormatBoundedValue(options.Kind)}`.",
                $"use one of: {string.Join(", ", acceptedKinds)}.",
                GetUsageLineOrThrow(commandName));
            return true;
        }

        return false;
    }

    private static bool TryWriteInvalidOutlineKindFilterError(QueryCommandOptions options)
    {
        if (options.Kind == null)
            return false;

        var kinds = BuildOutlineKindFilters(options.Kind);
        if (kinds.Count == 0)
        {
            CommandErrorWriter.Write(
                $"invalid --kind value `{ConsoleUi.FormatBoundedValue(options.Kind)}`.",
                $"use one or more of: {string.Join(", ", KnownSymbolKindFilters)}.",
                GetUsageLineOrThrow("outline"));
            return true;
        }

        foreach (var kind in kinds)
        {
            if (KnownSymbolKindFilters.Contains(kind))
                continue;

            CommandErrorWriter.Write(
                $"invalid --kind value `{ConsoleUi.FormatBoundedValue(kind)}`.",
                $"use one or more of: {string.Join(", ", KnownSymbolKindFilters)}.",
                GetUsageLineOrThrow("outline"));
            return true;
        }

        return false;
    }

    internal static bool IsKnownUnusedBucket(string value)
        => OrderedUnusedBuckets.Contains(value, StringComparer.Ordinal);

    internal static bool IsKnownUnusedConfidence(string value)
        => value is "medium" or "low";

    private static bool TryWriteInvalidUnusedFilterError(QueryCommandOptions options)
    {
        if (options.UnusedBucket != null && !IsKnownUnusedBucket(options.UnusedBucket))
        {
            CommandErrorWriter.Write(
                $"invalid --bucket value `{ConsoleUi.FormatBoundedValue(options.UnusedBucket)}`.",
                $"use one of: {string.Join(", ", OrderedUnusedBuckets)}.",
                GetUsageLineOrThrow("unused"));
            return true;
        }

        if (options.MinUnusedConfidence != null && !IsKnownUnusedConfidence(options.MinUnusedConfidence))
        {
            CommandErrorWriter.Write(
                $"invalid --min-confidence value `{ConsoleUi.FormatBoundedValue(options.MinUnusedConfidence)}`.",
                "use one of: medium, low.",
                GetUsageLineOrThrow("unused"));
            return true;
        }

        return false;
    }

    private static bool TryWriteUnsupportedOptionError(string commandName, string[] cmdArgs, IEnumerable<string> supportedOptions, string? queryLiteral = null)
    {
        var supported = supportedOptions.ToHashSet(StringComparer.Ordinal);
        var skippedQueryLiteral = false;
        for (var i = 0; i < cmdArgs.Length; i++)
        {
            var arg = cmdArgs[i];
            if (!arg.StartsWith("-", StringComparison.Ordinal))
                continue;

            if (queryLiteral != null && !skippedQueryLiteral && arg == queryLiteral)
            {
                skippedQueryLiteral = true;
                continue;
            }

            var inlineValue = TrySplitInlineOptionValue(arg, out var inlineOptionName)
                ? arg[(inlineOptionName!.Length + 1)..]
                : null;
            var normalizedArg = inlineOptionName ?? arg;
            if (arg.StartsWith("--check=", StringComparison.Ordinal) && supported.Contains("--check"))
                normalizedArg = "--check";
            if (normalizedArg == "--json"
                && !string.Equals(arg, "--json", StringComparison.Ordinal)
                && commandName != "search"
                && commandName != "files"
                && commandName != "symbols")
            {
                if (commandName == "validate" && string.Equals(inlineValue, JsonOutputFormatArray, StringComparison.OrdinalIgnoreCase))
                {
                    continue;
                }

                CommandErrorWriter.Write(
                    commandName == "validate"
                        ? "--json=<format> for validate only supports 'array'."
                        : "--json=<format> is only supported by 'search', 'files', 'symbols', and validate's array output.",
                    commandName == "validate"
                        ? "use plain `--json` or `--json=array`."
                        : "use plain `--json` here, rerun search/files/symbols with `--json=array`, or rerun validate with `--json=array`.",
                    GetUsageLineOrThrow(commandName));
                return true;
            }

            if (supported.Contains(normalizedArg))
            {
                if (normalizedArg == "--" && normalizedArg == arg && i + 1 < cmdArgs.Length)
                    i++;
                if (normalizedArg == arg && ValueTakingOptions.Contains(normalizedArg) && i + 1 < cmdArgs.Length)
                    i++;
                continue;
            }

            // `--query` is parsed specially so commands without query literals can emit the
            // dedicated parser message instead of the generic unsupported-option error.
            // `--query` は専用エラー文言を出したいので generic unsupported 判定からは外す。
            if (normalizedArg == "--query")
            {
                if (normalizedArg == arg && ValueTakingOptions.Contains(normalizedArg) && i + 1 < cmdArgs.Length)
                    i++;
                continue;
            }

            if (normalizedArg == "--group-by-name")
            {
                CommandErrorWriter.Write(
                    "--group-by-name is only supported by 'hotspots'.",
                    "remove `--group-by-name` here, or rerun with `cdidx hotspots --group-by-name ...`.",
                    GetUsageLineOrThrow(commandName));
                return true;
            }

            if (normalizedArg == "--group-by")
            {
                CommandErrorWriter.Write(
                    "--group-by is only supported by 'hotspots'.",
                    "remove `--group-by` here, or rerun with `cdidx hotspots --group-by <symbol|file|statement> ...`.",
                    GetUsageLineOrThrow(commandName));
                return true;
            }

            if (normalizedArg == arg && ValueTakingOptions.Contains(normalizedArg) && i + 1 < cmdArgs.Length)
                i++;

            // Suggest the closest accepted flag for this command when the user mistypes
            // a flag name (e.g. `--paht` → `--path`). Built on the same suggester used for
            // subcommand typos so the recovery experience is consistent (#1582).
            // TrySplitInlineOptionValue only splits inline `=value` when the prefix is a
            // known value-taking option, so for an unrecognized `--paht=foo` the normalized
            // arg keeps the `=value`. Strip any trailing `=value` here so the matcher can
            // still find `--path` from `--paht=foo`.
            // ユーザーがフラグ名をミスタイプしたとき (例: `--paht` → `--path`) に
            // そのコマンドで受理される最も近いフラグを提案する。サブコマンドの did-you-mean と
            // 同じ suggester を共用し、回復体験を統一する (#1582)。
            // TrySplitInlineOptionValue は prefix が既知の value-taking option のときだけ
            // inline `=value` を分解するため、`--paht=foo` のように未知のオプションでは
            // `=value` が残る。matcher のために `=` 以降を除去してから候補を探す。
            var nameForSuggestion = normalizedArg;
            var eq = nameForSuggestion.IndexOf('=');
            if (eq > 0)
                nameForSuggestion = nameForSuggestion[..eq];
            var suggestion = ConsoleUi.FindClosestMatch(nameForSuggestion, supported.Where(o => o != "--"));
            var displayArg = ConsoleUi.FormatBoundedValue(arg);
            var hint = suggestion == null
                ? $"remove `{displayArg}` and rerun, or use only the options shown in `{commandName} --help`."
                : $"Did you mean: {suggestion}? Remove `{displayArg}` and rerun, or use `{suggestion}` if that is what you meant.";
            CommandErrorWriter.Write(
                $"{displayArg} is not supported for {commandName}.",
                hint,
                GetUsageLineOrThrow(commandName));
            return true;
        }

        return false;
    }

    private static bool TryWriteUnexpectedExtraPositionals(string commandName, QueryCommandOptions options)
    {
        if (options.ExtraNames.Count == 0)
            return false;

        CommandErrorWriter.Write(
            $"unexpected extra positional {ConsoleUi.Counted(options.ExtraNames.Count, "argument")} for {commandName}: {string.Join(", ", options.ExtraNames.Select(name => $"`{name}`"))}.",
            BuildUnexpectedExtraPositionalsHint(commandName, options),
            GetUsageLineOrThrow(commandName));
        return true;
    }

    private static string BuildUnexpectedExtraPositionalsHint(string commandName, QueryCommandOptions options)
    {
        if (string.Equals(commandName, "search", StringComparison.Ordinal)
            && options.PathPatterns.Count > 0
            && options.ExtraNames.Any(IsPathLikeArgument))
        {
            return "quote --path globs so the shell passes one literal pattern, e.g. `--path 'src/CodeIndex/**'`; remove the expanded path arguments and rerun.";
        }

        return "quote multi-word queries as a single argument, or remove the extra positional values.";
    }

    private static bool IsPathLikeArgument(string value) =>
        value.Contains('/') || value.Contains('\\');

    private static bool TryWriteUnexpectedPositionals(string commandName, QueryCommandOptions options)
    {
        var unexpected = new List<string>();
        if (!string.IsNullOrWhiteSpace(options.Query))
            unexpected.Add($"`{options.Query}`");
        unexpected.AddRange(options.ExtraNames.Select(name => $"`{name}`"));
        if (unexpected.Count == 0)
            return false;

        CommandErrorWriter.Write(
            $"{commandName} does not accept positional arguments: {string.Join(", ", unexpected)}.",
            "remove the extra positional arguments and use the documented flags only.",
            GetUsageLineOrThrow(commandName));
        return true;
    }

    private static string BuildMissingSearchQueryHint(string[] cmdArgs)
    {
        var candidate = FindOptionLookingSearchLiteralCandidate(cmdArgs);
        if (candidate != null)
        {
            var display = ConsoleUi.FormatBoundedValue(candidate);
            return $"Add the text you want to search for after the command. If you meant to search for `{display}`, pass it as `--query \"{display}\"` or after `--`, for example: `cdidx search -- \"{display}\"`.";
        }

        return "Add the text you want to search for after the command, for example: `cdidx search authenticate`. If the query itself starts with `--`, pass it as `--query \"--profile\"` or after `--`, for example: `cdidx search -- \"--profile\"`.";
    }

    private static string? FindOptionLookingSearchLiteralCandidate(string[] cmdArgs)
    {
        for (var i = 0; i < cmdArgs.Length; i++)
        {
            var arg = cmdArgs[i];
            if (arg == "--")
                return i + 1 < cmdArgs.Length && cmdArgs[i + 1].StartsWith("-", StringComparison.Ordinal)
                    ? cmdArgs[i + 1]
                    : null;

            var inlineValue = TrySplitInlineOptionValue(arg, out var inlineOptionName)
                ? arg[(inlineOptionName!.Length + 1)..]
                : null;
            var normalizedArg = inlineOptionName ?? arg;
            if (ValueTakingOptions.Contains(normalizedArg))
            {
                if (inlineValue == null)
                    i++;
                continue;
            }

            if (!arg.StartsWith("--", StringComparison.Ordinal))
                continue;
            if (SearchMissingQueryControlFlags.Contains(normalizedArg))
                continue;

            return arg;
        }

        return null;
    }

    private static readonly HashSet<string> SearchMissingQueryControlFlags =
    [
        "--exact",
        "--exact-name",
        "--exact-substring",
        "--prefix",
        "--fts",
        "--json",
        "--pretty",
        "--count",
        "--no-dedup",
        "--no-visibility-rank",
        "--exclude-tests",
        "--strict-not-found",
        "--verbose",
        "--quiet",
        "--silent",
    ];

    private static string GetUsageLineOrThrow(string commandName) =>
        ConsoleUi.GetUsageLine(commandName)
        ?? throw new InvalidOperationException($"Missing usage line for command '{commandName}'.");

    // Human-readable reference_kind label for a grouped caller/callee row. Counts
    // keep high-volume relationships visible without requiring JSON re-querying.
    // grouped caller/callee 行の人間向け reference_kind ラベル。count を併記して、
    // JSON で再取得しなくても高頻度の関係が見えるようにする。
    private static string FormatReferenceKindLabel(string primary, IReadOnlyList<string> kinds, bool hasMixed, IReadOnlyDictionary<string, int>? counts)
    {
        if (counts == null || counts.Count == 0)
        {
            if (!hasMixed || kinds == null || kinds.Count <= 1)
                return primary ?? string.Empty;
            return string.Join("+", kinds);
        }

        var orderedKinds = kinds is { Count: > 0 } && kinds.Any(kind => counts.TryGetValue(kind, out var count) && count > 0)
            ? kinds
            : counts.Keys.Where(kind => counts[kind] > 0).OrderBy(kind => kind, StringComparer.Ordinal).ToArray();
        return string.Join(", ", orderedKinds
            .Where(kind => counts.TryGetValue(kind, out var count) && count > 0)
            .Select(kind => counts[kind] == 1 ? kind : $"{kind} x{counts[kind]}"));
    }

    // Pick a column width that fits every label in the current batch so mixed-kind
    // labels like `call+subscribe` do not overrun the neighbouring column. The
    // minimum matches the historic single-kind width (`instantiate` = 11) with a
    // small buffer so short-label batches still align consistently (issue #501).
    // 現在のバッチ内の全ラベルが収まる列幅を選び、`call+subscribe` のような
    // mixed ラベルが隣接列を押し出さないようにする。最小幅は従来の単一 kind
    // （`instantiate` = 11）と整合するよう余裕付きで設定する（issue #501）。
    private const int ReferenceKindColumnMinWidth = 12;

    private static int ComputeReferenceKindColumnWidth<T>(IEnumerable<T> rows, Func<T, string> labelSelector)
    {
        var max = ReferenceKindColumnMinWidth;
        foreach (var row in rows)
        {
            var label = labelSelector(row);
            if (label != null && label.Length > max)
                max = label.Length;
        }
        return max;
    }

    private static void WriteUsageError(string message, string usage, string hint)
        => CommandErrorWriter.Write(message, hint, usage);

    private static bool TryWriteUnsupportedOutputFormat(string commandName, QueryCommandOptions options, IReadOnlySet<string> supportedFormats, string hint)
    {
        if (supportedFormats.Contains(options.OutputFormat))
            return false;

        WriteUsageError(
            $"--format {options.OutputFormat} is not supported by {commandName}.",
            GetUsageLineOrThrow(commandName),
            hint);
        return true;
    }

    private static void AddFindScanJsonFields(JsonObject payload, FindScanSummary scan)
    {
        payload["candidate_files"] = scan.CandidateFiles;
        payload["files_scanned"] = scan.FilesScanned;
        payload["lines_scanned"] = scan.LinesScanned;
        payload["scan_truncated"] = scan.Truncated;
        payload["scan_cap_reached"] = scan.CapReached;
        payload["scan_timed_out"] = scan.TimedOut;
        if (scan.TruncationReason != null)
            payload["scan_truncation_reason"] = scan.TruncationReason;
        if (scan.CandidateFileLimit.HasValue)
            payload["candidate_file_limit"] = scan.CandidateFileLimit.Value;
        if (scan.LineLimit.HasValue)
            payload["line_scan_limit"] = scan.LineLimit.Value;
    }

    private static void WriteFindScanSummary(FindScanSummary scan)
    {
        var summary = $"scanned {scan.FilesScanned}/{scan.CandidateFiles} candidate files, {ConsoleUi.Counted(scan.LinesScanned, "line")}";
        if (scan.Truncated)
            summary += scan.TruncationReason == null ? "; truncated" : $"; truncated by {scan.TruncationReason}";
        CommandErrorWriter.WriteStderr($"({summary})");
    }

    // Reject queries that were supplied but resolve to empty / whitespace-only text so the user gets
    // a distinct error instead of the generic "<cmd> requires a query argument" message that fires
    // when the positional was actually missing. The null case is left to the existing missing-query
    // checks in each runner (issue #1505).
    // 入力されたクエリが空白のみ・空文字に正規化されたケースを「引数未指定」とは区別して
    // 専用エラーで弾く。null（未指定）は各 runner の既存チェックに委ねる (issue #1505)。
    private static bool TryWriteBlankQueryError(QueryCommandOptions options, string commandName)
    {
        if (options.Query is null)
            return false;
        if (!string.IsNullOrWhiteSpace(options.Query))
            return false;
        WriteUsageError(
            $"{commandName} query cannot be empty or whitespace-only",
            GetUsageLineOrThrow(commandName),
            $"Pass a non-empty value after `{commandName}`; empty or whitespace-only arguments (e.g. `\"\"` or `\"   \"`) are rejected.");
        return true;
    }

    private static void WriteValidationError(string message, string hint)
        => CommandErrorWriter.Write(message, hint);

    private static string StripErrorPrefix(string message)
    {
        const string prefix = "Error: ";
        if (message.StartsWith(prefix, StringComparison.Ordinal))
            return message[prefix.Length..];

        var codedPrefixEnd = message.IndexOf("]: ", StringComparison.Ordinal);
        if (message.StartsWith("Error [", StringComparison.Ordinal) && codedPrefixEnd >= 0)
            return message[(codedPrefixEnd + 3)..];

        return message;
    }

    private static string? ExtractErrorCode(string message)
    {
        const string prefix = "Error [";
        if (!message.StartsWith(prefix, StringComparison.Ordinal))
            return null;

        var end = message.IndexOf("]: ", StringComparison.Ordinal);
        return end > prefix.Length ? message[prefix.Length..end] : null;
    }

    private static void WriteRepoMapSection(string title, IEnumerable<string> rows)
    {
        var materialized = rows.ToList();
        if (materialized.Count == 0)
            return;

        Console.WriteLine();
        Console.WriteLine($"{title}:");
        foreach (var row in materialized)
            Console.WriteLine($"  {row}");
    }

    /// <summary>
    /// Write actionable hints when a query returns zero results.
    /// 0件時に実行可能なヒントを出力する。
    /// </summary>
    private static void WriteZeroResultHints(QueryCommandOptions options, DbReader reader, string? alternativeHint = null, string? filterHint = null)
    {
        var freshness = reader.GetFreshnessHint();
        if (freshness.FileCount == 0)
        {
            CommandErrorWriter.WriteStderr("Hint: the index is empty. Run 'cdidx index <projectPath>' first.");
            return;
        }

        if (options.Lang != null || options.PathPatterns.Count > 0 || options.ExcludeTests || options.ExcludeComments || options.ExcludeStrings || options.ExcludeFixtures || options.ExcludePaths.Count > 0)
            CommandErrorWriter.WriteStderr($"Hint: {filterHint ?? "try removing --lang, --path, --exclude-path, --exclude-tests, --exclude-comments, --exclude-strings, or --exclude-fixtures to broaden the search."}");

        if (alternativeHint != null)
            CommandErrorWriter.WriteStderr($"Hint: {alternativeHint}");

        var staleAfter = ResolveStaleAfter(options, CdidxEnvironment.GetEnvironmentVariable(StaleAfterEnvironmentVariable));
        if (staleAfter.Error != null)
        {
            CommandErrorWriter.WriteStderr(staleAfter.Error);
            return;
        }

        if (freshness.IndexedAt.HasValue)
        {
            var age = GetUtcNow() - freshness.IndexedAt.Value;
            if (age > staleAfter.Value)
                CommandErrorWriter.WriteStderr($"Hint: the index is {FormatDuration(age)} old (threshold: {FormatDuration(staleAfter.Value)}). Run 'cdidx index <projectPath>' to refresh.");
        }
    }

    private static SearchQueryHint? BuildSearchPathGlobHint(DbReader reader, QueryCommandOptions options)
    {
        if (options.PathPatterns.Count != 1)
            return null;
        var pattern = options.PathPatterns[0].Replace('\\', '/').TrimEnd('/');
        if (pattern.Length == 0 || ContainsGlobMeta(pattern) || pattern.EndsWith("/**", StringComparison.Ordinal))
            return null;

        if (!string.IsNullOrEmpty(Path.GetExtension(pattern)))
        {
            var exactMatches = reader.ListFiles(
                query: null,
                limit: 1,
                lang: options.Lang,
                pathPatterns: [pattern],
                excludePathPatterns: options.ExcludePaths,
                excludeTests: options.ExcludeTests,
                since: options.Since);
            if (exactMatches.Count > 0)
                return null;
        }

        var suggested = pattern + "/**";
        var prefixMatches = reader.ListFiles(
            query: null,
            limit: 1,
            lang: options.Lang,
            pathPatterns: [suggested],
            excludePathPatterns: options.ExcludePaths,
            excludeTests: options.ExcludeTests,
            since: options.Since);
        if (prefixMatches.Count == 0)
            return null;

        return new SearchQueryHint
        {
            Reason = "path_filter_looks_like_directory",
            SuggestedAction = $"`--path {pattern}` looks like an indexed directory prefix; use `--path {suggested}` to match files below it.",
            Flag = "--path",
            McpArgument = "path",
        };
    }

    private static bool ContainsGlobMeta(string pattern)
        => pattern.IndexOfAny(new[] { '*', '?', '[', ']' }) >= 0;

    private static void AddSearchPathHint(JsonObject payload, SearchQueryHint? pathHint)
    {
        if (pathHint != null)
            payload["path_filter_hint"] = BuildSearchQueryHintJson(pathHint);
    }

    private static void WriteExactSubstringHintIfNeeded(SearchQueryHint? hint)
    {
        if (hint == null)
            return;

        CommandErrorWriter.WriteStderr($"Hint: {hint.SuggestedAction}");
    }

    private static string BuildZeroResultLine(string message, QueryCommandOptions options)
    {
        var context = BuildQueryContextParts(options, includeDefaultLimit: true).ToList();
        if (context.Count == 0)
            return message + ".";

        return $"{message}. ({string.Join(", ", context)})";
    }

    private static IEnumerable<string> BuildQueryContextParts(QueryCommandOptions options, bool includeDefaultLimit)
    {
        if (!string.IsNullOrWhiteSpace(options.Query))
            yield return $"query: \"{options.Query}\"";
        if (options.PathPatterns.Count > 0)
            yield return $"path: {string.Join(", ", options.PathPatterns)}";
        if (options.ProjectFilters.Count > 0)
            yield return $"project: {string.Join(", ", options.ProjectFilters)}";
        if (!string.IsNullOrWhiteSpace(options.ProjectFilterRoot))
            yield return $"project-root: {options.ProjectFilterRoot}";
        if (!string.IsNullOrWhiteSpace(options.ProjectFilterRootFallbackReason))
            yield return $"project-root-fallback: {options.ProjectFilterRootFallbackReason}";
        if (options.ExcludePaths.Count > 0)
            yield return $"exclude-path: {string.Join(", ", options.ExcludePaths)}";
        if (options.Lang != null)
            yield return $"lang: {options.Lang}";
        if (options.Kind != null)
            yield return $"kind: {options.Kind}";
        if (options.UnusedBucket != null)
            yield return $"bucket: {options.UnusedBucket}";
        if (options.MinUnusedConfidence != null)
            yield return $"min-confidence: {options.MinUnusedConfidence}";
        if (options.UnusedActionable)
            yield return "actionable: true";
        if (options.RankMode != ReferenceRankMode.Weighted)
            yield return $"rank-by: {FormatReferenceRankMode(options.RankMode)}";
        if (options.ExcludeTests)
            yield return "exclude-tests: true";
        if (options.ExcludeComments)
            yield return "exclude-comments: true";
        if (options.ExcludeStrings)
            yield return "exclude-strings: true";
        if (options.ExcludeFixtures)
            yield return "exclude-fixtures: true";
        if (options.Since.HasValue)
            yield return $"since: {options.Since.Value:O}";
        if (options.CountOnly)
            yield return "count: true";
        if (options.RawFts)
            yield return "fts: true";
        if (options.Exact)
            yield return "exact: true";
        if (options.Prefix)
            yield return "prefix: true";
        if (options.NoDedup)
            yield return "dedup: false";
        if (options.ContextBefore > 0)
            yield return $"before: {options.ContextBefore}";
        if (options.ContextAfter > 0)
            yield return options.ContextAfterExplicit ? $"depth: {options.ContextAfter}" : $"after: {options.ContextAfter}";
        if (includeDefaultLimit || options.Limit != 20)
            yield return $"limit: {options.Limit}";
    }

    private static JsonObject BuildQueryContextJson(QueryCommandOptions options, JsonSerializerOptions jsonOptions)
    {
        var query = new JsonObject
        {
            ["limit"] = options.Limit,
        };
        if (!string.IsNullOrWhiteSpace(options.Query))
            query["text"] = options.Query;
        if (options.PathPatterns.Count > 0)
            query["path"] = JsonSerializer.SerializeToNode(options.PathPatterns, CliJsonSerializerContextFactory.Create(jsonOptions).ListString);
        if (options.ProjectFilters.Count > 0)
            query["project"] = JsonSerializer.SerializeToNode(options.ProjectFilters, CliJsonSerializerContextFactory.Create(jsonOptions).ListString);
        if (!string.IsNullOrWhiteSpace(options.ProjectFilterRoot))
            query["project_filter_root"] = options.ProjectFilterRoot;
        if (!string.IsNullOrWhiteSpace(options.ProjectFilterRootFallbackReason))
            query["project_filter_root_fallback_reason"] = options.ProjectFilterRootFallbackReason;
        if (options.ExcludePaths.Count > 0)
            query["exclude_path"] = JsonSerializer.SerializeToNode(options.ExcludePaths, CliJsonSerializerContextFactory.Create(jsonOptions).ListString);
        if (options.Lang != null)
            query["lang"] = options.Lang;
        if (options.Kind != null)
            query["kind"] = options.Kind;
        if (options.UnusedBucket != null)
            query["bucket"] = options.UnusedBucket;
        if (options.MinUnusedConfidence != null)
            query["min_confidence"] = options.MinUnusedConfidence;
        if (options.UnusedActionable)
            query["actionable"] = true;
        if (options.AuditScopeExplicit)
            query["audit_scope"] = options.AuditScope;
        if (options.VisibilityFilters.Count > 0)
            query["visibility"] = JsonSerializer.SerializeToNode(options.VisibilityFilters, CliJsonSerializerContextFactory.Create(jsonOptions).ListString);
        if (options.ExcludeVisibilityFilters.Count > 0)
            query["exclude_visibility"] = JsonSerializer.SerializeToNode(options.ExcludeVisibilityFilters, CliJsonSerializerContextFactory.Create(jsonOptions).ListString);
        if (options.UnusedCursorOffset.HasValue)
        {
            query["cursor"] = FormatUnusedCursor(options.UnusedCursorOffset.Value);
            query["offset"] = options.UnusedCursorOffset.Value;
        }
        if (options.RankMode != ReferenceRankMode.Weighted)
            query["rank_by"] = FormatReferenceRankMode(options.RankMode);
        if (options.SymbolSortMode != SymbolSortMode.Name)
            query["sort"] = options.SymbolSortMode.ToString().ToLowerInvariant();
        if (options.ExcludeTests)
            query["exclude_tests"] = true;
        if (options.ExcludeComments)
            query["exclude_comments"] = true;
        if (options.ExcludeStrings)
            query["exclude_strings"] = true;
        if (options.ExcludeFixtures)
            query["exclude_fixtures"] = true;
        if (options.IncludeGenerated)
            query["include_generated"] = true;
        if (options.Since.HasValue)
            query["since"] = options.Since.Value;
        if (options.CountOnly)
            query["count"] = true;
        if (options.All)
            query["all"] = true;
        if (options.RawFts)
            query["fts"] = true;
        if (options.Regex)
            query["regex"] = true;
        if (options.Exact)
            query["exact"] = true;
        if (options.Prefix)
            query["prefix"] = true;
        if (options.NoDedup)
            query["dedup"] = false;
        if (options.RawKinds)
            query["raw_kinds"] = true;
        if (options.FocusLine.HasValue)
            query["focus_line"] = options.FocusLine.Value;
        if (options.FocusColumn.HasValue)
            query["focus_column"] = options.FocusColumn.Value;
        if (options.ContextBefore > 0)
            query["before"] = options.ContextBefore;
        if (options.ContextAfter > 0)
            query[options.ContextAfterExplicit ? "depth" : "after"] = options.ContextAfter;
        return query;
    }

    internal static ExactZeroHintResult? BuildExactZeroHint<T>(bool shouldProbe, Func<bool> anyRelaxedMatch, Func<List<T>> relaxedSampleQuery, Func<T, string?> nameSelector)
    {
        return BuildExactZeroHint(shouldProbe, anyRelaxedMatch, relaxedCountQuery: null, relaxedSampleQuery, nameSelector);
    }

    internal static ExactZeroHintResult? BuildExactZeroHint<T>(bool shouldProbe, Func<bool> anyRelaxedMatch, Func<int>? relaxedCountQuery, Func<List<T>> relaxedSampleQuery, Func<T, string?> nameSelector)
    {
        if (!shouldProbe)
            return null;

        if (!anyRelaxedMatch())
            return null;

        int? relaxedCount = null;
        if (relaxedCountQuery != null)
        {
            relaxedCount = relaxedCountQuery();
            if (relaxedCount == 0)
                return null;
        }

        var relaxedResults = relaxedSampleQuery();
        if (relaxedResults.Count == 0)
            return null;

        var sampleNames = relaxedResults
            .Select(nameSelector)
            .Where(name => !string.IsNullOrWhiteSpace(name))
            .Distinct(StringComparer.Ordinal)
            .Take(5)
            .Select(name => name!)
            .ToList();

        return new ExactZeroHintResult
        {
            RelaxedCount = relaxedCount,
            SampleNames = sampleNames,
            Suggestion = ExactZeroHintResult.DefaultSuggestion,
        };
    }

    private static void AddFreshnessHint(JsonObject payload, DbReader reader)
    {
        var freshness = reader.GetFreshnessHint();
        payload["indexed_file_count"] = freshness.FileCount;
        payload["indexed_at"] = freshness.IndexedAt.HasValue
            ? JsonValue.Create(freshness.IndexedAt.Value)
            : null;
        payload["freshness_available"] = freshness.FreshnessAvailable;
        if (!freshness.FreshnessAvailable && freshness.FreshnessDegradedReason != null)
            payload["freshness_degraded_reason"] = freshness.FreshnessDegradedReason;
        AddReadOnlyFallbackDiagnostics(payload, reader);
    }

    internal static void AddReadOnlyFallbackDiagnostics(JsonObject payload, DbReader reader)
    {
        if (!HasReadOnlyFallbackDiagnostics(reader))
        {
            return;
        }

        payload["read_only_fallback"] = reader.ReadOnlyFallback;
        payload["wal_checkpoint_attempted"] = reader.WalCheckpointAttempted;
        payload["wal_checkpoint_succeeded"] = reader.WalCheckpointSucceeded;
        payload["read_only_immutable_fallback"] = reader.ReadOnlyImmutableFallback;
        if (reader.WalCheckpointSkippedReason != null)
            payload["wal_checkpoint_skipped_reason"] = reader.WalCheckpointSkippedReason;
        if (reader.WalCheckpointFailureReason != null)
            payload["wal_checkpoint_failure_reason"] = reader.WalCheckpointFailureReason;
        payload["wal_stale_snapshot_risk"] = reader.WalStaleSnapshotRisk;
        if (reader.WalStaleSnapshotReason != null)
            payload["wal_stale_snapshot_reason"] = reader.WalStaleSnapshotReason;
    }

    private static bool HasReadOnlyFallbackDiagnostics(DbReader? reader)
        => reader != null
           && (reader.ReadOnlyFallback
               || reader.WalCheckpointAttempted
               || reader.ReadOnlyImmutableFallback
               || reader.WalCheckpointSkippedReason != null
               || reader.WalCheckpointFailureReason != null
               || reader.WalStaleSnapshotRisk);

    private static JsonObject BuildCountJsonPayload(
        DbReader reader,
        JsonSerializerOptions jsonOptions,
        int count,
        int? files = null,
        string? query = null,
        QueryCommandOptions? queryOptions = null,
        bool? graphTableAvailable = null,
        bool degraded = false,
        ExactQuerySignal? exactSignal = null,
        ExactZeroHintResult? exactZeroHint = null,
        FtsQueryDiagnostics? ftsQueryDiagnostics = null,
        SearchQueryHint? exactSubstringHint = null,
        Action<JsonObject>? extraFields = null,
        bool deferAuthority = false)
    {
        var payload = new JsonObject
        {
            ["count"] = count,
        };
        if (files.HasValue)
        {
            payload["files"] = files.Value;
            payload["file_count"] = files.Value;
        }
        if (query != null)
            payload["query"] = query;
        if (graphTableAvailable.HasValue)
            payload["graph_table_available"] = graphTableAvailable.Value;
        if (degraded)
            payload["degraded"] = true;
        if (exactSignal.HasValue)
            AddExactJsonFields(payload, exactSignal.Value);
        if (exactZeroHint != null)
            payload["exact_zero_hint"] = JsonSerializer.SerializeToNode(exactZeroHint, CliJsonSerializerContextFactory.Create(jsonOptions).ExactZeroHintResult);
        if (ftsQueryDiagnostics is { HasDegradation: true })
        {
            payload["query_degraded_reason"] = ftsQueryDiagnostics.QueryDegradedReason;
            payload["tokens_dropped"] = JsonSerializer.SerializeToNode(ftsQueryDiagnostics.TokensDropped.ToList(), CliJsonSerializerContextFactory.Create(jsonOptions).ListString);
        }
        if (exactSubstringHint != null)
            payload["exact_substring_hint"] = BuildSearchQueryHintJson(exactSubstringHint);
        extraFields?.Invoke(payload);
        AddCountEnvelopeJsonFields(payload, reader, jsonOptions, queryOptions, deferAuthority);
        return payload;
    }

    private static void AddCountEnvelopeJsonFields(JsonObject payload, DbReader reader, JsonSerializerOptions jsonOptions, QueryCommandOptions? queryOptions, bool deferAuthority = false)
    {
        if (queryOptions != null)
            payload["query_context"] = BuildQueryContextJson(queryOptions, jsonOptions);
        AddFreshnessHint(payload, reader);
        if (!deferAuthority)
            AddCountAuthorityJsonFields(payload);
    }

    private static void AddCountAuthorityJsonFields(JsonObject payload)
    {
        var degraded =
            JsonBool(payload, "degraded") == true
            || JsonBool(payload, "graph_table_available") == false
            || JsonBool(payload, "exact_index_available") == false
            || JsonBool(payload, "sql_graph_contract_ready") == false
            || JsonBool(payload, "graph_degraded") == true
            || JsonBool(payload, "scan_truncated") == true
            || JsonBool(payload, "scan_cap_reached") == true
            || JsonBool(payload, "scan_timed_out") == true
            || JsonBool(payload, "truncated") == true
            || JsonBool(payload, "wal_stale_snapshot_risk") == true;
        payload["degraded"] = degraded;
        payload["authoritative_count"] = !degraded;
    }

    private static bool? JsonBool(JsonObject payload, string name)
    {
        return payload.TryGetPropertyValue(name, out var node)
            && node is JsonValue value
            && value.TryGetValue<bool>(out var boolValue)
            ? boolValue
            : null;
    }

    private static JsonObject BuildJsonZeroResultPayload(
        DbReader reader,
        JsonSerializerOptions jsonOptions,
        string? resultsKey = null,
        string? query = null,
        ExactZeroHintResult? exactZeroHint = null,
        FtsQueryDiagnostics? ftsQueryDiagnostics = null,
        bool includeFiles = false,
        bool? graphTableAvailable = null,
        bool? degraded = null,
        ExactQuerySignal? exactSignal = null,
        QueryCommandOptions? queryOptions = null,
        SearchQueryHint? exactSubstringHint = null,
        Action<JsonObject>? extraFields = null)
    {
        var payload = new JsonObject
        {
            ["count"] = 0,
        };

        if (query != null)
            payload["query"] = query;
        if (resultsKey != null)
            payload[resultsKey] = new JsonArray();
        if (includeFiles)
            payload["files"] = 0;
        if (graphTableAvailable.HasValue)
            payload["graph_table_available"] = graphTableAvailable.Value;
        if (degraded.HasValue)
            payload["degraded"] = degraded.Value;
        if (exactSignal.HasValue)
        {
            payload["exact_index_available"] = exactSignal.Value.ExactIndexAvailable;
            if (exactSignal.Value.DegradedReason != null)
                payload["degraded_reason"] = exactSignal.Value.DegradedReason;
        }
        if (exactZeroHint != null)
            payload["exact_zero_hint"] = JsonSerializer.SerializeToNode(exactZeroHint, CliJsonSerializerContextFactory.Create(jsonOptions).ExactZeroHintResult);
        if (ftsQueryDiagnostics is { HasDegradation: true })
        {
            payload["query_degraded_reason"] = ftsQueryDiagnostics.QueryDegradedReason;
            payload["tokens_dropped"] = JsonSerializer.SerializeToNode(ftsQueryDiagnostics.TokensDropped.ToList(), CliJsonSerializerContextFactory.Create(jsonOptions).ListString);
        }
        if (exactSubstringHint != null)
            payload["exact_substring_hint"] = BuildSearchQueryHintJson(exactSubstringHint);
        if (queryOptions != null)
            payload["query_context"] = BuildQueryContextJson(queryOptions, jsonOptions);
        extraFields?.Invoke(payload);
        AddFreshnessHint(payload, reader);

        return payload;
    }

    private static JsonObject BuildSearchQueryHintJson(SearchQueryHint hint) => new()
    {
        ["reason"] = hint.Reason,
        ["suggested_action"] = hint.SuggestedAction,
        ["flag"] = hint.Flag,
        ["mcp_argument"] = hint.McpArgument,
    };

    private static JsonObject BuildGroupedHotspotsZeroJsonPayload(DbReader reader, JsonSerializerOptions jsonOptions, bool countOnly, bool graphAvailable, QueryCommandOptions? queryOptions = null)
    {
        var payload = BuildJsonZeroResultPayload(
            reader,
            jsonOptions,
            resultsKey: countOnly ? null : "hotspots",
            includeFiles: countOnly,
            graphTableAvailable: graphAvailable,
            degraded: !graphAvailable,
            queryOptions: queryOptions,
            extraFields: static zeroPayload =>
            {
                zeroPayload["definition_site_total"] = 0;
                zeroPayload["grouped_by"] = HotspotsGroupedByNameKind;
            });
        if (!graphAvailable)
            payload["note"] = "symbol_references table is missing in this index (legacy or read-only DB). Zero result is degraded, not authoritative.";
        return payload;
    }

    private static void WriteExactZeroHint(ExactZeroHintResult? exactZeroHint)
    {
        if (exactZeroHint == null)
            return;

        var examples = exactZeroHint.SampleNames.Count == 0
            ? string.Empty
            : $" (e.g. {string.Join(", ", exactZeroHint.SampleNames.Select(name => $"`{name}`"))})";
        if (exactZeroHint.RelaxedCount.HasValue)
            CommandErrorWriter.WriteStderr($"Hint: --exact found 0 matches, but substring matching would return {exactZeroHint.RelaxedCount}{examples}. Drop --exact or use the exact indexed name.");
        else
            CommandErrorWriter.WriteStderr($"Hint: --exact found 0 matches, but substring matching would return results{examples}. Drop --exact or use the exact indexed name.");
    }

    private static bool IsSqlGraphContractSignal(ExactQuerySignal signal)
        => !signal.ExactIndexAvailable
           && !signal.HasMissingIndex
           && !signal.HasMissingTable
           && signal.DegradedReason?.Contains(DegradationReasonCodes.SqlGraphContractNotReady, StringComparison.OrdinalIgnoreCase) == true;

    private static bool IsCSharpCanonicalNameSignal(ExactQuerySignal signal)
        => !signal.ExactIndexAvailable
           && !signal.HasMissingIndex
           && !signal.HasMissingTable
           && signal.DegradedReason?.Contains(DegradationReasonCodes.CSharpSymbolNameNotReady, StringComparison.OrdinalIgnoreCase) == true;

    private static int WriteStatusReadinessExplanation(string fieldName)
    {
        var field = FindStatusReadinessField(fieldName);
        if (field == null)
        {
            CommandErrorWriter.WriteStderr($"Error: unknown status readiness field `{fieldName}`.");
            CommandErrorWriter.WriteStderr($"Hint: use one of: {string.Join(", ", StatusReadinessFields.Select(f => f.FieldName))}.");
            return CommandExitCodes.UsageError;
        }

        Console.WriteLine($"{field.Label} ({field.FieldName})");
        Console.WriteLine();
        Console.WriteLine($"Ready: {field.ReadyText}");
        Console.WriteLine($"Degraded: {field.DegradedText}");
        Console.WriteLine($"Remediation: {field.Remediation}");
        return CommandExitCodes.Success;
    }

    private static int WriteStatusReadinessExplanationJson(string fieldName)
    {
        var field = FindStatusReadinessField(fieldName);
        if (field == null)
        {
            CommandErrorWriter.WriteStderr($"Error: unknown status readiness field `{fieldName}`.");
            CommandErrorWriter.WriteStderr($"Hint: use one of: {string.Join(", ", StatusReadinessFields.Select(f => f.FieldName))}.");
            return CommandExitCodes.UsageError;
        }

        var knownFields = new JsonArray();
        foreach (var knownField in StatusReadinessFields)
            knownFields.Add(knownField.FieldName);

        var payload = new JsonObject
        {
            ["api_version"] = JsonOutputContract.ApiVersion,
            ["field"] = field.FieldName,
            ["label"] = field.Label,
            ["ready"] = field.ReadyText,
            ["degraded"] = field.DegradedText,
            ["remediation"] = field.Remediation,
            ["known_fields"] = knownFields,
        };
        Console.WriteLine(payload.ToJsonString());
        return CommandExitCodes.Success;
    }

    private static StatusReadinessField? FindStatusReadinessField(string fieldName)
        => StatusReadinessFields.FirstOrDefault(
            field => string.Equals(field.FieldName, fieldName, StringComparison.OrdinalIgnoreCase)
                     || string.Equals(field.Label, fieldName, StringComparison.OrdinalIgnoreCase));

    private static void WriteStatusReadinessSummary(StatusResult status, QueryCommandOptions options)
    {
        Console.WriteLine("Readiness:");
        foreach (var field in StatusReadinessFields)
        {
            var degraded = IsStatusReadinessFieldDegraded(status, field.FieldName);
            var state = degraded ? "degraded" : "ready";
            Console.WriteLine($"  {field.Label,-32} {state}");

            if (degraded)
            {
                Console.WriteLine($"    {BuildStatusReadinessDegradedDetail(status, options, field.FieldName, field.DegradedText)}");
                Console.WriteLine($"    {BuildStatusReadinessRemediation(status, options, field.FieldName, field.Remediation)}");
            }
        }
    }

    private static bool IsStatusReadinessFieldDegraded(StatusResult status, string fieldName)
        => fieldName switch
        {
            "graph_table_available" => !status.GraphTableAvailable,
            "issues_table_available" => !status.IssuesTableAvailable,
            "file_issues_data_current" => !status.FileIssuesDataCurrent,
            "migration_in_progress" => status.MigrationInProgress,
            "sql_graph_contract_ready" => !status.SqlGraphContractReady,
            "hotspot_family_ready" => !status.HotspotFamilyReady,
            "csharp_symbol_name_ready" => !status.CSharpSymbolNameReady,
            "csharp_metadata_target_ready" => !status.CSharpMetadataTargetReady,
            "fold_ready" => !status.FoldReady,
            "index_newer_than_reader" => status.IndexNewerThanReader,
            _ => false,
        };

    private static string BuildStatusReadinessDegradedDetail(StatusResult status, QueryCommandOptions options, string fieldName, string fallback)
        => fieldName switch
        {
            "sql_graph_contract_ready" => status.SqlGraphContractDegradedReason ?? fallback,
            "hotspot_family_ready" => status.HotspotFamilyDegradedReason ?? fallback,
            "fold_ready" => BuildFoldNotReadyExplanation(status.FoldReadyReason),
            "index_newer_than_reader" => status.IndexNewerThanReaderReason ?? fallback,
            "graph_table_available" => DegradationReasonCodes.GetMetadata(DegradationReasonCodes.GraphTableMissing).HumanText,
            "issues_table_available" => DegradationReasonCodes.GetMetadata(DegradationReasonCodes.IssuesTableMissing).HumanText,
            "file_issues_data_current" => DegradationReasonCodes.GetMetadata(DegradationReasonCodes.FileIssuesDataStale).HumanText,
            "migration_in_progress" => DegradationReasonCodes.GetMetadata(DegradationReasonCodes.MigrationInProgress).HumanText,
            "csharp_symbol_name_ready" => DegradationReasonCodes.GetMetadata(DegradationReasonCodes.CSharpSymbolNameNotReady).HumanText,
            "csharp_metadata_target_ready" => DegradationReasonCodes.GetMetadata(status.CSharpMetadataTargetDegradedReason ?? DegradationReasonCodes.CSharpMetadataTargetNotReady).HumanText,
            _ => fallback,
        };

    private static string BuildStatusReadinessRemediation(StatusResult status, QueryCommandOptions options, string fieldName, string fallback)
        => fieldName switch
        {
            "sql_graph_contract_ready" => $"Run `{BuildSqlGraphContractRepairCommand(status.ProjectRoot, options.DbPath, options.DbPathExplicit)}` before trusting SQL references/callers/deps/unused/hotspots.",
            "hotspot_family_ready" => $"Run `{BuildHotspotFamilyRebuildRepairCommand(status.ProjectRoot, options.DbPath, options.DbPathExplicit)}` to restamp authoritative hotspot families for every indexed row.",
            "csharp_symbol_name_ready" => $"Run `{BuildCSharpCanonicalNameRepairCommand(status.ProjectRoot, options.DbPath, options.DbPathExplicit)}` to upgrade canonical C# symbol names in place.",
            "fold_ready" => $"Run `{BuildFoldBackfillCommand(options.DbPath, options.DbPathExplicit)}` to restamp folded-name columns in place, or `{BuildFoldRebuildRepairCommand(status.ProjectRoot, options.DbPath, options.DbPathExplicit)}` for a full rebuild.",
            "csharp_metadata_target_ready" => DegradationReasonCodes.GetMetadata(status.CSharpMetadataTargetDegradedReason ?? DegradationReasonCodes.CSharpMetadataTargetNotReady).RecommendedAction,
            "file_issues_data_current" => DegradationReasonCodes.GetMetadata(DegradationReasonCodes.FileIssuesDataStale).RecommendedAction,
            "migration_in_progress" => DegradationReasonCodes.GetMetadata(DegradationReasonCodes.MigrationInProgress).RecommendedAction,
            "index_newer_than_reader" => "Run status with a current cdidx binary, or rebuild the DB with the version you intend to use.",
            _ => fallback,
        };

    private static void ApplyStatusDegradationGuidance(StatusResult status, QueryCommandOptions options)
    {
        var degradations = BuildStatusReadinessDegradations(status, options);
        if (degradations.Count == 0)
            return;

        status.ReadinessDegradations = degradations;
        var primary = degradations[0];
        status.DegradedRootCause = primary.RootCause;
        status.DegradedReason = primary.DegradedReason;
        status.RecommendedAction = primary.RecommendedAction;
        status.AlternativeAction = primary.AlternativeAction;
    }

    private static List<StatusReadinessDegradation> BuildStatusReadinessDegradations(StatusResult status, QueryCommandOptions options)
    {
        var result = new List<StatusReadinessDegradation>();
        if (status.MigrationInProgress)
            result.Add(BuildStatusReadinessDegradation("migration_in_progress", DegradationReasonCodes.MigrationInProgress, options, status));
        if (!status.GraphTableAvailable)
            result.Add(BuildStatusReadinessDegradation("graph_table_available", DegradationReasonCodes.GraphTableMissing, options, status));
        if (!status.IssuesTableAvailable)
            result.Add(BuildStatusReadinessDegradation("issues_table_available", DegradationReasonCodes.IssuesTableMissing, options, status));
        else if (!status.FileIssuesDataCurrent)
            result.Add(BuildStatusReadinessDegradation("file_issues_data_current", DegradationReasonCodes.FileIssuesDataStale, options, status));
        if (!status.SqlGraphContractReady)
            result.Add(BuildStatusReadinessDegradation("sql_graph_contract_ready", DegradationReasonCodes.SqlGraphContractNotReady, options, status));
        if (!status.HotspotFamilyReady)
            result.Add(BuildStatusReadinessDegradation("hotspot_family_ready", DegradationReasonCodes.HotspotFamilyNotReady, options, status));
        if (!status.CSharpSymbolNameReady)
            result.Add(BuildStatusReadinessDegradation("csharp_symbol_name_ready", DegradationReasonCodes.CSharpSymbolNameNotReady, options, status));
        if (!status.CSharpMetadataTargetReady)
            result.Add(BuildStatusReadinessDegradation("csharp_metadata_target_ready", status.CSharpMetadataTargetDegradedReason ?? DegradationReasonCodes.CSharpMetadataTargetNotReady, options, status));
        if (!status.FoldReady)
            result.Add(BuildStatusReadinessDegradation("fold_ready", DegradationReasonCodes.NormalizeFoldReason(status.FoldReadyReason), options, status));
        if (status.IndexNewerThanReader)
            result.Add(BuildStatusReadinessDegradation("index_newer_than_reader", DegradationReasonCodes.IndexNewerThanReader, options, status));
        return result;
    }

    private static StatusReadinessDegradation BuildStatusReadinessDegradation(string field, string rootCause, QueryCommandOptions options, StatusResult status)
    {
        var metadata = DegradationReasonCodes.GetMetadata(rootCause);
        return new StatusReadinessDegradation
        {
            Field = field,
            RootCause = metadata.Code,
            DegradedReason = metadata.HumanText,
            RecommendedAction = field switch
            {
                "fold_ready" => BuildFoldBackfillCommand(options.DbPath, options.DbPathExplicit),
                "sql_graph_contract_ready" => BuildSqlGraphContractRepairCommand(status.ProjectRoot, options.DbPath, options.DbPathExplicit),
                "hotspot_family_ready" => BuildHotspotFamilyRebuildRepairCommand(status.ProjectRoot, options.DbPath, options.DbPathExplicit),
                "csharp_symbol_name_ready" => BuildCSharpCanonicalNameRepairCommand(status.ProjectRoot, options.DbPath, options.DbPathExplicit),
                _ => metadata.RecommendedAction,
            },
            AlternativeAction = field == "fold_ready"
                ? BuildFoldRebuildRepairCommand(status.ProjectRoot, options.DbPath, options.DbPathExplicit)
                : metadata.AlternativeAction,
        };
    }

    private static bool IsStatusDegraded(StatusResult status)
        => !status.GraphTableAvailable
           || !status.IssuesTableAvailable
           || !status.FileIssuesDataCurrent
           || !status.SqlGraphContractReady
           || !status.HotspotFamilyReady
           || !status.CSharpSymbolNameReady
           || !status.CSharpMetadataTargetReady
           || !status.FoldReady
           || status.IndexNewerThanReader
           || status.MigrationInProgress;

    private sealed record StatusCheckFailure(string Name, bool IsStale, string Diagnostic);

    private static IReadOnlyList<StatusCheckFailure> BuildStatusCheckFailures(StatusResult status, IReadOnlySet<string>? scopedChecks)
    {
        var failures = new List<StatusCheckFailure>();
        var checkAll = scopedChecks is not { Count: > 0 };
        bool Includes(string scope) => checkAll || scopedChecks!.Contains(scope);

        if (Includes("workspace"))
        {
            if (status.WorkspaceCheck?.Checked != true)
            {
                failures.Add(new StatusCheckFailure("workspace_unavailable", true, "[stale] workspace_check unavailable"));
            }
            else if (!status.WorkspaceCheck.MatchesWorkspace)
            {
                var check = status.WorkspaceCheck;
                failures.Add(new StatusCheckFailure(
                    "workspace_stale",
                    true,
                    $"[stale] workspace_check reason={check.Reason} changed={check.ChangedFileCount} missing={check.MissingFileCount} unindexed={check.UnindexedFileCount}"));
            }
        }

        if (Includes("graph") && !status.GraphTableAvailable)
            failures.Add(new StatusCheckFailure("graph_table_available", false, "[degraded] graph_table_available=false"));
        if (Includes("issues") && !status.IssuesTableAvailable)
            failures.Add(new StatusCheckFailure("issues_table_available", false, "[degraded] issues_table_available=false"));
        if (Includes("issues") && status.IssuesTableAvailable && !status.FileIssuesDataCurrent)
            failures.Add(new StatusCheckFailure("file_issues_data_current", false, "[degraded] file_issues_data_current=false"));
        if (Includes("workspace") && status.MigrationInProgress)
            failures.Add(new StatusCheckFailure("migration_in_progress", false, "[degraded] migration_in_progress=true"));
        if (Includes("sql") && !status.SqlGraphContractReady)
            failures.Add(new StatusCheckFailure("sql_graph_contract_ready", false, $"[degraded] sql_graph_contract_ready=false reason={status.SqlGraphContractDegradedReason ?? "unknown"}"));
        if (Includes("hotspot") && !status.HotspotFamilyReady)
            failures.Add(new StatusCheckFailure("hotspot_family_ready", false, $"[degraded] hotspot_family_ready=false reason={status.HotspotFamilyDegradedReason ?? "unknown"}"));
        if (Includes("csharp") && !status.CSharpSymbolNameReady)
            failures.Add(new StatusCheckFailure("csharp_symbol_name_ready", false, "[degraded] csharp_symbol_name_ready=false"));
        if (Includes("csharp") && !status.CSharpMetadataTargetReady)
            failures.Add(new StatusCheckFailure("csharp_metadata_target_ready", false, $"[degraded] csharp_metadata_target_ready=false reason={status.CSharpMetadataTargetDegradedReason ?? "unknown"}"));
        if (Includes("fold") && !status.FoldReady)
            failures.Add(new StatusCheckFailure("fold_ready", false, $"[degraded] fold_ready=false reason={status.FoldReadyReason ?? "unknown"}"));
        if (Includes("newer") && status.IndexNewerThanReader)
            failures.Add(new StatusCheckFailure("index_newer_than_reader", false, $"[degraded] index_newer_than_reader=true reason={status.IndexNewerThanReaderReason ?? "unknown"}"));

        return failures;
    }

    private static List<StatusRepairCommand>? BuildStatusRepairCommands(
        StatusResult status,
        IReadOnlyList<StatusCheckFailure> failures,
        QueryCommandOptions options)
    {
        if (failures.Count == 0)
            return null;

        var commands = new List<StatusRepairCommand>();
        foreach (var failure in failures)
        {
            var command = failure.Name switch
            {
                "workspace_stale" or "workspace_unavailable" => BuildIndexRepairCommand(
                    status,
                    options,
                    failure.Name,
                    rebuild: false,
                    "Re-runs indexing for the current workspace snapshot."),
                "graph_table_available" or "issues_table_available" or "file_issues_data_current"
                    or "sql_graph_contract_ready" or "csharp_symbol_name_ready" or "csharp_metadata_target_ready"
                    => BuildIndexRepairCommand(
                        status,
                        options,
                        failure.Name,
                        rebuild: false,
                        "Rewrites stale or missing index metadata before query results are trusted."),
                "hotspot_family_ready" or "index_newer_than_reader" => BuildIndexRepairCommand(
                    status,
                    options,
                    failure.Name,
                    rebuild: true,
                    "Performs a full rebuild because partial updates cannot prove every indexed row was restamped."),
                "fold_ready" => BuildBackfillFoldRepairCommand(options, failure.Name),
                "migration_in_progress" => BuildStatusCheckRepairCommand(options, failure.Name),
                _ => null,
            };
            if (command != null)
                commands.Add(command);
        }

        return commands.Count == 0 ? null : commands;
    }

    private static StatusRepairCommand BuildIndexRepairCommand(
        StatusResult status,
        QueryCommandOptions options,
        string reason,
        bool rebuild,
        string safetyNote)
    {
        var args = new List<string>
        {
            "index",
            string.IsNullOrWhiteSpace(status.ProjectRoot) ? "." : status.ProjectRoot!,
        };
        if (options.DbPathExplicit)
        {
            args.Add("--db");
            args.Add(ResolveWritableDbPathOrPlaceholder(options.DbPath));
        }
        if (rebuild)
            args.Add("--rebuild");

        return new StatusRepairCommand
        {
            Name = "cdidx",
            Args = args,
            Reason = reason,
            SafetyNotes =
            [
                safetyNote,
                "Avoid running concurrently with another cdidx index writer for the same database.",
            ],
        };
    }

    private static StatusRepairCommand BuildBackfillFoldRepairCommand(QueryCommandOptions options, string reason)
    {
        var args = new List<string> { "backfill-fold" };
        if (options.DbPathExplicit)
        {
            args.Add("--db");
            args.Add(ResolveWritableDbPathOrPlaceholder(options.DbPath));
        }

        return new StatusRepairCommand
        {
            Name = "cdidx",
            Args = args,
            Reason = reason,
            SafetyNotes =
            [
                "Restamps folded-name columns in place without reparsing source files.",
                "Use a full index rebuild instead if the database must be regenerated from source.",
            ],
        };
    }

    private static StatusRepairCommand BuildStatusCheckRepairCommand(QueryCommandOptions options, string reason)
    {
        var args = new List<string> { "status", "--check", "--json" };
        if (options.DbPathExplicit)
        {
            args.Add("--db");
            args.Add(options.DbPath);
        }

        return new StatusRepairCommand
        {
            Name = "cdidx",
            Args = args,
            Reason = reason,
            SafetyNotes =
            [
                "Wait for the active index or migration writer to finish before rerunning status.",
                "Do not start a second writer unless the existing writer is known to be gone.",
            ],
        };
    }

    private static void WriteStatusCheckDiagnostics(IReadOnlyList<StatusCheckFailure> failures)
    {
        foreach (var failure in failures)
            CommandErrorWriter.WriteStderr(failure.Diagnostic);
    }

    private static int GetStatusCheckExitCode(IReadOnlyList<StatusCheckFailure> failures)
    {
        var stale = failures.Any(f => f.IsStale);
        var degraded = failures.Any(f => !f.IsStale);
        return (stale, degraded) switch
        {
            (false, false) => CommandExitCodes.Success,
            (true, false) => 1,
            (false, true) => 2,
            _ => 3,
        };
    }

    private static bool IsFoldOnlyReadinessDegraded(StatusResult status)
        => !status.FoldReady
           && status.GraphTableAvailable
           && status.IssuesTableAvailable
           && status.SqlGraphContractReady
           && status.HotspotFamilyReady
           && status.CSharpSymbolNameReady
           && status.CSharpMetadataTargetReady;

    private static string BuildFoldNotReadyExplanation(string? foldReadyReason)
        => DegradationReasonCodes.BuildFoldNotReadyExplanation(foldReadyReason);

    private static string BuildFoldNotReadyWarning(string? foldReadyReason, string backfillCommand, string rebuildCommand)
        => $"{BuildFoldNotReadyExplanation(foldReadyReason)} Run `{backfillCommand}` to restamp folded-name columns in place, or `{rebuildCommand}` for a full rebuild.";

    private static string BuildStatusFreshnessLabel(StatusResult status)
    {
        if (status.WorkspaceCheck != null)
            return status.WorkspaceCheck.Checked
                ? (status.WorkspaceCheck.MatchesWorkspace ? "fresh" : "stale")
                : "unknown";

        if (!status.IndexedAt.HasValue || !status.LatestModified.HasValue)
            return "unknown";

        if (status.GitIsDirty == true)
            return "stale";

        return status.IndexedAt.Value >= status.LatestModified.Value ? "fresh" : "stale";
    }

    private static void WriteWorkspaceCheck(IndexFreshnessCheckResult check)
    {
        if (!check.Checked)
        {
            Console.WriteLine($"Check   : unavailable ({check.Reason})");
        }
        else if (check.MatchesWorkspace)
        {
            Console.WriteLine($"Check   : matches workspace ({check.MatchedFileCount:N0} files)");
        }
        else
        {
            Console.WriteLine($"Check   : stale ({check.Reason})");
        }

        if (check.ChangedFileCount > 0)
            Console.WriteLine($"  Changed indexed files : {check.ChangedFileCount:N0}{FormatSamples(check.ChangedFiles)}");
        if (check.MissingFileCount > 0)
            Console.WriteLine($"  Missing indexed files : {check.MissingFileCount:N0}{FormatSamples(check.MissingFiles)}");
        if (check.OutsideSparseConeFileCount > 0)
            Console.WriteLine($"  Outside sparse cone : {check.OutsideSparseConeFileCount:N0}{FormatSamples(check.OutsideSparseConeFiles)}");
        if (check.UnindexedFileCount > 0)
            Console.WriteLine($"  Unindexed workspace files : {check.UnindexedFileCount:N0}{FormatSamples(check.UnindexedFiles)}");
        if (check.UnverifiableFileCount > 0)
            Console.WriteLine($"  Unverifiable DB rows : {check.UnverifiableFileCount:N0}{FormatSamples(check.UnverifiableFiles)}");
        if (check.ScanErrorCount > 0)
            Console.WriteLine($"  Scan errors : {check.ScanErrorCount:N0}{FormatSamples(check.ScanErrors)}");
    }

    private static void WriteStatusAge(StatusResult status, TimeSpan staleAfter)
    {
        if (!status.IndexedAt.HasValue)
            return;

        var age = GetUtcNow() - status.IndexedAt.Value;
        if (age < TimeSpan.Zero)
            age = TimeSpan.Zero;

        Console.WriteLine($"Age     : index is {FormatDuration(age)} old (threshold: {FormatDuration(staleAfter)})");
    }

    internal static string FormatDuration(TimeSpan duration)
    {
        if (duration < TimeSpan.Zero)
            duration = TimeSpan.Zero;

        var totalDays = (int)duration.TotalDays;
        var hours = duration.Hours;
        var minutes = duration.Minutes;
        var seconds = duration.Seconds;

        if (totalDays > 0)
            return hours > 0 ? $"{totalDays}d{hours}h" : $"{totalDays}d";
        if (duration.TotalHours >= 1)
            return minutes > 0 ? $"{(int)duration.TotalHours}h{minutes}m" : $"{(int)duration.TotalHours}h";
        if (duration.TotalMinutes >= 1)
            return seconds > 0 ? $"{(int)duration.TotalMinutes}m{seconds}s" : $"{(int)duration.TotalMinutes}m";
        return $"{Math.Max(1, (int)Math.Round(duration.TotalSeconds, MidpointRounding.AwayFromZero))}s";
    }

    private static string FormatHotspotScore(double score) => score.ToString("0.#", CultureInfo.InvariantCulture);

    private static string FormatSamples(IReadOnlyList<string> samples)
        => samples.Count == 0 ? string.Empty : $" ({string.Join(", ", samples)})";

    private static string ShortSha(string? sha)
    {
        if (string.IsNullOrWhiteSpace(sha))
            return "<unknown>";
        return sha.Length <= 12 ? sha : sha[..12];
    }

    private static string BuildFoldBackfillCommand(string dbPath, bool dbPathExplicit)
    {
        if (!dbPathExplicit)
            return "cdidx backfill-fold";

        return $"cdidx backfill-fold --db {QuoteCommandArgument(ResolveWritableDbPathOrPlaceholder(dbPath))}";
    }

    private static string BuildCSharpCanonicalNameRepairCommand(DbReader reader, QueryCommandOptions options)
    {
        var status = reader.GetStatus();
        WorkspaceMetadataEnricher.Enrich(status, options.DbPath, options.DbPathExplicit);
        return BuildCSharpCanonicalNameRepairCommand(status.ProjectRoot, options.DbPath, options.DbPathExplicit);
    }

    private static string BuildCSharpCanonicalNameRepairCommand(string? projectRoot, string dbPath, bool dbPathExplicit)
        => BuildReindexRepairCommand(projectRoot, dbPath, dbPathExplicit);

    private static string BuildSqlGraphContractRepairCommand(DbReader reader, QueryCommandOptions options)
    {
        var status = reader.GetStatus();
        WorkspaceMetadataEnricher.Enrich(status, options.DbPath, options.DbPathExplicit);
        return BuildSqlGraphContractRepairCommand(status.ProjectRoot, options.DbPath, options.DbPathExplicit);
    }

    private static string BuildSqlGraphContractRepairCommand(string? projectRoot, string dbPath, bool dbPathExplicit)
        => BuildReindexRepairCommand(projectRoot, dbPath, dbPathExplicit);

    private static string BuildHotspotFamilyRebuildRepairCommand(string? projectRoot, string dbPath, bool dbPathExplicit)
        => BuildReindexRepairCommand(projectRoot, dbPath, dbPathExplicit, rebuild: true);

    private static string BuildFoldRebuildRepairCommand(string? projectRoot, string dbPath, bool dbPathExplicit)
        => BuildReindexRepairCommand(projectRoot, dbPath, dbPathExplicit, rebuild: true);

    private static string BuildReindexRepairCommand(string? projectRoot, string dbPath, bool dbPathExplicit, bool rebuild = false)
    {
        var rebuildSuffix = rebuild ? " --rebuild" : string.Empty;
        if (!dbPathExplicit)
            return $"cdidx index .{rebuildSuffix}";

        var resolvedDbPath = ResolveWritableDbPathOrPlaceholder(dbPath);
        var targetProject = string.IsNullOrWhiteSpace(projectRoot)
            ? "<projectPath>"
            : QuoteCommandArgument(projectRoot);
        return $"cdidx index {targetProject} --db {QuoteCommandArgument(resolvedDbPath)}{rebuildSuffix}";
    }

    private static string ResolveWritableDbPathOrPlaceholder(string dbPath)
        => DbPathResolver.TryResolveWritableMutationDbPath(dbPath, out var writableDbPath)
            ? writableDbPath
            : "<writable-db-path>";

    private static string QuoteCommandArgument(string value)
    {
        if (value.Length >= 2 && value[0] == '<' && value[^1] == '>')
            return value;

        var fullPath = DbPathResolver.NormalizeDbPath(value);
        if (!fullPath.StartsWith("file:", StringComparison.OrdinalIgnoreCase))
            fullPath = Path.GetFullPath(fullPath);

        return fullPath.IndexOfAny([' ', '\t', '"']) >= 0
            ? $"\"{fullPath.Replace("\"", "\\\"", StringComparison.Ordinal)}\""
            : fullPath;
    }

    private static void WriteExactSymbolWarningIfNeeded(bool exact, bool json, ExactQuerySignal signal, DbReader reader, QueryCommandOptions options)
    {
        if (!exact || json || signal.ExactIndexAvailable || signal.DegradedReason == null)
            return;

        if (signal.HasMissingIndex)
        {
            CommandErrorWriter.WriteStderr($"WARN: --exact symbol query ran without the supporting index ({signal.DegradedReason}). Results are correct but may be slow.");
            CommandErrorWriter.WriteStderr("Hint: re-index with `cdidx index <projectPath>` to upgrade the DB layout.");
            return;
        }

        if (IsCSharpCanonicalNameSignal(signal))
        {
            CommandErrorWriter.WriteStderr($"WARN: --exact symbol query may return false negatives ({signal.DegradedReason}).");
            CommandErrorWriter.WriteStderr($"Hint: run `{BuildCSharpCanonicalNameRepairCommand(reader, options)}` to refresh canonical C# symbol names.");
            return;
        }

        if (IsSqlGraphContractSignal(signal))
        {
            CommandErrorWriter.WriteStderr($"WARN: --exact symbol query may return false negatives ({signal.DegradedReason}).");
            CommandErrorWriter.WriteStderr($"Hint: run `{BuildSqlGraphContractRepairCommand(reader, options)}` to refresh SQL graph rows.");
        }
    }

    /// <summary>
    /// Show available symbol kinds when --kind produces zero results.
    /// --kind で 0 件のとき、有効なシンボル種別を表示する。
    /// </summary>
    /// <summary>
    /// Show available languages when --lang produces zero results.
    /// --lang で 0 件のとき、有効な言語を表示する。
    /// </summary>
    private static void WriteLangHint(string? lang, DbReader reader)
    {
        if (lang == null) return;
        var status = reader.GetStatus();
        if (status.Languages.Count > 0 && status.Languages.ContainsKey(lang))
            return;

        if (status.Languages.Count > 0)
            CommandErrorWriter.WriteStderr($"Hint: '{lang}' not found in index. Available: {string.Join(", ", status.Languages.Keys.OrderBy(l => l))}");

        // Recover from `--lang pythno` / `--lang csarp` typos by suggesting the
        // closest indexed language first; if the typo does not match anything currently
        // in the DB (or the DB has no languages yet) fall back to the full supported set
        // exposed by `ReferenceExtractor.GetSupportedLanguages()` so the suggester is still
        // useful against an empty/fresh index (#1582).
        // `--lang pythno` / `--lang csarp` のようなタイプミスから回復させるため、
        // インデックスに存在する言語の中から最も近いものを優先的に提案する。
        // インデックスに無い、もしくは languages が空の場合は
        // `ReferenceExtractor.GetSupportedLanguages()` 全体から候補を探し、
        // 空のインデックスでも did-you-mean が機能するようにする (#1582)。
        // Skip the suggestion entirely if the closest candidate is the exact value the user
        // already supplied (case-insensitive). FindClosestMatch returns the input verbatim when
        // it is a member of the candidate set — e.g. `--lang java` against a Java-supported but
        // unindexed repo would otherwise self-suggest "Did you mean: --lang java?".
        // 提案候補がユーザー指定値そのものと一致する場合は提案を出さない。
        // FindClosestMatch は候補集合に同名がいれば入力をそのまま返すため、例えば Java は
        // サポート対象だが index 済みでない場合の `--lang java` で自己提案を出してしまう。
        var suggestion = ConsoleUi.FindClosestMatch(lang, status.Languages.Keys)
                         ?? ConsoleUi.FindClosestMatch(lang, ReferenceExtractor.GetSupportedLanguages());
        if (suggestion != null && !string.Equals(suggestion, lang, StringComparison.OrdinalIgnoreCase))
            CommandErrorWriter.WriteStderr($"Did you mean: --lang {suggestion}?");
    }

    private static void WriteSymbolExtractionCapabilityHint(string? lang, DbReader reader)
    {
        if (string.IsNullOrWhiteSpace(lang))
            return;
        if (SymbolExtractor.GetSupportedLanguages().Contains(lang, StringComparer.Ordinal))
            return;

        var status = reader.GetStatus();
        if (status.Languages.Count == 0 || !status.Languages.ContainsKey(lang))
            return;

        CommandErrorWriter.WriteStderr($"Hint: '{lang}' is indexed for full-text search, but symbol extraction is not available for that language. Use `cdidx search <query> --lang {lang}` for text matches or `cdidx languages --capability missing-symbols` to audit capability gaps.");
    }

    // All valid symbol kinds emitted by SymbolExtractor / SymbolExtractor が出力する全有効シンボル種別
    private static readonly string[] AllValidKinds =
        KnownSymbolKindFilters.OrderBy(kind => kind, StringComparer.Ordinal).ToArray();
    // Reference kinds valid on `references --kind`. Includes the compile-time type-position
    // `type_reference` edge emitted by ReferenceExtractor for C#/Java base lists, declaration
    // types, generic constraints, `throws`, `is`/`as`/`instanceof`, and XML-doc `cref` targets.
    // C++ `friend` declarations are also accepted because they are extractor-owned dependency
    // edges and participate in graph queries.
    // `references --kind` で有効な reference kind。ReferenceExtractor が C#/Java の継承リスト、
    // 宣言型、generic 制約、`throws`、`is`/`as`/`instanceof`、XML-doc `cref` 対象向けに出力する
    // compile-time な `type_reference` エッジを含む。C++ の `friend` 宣言も extractor が出す
    // dependency edge として受け付け、graph query にも参加させる。
    private static readonly string[] AllValidReferenceKinds =
        ["annotation", "attribute", "augmentation", "bcl_regex_without_timeout", "call", "consumes_hook", "friend", "import", "instantiate", "razor_event_binding", "subscribe", "type_reference", "unsubscribe"];
    // Reference kinds that `callers` / `callees` can legitimately return. Metadata kinds
    // (`attribute` / `annotation`) and type-position edges (`type_reference`) are structurally
    // not call-graph edges, so those queries are rejected at the CLI / MCP boundary. C++ `friend`
    // is a graph-visible coupling edge.
    // `callers` / `callees` が正しく返せる reference kind。metadata 種別 (`attribute` / `annotation`)
    // や型位置エッジ (`type_reference`) は構造的に call-graph エッジではないため、CLI / MCP 境界で弾く。
    // C++ の `friend` は graph に出す coupling edge。
    private static readonly string[] CallGraphOnlyReferenceKinds =
        ["augmentation", "call", "consumes_hook", "friend", "instantiate", "razor_event_binding", "subscribe", "unsubscribe"];

    private static void WriteKindHint(string? kind, DbReader reader)
    {
        if (kind == null) return;
        if (!AllValidKinds.Contains(kind))
        {
            CommandErrorWriter.WriteStderr($"Hint: '{kind}' is not a known kind. Available: {string.Join(", ", AllValidKinds)}");
            var suggestion = ConsoleUi.FindClosestMatch(kind, AllValidKinds);
            if (suggestion != null)
                CommandErrorWriter.WriteStderr($"Did you mean: --kind {suggestion}?");
            return;
        }
        // Kind is valid but not found in this index — hint that no symbols of this kind exist
        // 種別は有効だがインデックスに存在しない場合のヒント
        var existingKinds = reader.GetDistinctKinds();
        if (!existingKinds.Contains(kind))
            CommandErrorWriter.WriteStderr($"Hint: no '{kind}' symbols in the index. Indexed kinds: {string.Join(", ", existingKinds)}");
    }

    private static void WriteValidateKindHint(string? kind)
    {
        if (string.IsNullOrEmpty(kind)) return;
        if (AllValidValidateKinds.Contains(kind, StringComparer.Ordinal))
            return;

        // `validate --kind` accepts only the file-issue kinds emitted by FileIndexer. A typo
        // like `--kind replacement_chra` filters to zero rows, which previously printed the
        // same "No encoding issues found." message as a genuinely clean repo and silently
        // hid the typo. Surface a hint + suggester for the closest known kind (#1582).
        // `validate --kind` は FileIndexer が出す file_issues kind のみ受理する。
        // `--kind replacement_chra` のようなタイプミスは 0 行となり、クリーンな状態と区別が
        // つかないまま暗黙に握り潰されていた。ヒントと did-you-mean を出すよう改修 (#1582)。
        CommandErrorWriter.WriteStderr($"Hint: '{kind}' is not a known validate kind. Available: {string.Join(", ", AllValidValidateKinds)}");
        var suggestion = ConsoleUi.FindClosestMatch(kind, AllValidValidateKinds);
        if (suggestion != null)
            CommandErrorWriter.WriteStderr($"Did you mean: --kind {suggestion}?");
    }

    private static void WriteGraphReferenceKindHint(string command, string? kind, bool json)
    {
        if (json || string.IsNullOrWhiteSpace(kind))
            return;

        // `references` accepts all reference kinds emitted by the extractor; `callers` / `callees`
        // are restricted to call-graph kinds. Pick the right acceptance set per command.
        // `references` は extractor が出す全 reference kind を受け付ける。`callers` / `callees` は
        // call-graph 種別のみ。コマンドごとに許容集合を使い分ける。
        var acceptedKinds = command == "references" ? AllValidReferenceKinds : CallGraphOnlyReferenceKinds;
        if (acceptedKinds.Contains(kind))
            return;

        if (AllValidKinds.Contains(kind))
        {
            CommandErrorWriter.WriteStderr($"WARN: '{ConsoleUi.FormatBoundedValue(kind)}' is a symbol kind, but --kind on '{command}' filters by reference kind ({string.Join(", ", acceptedKinds)}). Use symbols/definition/hotspots/unused to filter by symbol kind.");
            return;
        }

        CommandErrorWriter.WriteStderr($"Hint: '{ConsoleUi.FormatBoundedValue(kind)}' is not a known reference kind for '{command}'. Available reference kinds: {string.Join(", ", acceptedKinds)}");
        var suggestion = ConsoleUi.FindClosestMatch(kind, acceptedKinds);
        if (suggestion != null)
            CommandErrorWriter.WriteStderr($"Did you mean: --kind {suggestion}?");
    }

    // Reference kinds that are valid `references --kind` values but NOT valid
    // `callers --kind` / `callees --kind` values.
    // - `attribute` / `annotation`: metadata rows are attributed to the enclosing body-range
    //   symbol rather than the annotated target itself, so `callers Obsolete --kind attribute`
    //   and equivalent `callees` queries return structurally wrong answers (method-level
    //   metadata reported under the enclosing class; file-level targets such as
    //   `[assembly: ...]` drop entirely because `container_name` is null).
    // - `type_reference`: type-position edges are compile-time references, not runtime calls,
    //   so `callers Foo --kind type_reference` misreports type mentions as caller edges
    //   (declaration types, generic constraints, `is`/`as`, XML-doc `cref`, etc.).
    // Reject these kinds at the CLI boundary and redirect users to
    // `references --kind <kind>` (which IS correct).
    // `references --kind` では有効だが、`callers --kind` / `callees --kind` では
    // 使ってはいけない reference kind。
    // - `attribute` / `annotation`: metadata 行は注釈対象そのものではなく body-range 上の
    //   外側シンボルに帰属するため、`callers` / `callees` でこの kind を受け付けると
    //   構造的に誤答する（メソッドレベルは外側クラスに寄り、`[assembly: ...]` のような
    //   ファイルレベルは `container_name = null` で丸ごと消える）。
    // - `type_reference`: 型位置エッジは compile-time な参照であり実行時呼び出しではない。
    //   `callers Foo --kind type_reference` は宣言型や generic 制約、`is`/`as`、XML-doc `cref`
    //   などの型言及を caller edge として誤って返す。
    // - `import`: import/include dependency edges are structural, not call-graph edges.
    // CLI 境界で弾き、正しい列挙パスである `references --kind <kind>` に誘導する。
    private static readonly HashSet<string> NonCallGraphReferenceKinds = new(StringComparer.Ordinal)
    {
        "attribute", "annotation", "type_reference", "import",
    };

    /// <summary>
    /// Reject non-call-graph reference kinds (`attribute` / `annotation` / `type_reference` / `import`) on
    /// commands (`callers` / `callees`) whose data model cannot answer those queries correctly.
    /// Returns true if the kind was rejected; the caller should then return
    /// `CommandExitCodes.UsageError`.
    /// `callers` / `callees` のようにデータモデル的に metadata / 型位置参照に答えられない
    /// コマンドで `--kind attribute` / `--kind annotation` / `--kind type_reference` / `--kind import` を弾く。
    /// 弾いた場合 true を返すので、呼び出し側は `CommandExitCodes.UsageError` を返すこと。
    /// </summary>
    private static bool TryRejectNonCallGraphKindForGraphCommand(string command, string? kind)
    {
        if (string.IsNullOrWhiteSpace(kind) || !NonCallGraphReferenceKinds.Contains(kind))
            return false;

        if (kind == "type_reference")
            CommandErrorWriter.WriteStderr($"Error: '--kind type_reference' is not supported on '{command}'. Type-position references are compile-time edges (declaration types, generic constraints, `is`/`as`/`instanceof`, XML-doc `cref`), not runtime calls, so `{command} --kind type_reference` cannot return accurate call-graph rows.");
        else if (kind == "import")
            CommandErrorWriter.WriteStderr($"Error: '--kind import' is not supported on '{command}'. Import references are structural dependency edges, not runtime calls, so `{command} --kind import` cannot return accurate call-graph rows.");
        else
            CommandErrorWriter.WriteStderr($"Error: '--kind {kind}' is not supported on '{command}'. Metadata references are attributed to the enclosing body-range symbol rather than the annotated target, so `{command} --kind {kind}` cannot return accurate rows (file-level targets such as `[assembly: ...]` drop entirely).");
        CommandErrorWriter.WriteStderr($"Hint: use `cdidx references <name> --kind {kind}` instead.");
        return true;
    }

    private static void WriteGraphSupportHint(string? lang)
    {
        if (lang != null && !ReferenceExtractor.SupportsLanguage(lang))
            CommandErrorWriter.WriteStderr($"Note: call-graph queries are not indexed for '{lang}'. Use search, definition, excerpt, or files instead.");
    }

    private static void WriteImpactResolutionHint(ImpactAnalysisResult analysis)
    {
        if (analysis.DefinitionCount > 0)
        {
            var kinds = string.Join(", ", analysis.Definitions.Select(d => d.Kind).Distinct().OrderBy(k => k));
            var pathPreview = analysis.Definitions
                .Select(d => d.Path)
                .Distinct(StringComparer.OrdinalIgnoreCase)
                .Take(3)
                .ToList();
            var extra = analysis.DefinitionFileCount > pathPreview.Count
                ? $" (+{analysis.DefinitionFileCount - pathPreview.Count} more)"
                : string.Empty;
            CommandErrorWriter.WriteStderr($"Note: '{analysis.Query}' resolved to '{analysis.ResolvedName}' ({kinds}) as {ConsoleUi.Counted(analysis.DefinitionCount, "definition")} across {ConsoleUi.Counted(analysis.DefinitionFileCount, "file")}: {string.Join(", ", pathPreview)}{extra}");
        }
        else if (analysis.ZeroResultReason == "no_matching_definition")
        {
            CommandErrorWriter.WriteStderr($"Note: no indexed definition matched '{analysis.Query}'.");
        }

        if (!string.IsNullOrWhiteSpace(analysis.Suggestion))
            CommandErrorWriter.WriteStderr($"Hint: {analysis.Suggestion}");
    }

    // Emit a zero-result payload that distinguishes "real 0 hits" from "graph table missing
    // (degraded)". Without this, AI agents and humans cannot tell the index from a legacy /
    // read-only DB apart from a DB that genuinely has no callers for the query.
    // graph テーブル欠損による 0 と本物の 0 を JSON で区別できるようにする。
    private static void WriteDegradedGraphZeroResult(DbReader reader, string resultsKey, bool json, bool graphAvailable, JsonSerializerOptions jsonOptions,
        ExactQuerySignal? exactSignal = null, QueryCommandOptions? queryOptions = null, Action<JsonObject>? extraFields = null)
    {
        if (graphAvailable) return;
        if (json)
        {
            var payload = BuildJsonZeroResultPayload(reader, jsonOptions, resultsKey: resultsKey, graphTableAvailable: false, degraded: true, exactSignal: exactSignal, queryOptions: queryOptions, extraFields: extraFields);
            payload["note"] = "symbol_references table is missing in this index (legacy or read-only DB). Zero result is degraded, not authoritative.";
            Console.WriteLine(payload.ToJsonString(jsonOptions));
        }
        else
        {
            CommandErrorWriter.WriteStderr("WARN: symbol_references table missing — this 0-result is degraded, not authoritative.");
        }
    }

    private static void WriteExactGraphWarningIfNeeded(bool exact, bool json, ExactQuerySignal signal, DbReader reader, QueryCommandOptions options)
    {
        if (!exact || json || signal.ExactIndexAvailable || signal.DegradedReason == null)
            return;

        if (signal.HasMissingIndex)
        {
            CommandErrorWriter.WriteStderr($"WARN: --exact graph query ran without the supporting index ({signal.DegradedReason}). Results are correct but may be slow.");
            CommandErrorWriter.WriteStderr("Hint: re-index with `cdidx index <projectPath>` to upgrade the DB layout.");
            return;
        }

        if (IsCSharpCanonicalNameSignal(signal))
        {
            CommandErrorWriter.WriteStderr($"WARN: --exact graph query may return false negatives ({signal.DegradedReason}).");
            CommandErrorWriter.WriteStderr($"Hint: run `{BuildCSharpCanonicalNameRepairCommand(reader, options)}` to refresh canonical C# symbol names.");
            return;
        }

        if (IsSqlGraphContractSignal(signal))
        {
            CommandErrorWriter.WriteStderr($"WARN: --exact graph query may return false negatives ({signal.DegradedReason}).");
            CommandErrorWriter.WriteStderr($"Hint: run `{BuildSqlGraphContractRepairCommand(reader, options)}` to refresh SQL graph rows.");
        }
    }

    private static void WriteExactBundleWarningIfNeeded(bool exact, bool json, ExactQuerySignal signal, DbReader reader, QueryCommandOptions options)
    {
        if (!exact || json || signal.ExactIndexAvailable || signal.DegradedReason == null)
            return;

        if (signal.HasMissingIndex)
        {
            CommandErrorWriter.WriteStderr($"WARN: --exact inspect bundle ran without all supporting indexes ({signal.DegradedReason}). Results are correct but may be slow.");
            CommandErrorWriter.WriteStderr("Hint: re-index with `cdidx index <projectPath>` to upgrade the DB layout.");
            return;
        }

        if (IsCSharpCanonicalNameSignal(signal))
        {
            CommandErrorWriter.WriteStderr($"WARN: --exact inspect bundle may return false negatives ({signal.DegradedReason}).");
            CommandErrorWriter.WriteStderr($"Hint: run `{BuildCSharpCanonicalNameRepairCommand(reader, options)}` to refresh canonical C# symbol names.");
            return;
        }

        if (IsSqlGraphContractSignal(signal))
        {
            CommandErrorWriter.WriteStderr($"WARN: --exact inspect bundle may return false negatives ({signal.DegradedReason}).");
            CommandErrorWriter.WriteStderr($"Hint: run `{BuildSqlGraphContractRepairCommand(reader, options)}` to refresh SQL graph rows.");
        }
    }

    private static void WriteGraphCountResult(DbReader reader, int count, int files, QueryCommandOptions options, JsonSerializerOptions jsonOptions,
        bool graphAvailable, ExactQuerySignal exactSignal, ExactZeroHintResult? exactZeroHint = null, GraphSupportOverride? graphSupportOverride = null, Action<JsonObject>? extraFields = null)
    {
        if (!options.Json)
        {
            Console.WriteLine($"{count}");
            WriteGraphSupportOverrideHint(graphSupportOverride);
            if (!graphAvailable)
                CommandErrorWriter.WriteStderr("WARN: symbol_references table missing — this count result is degraded, not authoritative.");
            return;
        }

        var payload = BuildCountJsonPayload(
            reader,
            jsonOptions,
            count,
            files,
            query: options.Query,
            queryOptions: options,
            graphTableAvailable: graphAvailable,
            degraded: !graphAvailable,
            deferAuthority: true);
        AddGraphSupportOverrideFields(payload, graphSupportOverride);
        if (options.Exact || options.ExactName)
            AddExactGraphJsonFields(payload, exactSignal);
        if (exactZeroHint != null)
            payload["exact_zero_hint"] = JsonSerializer.SerializeToNode(exactZeroHint, CliJsonSerializerContextFactory.Create(jsonOptions).ExactZeroHintResult);
        extraFields?.Invoke(payload);
        AddCountAuthorityJsonFields(payload);
        Console.WriteLine(payload.ToJsonString(jsonOptions));
    }

    private static void WriteGraphZeroJsonResult(DbReader reader, string resultsKey, JsonSerializerOptions jsonOptions, bool graphAvailable,
        ExactQuerySignal? exactSignal, ExactZeroHintResult? exactZeroHint = null, GraphSupportOverride? graphSupportOverride = null, QueryCommandOptions? queryOptions = null, Action<JsonObject>? extraFields = null)
    {
        var payload = BuildJsonZeroResultPayload(reader, jsonOptions, resultsKey: resultsKey, graphTableAvailable: graphAvailable, queryOptions: queryOptions);
        if (!graphAvailable)
        {
            payload["degraded"] = true;
            payload["note"] = "symbol_references table is missing in this index (legacy or read-only DB). Zero result is degraded, not authoritative.";
        }
        AddGraphSupportOverrideFields(payload, graphSupportOverride);
        if (exactSignal != null)
            AddExactGraphJsonFields(payload, exactSignal.Value);
        if (exactZeroHint != null)
            payload["exact_zero_hint"] = JsonSerializer.SerializeToNode(exactZeroHint, CliJsonSerializerContextFactory.Create(jsonOptions).ExactZeroHintResult);
        extraFields?.Invoke(payload);
        Console.WriteLine(payload.ToJsonString(jsonOptions));
    }

    private static void WriteGraphJsonResult<T>(T result, JsonTypeInfo<T> jsonTypeInfo, ExactQuerySignal exactSignal, JsonSerializerOptions jsonOptions, GraphSupportOverride? graphSupportOverride = null, Action<JsonObject>? extraFields = null)
    {
        var payload = JsonSerializer.SerializeToNode(result, jsonTypeInfo)!.AsObject();
        AddExactGraphJsonFields(payload, exactSignal);
        AddGraphSupportOverrideFields(payload, graphSupportOverride);
        extraFields?.Invoke(payload);
        Console.WriteLine(payload.ToJsonString(jsonOptions));
    }

    private static void WriteJsonResult<T>(T result, JsonTypeInfo<T> jsonTypeInfo, JsonSerializerOptions jsonOptions, Action<JsonObject>? extraFields = null)
    {
        var payload = JsonSerializer.SerializeToNode(result, jsonTypeInfo)!.AsObject();
        extraFields?.Invoke(payload);
        Console.WriteLine(payload.ToJsonString(jsonOptions));
    }

    private static void WriteJsonResultWithExactSignal<T>(T result, JsonTypeInfo<T> jsonTypeInfo, ExactQuerySignal exactSignal, JsonSerializerOptions jsonOptions)
    {
        var payload = JsonSerializer.SerializeToNode(result, jsonTypeInfo)!.AsObject();
        AddExactJsonFields(payload, exactSignal);
        Console.WriteLine(payload.ToJsonString(jsonOptions));
    }

    private static void AddExactGraphJsonFields(JsonObject payload, ExactQuerySignal exactSignal)
    {
        AddExactJsonFields(payload, exactSignal);
    }

    private static void AddExactJsonFields(JsonObject payload, ExactQuerySignal exactSignal)
    {
        payload["exact_index_available"] = exactSignal.ExactIndexAvailable;
        if (exactSignal.DegradedReason != null)
            payload["degraded_reason"] = exactSignal.DegradedReason;
    }

    private static void AddGraphSupportOverrideFields(JsonObject payload, GraphSupportOverride? graphSupportOverride)
    {
        if (graphSupportOverride == null)
            return;

        if (graphSupportOverride.GraphLanguage != null)
            payload["graph_language"] = graphSupportOverride.GraphLanguage;
        if (graphSupportOverride.GraphSupported.HasValue)
            payload["graph_supported"] = graphSupportOverride.GraphSupported.Value;
        if (graphSupportOverride.GraphSupportReason != null)
            payload["graph_support_reason"] = graphSupportOverride.GraphSupportReason;
        if (graphSupportOverride.GraphDegraded)
            payload["graph_degraded"] = true;
        if (graphSupportOverride.UnsupportedSymbolKind != null)
            payload["unsupported_symbol_kind"] = graphSupportOverride.UnsupportedSymbolKind;
    }

    private static void AddImpactOptionWarnings(JsonObject payload, QueryCommandOptions options)
    {
        if (!options.ImpactDeprecatedDepthUsed)
            return;

        JsonArray warnings;
        if (payload["warnings"] is JsonArray existingWarnings)
        {
            warnings = existingWarnings;
        }
        else
        {
            warnings = [];
            payload["warnings"] = warnings;
        }

        warnings.Add("--depth is deprecated for impact; use --max-hops instead.");
    }

    private static void WriteGraphSupportOverrideHint(GraphSupportOverride? graphSupportOverride)
    {
        if (graphSupportOverride == null)
            return;

        CommandErrorWriter.WriteStderr($"Note: {graphSupportOverride.GraphSupportReason}");
    }

    private sealed record GraphSupportOverride(
        string? GraphLanguage,
        bool? GraphSupported,
        string GraphSupportReason,
        string? UnsupportedSymbolKind,
        bool GraphDegraded);

    private static void AddHotspotFamilyJsonFields(JsonObject payload, HotspotFamilySignal signal)
    {
        payload["hotspot_family_ready"] = signal.Ready;
        if (!signal.Ready)
        {
            payload["degraded"] = true;
            if (signal.DegradedReason != null)
                payload["hotspot_family_degraded_reason"] = signal.DegradedReason;
        }
    }

    private static void WriteHotspotFamilyWarningIfNeeded(bool json, HotspotFamilySignal signal)
    {
        if (json || signal.Ready || signal.DegradedReason == null)
            return;

        CommandErrorWriter.WriteStderr($"WARN: {signal.DegradedReason}");
        CommandErrorWriter.WriteStderr("Hint: rerun `cdidx index <projectPath>` to restore authoritative cross-file hotspot families.");
    }

    internal static SqlGraphContractSignal NarrowSqlGraphContractSignal(SqlGraphContractSignal signal, bool relevant)
    {
        if (!signal.Relevant || relevant)
            return signal;

        return new SqlGraphContractSignal(Ready: true, Relevant: false, DegradedReason: null);
    }

    internal static SqlGraphContractSignal NarrowSqlGraphContractSignalByLanguages(
        SqlGraphContractSignal signal,
        IEnumerable<string?> langs,
        params string?[] additionalLangs)
        => NarrowSqlGraphContractSignal(
            signal,
            additionalLangs.Any(DbReader.IsSqlLanguage) || DbReader.ContainsSqlLanguage(langs));

    internal static SqlGraphContractSignal NarrowSqlGraphContractSignalByPaths(
        DbReader reader,
        SqlGraphContractSignal signal,
        IEnumerable<string> paths,
        params string?[] additionalLangs)
        => NarrowSqlGraphContractSignal(
            signal,
            additionalLangs.Any(DbReader.IsSqlLanguage) || reader.AnyFilePathHasLanguage(paths, "sql"));

    private static void AddSqlGraphContractJsonFields(JsonObject payload, SqlGraphContractSignal signal)
    {
        if (!signal.Relevant)
            return;

        payload["sql_graph_contract_ready"] = signal.Ready;
        if (!signal.Ready)
        {
            payload["degraded"] = true;
            if (signal.DegradedReason != null)
                payload["sql_graph_contract_degraded_reason"] = signal.DegradedReason;
        }
    }

    private static void WriteSqlGraphContractWarningIfNeeded(bool json, SqlGraphContractSignal signal, DbReader reader, QueryCommandOptions options)
    {
        if (json || !signal.Relevant || signal.Ready || signal.DegradedReason == null)
            return;

        CommandErrorWriter.WriteStderr($"WARN: {signal.DegradedReason}");
        CommandErrorWriter.WriteStderr($"Hint: run `{BuildSqlGraphContractRepairCommand(reader, options)}` to refresh SQL graph rows before trusting SQL graph/dependency results.");
    }

    // Per-flag upper bounds for numeric CLI options. Without a cap, `--limit 2147483647` or
    // `--snippet-lines 999999` previously parsed silently and either ran with the absurd value
    // (huge allocations / output) or got quietly clamped (e.g. snippet-lines down to 20 with no
    // signal), hiding typos from users. Each cap below is the documented user-facing maximum.
    // 数値 CLI フラグごとの上限値。上限が無いと `--limit 2147483647` や
    // `--snippet-lines 999999` が黙って通り、巨大確保/出力をそのまま走らせるか silent に clamp
    // されてユーザーのタイポを隠していた。下の値は各フラグのドキュメント上の最大値。
    internal static readonly IReadOnlyDictionary<string, int> NumericFlagUpperBounds =
        new Dictionary<string, int>(StringComparer.Ordinal)
        {
            ["--limit"] = 10_000,
            ["--max-results"] = 10_000,
            ["--snippet-lines"] = SearchSnippetFormatter.MaxSnippetLines,
            ["--max-line-width"] = LineWidthFormatter.MaxAllowedLineWidth,
            ["--slow-query-ms"] = 3_600_000,
            ["--body-start"] = 10_000_000,
            ["--body-lines"] = DbReader.DefinitionBodyMaxRequestedLines,
            ["--body-line-count"] = DbReader.DefinitionBodyMaxRequestedLines,
            ["--max-hops"] = 64,
            ["--depth"] = 64,
            ["--before"] = 1_000,
            ["--after"] = 1_000,
            ["--start"] = 10_000_000,
            ["--end"] = 10_000_000,
            ["--focus-line"] = 10_000_000,
            ["--focus-column"] = 100_000,
            ["--focus-length"] = 100_000,
        };

    // Per-flag hints appended to "Error: <flag> requires a value." so users learn the expected
    // value type or range without consulting `--help`. Routed through BuildMissingOptionValueError
    // so every missing-value site reuses the same table and the messages stay consistent.
    // 「<flag> requires a value.」 missing-value error に追記するフラグ別ヒント。
    // すべての missing-value 経路を BuildMissingOptionValueError 経由にして、コマンド間で
    // メッセージを揃え、ヒントの単一情報源を維持する。
    private static readonly Dictionary<string, string> MissingOptionValueHints = new(StringComparer.Ordinal)
    {
        ["--db"] = "pass a path to a CodeIndex SQLite database, e.g. `--db .cdidx/codeindex.db` or `--db file:///absolute/path/to/codeindex.db?immutable=1`, or omit `--db` to use `.cdidx/codeindex.db`.",
        ["--workspace-db"] = "pass a path to another workspace member CodeIndex SQLite database. Repeat the flag up to 7 distinct additional DBs to aggregate multiple member DBs.",
        ["--data-dir"] = "pass a directory where cdidx should store `codeindex.db`, e.g. `--data-dir /var/cache/cdidx`.",
        ["--limit"] = "pass a positive integer, e.g. `--limit 20` (default 20).",
        ["--top"] = "pass a positive integer, e.g. `--top 20` (alias for `--limit`, default 20).",
        ["--body-start"] = "pass a 1-based source line inside the symbol body, e.g. `--body-start 120`.",
        ["--body-lines"] = "pass a positive line count for the body slice, e.g. `--body-lines 40`.",
        ["--body-line-count"] = "pass a positive line count for the body slice, e.g. `--body-line-count 40` (alias for `--body-lines`).",
        ["--lang"] = "pass a language identifier, e.g. `--lang csharp`. Run `cdidx languages` for the supported set.",
        ["--query"] = "pass a search literal, e.g. `--query \"authenticate\"`. Use the `--query` form when the literal starts with `-`.",
        ["--recipe"] = "pass a built-in audit recipe name, e.g. `--recipe risky-code`, or a child query selector such as `--recipe risky-code/raw-diagnostic-echo`; run `cdidx search --list-recipes` to list available recipes.",
        ["--include-query"] = "pass a child query name from the selected recipe, e.g. `--include-query raw-diagnostic-echo`; repeat or comma-separate values.",
        ["--exclude-query"] = "pass a child query name to omit from the selected recipe, e.g. `--exclude-query cancellation-gap`; repeat or comma-separate values.",
        ["--open-issues"] = "pass an open-issues JSON file or GitHub source, e.g. `--open-issues open-issues.json` or `--open-issues github --repo owner/name`; only valid with `search --format issue-drafts`.",
        ["--repo"] = "pass a GitHub repository in owner/name form for `--open-issues github`, e.g. `--repo Widthdom/CodeIndex`.",
        ["--issue-title"] = "pass an issue title hint for ad hoc search issue-drafts, e.g. `--issue-title \"Thread.Yield audit\"`.",
        ["--issue-label"] = "pass an issue label hint for search issue-drafts, e.g. `--issue-label audit`; repeat or comma-separate values.",
        ["--cursor"] = "pass the `next_cursor` returned by a prior paged response, such as a recipe search cursor, `outline:<offset>`, or `unused:<offset>`.",
        ["--kind"] = "pass a kind identifier, e.g. `--kind function`. definition/symbols/outline/hotspots/unused take a symbol kind; references/callers/callees take a reference kind such as `call`, `instantiate`, or `subscribe`. Run the command's `--help` for the kind list.",
        ["--outline-fields"] = "pass outline symbol field names such as `name,line,signature`, or `all` for the full symbol payload.",
        ["--bucket"] = "pass one unused-symbol bucket: likely_unused_private, maybe_unused_nonpublic, public_or_exported_no_refs, or reflection_or_config_suspect.",
        ["--confidence"] = "pass one unused-symbol confidence threshold: medium or low.",
        ["--min-confidence"] = "pass one unused-symbol confidence threshold: medium or low.",
        ["--visibility"] = "pass one or more of public, protected, internal, private, e.g. `--visibility public,internal`.",
        ["--exclude-visibility"] = "pass one or more of public, protected, internal, private to exclude, e.g. `--exclude-visibility private`.",
        ["--rank-by"] = "pass `weighted`, `count`, or `kind` (callers/callees only).",
        ["--max-hops"] = "pass a non-negative integer, e.g. `--max-hops 5` (default 5).",
        ["--depth"] = "deprecated alias for `--max-hops`; pass a non-negative integer, e.g. `--max-hops 5` (default 5).",
        ["--path"] = "pass a glob-style path pattern, e.g. `--path src/**`. Repeat `--path` to add more patterns.",
        ["--exclude-path"] = "pass a glob-style path pattern to exclude, e.g. `--exclude-path tests/**`. Repeat `--exclude-path` to add more.",
        ["--since"] = "pass an ISO 8601 datetime, e.g. `--since 2024-01-01` or `--since 2024-01-01T00:00:00Z`.",
        ["--start"] = "pass a 1-based line number, e.g. `--start 10`.",
        ["--end"] = "pass a 1-based line number greater than or equal to `--start`, e.g. `--end 20`.",
        ["--before"] = "pass a non-negative integer of context lines before each match, e.g. `--before 2`.",
        ["--after"] = "pass a non-negative integer of context lines after each match, e.g. `--after 2`.",
        ["--focus-line"] = "pass a 1-based line number to focus on, e.g. `--focus-line 12`.",
        ["--focus-column"] = "pass a 1-based column number to keep visible, e.g. `--focus-column 80`.",
        ["--focus-length"] = "pass a positive integer for the focused span width, e.g. `--focus-length 1` (default 1).",
        ["--name"] = "pass a literal symbol name, e.g. `--name UserService`. Repeat `--name` to add more names.",
        ["--snippet-lines"] = "pass an integer between 1 and 20, e.g. `--snippet-lines 8` (default 8).",
        ["--snippet-focus"] = "pass one of `leftmost`, `quality`, or `proximity`, e.g. `--snippet-focus quality` (default quality).",
        ["--max-line-width"] = "pass a non-negative integer (`0` disables clamping), e.g. `--max-line-width 512` (default 512).",
        ["--stale-after"] = "pass a compact positive duration, e.g. `--stale-after 30m`, `--stale-after 2h`, or `--stale-after 7d`.",
        ["--slow-query-ms"] = "pass a non-negative millisecond threshold, e.g. `--slow-query-ms 500`; use 0 to log every profiled SQL statement.",
        ["--min-entrypoint-confidence"] = "pass a decimal from 0.0 through 1.0, e.g. `--min-entrypoint-confidence 0.6`.",
        ["--sections"] = "pass a comma-separated map section list, e.g. `--sections tree,languages`. Supported sections: tree, languages, hotspots, metrics.",
    };

    // Build a missing-value error string with optional caller-supplied hint lines first, then the
    // per-flag hint from MissingOptionValueHints. Newline-separated so each Hint stays on its own
    // line when written via CommandErrorWriter.WriteStderr. Returns just the base error if no hint exists.
    // 呼び出し元固有のヒント (例: inline-form) を先に、テーブル由来のフラグ別ヒントを後ろに追記する。
    // CommandErrorWriter.WriteStderr 経由で出力されたとき各 Hint が別行になるよう改行で連結する。
    private static string BuildMissingOptionValueError(string optionName, params string?[] extraHintLines)
    {
        var sb = new System.Text.StringBuilder();
        sb.Append("Error: ").Append(optionName).Append(" requires a value.");
        foreach (var hint in extraHintLines)
        {
            if (string.IsNullOrEmpty(hint))
                continue;
            sb.Append('\n').Append(hint);
        }
        if (MissingOptionValueHints.TryGetValue(optionName, out var perFlagHint))
            sb.Append('\n').Append("Hint: ").Append(perFlagHint);
        return sb.ToString();
    }

    private static int ResolveDefaultPositiveInt(string environmentVariable, int fallback, string optionName, out string? error)
    {
        var raw = CdidxEnvironment.GetEnvironmentVariable(environmentVariable);
        if (string.IsNullOrWhiteSpace(raw))
        {
            error = null;
            return fallback;
        }

        if (TryParsePositiveInt(raw, optionName, out var value, out var parseError, ConsoleUi.FormatBoundedValue(raw)))
        {
            error = null;
            return value;
        }

        error = parseError!.Replace(optionName, environmentVariable, StringComparison.Ordinal);
        return fallback;
    }

    private static int ResolveDefaultNonNegativeInt(string environmentVariable, int fallback, string optionName, out string? error)
    {
        var raw = CdidxEnvironment.GetEnvironmentVariable(environmentVariable);
        if (string.IsNullOrWhiteSpace(raw))
        {
            error = null;
            return fallback;
        }

        if (TryParseNonNegativeInt(raw, optionName, out var value, out var parseError, ConsoleUi.FormatBoundedValue(raw)))
        {
            error = null;
            return value;
        }

        error = parseError!.Replace(optionName, environmentVariable, StringComparison.Ordinal);
        return fallback;
    }

    private static bool TryParsePositiveInt(string rawValue, string optionName, out int value, out string? error, string? displayRawValue = null)
    {
        if (string.Equals(optionName, "--max-line-width", StringComparison.Ordinal))
            return TryParseNonNegativeInt(rawValue, optionName, out value, out error, displayRawValue);

        displayRawValue ??= ConsoleUi.FormatBoundedValue(rawValue);
        if (!int.TryParse(rawValue, NumberStyles.Integer, CultureInfo.InvariantCulture, out value) || value <= 0)
        {
            value = 0;
            error = BuildPositiveIntegerError(optionName, displayRawValue);
            return false;
        }

        if (NumericFlagUpperBounds.TryGetValue(optionName, out var maxAllowed) && value > maxAllowed)
        {
            error = BuildPositiveIntegerUpperBoundError(optionName, displayRawValue, maxAllowed);
            value = 0;
            return false;
        }

        error = null;
        return true;
    }

    private static bool TryParseNonNegativeInt(string rawValue, string optionName, out int value, out string? error, string? displayRawValue = null)
    {
        displayRawValue ??= ConsoleUi.FormatBoundedValue(rawValue);
        if (!int.TryParse(rawValue, NumberStyles.Integer, CultureInfo.InvariantCulture, out value) || value < 0)
        {
            value = 0;
            error = BuildNonNegativeIntegerError(optionName, displayRawValue);
            return false;
        }

        if (NumericFlagUpperBounds.TryGetValue(optionName, out var maxAllowed) && value > maxAllowed)
        {
            error = BuildNonNegativeIntegerUpperBoundError(optionName, displayRawValue, maxAllowed);
            value = 0;
            return false;
        }

        error = null;
        return true;
    }

    private static string BuildPositiveIntegerError(string optionName, string rawValue, string? displayOptionName = null)
    {
        displayOptionName ??= optionName;
        if (NumericFlagUpperBounds.TryGetValue(optionName, out var maxAllowed))
            return $"Error: {displayOptionName} requires an integer between 1 and {maxAllowed}, got '{rawValue}'. Hint: retry with `{displayOptionName} 1` or another value up to {maxAllowed}.";
        return $"Error: {displayOptionName} requires a positive integer, got '{rawValue}'. Hint: retry with `{displayOptionName} 1` or another positive integer.";
    }

    private static string BuildPositiveIntegerUpperBoundError(string optionName, string rawValue, int maxAllowed)
    {
        return $"Error: {optionName} must be less than or equal to {maxAllowed}, got '{rawValue}'. Hint: retry with `{optionName} {maxAllowed}` or a smaller positive integer.";
    }

    private static string BuildNonNegativeIntegerError(string optionName, string rawValue)
    {
        if (NumericFlagUpperBounds.TryGetValue(optionName, out var maxAllowed))
            return $"Error: {optionName} requires an integer between 0 and {maxAllowed}, got '{rawValue}'. Hint: retry with `{optionName} 0` or another value up to {maxAllowed}.";
        return $"Error: {optionName} requires a non-negative integer, got '{rawValue}'. Hint: retry with `{optionName} 0` or another non-negative integer.";
    }

    private static string BuildNonNegativeIntegerUpperBoundError(string optionName, string rawValue, int maxAllowed)
    {
        return $"Error: {optionName} must be less than or equal to {maxAllowed}, got '{rawValue}'. Hint: retry with `{optionName} {maxAllowed}` or a smaller non-negative integer.";
    }

    private static bool TryReadRawOptionValue(string[] args, ref int index, string optionName, string? inlineValue, out string? value, out string? error)
    {
        if (inlineValue != null)
        {
            value = inlineValue;
            error = null;
            return true;
        }

        if (index + 1 >= args.Length)
        {
            value = null;
            error = BuildMissingOptionValueError(optionName);
            return false;
        }

        var candidate = args[index + 1];
        // If the next token is itself a recognized CLI option, treat this as a missing-value
        // case rather than consuming the option as if it were a value. Without this guard
        // `--limit --lang rust` was parsed as `--limit=--lang` (numeric-parse failure) and then
        // the trailing `rust` was silently dropped, leaving the user with a confusing message
        // about `--lang` being an invalid integer.
        // 次トークンが別の既知オプションなら「値欠如」として扱い、index を進めない。これを
        // 入れないと `--limit --lang rust` が `--limit=--lang` と解釈され、後続の `rust` が
        // 黙って捨てられ、`--lang` が integer じゃないという混乱したメッセージが出てしまう。
        if (IsRecognizedOptionToken(candidate))
        {
            value = null;
            error = BuildMissingOptionValueError(optionName);
            return false;
        }

        index++;
        value = candidate;
        error = null;
        return true;
    }

    private static bool TryReadStringOptionValue(string[] args, ref int index, string optionName, string? inlineValue, bool allowSeparatedDashPrefixedLiteralValue, out string? value, out string? error)
    {
        if (inlineValue != null)
        {
            if (string.IsNullOrWhiteSpace(inlineValue))
            {
                value = null;
                error = BuildMissingOptionValueError(optionName);
                return false;
            }

            value = inlineValue;
            error = null;
            return true;
        }

        if (index + 1 >= args.Length)
        {
            value = null;
            error = BuildMissingOptionValueError(optionName);
            return false;
        }

        var candidate = args[index + 1];
        // Apply the recognized-option guard only when the option does NOT legitimately accept
        // separated dash-prefixed literal values. For flags like `--lang` / `--kind` / `--since`
        // / `--name` (allowSeparatedDashPrefixedLiteralValue=false), `--lang --limit 5` must stop
        // at `--limit` instead of consuming a known CLI flag as the `--lang` value. For flags like
        // `--db` / `--path` / `--exclude-path` / `--query` (allowSeparatedDashPrefixedLiteralValue=true),
        // skip this guard so the downstream `IsRejectedSeparatedStringValue` can emit the
        // inline-form hint for double-dash literals, preserving the pre-existing contract.
        // dash-prefix ヒューリスティックより前に既知オプション判定を置くが、この guard は
        // `allowSeparatedDashPrefixedLiteralValue=false` の時だけ適用する。`--lang` / `--kind` /
        // `--since` / `--name` は `--lang --limit 5` のとき `--limit` を値として飲み込まず値欠如
        // として扱う。`--db` / `--path` / `--exclude-path` / `--query` は dashed literal を受け入れる
        // 設計なので対象外とし、後段の `IsRejectedSeparatedStringValue` 側で double-dash に対する
        // inline-form ヒントを返して既存契約を維持する。
        if (!allowSeparatedDashPrefixedLiteralValue && IsRecognizedOptionToken(candidate))
        {
            value = null;
            error = BuildMissingOptionValueError(optionName);
            return false;
        }
        if (optionName != "--query" && IsRejectedSeparatedStringValue(candidate, allowSeparatedDashPrefixedLiteralValue))
        {
            value = null;
            var inlineFormHint = allowSeparatedDashPrefixedLiteralValue && candidate.StartsWith("--", StringComparison.Ordinal)
                ? $"Hint: if the literal value starts with `--`, pass it as `{optionName}=<value>`."
                : null;
            error = BuildMissingOptionValueError(optionName, inlineFormHint);
            return false;
        }

        if (string.IsNullOrWhiteSpace(candidate))
        {
            value = null;
            error = BuildMissingOptionValueError(optionName);
            return false;
        }

        index++;
        value = candidate;
        error = null;
        return true;
    }

    private static bool IsRejectedSeparatedStringValue(string candidate, bool allowSeparatedDashPrefixedLiteralValue)
    {
        if (!candidate.StartsWith("-", StringComparison.Ordinal))
            return false;

        if (!allowSeparatedDashPrefixedLiteralValue)
            return true;

        return candidate.StartsWith("--", StringComparison.Ordinal);
    }

    private static bool IsRecognizedOptionToken(string value) =>
        ValueTakingOptions.Contains(value) || FlagOnlyOptions.Contains(value);

    private static bool IsBareVerbatimQueryToken(string value)
    {
        var trimmed = value.Trim();
        return trimmed.Length > 0 && trimmed.All(ch => ch == '@');
    }

    private static bool TrySplitInlineOptionValue(string token, out string? optionName)
    {
        optionName = null;
        var separator = token.IndexOf('=');
        if (separator <= 0)
            return false;

        var candidate = token[..separator];
        if (!InlineValueOptions.Contains(candidate))
            return false;

        optionName = candidate;
        return true;
    }

    // Accepted ISO 8601 formats for --since / --sinceフィルタで受け付けるISO 8601書式
    private static readonly string[] Iso8601Formats =
    [
        // date only / 日付のみ
        "yyyy-MM-dd",
        // minute precision / 分精度
        "yyyy-MM-ddTHH:mm",
        "yyyy-MM-ddTHH:mmZ",
        "yyyy-MM-ddTHH:mmzzz",
        // second precision / 秒精度
        "yyyy-MM-ddTHH:mm:ss",
        "yyyy-MM-ddTHH:mm:ssZ",
        "yyyy-MM-ddTHH:mm:sszzz",
        // fractional seconds (1-7 digits via 'F') / 小数秒（1-7桁、'F'で可変長）
        "yyyy-MM-ddTHH:mm:ss.FFFFFFFZ",
        "yyyy-MM-ddTHH:mm:ss.FFFFFFFzzz",
        "yyyy-MM-ddTHH:mm:ss.FFFFFFF",
        // round-trip format / ラウンドトリップ書式
        "o",
    ];

    /// <summary>
    /// Parse a --since value using invariant ISO 8601 formats only.
    /// Rejects ambiguous locale-dependent formats like MM/dd/yyyy.
    /// Offsetless inputs are treated as UTC so the same `--since 2024-01-01T00:00:00`
    /// resolves to the same logical UTC moment regardless of the caller's timezone
    /// (Issue #1545). Append `Z` or an explicit offset (`+09:00`) to opt out.
    /// ISO 8601形式のみで--since値をパースする。MM/dd/yyyyなどロケール依存の曖昧な形式は拒否する。
    /// オフセットなしの入力はUTCとして扱い、呼び出し側のタイムゾーンに依らず同じUTC時点になる
    /// （Issue #1545）。明示的にオフセットを付けたい場合は `Z` または `+09:00` 等を付与する。
    /// </summary>
    internal static bool TryParseIso8601Since(string value, out DateTime result)
    {
        if (DateTimeOffset.TryParseExact(value, Iso8601Formats, CultureInfo.InvariantCulture, DateTimeStyles.AssumeUniversal, out var dto))
        {
            result = dto.UtcDateTime;
            return true;
        }
        result = default;
        return false;
    }

    public static string FormatReferenceRankMode(ReferenceRankMode mode) => mode switch
    {
        ReferenceRankMode.Count => "count",
        ReferenceRankMode.Kind => "kind",
        _ => "weighted",
    };
}

public sealed class QueryCommandOptions
{
    public string DbPath { get; init; } = Path.Combine(".cdidx", "codeindex.db");
    public bool DbPathExplicit { get; init; }
    public bool ReadOnly { get; init; }
    public bool DryRun { get; init; }
    public string? DataDir { get; init; }
    public string? DataDirSource { get; init; }
    public bool Json { get; init; }
    public string JsonOutputFormat { get; init; } = "ndjson";
    public string OutputFormat { get; init; } = "text";
    public int Limit { get; init; } = 20;
    public bool LimitExplicit { get; init; }
    public string? Lang { get; init; }
    public string? Kind { get; init; }
    public string? UnusedBucket { get; init; }
    public string? MinUnusedConfidence { get; init; }
    public bool UnusedActionable { get; init; }
    public string? Severity { get; init; }
    public List<string> VisibilityFilters { get; init; } = [];
    public List<string> ExcludeVisibilityFilters { get; init; } = [];
    public string? Query { get; init; }
    public bool RawFts { get; init; }
    public bool IncludeBody { get; init; }
    public int? BodyStartLine { get; init; }
    public int? BodyLines { get; init; }
    public int? StartLine { get; init; }
    public int? EndLine { get; init; }
    public int ContextBefore { get; init; }
    public int ContextAfter { get; init; }
    public bool ContextAfterExplicit { get; init; }
    public bool ImpactDeprecatedDepthUsed { get; init; }
    public int? FocusLine { get; init; }
    public int? FocusColumn { get; init; }
    public int FocusLength { get; init; } = 1;
    public int SnippetLines { get; init; } = SearchSnippetFormatter.DefaultSnippetLines;
    public SearchSnippetFocusMode SnippetFocus { get; init; } = SearchSnippetFocusMode.Quality;
    public int MaxLineWidth { get; init; } = LineWidthFormatter.DefaultMaxLineWidth;
    public List<string> PathPatterns { get; init; } = [];
    public List<string> WorkspaceDbPaths { get; init; } = [];
    public List<string> ProjectFilters { get; init; } = [];
    public string? ProjectFilterRoot { get; init; }
    public string? ProjectFilterRootFallbackReason { get; init; }
    public string? SolutionFilter { get; init; }
    public List<string> ExcludePaths { get; init; } = [];
    public bool ExcludeTests { get; init; }
    public bool IncludeGenerated { get; init; }
    public bool CountOnly { get; init; }
    public bool All { get; init; }
    public bool StrictNotFound { get; init; }
    public bool Strict { get; init; }
    public DateTime? Since { get; init; }
    public bool NoDedup { get; init; }
    public bool NoVisibilityRank { get; init; }
    public bool Exact { get; init; }
    public bool Regex { get; init; }
    public bool Prefix { get; init; }
    public List<SearchGuardFilter> GuardFilters { get; init; } = [];
    public int GuardWindow { get; init; } = DbReader.DefaultSearchGuardWindow;
    public SearchGuardScope GuardScope { get; init; } = SearchGuardScope.Window;
    public bool ExcludeComments { get; init; }
    public bool ExcludeStrings { get; init; }
    public bool ExcludeFixtures { get; init; }
    public bool ExactName { get; init; }
    public bool ExactSubstring { get; init; }
    public bool CheckWorkspace { get; init; }
    public TimeSpan? StaleAfter { get; init; }
    public IReadOnlySet<string>? StatusCheckScopes { get; init; }
    public bool WithPaths { get; init; }
    public string? GroupBy { get; init; }
    public string? UniqueBy { get; init; }
    public string? CountBy { get; init; }
    public List<string> MatchOrigins { get; init; } = [];
    public List<string> ExcludeOrigins { get; init; } = [];
    public List<string> ResultKinds { get; init; } = [];
    public List<string>? SearchFields { get; init; }
    public List<string>? OutlineFields { get; init; }
    public bool OutlineFieldsExplicit { get; init; }
    public bool FirstPerFile { get; init; }
    public bool ResultsOnly { get; init; }
    public bool NextSteps { get; init; }
    public int GroupedPerFileLimit { get; init; } = 3;
    public int? SampleSize { get; init; }
    public int? MaxJsonBytes { get; init; }
    public bool RawBytes { get; init; }
    public bool RawKinds { get; init; }
    public bool Verbose { get; init; }
    public bool Profile { get; init; }
    public int? SlowQueryMs { get; init; }
    public bool Compact { get; init; }
    public List<string>? InspectFields { get; init; }
    public double MinEntrypointConfidence { get; init; }
    public string? StatusExplainField { get; init; }
    public bool StatusLogPath { get; init; }
    public bool StatusConfig { get; init; }
    public ReferenceRankMode RankMode { get; init; } = ReferenceRankMode.Weighted;
    public SymbolSortMode SymbolSortMode { get; init; } = SymbolSortMode.Name;
    public List<string> ExtraNames { get; init; } = [];
    public List<string>? MapSections { get; init; }
    public bool MapSummaryOnly { get; init; }
    public bool DependencyCycles { get; init; }
    public string? RecipeName { get; init; }
    public List<string> IncludeRecipeQueries { get; init; } = [];
    public List<string> ExcludeRecipeQueries { get; init; } = [];
    public bool ShowExcluded { get; init; }
    public bool ListRecipes { get; init; }
    public string? OpenIssuesPath { get; init; }
    public string AuditScope { get; init; } = SearchAuditRecipes.DefaultAuditScope;
    public bool AuditScopeExplicit { get; init; }
    public string? OpenIssuesRepository { get; init; }
    public string DuplicateConfidence { get; init; } = IssueDuplicatePreflight.DefaultDuplicateConfidence;
    public double DuplicateThreshold { get; init; } = IssueDuplicatePreflight.DefaultDuplicateThreshold;
    public bool DuplicatePreflightTuningExplicit { get; init; }
    public string? IssueTitle { get; init; }
    public List<string> IssueLabels { get; init; } = [];
    public SearchCursor? SearchCursor { get; init; }
    public int? UnusedCursorOffset { get; init; }
    public int? OutlineCursorOffset { get; init; }
    public List<SearchNamedQuery> NamedSearchQueries { get; init; } = [];
    public bool LanguagesIndexedOnly { get; init; }
    public List<string> LanguageCapabilities { get; init; } = [];
    public List<string> LanguageLookups { get; init; } = [];
    public List<string> LanguageExtensionLookups { get; init; } = [];
    public List<string> LanguageAliasLookups { get; init; } = [];
    public string? ParseError { get; init; }
}

public sealed record SearchNamedQuery(string Name, string Query);

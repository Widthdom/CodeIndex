using System.Globalization;
using System.Text;
using System.Text.RegularExpressions;
using System.Text.Json;
using System.Text.Json.Nodes;
using System.Text.Json.Serialization.Metadata;
using CodeIndex.Database;
using CodeIndex.Diagnostics;
using CodeIndex.Indexer;
using CodeIndex.Indexer.Extensibility;
using CodeIndex.Indexer.Hooks;
using CodeIndex.Models;
using Microsoft.Data.Sqlite;
using Regex = CodeIndex.Indexer.BoundedRegex;

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
    internal const int MapIssueDraftLineThreshold = 800;
    internal const long MapIssueDraftByteThreshold = 64 * 1024;
    private const int MaxNamedSearchQueryNameLength = 128;
    internal const int DefaultImpactLimit = 50;
    internal const int DefaultDependencyCycleGraphLimit = 50;
    internal const int GraphLivenessLimitThreshold = 80;
    internal const string DependencyCycleDetectionMode = "bounded_approximate_candidate_edges";
    internal const int MaxWorkspaceDependencyDatabaseCount = 8;
    internal const int MaxWorkspaceDependencyDatabasePairCount = MaxWorkspaceDependencyDatabaseCount * (MaxWorkspaceDependencyDatabaseCount - 1);
    internal const int FindAllCandidateFileLimit = 4096;
    internal const int FindAllLineScanLimit = 250_000;
    internal const int BatchMaxLineChars = 1024 * 1024;
    internal const int BatchMaxLineUtf8Bytes = BatchMaxLineChars * 4;
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
    private const int MaxIssueDraftEvidenceItems = 5;
    private const int MaxIssueDraftEvidenceSnippetLength = 512;
    private const string BareTokenAuthAuditHint = "Bare `token` searches are intentionally broad. For credential/auth-token review, run `cdidx search --recipe auth-token-audit`; use `cdidx search --recipe broad-token-audit` only when parser, LSP, or cancellation token domains are intentional.";
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
    internal const int MaxQueryResultLimit = 10_000;
    private const int SearchOriginFilterMaxCandidates = MaxQueryResultLimit;
    private const int SearchOriginFilterMaxPages = 50;
    private const int SearchEnvelopeMinCandidates = 200;
    private const int SearchEnvelopeOverFetchFactor = 50;
    private const int SearchEnvelopeMaxCandidates = MaxQueryResultLimit;
    private const int MaxUnusedPaginationPages = 10;
    internal const int MaxUnusedPaginationFetchLimit = MaxQueryResultLimit * MaxUnusedPaginationPages + 1;
    internal const int MaxUnusedPaginationOffset = MaxUnusedPaginationFetchLimit - MaxQueryResultLimit - 1;
    private const int UnusedDefaultSuppressionOverfetchMultiplier = 6;
    private const string SearchFilterNoMatchSentinel = "\0__cdidx_no_match__";
    internal const string HotspotsGroupedByNameKind = "name_kind";
    internal const string HotspotsGroupedBySymbol = "symbol";
    internal const string HotspotsGroupedByFile = "file";
    internal const string HotspotsGroupedByStatement = "statement";
    private const string JsonOutputFormatNdjson = "ndjson";
    private const string JsonOutputFormatArray = "array";
    private sealed record StatusFieldExplanation(
        string FieldName,
        string Label,
        string ReadyText,
        string DegradedText,
        string Remediation);

    private static readonly StatusFieldExplanation[] StatusReadinessFields =
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

    private static readonly StatusFieldExplanation[] StatusExplainFields =
        StatusReadinessFields.Concat(
        [
            new(
                "git_head",
                "Runtime Git HEAD",
                "the current workspace Git HEAD commit was resolved at status time.",
                "the field is absent outside a Git checkout or when Git HEAD cannot be resolved.",
                "Run inside a Git workspace or pass a database tied to a Git workspace to compare index stamps."),
            new(
                "git_is_dirty",
                "Runtime Git dirty state",
                "`true` means git status reported uncommitted changes, including untracked files; `false` means no changes were reported.",
                "the field is absent outside a Git checkout or when dirty-state detection is unavailable.",
                "Run `git status` in the workspace to inspect uncommitted changes directly."),
            new(
                "indexed_head_commit",
                "Legacy full-scan HEAD stamp",
                "the index records the Git HEAD from the most recent successful full scan for legacy compatibility.",
                "this full-scan-only stamp can differ from `indexed_head_sha` after incremental indexing and may be absent in legacy or non-Git indexes.",
                "Prefer `indexed_head_sha` for current freshness checks; rebuild or run `cdidx index <projectPath>` when only this legacy stamp is available."),
            new(
                "worktree_head_changed",
                "Worktree HEAD drift",
                "`false` means the runtime HEAD matches the latest index HEAD stamp; `true` means the checkout moved since the index stamp.",
                "the field is absent when neither `indexed_head_sha` nor the legacy `indexed_head_commit` can be compared with runtime HEAD.",
                "Run `cdidx index <projectPath>` to refresh the index for the current checkout."),
            new(
                "indexed_head_sha",
                "Latest index HEAD stamp",
                "the index records the Git HEAD from the last successful index run, including incremental updates.",
                "the field is absent in legacy indexes, non-Git workspaces, or when the index run could not resolve HEAD.",
                "Use this field before `indexed_head_commit` when auditing freshness after incremental indexing."),
            new(
                "indexed_head_branch",
                "Latest index branch stamp",
                "the index records the branch short name captured with `indexed_head_sha`.",
                "the field is absent for detached HEAD, legacy indexes, non-Git workspaces, or unresolved branch names.",
                "Use it as context for `indexed_head_sha`; rerun `cdidx index <projectPath>` after switching branches."),
            new(
                "indexed_head_timestamp",
                "Latest index HEAD timestamp",
                "the index records when `indexed_head_sha` and `indexed_head_branch` were stamped.",
                "the field is absent in legacy indexes or when the index run could not persist the timestamp.",
                "Rerun `cdidx index <projectPath>` to refresh the timestamp with the current checkout."),
            new(
                "commits_ahead_of_indexed_head",
                "Commits ahead of indexed HEAD",
                "`0` means runtime HEAD is not ahead of `indexed_head_sha`; positive values mean the checkout advanced after indexing.",
                "the field is absent when Git comparison is unavailable or history is not comparable.",
                "Run `cdidx index <projectPath>` when the value is positive before trusting freshness-sensitive results."),
            new(
                "head_freshness",
                "Compact HEAD freshness summary",
                "`state=fresh` means `status --check` proved the index matches the workspace; without `--check`, `state=head_current` means only the runtime HEAD matched `indexed_head` (see `indexed_head_source`).",
                "`state=stale`, `state=head_changed`, `state=check_unavailable`, or `state=unchecked` means consumers should inspect `state_reason`, `indexed_head_source`, and the nested head fields before trusting freshness-sensitive results.",
                "Use this summary for machine routing, and use `indexed_head_sha` over legacy `indexed_head_commit` when `indexed_head_source=latest_index`."),
            new(
                "path_case_sensitive",
                "Filesystem case sensitivity",
                "`true` means the indexed workspace path comparison is case-sensitive; `false` means case-insensitive.",
                "the field is absent on legacy indexes that predate the workspace case-sensitivity stamp.",
                "Run `cdidx index <projectPath>` with a current cdidx binary to stamp filesystem case sensitivity."),
        ]).ToArray();

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
        "--names",
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
        "--outline-only",
        "--first-per-file",
        "--results-only",
        "--next-steps",
        "--source-only",
        "--no-semantic-tokens",
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
        OutputFormatIssueDrafts,
    };
    private static readonly HashSet<string> SymbolOutputFormats = new(StringComparer.Ordinal)
    {
        OutputFormatText,
        OutputFormatJson,
        OutputFormatCount,
        OutputFormatCompact,
        OutputFormatLsp,
        OutputFormatQf,
        OutputFormatSarif,
    };
    private static readonly HashSet<string> FilesOutputFormats = new(StringComparer.Ordinal)
    {
        OutputFormatText,
        OutputFormatJson,
        OutputFormatCount,
        OutputFormatCompact,
    };
    private static readonly HashSet<string> InspectOutputFormats = new(StringComparer.Ordinal)
    {
        OutputFormatText,
        OutputFormatJson,
        OutputFormatCompact,
    };
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
            var fileGroupSelection = ApplySearchGroupOutputSelection(fileCountGroups, options);

            if (options.Json)
            {
                var json = JsonSerializer.Serialize(
                        new SearchGroupedCountJsonResult(
                            JsonOutputContract.ApiVersion,
                            options.Query!,
                            options.GroupBy!,
                            totalCount,
                            fileGroups.Count,
                            fileGroupSelection.Groups.Count,
                            fileGroupSelection.TotalGroups,
                            fileGroupSelection.Truncated,
                            options.Limit,
                            fileGroupSelection.Groups),
                        CliJsonSerializerContextFactory.Create(jsonOptions).SearchGroupedCountJsonResult);
                return WriteJsonObjectWithOptionalByteLimit(
                    json,
                    options,
                    "grouped search count",
                    "Reduce --limit or increase --max-json-bytes.");
            }
            else
            {
                WriteSearchGroupedCounts(options.GroupBy!, fileGroupSelection.Groups, totalCount, fileGroups.Count, fileGroupSelection.TotalGroups);
                WriteExactSubstringHintIfNeeded(exactSubstringHint);
            }

            return CommandExitCodes.Success;
        }

        var results = reader.Search(options.Query!, int.MaxValue, options.Lang, options.RawFts, options.PathPatterns, options.ExcludePaths, options.ExcludeTests, !options.NoDedup, options.Since, exact, options.Prefix, !options.NoVisibilityRank, guardFilters: options.GuardFilters, guardWindow: options.GuardWindow, guardScope: options.GuardScope);
        var displayRows = BuildSearchDisplayRows(results, options, exact);
        var groups = BuildSearchGroupedCounts(options.GroupBy!, displayRows);
        var fallbackGroupSelection = ApplySearchGroupOutputSelection(groups, options);
        var fileCount = displayRows.Select(row => row.Result.Path).Distinct(StringComparer.Ordinal).Count();

        if (options.Json)
        {
            var json = JsonSerializer.Serialize(
                    new SearchGroupedCountJsonResult(
                        JsonOutputContract.ApiVersion,
                        options.Query!,
                        options.GroupBy!,
                        displayRows.Count,
                        fileCount,
                        fallbackGroupSelection.Groups.Count,
                        fallbackGroupSelection.TotalGroups,
                        fallbackGroupSelection.Truncated,
                        options.Limit,
                        fallbackGroupSelection.Groups),
                    CliJsonSerializerContextFactory.Create(jsonOptions).SearchGroupedCountJsonResult);
            return WriteJsonObjectWithOptionalByteLimit(
                json,
                options,
                "grouped search count",
                "Reduce --limit or increase --max-json-bytes.");
        }
        else
        {
            WriteSearchGroupedCounts(options.GroupBy!, fallbackGroupSelection.Groups, displayRows.Count, fileCount, fallbackGroupSelection.TotalGroups);
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

    private static void WriteSearchGroupedCounts(string groupBy, List<SearchGroupedCountItemJsonResult> groups, int totalCount, int fileCount, int? totalGroups = null)
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

        var truncation = totalGroups.HasValue && groups.Count < totalGroups.Value
            ? $"; showing {groups.Count} of {totalGroups.Value} groups"
            : string.Empty;
        CommandErrorWriter.WriteStderr($"({totalCount} results in {fileCount} files; grouped by {groupBy}{truncation})");
    }

    private static int RunSearchAggregation(DbReader reader, QueryCommandOptions options, JsonSerializerOptions jsonOptions, bool exact, SearchQueryHint? exactSubstringHint)
    {
        var results = reader.Search(options.Query!, int.MaxValue, options.Lang, options.RawFts, options.PathPatterns, options.ExcludePaths, options.ExcludeTests, !options.NoDedup, options.Since, exact, options.Prefix, !options.NoVisibilityRank, guardFilters: options.GuardFilters, guardWindow: options.GuardWindow, guardScope: options.GuardScope);
        var rows = BuildSearchDisplayRows(results, options, exact);
        var groupBy = NormalizeSearchAggregationKey(options.CountBy ?? options.UniqueBy!);
        var groups = BuildSearchGroupedCounts(groupBy, rows);
        var selection = ApplySearchGroupOutputSelection(groups, options);
        var uniqueOnly = options.UniqueBy != null;
        var fileCount = rows.Select(row => row.Result.Path).Distinct(StringComparer.Ordinal).Count();

        if (options.Json)
        {
            var json = JsonSerializer.Serialize(
                    new SearchAggregationJsonResult(
                        JsonOutputContract.ApiVersion,
                        options.Query!,
                        uniqueOnly ? "unique" : "count_by",
                        groupBy,
                        rows.Count,
                        fileCount,
                        uniqueOnly,
                        selection.Groups.Count,
                        selection.TotalGroups,
                        selection.Truncated,
                        options.Limit,
                        selection.Groups),
                    CliJsonSerializerContextFactory.Create(jsonOptions).SearchAggregationJsonResult);
            return WriteJsonObjectWithOptionalByteLimit(
                json,
                options,
                "search aggregation",
                "Reduce --limit or increase --max-json-bytes.");
        }
        else
        {
            if (uniqueOnly)
            {
                foreach (var group in selection.Groups)
                    Console.WriteLine(group.Key);
                var truncation = selection.Truncated
                    ? $"showing {selection.Groups.Count} of {selection.TotalGroups}"
                    : selection.Groups.Count.ToString(CultureInfo.InvariantCulture);
                CommandErrorWriter.WriteStderr($"({truncation} unique {groupBy} values from {rows.Count} results in {fileCount} files)");
            }
            else
            {
                WriteSearchGroupedCounts(groupBy, selection.Groups, rows.Count, fileCount, selection.TotalGroups);
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

    private static int WriteGroupedSearchResults(List<SearchDisplayRow> rows, QueryCommandOptions options, JsonSerializerOptions jsonOptions)
    {
        var groups = BuildSearchFileGroups(rows, options);
        var totalMatches = rows.Count;
        var json = JsonSerializer.Serialize(
                new SearchFileGroupedJsonResult(
                    JsonOutputContract.ApiVersion,
                    options.Query!,
                    totalMatches,
                    groups.Count,
                    rows.Select(row => row.Result.Path).Distinct(StringComparer.Ordinal).Count(),
                    options.GroupedPerFileLimit,
                    groups.Any(group => group.Truncated),
                    groups),
                CliJsonSerializerContextFactory.Create(jsonOptions).SearchFileGroupedJsonResult);
        return WriteJsonObjectWithOptionalByteLimit(
            json,
            options,
            "grouped search results",
            "Reduce --limit, --per-file-limit, or increase --max-json-bytes.");
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

    private static int WriteProjectedSearchResults(CompactSearchResult[] results, QueryCommandOptions options, JsonSerializerOptions jsonOptions, JsonSerializerOptions ndjsonOptions, out int emittedCount, out bool interrupted)
    {
        var projected = results.Select(result => BuildProjectedSearchResult(result, options.SearchFields!, queryName: null, recipeName: null)).ToArray();
        if (options.JsonOutputFormat == JsonOutputFormatArray)
        {
            emittedCount = projected.Length;
            interrupted = false;
            using var writer = new StringWriter(CultureInfo.InvariantCulture);
            WriteJsonArray(
                writer,
                projected,
                (writer, result) => writer.Write(result.ToJsonString(jsonOptions)),
                jsonOptions);
            return WriteJsonObjectWithOptionalByteLimit(
                writer.ToString().TrimEnd('\r', '\n'),
                options,
                "projected search result array",
                "Reduce --limit, --search-fields, or use `--json=ndjson --max-json-bytes` for streaming output.");
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

        return CommandExitCodes.Success;
    }

    private static JsonObject BuildProjectedSearchResult(
        CompactSearchResult result,
        IReadOnlyList<string> fields,
        string? queryName,
        string? recipeName)
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
                case "query_name":
                    payload["query_name"] = queryName ?? result.Query;
                    break;
                case "recipe":
                    payload["recipe"] = recipeName;
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
            List<SearchCommandHint> nextSteps =
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
            if (IsBareTokenSearch(options))
            {
                nextSteps.Add(new SearchCommandHint
                {
                    Command = "cdidx search --recipe auth-token-audit --exclude-tests",
                    Purpose = "narrow bare token search to credential and auth-token contexts",
                });
            }
            result.NextSteps = nextSteps;
            result.NextStepsTruncated = truncated;
        }
    }

    private sealed record SearchOutputSelection(List<SearchDisplayRow> Rows, int OriginalCount, bool Truncated);

    private sealed record SearchGroupOutputSelection(
        List<SearchGroupedCountItemJsonResult> Groups,
        int TotalGroups,
        bool Truncated);

    private static SearchGroupOutputSelection ApplySearchGroupOutputSelection(List<SearchGroupedCountItemJsonResult> groups, QueryCommandOptions options)
    {
        var totalGroups = groups.Count;
        if (groups.Count > options.Limit)
            groups = groups.Take(options.Limit).ToList();

        return new SearchGroupOutputSelection(groups, totalGroups, groups.Count < totalGroups);
    }

    private static bool SupportsSearchJsonByteLimit(QueryCommandOptions options)
    {
        if (!options.Json)
            return false;
        if (options.OutputFormat is OutputFormatCount or OutputFormatCompact or OutputFormatGrouped or OutputFormatIssueDrafts)
            return true;
        if (options.OutputFormat == OutputFormatJson)
            return options.JsonOutputFormat is JsonOutputFormatNdjson or JsonOutputFormatArray;
        return false;
    }

    private static bool TryWriteEmptySearchJsonWithOptionalByteLimit(QueryCommandOptions options, JsonSerializerOptions jsonOptions, out int exitCode)
    {
        exitCode = CommandExitCodes.Success;
        if (!options.MaxJsonBytes.HasValue)
            return false;

        if (options.OutputFormat == OutputFormatCompact)
        {
            exitCode = WriteJsonObjectWithOptionalByteLimit(
                "[]",
                options,
                "compact search results",
                "Increase --max-json-bytes or remove the byte cap.");
            return true;
        }

        if (options.OutputFormat == OutputFormatCount)
        {
            exitCode = WriteJsonObjectWithOptionalByteLimit(
                new JsonObject
                {
                    ["count"] = 0,
                    ["total_estimated"] = 0,
                }.ToJsonString(jsonOptions),
                options,
                "search count",
                "Increase --max-json-bytes or remove the byte cap.");
            return true;
        }

        return false;
    }

    private static int RunSearchNamedBatch(QueryCommandOptions options, JsonSerializerOptions jsonOptions, bool userExact)
    {
        return WithDb(options, jsonOptions, reader =>
        {
            var queryResults = CollectSearchNamedBatchQueryResults(reader, options, userExact, out var total);

            if (options.Json)
            {
                var json = JsonSerializer.Serialize(
                    new SearchNamedBatchRunJsonResult(
                        JsonOutputContract.ApiVersion,
                        queryResults.Count,
                        total,
                        queryResults),
                    CliJsonSerializerContextFactory.Create(jsonOptions).SearchNamedBatchRunJsonResult);
                return WriteJsonObjectWithOptionalByteLimit(
                    json,
                    options,
                    "named-query search",
                    "Reduce --limit, use --snippet-lines 0, or increase --max-json-bytes.");
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
        var emitsJson = options.NamesOnly
            ? options.Json
            : options.Json || options.OutputFormat == OutputFormatCompact;
        if (options.MaxJsonBytes.HasValue && !emitsJson)
        {
            WriteUsageError(
                "--max-json-bytes is only supported with JSON recipe-list output.",
                GetUsageLineOrThrow("search"),
                "Add `--json` or `--format compact`, or remove --max-json-bytes for text recipe output.");
            return CommandExitCodes.UsageError;
        }

        var recipes = SearchAuditRecipes.All
            .Select(recipe => ToFilteredSearchRecipeListItem(recipe, options.Query))
            .OfType<SearchRecipeListItemJsonResult>()
            .ToList();
        if (options.NamesOnly)
        {
            var names = recipes
                .Select(recipe => recipe.Name)
                .OrderBy(name => name, StringComparer.Ordinal)
                .ToList();
            if (options.Json)
            {
                var json = JsonSerializer.Serialize(
                    new SearchRecipeNameListJsonResult(JsonOutputContract.ApiVersion, names.Count, names),
                    CliJsonSerializerContextFactory.Create(jsonOptions).SearchRecipeNameListJsonResult);
                return WriteJsonObjectWithOptionalByteLimit(
                    json,
                    options,
                    "recipe-name list",
                    "Use a larger --max-json-bytes value or remove recipe filters.");
            }

            foreach (var name in names)
                Console.WriteLine(name);
            return CommandExitCodes.Success;
        }
        if (options.OutputFormat == OutputFormatCompact || (options.SummaryOnly && options.Json))
        {
            var compactRecipes = recipes
                .Select(recipe => ToSearchRecipeCompactListItem(recipe, recipe.Queries))
                .ToList();
            var json = JsonSerializer.Serialize(
                new SearchRecipeCompactListJsonResult(JsonOutputContract.ApiVersion, compactRecipes.Count, compactRecipes),
                CliJsonSerializerContextFactory.Create(jsonOptions).SearchRecipeCompactListJsonResult);
            return WriteJsonObjectWithOptionalByteLimit(
                json,
                options,
                "recipe summary",
                "Use `cdidx recipes --names --json` for the smallest recipe-list JSON.");
        }
        if (options.SummaryOnly)
        {
            foreach (var recipe in recipes)
                Console.WriteLine($"{recipe.Name}: {recipe.Description} (queries: {recipe.Queries.Count}, scope: {recipe.DefaultScope})");
            return CommandExitCodes.Success;
        }
        if (options.Json)
        {
            var json = JsonSerializer.Serialize(
                new SearchRecipeListJsonResult(JsonOutputContract.ApiVersion, recipes.Count, recipes),
                CliJsonSerializerContextFactory.Create(jsonOptions).SearchRecipeListJsonResult);
            return WriteJsonObjectWithOptionalByteLimit(
                json,
                options,
                "recipe list",
                "Use `cdidx recipes --names --json` or `cdidx recipes --summary-only --json` for smaller output.");
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
                if (query.StringComparisonTaxonomy is not null)
                    Console.WriteLine($"    string comparison domains: {FormatSearchRecipeStringComparisonDomains(query.StringComparisonTaxonomy)}");
                if (query.BroadCatchTaxonomy is not null)
                {
                    Console.WriteLine($"    broad catch boundaries: {string.Join(", ", query.BroadCatchTaxonomy.BoundaryCategories.Select(category => category.Name))}");
                    Console.WriteLine($"    broad catch diagnostics: {string.Join(", ", query.BroadCatchTaxonomy.DiagnosticBehaviors.Select(behavior => behavior.Name))}");
                }
            }
        }

        return CommandExitCodes.Success;
    }

    private static int WriteJsonObjectWithOptionalByteLimit(
        string json,
        QueryCommandOptions options,
        string outputDescription,
        string hint,
        string commandName = "search")
    {
        if (options.MaxJsonBytes.HasValue)
        {
            var byteCount = Encoding.UTF8.GetByteCount(json) + Environment.NewLine.Length;
            if (byteCount > options.MaxJsonBytes.Value)
            {
                WriteUsageError(
                    $"{outputDescription} JSON output is {byteCount.ToString(CultureInfo.InvariantCulture)} bytes and exceeds --max-json-bytes {options.MaxJsonBytes.Value.ToString(CultureInfo.InvariantCulture)}.",
                    GetUsageLineOrThrow(commandName),
                    hint);
                return CommandExitCodes.UsageError;
            }
        }

        Console.WriteLine(json);
        return CommandExitCodes.Success;
    }

    private static int WriteJsonPayloadWithOptionalByteLimit(
        JsonObject payload,
        QueryCommandOptions options,
        JsonSerializerOptions jsonOptions,
        string commandName,
        string outputDescription,
        string hint)
        => WriteJsonObjectWithOptionalByteLimit(
            payload.ToJsonString(EnsureJsonNodeSerializerOptions(jsonOptions)),
            options,
            outputDescription,
            hint,
            commandName);

    private static JsonSerializerOptions EnsureJsonNodeSerializerOptions(JsonSerializerOptions jsonOptions)
    {
        if (jsonOptions.TypeInfoResolver != null)
            return jsonOptions;

        return new JsonSerializerOptions(jsonOptions)
        {
            TypeInfoResolver = CliJsonSerializerContext.Default,
        };
    }

    private static bool ShouldEmitGraphLiveness(QueryCommandOptions options)
        => options.Verbose || options.Limit >= GraphLivenessLimitThreshold;

    private static void WriteGraphLiveness(
        string commandName,
        string phase,
        QueryCommandOptions options,
        string? format = null,
        string? groupBy = null,
        int? rows = null,
        int? cycleCount = null)
    {
        if (!ShouldEmitGraphLiveness(options))
            return;

        var parts = new List<string>
        {
            $"Progress: {commandName}",
            $"phase={phase}",
            $"limit={options.Limit.ToString(CultureInfo.InvariantCulture)}",
        };
        if (format != null)
            parts.Add($"format={format}");
        if (groupBy != null)
            parts.Add($"group_by={groupBy}");
        if (rows.HasValue)
            parts.Add($"rows={rows.Value.ToString(CultureInfo.InvariantCulture)}");
        if (cycleCount.HasValue)
            parts.Add($"cycles={cycleCount.Value.ToString(CultureInfo.InvariantCulture)}");
        if (options.PathPatterns.Count > 0)
            parts.Add($"path_filters={options.PathPatterns.Count.ToString(CultureInfo.InvariantCulture)}");
        if (options.ExcludePaths.Count > 0)
            parts.Add($"exclude_filters={options.ExcludePaths.Count.ToString(CultureInfo.InvariantCulture)}");
        if (options.ExcludeTests)
            parts.Add("exclude_tests=true");
        if (options.DependencyCycles)
            parts.Add("cycles=true");
        if (options.SummaryOnly)
            parts.Add("summary_only=true");
        if (options.MaxJsonBytes.HasValue)
            parts.Add($"max_json_bytes={options.MaxJsonBytes.Value.ToString(CultureInfo.InvariantCulture)}");

        CommandErrorWriter.WriteStderr(string.Join(" ", parts));
    }

    private static SearchRecipeListItemJsonResult? ToFilteredSearchRecipeListItem(SearchAuditRecipe recipe, string? filter)
    {
        if (string.IsNullOrWhiteSpace(filter))
            return ToSearchRecipeListItem(recipe);

        var recipeMatches = SearchRecipeMatchesFilter(recipe, filter);
        var queries = recipe.Queries
            .Where(query => recipeMatches || SearchRecipeQueryMatchesFilter(recipe, query, filter))
            .ToList();
        return recipeMatches || queries.Count > 0
            ? ToSearchRecipeListItem(recipe, queries)
            : null;
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
            var suggestions = BuildRecipeSelectorSuggestions(recipeSelector);
            var suggestionText = suggestions.Count > 0
                ? $" Did you mean: {string.Join(", ", suggestions)}?"
                : string.Empty;
            error = $"unknown search recipe '{recipeName}'. Available recipes: {available}.{suggestionText}";
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
            var suggestions = BuildRecipeSelectorSuggestions(directQueryName);
            var suggestionText = suggestions.Count > 0
                ? $" Suggestions across all recipes: {string.Join(", ", suggestions)}."
                : string.Empty;
            error = $"unknown recipe query '{directQueryName}' for recipe '{recipe.Name}'. Available queries: {availableQueries}.{suggestionText}";
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

    private static List<string> BuildRecipeSelectorSuggestions(string rawSelector)
    {
        var tokens = NormalizeDiscoveryTokens(rawSelector);
        if (tokens.Count == 0)
            return [];

        return SearchAuditRecipes.All
            .SelectMany(recipe => recipe.Queries.Select(query => new
            {
                Selector = $"{recipe.Name}/{query.Name}",
                Score = ScoreRecipeSelectorSuggestion(tokens, recipe, query),
            }))
            .Where(item => item.Score > 0)
            .OrderByDescending(item => item.Score)
            .ThenBy(item => item.Selector, StringComparer.Ordinal)
            .Take(3)
            .Select(item => item.Selector)
            .ToList();
    }

    private static int ScoreRecipeSelectorSuggestion(IReadOnlyList<string> tokens, SearchAuditRecipe recipe, SearchAuditRecipeQuery query)
    {
        var haystack = NormalizeDiscoveryText(string.Join(' ', BuildRecipeQuerySearchFields(recipe, query)));
        var score = 0;
        foreach (var token in tokens)
        {
            if (haystack.Contains(token, StringComparison.Ordinal))
                score += token == "sql" && haystack.Contains("sqlite", StringComparison.Ordinal) ? 80 : 25;
        }

        var normalizedSelector = NormalizeDiscoveryText($"{recipe.Name} {query.Name}");
        var normalizedRaw = string.Join(' ', tokens);
        if (normalizedSelector.Contains(normalizedRaw, StringComparison.Ordinal))
            score += 100;
        return score;
    }

    private static bool SearchRecipeMatchesFilter(SearchAuditRecipe recipe, string filter)
        => DiscoveryFilterMatches(filter,
            recipe.Name,
            recipe.Description,
            recipe.DefaultScope,
            string.Join(' ', recipe.RecommendedLabels),
            string.Join(' ', recipe.DefaultPathPatterns),
            string.Join(' ', recipe.DefaultExcludePaths));

    private static bool SearchRecipeQueryMatchesFilter(SearchAuditRecipe recipe, SearchAuditRecipeQuery query, string filter)
        => DiscoveryFilterMatches(filter, BuildRecipeQuerySearchFields(recipe, query));

    private static IEnumerable<string> BuildRecipeQuerySearchFields(SearchAuditRecipe? recipe, SearchAuditRecipeQuery query)
    {
        if (recipe != null)
        {
            yield return recipe.Name;
            yield return recipe.Description;
            yield return recipe.DefaultScope;
        }

        yield return query.Name;
        yield return query.Query;
        yield return query.Description;
        yield return query.FalsePositiveGuidance;
        yield return query.Severity;
        foreach (var label in query.RecommendedLabels)
            yield return label;
        foreach (var path in query.PathPatterns)
            yield return path;
        foreach (var path in query.ExcludePaths)
            yield return path;
        foreach (var origin in query.MatchOrigins)
            yield return origin;
        foreach (var origin in query.ExcludeOrigins)
            yield return origin;
        foreach (var kind in query.ResultKinds)
            yield return kind;
    }

    private static bool DiscoveryFilterMatches(string filter, params string[] fields)
        => DiscoveryFilterMatches(filter, (IEnumerable<string>)fields);

    private static bool DiscoveryFilterMatches(string filter, IEnumerable<string> fields)
    {
        var tokens = NormalizeDiscoveryTokens(filter);
        if (tokens.Count == 0)
            return true;
        var haystack = NormalizeDiscoveryText(string.Join(' ', fields));
        return tokens.All(token => haystack.Contains(token, StringComparison.Ordinal));
    }

    private static List<string> NormalizeDiscoveryTokens(string value)
        => NormalizeDiscoveryText(value)
            .Split(' ', StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries)
            .Distinct(StringComparer.Ordinal)
            .ToList();

    private static string NormalizeDiscoveryText(string value)
    {
        var builder = new StringBuilder(value.Length);
        var previousWasSpace = true;
        foreach (var ch in value)
        {
            if (char.IsLetterOrDigit(ch))
            {
                builder.Append(char.ToLowerInvariant(ch));
                previousWasSpace = false;
            }
            else if (!previousWasSpace)
            {
                builder.Append(' ');
                previousWasSpace = true;
            }
        }

        return builder.ToString().Trim();
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
            if (options.ResultsOnly || options.SearchFields != null || (options.Json && options.JsonOutputFormatExplicit && options.JsonOutputFormat == JsonOutputFormatNdjson))
            {
                var rowQueryResults = CollectSearchRecipeQueryResults(reader, selection.Queries, scope, options, userExact, out _);
                WriteRecipeSearchResultRows(
                    recipe.Name,
                    rowQueryResults,
                    options,
                    GetCompactJsonOptions(jsonOptions),
                    out _,
                    out _);
                return CommandExitCodes.Success;
            }

            if (options.OutputFormat == OutputFormatCompact)
            {
                var compactQueryResults = CollectSearchRecipeCompactQueryResults(reader, selection.Queries, scope, options, userExact, out var compactTotal);
                var compactPayload = BuildSearchRecipeCompactRunPayload(
                    recipe,
                    selection.Queries,
                    scope,
                    options,
                    jsonOptions,
                    compactQueryResults,
                    compactTotal);
                var compactJson = compactPayload.ToJsonString(GetJsonNodeSerializationOptions(jsonOptions));
                return WriteJsonObjectWithOptionalByteLimit(
                    compactJson,
                    options,
                    "recipe compact",
                    "Reduce --limit or --total-limit, select one child query with --recipe <recipe>/<query>, stream rows with --json=ndjson, or increase --max-json-bytes.");
            }

            var queryResults = CollectSearchRecipeQueryResults(reader, selection.Queries, scope, options, userExact, out var total);

            if (options.Json)
            {
                var json = JsonSerializer.Serialize(
                        new SearchRecipeRunJsonResult(
                            JsonOutputContract.ApiVersion,
                            ToSearchRecipeListItem(recipe, selection.Queries),
                            scope,
                            selection.Queries.Count,
                            total,
                            BuildSearchRecipeRunSummary(queryResults, options.Limit, options.TotalLimit, total),
                            queryResults),
                        CliJsonSerializerContextFactory.Create(jsonOptions).SearchRecipeRunJsonResult);
                return WriteJsonObjectWithOptionalByteLimit(
                    json,
                    options,
                    "recipe search",
                    "Reduce --limit, use --snippet-lines 0, or increase --max-json-bytes.");
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
                if (queryResult.StringComparisonTaxonomy is not null)
                    Console.WriteLine($"string comparison domains: {FormatSearchRecipeStringComparisonDomains(queryResult.StringComparisonTaxonomy)}");
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

    private static JsonObject BuildSearchRecipeCompactRunPayload(
        SearchAuditRecipe recipe,
        IReadOnlyList<SearchAuditRecipeQuery> selectedQueries,
        SearchRecipeScopeJsonResult scope,
        QueryCommandOptions options,
        JsonSerializerOptions jsonOptions,
        List<SearchRecipeCompactQueryResultJsonResult> compactQueryResults,
        int compactTotal)
    {
        var run = new SearchRecipeCompactRunJsonResult(
            JsonOutputContract.ApiVersion,
            new SearchRecipeCompactListItemJsonResult(
                recipe.Name,
                recipe.Description,
                recipe.DefaultScope,
                selectedQueries.Count,
                recipe.RecommendedLabels,
                recipe.DefaultPathPatterns,
                recipe.DefaultExcludePaths),
            scope,
            selectedQueries.Count,
            compactTotal,
            BuildSearchRecipeRunSummary(compactQueryResults, options.Limit, options.TotalLimit, compactTotal),
            compactQueryResults);
        var payload = JsonSerializer.SerializeToNode(
            run,
            CliJsonSerializerContextFactory.Create(jsonOptions).SearchRecipeCompactRunJsonResult)!.AsObject();
        payload["compact"] = true;
        AddJsonByteLimitField(payload, options);
        payload["truncation"] = BuildSearchRecipeCompactTruncationMetadata(compactQueryResults, options);
        payload["next_commands"] = BuildSearchRecipeCompactNextCommands(recipe.Name, compactQueryResults, options);
        return payload;
    }

    private static JsonObject BuildSearchRecipeCompactTruncationMetadata(
        IReadOnlyList<SearchRecipeCompactQueryResultJsonResult> queryResults,
        QueryCommandOptions options)
    {
        var queries = new JsonArray();
        var truncatedQueryCount = 0;
        var emittedResultCount = 0;
        var minimumMatchedResultCount = 0;
        var minimumOmittedResultCount = 0;
        foreach (var query in queryResults)
        {
            if (query.Truncated)
                truncatedQueryCount++;
            emittedResultCount += query.EmittedCount;
            minimumMatchedResultCount += query.MinimumMatchedCount;
            minimumOmittedResultCount += query.MinimumOmittedResultCount;
            queries.Add(new JsonObject
            {
                ["name"] = query.Name,
                ["returned"] = query.Results.Count,
                ["emitted_count"] = query.EmittedCount,
                ["minimum_matched_count"] = query.MinimumMatchedCount,
                ["minimum_omitted_result_count"] = query.MinimumOmittedResultCount,
                ["result_limit"] = query.ResultLimit,
                ["truncated"] = query.Truncated,
                ["next_cursor"] = query.NextCursor,
            });
        }

        var metadata = new JsonObject
        {
            ["selected_query_count"] = queryResults.Count,
            ["limit_per_query"] = options.Limit,
            ["total_limit"] = options.TotalLimit,
            ["emitted_result_count"] = emittedResultCount,
            ["minimum_matched_result_count"] = minimumMatchedResultCount,
            ["minimum_omitted_result_count"] = minimumOmittedResultCount,
            ["truncated_query_count"] = truncatedQueryCount,
            ["queries"] = queries,
        };
        if (options.MaxJsonBytes.HasValue)
            metadata["aggregate_byte_limit"] = options.MaxJsonBytes.Value;
        return metadata;
    }

    private static JsonArray BuildSearchRecipeCompactNextCommands(
        string recipeName,
        IReadOnlyList<SearchRecipeCompactQueryResultJsonResult> queryResults,
        QueryCommandOptions options)
    {
        var commands = new JsonArray();
        foreach (var query in queryResults.Where(query => query.NextCursor != null).Take(3))
        {
            commands.Add(BuildSearchRecipeCompactReplayCommand(
                $"{recipeName}/{query.Name}",
                options,
                query.NextCursor,
                resultsOnly: false,
                includeRecipeQuerySelectors: false));
        }

        if (commands.Count == 0 && queryResults.Count > 1)
        {
            commands.Add(BuildSearchRecipeCompactReplayCommand(
                $"{recipeName}/{queryResults[0].Name}",
                options,
                cursor: null,
                resultsOnly: false,
                includeRecipeQuerySelectors: false));
        }
        var resultsOnlySelector = queryResults.Count == 1
            ? $"{recipeName}/{queryResults[0].Name}"
            : recipeName;
        commands.Add(BuildSearchRecipeCompactReplayCommand(
            resultsOnlySelector,
            options,
            cursor: null,
            resultsOnly: true,
            includeRecipeQuerySelectors: queryResults.Count != 1));
        return commands;
    }

    private static string BuildSearchRecipeCompactReplayCommand(
        string recipeSelector,
        QueryCommandOptions options,
        string? cursor,
        bool resultsOnly,
        bool includeRecipeQuerySelectors)
    {
        var args = new List<string>
        {
            "cdidx",
            "search",
            "--recipe",
            recipeSelector,
        };
        if (resultsOnly)
        {
            args.Add("--json=ndjson");
            args.Add("--results-only");
        }
        else
        {
            args.Add("--format");
            args.Add(OutputFormatCompact);
        }
        if (!string.IsNullOrWhiteSpace(cursor))
            AddReplayValueOption(args, "--cursor", cursor);
        AddReplayValueOption(args, "--limit", options.Limit.ToString(CultureInfo.InvariantCulture));
        AddSearchRecipeCompactReplayOptions(args, options, includeRecipeQuerySelectors);
        var command = string.Join(" ", args.Select(QuoteReplayShellArg));
        return resultsOnly && !options.MaxJsonBytes.HasValue
            ? command + " --max-json-bytes <bytes>"
            : command;
    }

    private static void AddSearchRecipeCompactReplayOptions(List<string> args, QueryCommandOptions options, bool includeRecipeQuerySelectors)
    {
        if (options.DbPathExplicit)
            AddReplayValueOption(args, "--db", options.DbPath);
        if (options.SourceOnly)
            args.Add("--source-only");
        else if (options.AuditScopeExplicit)
            AddReplayValueOption(args, "--audit-scope", options.AuditScope);
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
        if (options.Prefix)
            args.Add("--prefix");
        foreach (var guardFilter in options.GuardFilters)
            AddReplayValueOption(args, BuildSearchGuardReplayOptionName(guardFilter), guardFilter.Query);
        if (options.GuardFilters.Count > 0 && options.GuardWindow != DbReader.DefaultSearchGuardWindow)
            AddReplayValueOption(args, "--guard-window", options.GuardWindow.ToString(CultureInfo.InvariantCulture));
        if (options.GuardFilters.Count > 0 && options.GuardScope != SearchGuardScope.Window)
            AddReplayValueOption(args, "--guard-scope", FormatSearchGuardScope(options.GuardScope));
        if (options.ExcludeComments)
            args.Add("--exclude-comments");
        if (options.ExcludeStrings)
            args.Add("--exclude-strings");
        if (options.ExcludeFixtures)
            args.Add("--exclude-fixtures");
        foreach (var origin in options.MatchOrigins)
            AddReplayValueOption(args, "--origin", origin);
        foreach (var origin in options.ExcludeOrigins)
            AddReplayValueOption(args, "--exclude-origin", origin);
        foreach (var kind in options.ResultKinds)
            AddReplayValueOption(args, "--result-kind", kind);
        if (options.TotalLimit.HasValue)
            AddReplayValueOption(args, "--total-limit", options.TotalLimit.Value.ToString(CultureInfo.InvariantCulture));
        if (options.MaxJsonBytes.HasValue)
            AddReplayValueOption(args, "--max-json-bytes", options.MaxJsonBytes.Value.ToString(CultureInfo.InvariantCulture));
        if (options.ShowExcluded)
            args.Add("--show-excluded");
        if (includeRecipeQuerySelectors)
        {
            foreach (var includeQuery in options.IncludeRecipeQueries)
                AddReplayValueOption(args, "--include-query", includeQuery);
            foreach (var excludeQuery in options.ExcludeRecipeQueries)
                AddReplayValueOption(args, "--exclude-query", excludeQuery);
        }
    }

    private static int RunSearchRecipeAggregation(QueryCommandOptions options, JsonSerializerOptions jsonOptions, bool userExact)
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
        var groupBy = NormalizeSearchAggregationKey(options.GroupBy ?? options.CountBy ?? options.UniqueBy!);
        var uniqueOnly = options.UniqueBy != null;
        var mode = uniqueOnly ? "unique" : options.GroupBy != null ? "group_by" : "count_by";
        return WithDb(options, jsonOptions, reader =>
        {
            var queryResults = CollectSearchRecipeAggregationResults(
                reader,
                selection.Queries,
                scope,
                options,
                userExact,
                groupBy,
                out var total,
                out var fileCount);

            if (options.Json)
            {
                var json = JsonSerializer.Serialize(
                        new SearchRecipeAggregationRunJsonResult(
                            JsonOutputContract.ApiVersion,
                            ToSearchRecipeListItem(recipe, selection.Queries),
                            scope,
                            mode,
                            groupBy,
                            uniqueOnly,
                            selection.Queries.Count,
                            total,
                            fileCount,
                            queryResults),
                        CliJsonSerializerContextFactory.Create(jsonOptions).SearchRecipeAggregationRunJsonResult);
                return WriteJsonObjectWithOptionalByteLimit(
                    json,
                    options,
                    "recipe aggregation",
                    "Reduce --limit or increase --max-json-bytes.");
            }
            else
            {
                foreach (var query in queryResults)
                {
                    Console.WriteLine($"[{query.Name}] {query.Query}");
                    if (uniqueOnly)
                    {
                        foreach (var group in query.Groups)
                            Console.WriteLine(group.Key);
                        var truncation = query.GroupsTruncated
                            ? $"showing {query.ReturnedGroups} of {query.TotalGroups}"
                            : query.Groups.Count.ToString(CultureInfo.InvariantCulture);
                        CommandErrorWriter.WriteStderr($"({truncation} unique {groupBy} values from {query.Count} results in {query.FileCount} files)");
                    }
                    else
                    {
                        WriteSearchGroupedCounts(groupBy, query.Groups, query.Count, query.FileCount, query.TotalGroups);
                    }
                    Console.WriteLine();
                }
                CommandErrorWriter.WriteStderr($"({total} recipe results in {fileCount} files across {selection.Queries.Count} queries; {mode} {groupBy})");
            }

            return CommandExitCodes.Success;
        });
    }

    private static void WriteRecipeSearchResultRows(
        string recipeName,
        IReadOnlyList<SearchRecipeQueryResultJsonResult> queryResults,
        QueryCommandOptions options,
        JsonSerializerOptions ndjsonOptions,
        out int emittedCount,
        out bool interrupted)
    {
        emittedCount = 0;
        interrupted = false;
        var bytesWritten = 0;
        foreach (var query in queryResults)
        {
            foreach (var result in query.Results)
            {
                JsonObject payload = options.SearchFields != null
                    ? BuildProjectedSearchResult(result, options.SearchFields, query.Name, recipeName)
                    : BuildRecipeSearchResultRow(recipeName, query.Name, result, ndjsonOptions);
                var line = payload.ToJsonString(ndjsonOptions);
                if (WouldExceedJsonByteLimit(options, bytesWritten, line, out interrupted))
                    return;
                Console.WriteLine(line);
                bytesWritten += Encoding.UTF8.GetByteCount(line) + Environment.NewLine.Length;
                emittedCount++;
            }
        }
    }

    private static JsonObject BuildRecipeSearchResultRow(
        string recipeName,
        string queryName,
        CompactSearchResult result,
        JsonSerializerOptions jsonOptions)
    {
        var payload = JsonSerializer.SerializeToNode(result, jsonOptions)?.AsObject() ?? [];
        payload["recipe"] = recipeName;
        payload["query_name"] = queryName;
        return payload;
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
            var fullRecipeMetadata = options.SummaryOnly ? null : ToSearchRecipeListItem(recipe, selection.Queries);
            var recipeSummaryMetadata = options.SummaryOnly ? ToSearchRecipeCompactListItem(recipe, selection.Queries) : null;
            var json = JsonSerializer.Serialize(
                new SearchIssueDraftExportJsonResult(
                    JsonOutputContract.ApiVersion,
                    fullRecipeMetadata,
                    recipeSummaryMetadata,
                    options.SummaryOnly ? "summary" : "full",
                    scope,
                    selection.Queries.Count,
                    total,
                    BuildSearchRecipeQueryFreshness(queryResults),
                    drafts.Count,
                    new SuggestionIssueDraftPreflightSummaryJsonResult(
                        preflight.Checked,
                        preflight.Source,
                        preflight.OpenIssueCount,
                        options.DuplicateConfidence,
                        options.DuplicateThreshold),
                    drafts),
                CliJsonSerializerContextFactory.Create(jsonOptions).SearchIssueDraftExportJsonResult);
            return WriteJsonObjectWithOptionalByteLimit(
                json,
                options,
                "issue-draft",
                "Reduce --limit, use --snippet-lines 0, or increase --max-json-bytes.");
        });
    }

    private static int RunSearchRecipeCount(QueryCommandOptions options, JsonSerializerOptions jsonOptions, bool userExact)
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
        return WithDb(options, jsonOptions, reader =>
        {
            var queryCounts = CountSearchRecipeQueryResults(
                reader,
                selection.Queries,
                scope,
                options,
                userExact,
                out var total,
                out var fileCount);

            if (options.Json)
            {
                if (options.SummaryOnly)
                {
                    var summaryQueries = queryCounts
                        .Select(query => new SearchRecipeCountSummaryQueryJsonResult(
                            query.Name,
                            query.Count,
                            query.FileCount))
                        .ToList();
                    var summaryJson = JsonSerializer.Serialize(
                        new SearchRecipeCountSummaryRunJsonResult(
                            JsonOutputContract.ApiVersion,
                            recipe.Name,
                            scope.Name,
                            selection.Queries.Count,
                            total,
                            fileCount,
                            BuildSearchRecipeQueryFreshness(queryCounts),
                            summaryQueries),
                        CliJsonSerializerContextFactory.Create(jsonOptions).SearchRecipeCountSummaryRunJsonResult);
                    return WriteJsonObjectWithOptionalByteLimit(
                        summaryJson,
                        options,
                        "recipe count summary",
                        "Use a larger --max-json-bytes value or narrow the recipe/query selection.");
                }

                var json = JsonSerializer.Serialize(
                    new SearchRecipeCountRunJsonResult(
                        JsonOutputContract.ApiVersion,
                        ToSearchRecipeListItem(recipe, selection.Queries),
                        scope,
                        selection.Queries.Count,
                        total,
                        fileCount,
                        queryCounts),
                    CliJsonSerializerContextFactory.Create(jsonOptions).SearchRecipeCountRunJsonResult);
                return WriteJsonObjectWithOptionalByteLimit(
                    json,
                    options,
                    "recipe count",
                    "Use `--summary-only` to omit recipe metadata from count output.");
            }
            else
            {
                Console.WriteLine(total.ToString(CultureInfo.InvariantCulture));
                CommandErrorWriter.WriteStderr($"({total} recipe results in {fileCount} files across {selection.Queries.Count} queries)");
            }

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
                [],
                [],
                exact,
                SearchAuditRecipes.DefaultQuerySeverity,
                [],
                [],
                [],
                [],
                [],
                null,
                null,
                null,
                rows.Count,
                rows.Count,
                rows.Count,
                0,
                options.Limit,
                0,
                BuildSearchRecipeTopFiles(rows),
                false,
                null,
                rows.Select(row => row.Compact).ToList());
            var drafts = rows.Count == 0
                ? []
                : new List<SearchIssueDraftJsonResult> { ToAdHocSearchIssueDraft(options, queryResult, preflight) };

            var json = JsonSerializer.Serialize(
                new SearchIssueDraftExportJsonResult(
                    JsonOutputContract.ApiVersion,
                    null,
                    null,
                    "none",
                    null,
                    1,
                    rows.Count,
                    null,
                    drafts.Count,
                    new SuggestionIssueDraftPreflightSummaryJsonResult(
                        preflight.Checked,
                        preflight.Source,
                        preflight.OpenIssueCount,
                        options.DuplicateConfidence,
                        options.DuplicateThreshold),
                    drafts),
                CliJsonSerializerContextFactory.Create(jsonOptions).SearchIssueDraftExportJsonResult);
            return WriteJsonObjectWithOptionalByteLimit(
                json,
                options,
                "issue-draft",
                "Reduce --limit, use --snippet-lines 0, or increase --max-json-bytes.");
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
            var resultLimit = GetSearchRecipeEffectiveResultLimit(options, total);
            var guardFilters = BuildSearchRecipeGuardFilters(options, recipeQuery);
            var results = reader.Search(
                recipeQuery.Query,
                FetchLimitForSearchEnvelope(resultLimit),
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
                guardFilters: guardFilters,
                guardWindow: options.GuardWindow,
                guardScope: options.GuardScope,
                requiredPathPatterns: GetSearchRecipeRequiredPathPatterns(options, recipeQuery));
            results = ApplySearchRecipeFileRejectQueries(reader, results, options, recipeQuery);
            var rows = BuildSearchDisplayRows(results, options, exact, recipeQuery.Query, rawFtsOverride: false, recipeQuery: recipeQuery);
            var availableCount = rows.Count;
            var truncated = TrimSearchRowsToRequestedLimit(rows, resultLimit);
            var minimumOmitted = truncated ? Math.Max(1, availableCount - rows.Count) : 0;
            total += rows.Count;
            queryResults.Add(new SearchRecipeQueryResultJsonResult(
                recipeQuery.Name,
                recipeQuery.Query,
                recipeQuery.Description,
                recipeQuery.RecommendedLabels,
                recipeQuery.FalsePositiveGuidance,
                [.. recipeQuery.RiskEvidence],
                ToSearchRecipeGuardFilterJsonResults(recipeQuery.GuardFilters),
                exact,
                recipeQuery.Severity,
                [.. recipeQuery.PathPatterns],
                [.. recipeQuery.ExcludePaths],
                [.. recipeQuery.MatchOrigins],
                [.. recipeQuery.ExcludeOrigins],
                [.. recipeQuery.ResultKinds],
                recipeQuery.StringComparisonTaxonomy,
                recipeQuery.BroadCatchTaxonomy,
                recipeQuery.NullableContractTaxonomy,
                rows.Count,
                rows.Count,
                rows.Count + minimumOmitted,
                minimumOmitted,
                resultLimit,
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
            var resultLimit = GetSearchRecipeEffectiveResultLimit(options, total);
            var guardFilters = BuildSearchRecipeGuardFilters(options, recipeQuery);
            var results = reader.Search(
                recipeQuery.Query,
                FetchLimitForSearchEnvelope(resultLimit),
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
                guardFilters: guardFilters,
                guardWindow: options.GuardWindow,
                guardScope: options.GuardScope,
                requiredPathPatterns: GetSearchRecipeRequiredPathPatterns(options, recipeQuery));
            results = ApplySearchRecipeFileRejectQueries(reader, results, options, recipeQuery);
            var rows = BuildSearchDisplayRows(results, options, exact, recipeQuery.Query, recipeQuery: recipeQuery);
            var availableCount = rows.Count;
            var truncated = TrimSearchRowsToRequestedLimit(rows, resultLimit);
            var minimumOmitted = truncated ? Math.Max(1, availableCount - rows.Count) : 0;
            total += rows.Count;
            queryResults.Add(new SearchRecipeCompactQueryResultJsonResult(
                recipeQuery.Name,
                recipeQuery.Query,
                recipeQuery.Description,
                recipeQuery.Severity,
                [.. recipeQuery.RiskEvidence],
                ToSearchRecipeGuardFilterJsonResults(recipeQuery.GuardFilters),
                [.. recipeQuery.PathPatterns],
                [.. recipeQuery.ExcludePaths],
                [.. recipeQuery.MatchOrigins],
                [.. recipeQuery.ExcludeOrigins],
                [.. recipeQuery.ResultKinds],
                recipeQuery.StringComparisonTaxonomy,
                recipeQuery.BroadCatchTaxonomy,
                rows.Count,
                rows.Count,
                rows.Count + minimumOmitted,
                minimumOmitted,
                resultLimit,
                minimumOmitted,
                BuildSearchRecipeTopFiles(rows),
                truncated,
                truncated && rows.Count > 0 ? FormatSearchCursor(rows[^1].Result) : null,
                rows.Select(row => new SearchRecipeCompactResultJsonResult(
                    row.Result.Path,
                    row.Result.Lang,
                    row.Result.Visibility,
                    [.. recipeQuery.RiskEvidence],
                    row.Result.StartLine,
                    row.Result.EndLine,
                    row.Compact.MatchLines,
                    row.Compact.EnclosingSymbolName,
                    row.Compact.EnclosingSymbolKind)).ToList()));
        }

        return queryResults;
    }

    private static List<SearchRecipeCountQueryJsonResult> CountSearchRecipeQueryResults(
        DbReader reader,
        IReadOnlyList<SearchAuditRecipeQuery> recipeQueries,
        SearchRecipeScopeJsonResult scope,
        QueryCommandOptions options,
        bool userExact,
        out int total,
        out int fileCount)
    {
        var queryCounts = new List<SearchRecipeCountQueryJsonResult>();
        var paths = new HashSet<string>(StringComparer.Ordinal);
        total = 0;
        foreach (var recipeQuery in recipeQueries)
        {
            var exact = userExact || recipeQuery.ExactSubstring;
            var queryScope = BuildSearchRecipeQueryScope(scope, recipeQuery);
            var guardFilters = BuildSearchRecipeGuardFilters(options, recipeQuery);
            var results = reader.Search(
                recipeQuery.Query,
                int.MaxValue,
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
                guardFilters: guardFilters,
                guardWindow: options.GuardWindow,
                guardScope: options.GuardScope,
                requiredPathPatterns: GetSearchRecipeRequiredPathPatterns(options, recipeQuery));
            results = ApplySearchRecipeFileRejectQueries(reader, results, options, recipeQuery);
            var rows = BuildSearchDisplayRows(results, options, exact, recipeQuery.Query, rawFtsOverride: false, recipeQuery: recipeQuery);
            var count = rows.Count;
            var fileCountForQuery = rows.Select(row => row.Result.Path).Distinct(StringComparer.Ordinal).Count();
            foreach (var path in rows.Select(row => row.Result.Path))
                paths.Add(path);

            total += count;
            queryCounts.Add(new SearchRecipeCountQueryJsonResult(
                recipeQuery.Name,
                recipeQuery.Query,
                recipeQuery.Description,
                recipeQuery.Severity,
                count,
                count,
                0,
                count,
                fileCountForQuery,
                false,
                BuildSearchRecipeTopFiles(rows)));
        }

        fileCount = paths.Count;
        return queryCounts;
    }

    private static List<SearchRecipeAggregationQueryJsonResult> CollectSearchRecipeAggregationResults(
        DbReader reader,
        IReadOnlyList<SearchAuditRecipeQuery> recipeQueries,
        SearchRecipeScopeJsonResult scope,
        QueryCommandOptions options,
        bool userExact,
        string groupBy,
        out int total,
        out int fileCount)
    {
        var queryResults = new List<SearchRecipeAggregationQueryJsonResult>();
        var paths = new HashSet<string>(StringComparer.Ordinal);
        total = 0;
        foreach (var recipeQuery in recipeQueries)
        {
            var exact = userExact || recipeQuery.ExactSubstring;
            var queryScope = BuildSearchRecipeQueryScope(scope, recipeQuery);
            var guardFilters = BuildSearchRecipeGuardFilters(options, recipeQuery);
            var results = reader.Search(
                recipeQuery.Query,
                int.MaxValue,
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
                guardFilters: guardFilters,
                guardWindow: options.GuardWindow,
                guardScope: options.GuardScope,
                requiredPathPatterns: GetSearchRecipeRequiredPathPatterns(options, recipeQuery));
            results = ApplySearchRecipeFileRejectQueries(reader, results, options, recipeQuery);
            var rows = BuildSearchDisplayRows(results, options, exact, recipeQuery.Query, rawFtsOverride: false, recipeQuery: recipeQuery);
            foreach (var path in rows.Select(row => row.Result.Path))
                paths.Add(path);

            var groups = BuildSearchGroupedCounts(groupBy, rows);
            var selection = ApplySearchGroupOutputSelection(groups, options);
            total += rows.Count;
            queryResults.Add(new SearchRecipeAggregationQueryJsonResult(
                recipeQuery.Name,
                recipeQuery.Query,
                recipeQuery.Description,
                recipeQuery.Severity,
                rows.Count,
                rows.Select(row => row.Result.Path).Distinct(StringComparer.Ordinal).Count(),
                selection.Groups.Count,
                selection.TotalGroups,
                selection.Truncated,
                options.Limit,
                selection.Groups));
        }

        fileCount = paths.Count;
        return queryResults;
    }

    private static IReadOnlyList<SearchGuardFilter> BuildSearchRecipeGuardFilters(QueryCommandOptions options, SearchAuditRecipeQuery recipeQuery)
    {
        if (recipeQuery.GuardFilters.Count == 0)
            return options.GuardFilters;
        if (options.GuardFilters.Count == 0)
            return recipeQuery.GuardFilters;

        var guardFilters = new List<SearchGuardFilter>(recipeQuery.GuardFilters.Count + options.GuardFilters.Count);
        guardFilters.AddRange(recipeQuery.GuardFilters);
        guardFilters.AddRange(options.GuardFilters);
        return guardFilters;
    }

    private static SearchRecipeRunSummaryJsonResult BuildSearchRecipeRunSummary(
        IReadOnlyList<SearchRecipeQueryResultJsonResult> queryResults,
        int limitPerQuery,
        int? totalLimit,
        int emittedResultCount)
        => new(
            limitPerQuery,
            totalLimit,
            emittedResultCount,
            queryResults.Count(query => query.Truncated),
            queryResults.Sum(query => query.MinimumOmittedResultCount),
            BuildSearchRecipeQueryFreshness(queryResults),
            queryResults.Any(query => query.Truncated && !string.IsNullOrWhiteSpace(query.NextCursor)),
            "When a query is truncated, rerun a single child query with --recipe <recipe>/<query> --cursor <next_cursor> to page the next result set.");

    private static SearchRecipeRunSummaryJsonResult BuildSearchRecipeRunSummary(
        IReadOnlyList<SearchRecipeCompactQueryResultJsonResult> queryResults,
        int limitPerQuery,
        int? totalLimit,
        int emittedResultCount)
        => new(
            limitPerQuery,
            totalLimit,
            emittedResultCount,
            queryResults.Count(query => query.Truncated),
            queryResults.Sum(query => query.MinimumOmittedResultCount),
            BuildSearchRecipeQueryFreshness(queryResults),
            queryResults.Any(query => query.Truncated && !string.IsNullOrWhiteSpace(query.NextCursor)),
            "When a query is truncated, rerun a single child query with --recipe <recipe>/<query> --cursor <next_cursor> to page the next result set.");

    private static SearchRecipeQueryFreshnessJsonResult BuildSearchRecipeQueryFreshness(IReadOnlyList<SearchRecipeQueryResultJsonResult> queryResults)
        => BuildSearchRecipeQueryFreshness(queryResults.Select(query => (query.Name, query.MinimumMatchedCount)));

    private static SearchRecipeQueryFreshnessJsonResult BuildSearchRecipeQueryFreshness(IReadOnlyList<SearchRecipeCompactQueryResultJsonResult> queryResults)
        => BuildSearchRecipeQueryFreshness(queryResults.Select(query => (query.Name, query.MinimumMatchedCount)));

    private static SearchRecipeQueryFreshnessJsonResult BuildSearchRecipeQueryFreshness(IReadOnlyList<SearchRecipeCountQueryJsonResult> queryResults)
        => BuildSearchRecipeQueryFreshness(queryResults.Select(query => (query.Name, query.Count)));

    private static SearchRecipeQueryFreshnessJsonResult BuildSearchRecipeQueryFreshness(IEnumerable<(string Name, int Count)> queryResults)
    {
        var results = queryResults.ToList();
        var staleQueryNames = results
            .Where(query => query.Count == 0)
            .Select(query => query.Name)
            .ToList();
        return new(
            results.Count(query => query.Count > 0),
            staleQueryNames.Count,
            staleQueryNames);
    }

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

    private static List<SearchRecipeTopFileJsonResult> BuildSearchRecipeTopFiles(IReadOnlyList<SearchFileCountResult> fileCounts)
        => fileCounts
            .Select(file => new SearchRecipeTopFileJsonResult(file.Path, file.Count))
            .OrderByDescending(file => file.Count)
            .ThenBy(file => file.Path, StringComparer.Ordinal)
            .Take(10)
            .ToList();

    private static IReadOnlyList<string>? GetSearchRecipeRequiredPathPatterns(QueryCommandOptions options, SearchAuditRecipeQuery recipeQuery)
        => options.PathPatterns.Count > 0 && recipeQuery.PathPatterns.Count > 0
            ? options.PathPatterns
            : null;

    private static int GetSearchRecipeEffectiveResultLimit(QueryCommandOptions options, int emittedSoFar)
    {
        if (!options.TotalLimit.HasValue)
            return options.Limit;

        var remaining = options.TotalLimit.Value - emittedSoFar;
        if (remaining <= 0)
            return 0;

        return Math.Min(options.Limit, remaining);
    }

    private static int FetchLimitForSearchEnvelope(int limit)
    {
        if (limit <= 0)
            return 1;

        var requested = (long)limit + 1;
        var overFetched = requested * SearchEnvelopeOverFetchFactor;
        var candidateLimit = Math.Max(SearchEnvelopeMinCandidates, Math.Max(requested, overFetched));
        return (int)Math.Min(SearchEnvelopeMaxCandidates, candidateLimit);
    }

    internal static int FetchLimitForSearchEnvelopeForTests(int limit) => FetchLimitForSearchEnvelope(limit);

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

    private static List<SearchResult> ApplySearchRecipeFileRejectQueries(
        DbReader reader,
        List<SearchResult> results,
        QueryCommandOptions options,
        SearchAuditRecipeQuery recipeQuery)
    {
        if (recipeQuery.RejectFileQueries.Count == 0 || results.Count == 0)
            return results;

        var rejectedPaths = new Dictionary<string, bool>(StringComparer.Ordinal);
        return results
            .Where(result => !ShouldRejectSearchRecipeFile(reader, result.Path, options, recipeQuery, rejectedPaths))
            .ToList();
    }

    private static bool ShouldRejectSearchRecipeFile(
        DbReader reader,
        string path,
        QueryCommandOptions options,
        SearchAuditRecipeQuery recipeQuery,
        Dictionary<string, bool> rejectedPaths)
    {
        if (rejectedPaths.TryGetValue(path, out var rejected))
            return rejected;

        foreach (var rejectQuery in recipeQuery.RejectFileQueries)
        {
            var matches = reader.Search(
                rejectQuery,
                1,
                options.Lang,
                rawQuery: false,
                pathPatterns: [path],
                excludePathPatterns: null,
                excludeTests: false,
                deduplicate: true,
                since: options.Since,
                exact: true,
                prefix: false,
                visibilityRank: false);
            if (matches.Count == 0)
                continue;

            rejectedPaths[path] = true;
            return true;
        }

        rejectedPaths[path] = false;
        return false;
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
        var evidence = BuildSearchIssueDraftEvidence(queryResult, includeSnippets: options.SnippetLines > 0);
        var missingLabels = BuildMissingIssueDraftLabels(labels, preflight);
        var labelWarning = BuildIssueDraftLabelWarning(missingLabels, preflight);
        var duplicateProbeTriage = BuildSearchIssueDraftTriage(queryResult, preflight.Checked, 0);
        var duplicateProbeBody = BuildSearchIssueDraftBody(recipe, queryResult, evidencePaths, evidence, duplicateProbeTriage, options);
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
            missingLabels,
            labelWarning,
            evidencePaths,
            evidence,
            triage,
            BuildSearchIssueDraftBody(recipe, queryResult, evidencePaths, evidence, triage, options),
            new SearchIssueDraftSourceJsonResult(
                recipe.Name,
                queryResult.Name,
                queryResult.Query,
                queryResult.Description,
                queryResult.FalsePositiveGuidance,
                queryResult.RiskEvidence,
                queryResult.ExactSubstring,
                queryResult.Count,
                queryResult.ResultLimit,
                queryResult.OmittedCount,
                queryResult.MinimumOmittedResultCount,
                queryResult.Truncated,
                queryResult.NextCursor),
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
        var evidence = BuildSearchIssueDraftEvidence(queryResult, includeSnippets: options.SnippetLines > 0);
        var missingLabels = BuildMissingIssueDraftLabels(labels, preflight);
        var labelWarning = BuildIssueDraftLabelWarning(missingLabels, preflight);
        var duplicateProbeTriage = BuildSearchIssueDraftTriage(queryResult, preflight.Checked, 0);
        var duplicateProbeBody = BuildAdHocSearchIssueDraftBody(queryResult, evidencePaths, evidence, duplicateProbeTriage, options);
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
            missingLabels,
            labelWarning,
            evidencePaths,
            evidence,
            triage,
            BuildAdHocSearchIssueDraftBody(queryResult, evidencePaths, evidence, triage, options),
            new SearchIssueDraftSourceJsonResult(
                null,
                null,
                queryResult.Query,
                queryResult.Description,
                queryResult.FalsePositiveGuidance,
                queryResult.RiskEvidence,
                queryResult.ExactSubstring,
                queryResult.Count,
                queryResult.ResultLimit,
                queryResult.OmittedCount,
                queryResult.MinimumOmittedResultCount,
                queryResult.Truncated,
                queryResult.NextCursor),
            new SuggestionIssueDraftDuplicatePreflightJsonResult(
                preflight.Checked,
                duplicateMatches.Count,
                duplicateMatches));
    }

    private static List<string> BuildMissingIssueDraftLabels(
        IReadOnlyList<string> labels,
        IssueDuplicatePreflight preflight)
    {
        if (!preflight.RepositoryLabelsChecked || labels.Count == 0)
            return [];

        var repositoryLabels = preflight.RepositoryLabels.ToHashSet(StringComparer.OrdinalIgnoreCase);
        return labels
            .Where(label => !repositoryLabels.Contains(label))
            .Distinct(StringComparer.OrdinalIgnoreCase)
            .OrderBy(label => label, StringComparer.OrdinalIgnoreCase)
            .ToList();
    }

    private static string? BuildIssueDraftLabelWarning(
        IReadOnlyList<string> missingLabels,
        IssueDuplicatePreflight preflight)
    {
        if (missingLabels.Count == 0)
            return null;

        var source = string.IsNullOrWhiteSpace(preflight.Source)
            ? "repository label preflight"
            : preflight.Source;
        return $"Repository label validation against {source} found missing label(s): {string.Join(", ", missingLabels)}.";
    }

    private static List<SearchIssueDraftEvidenceJsonResult> BuildSearchIssueDraftEvidence(
        SearchRecipeQueryResultJsonResult queryResult,
        bool includeSnippets)
    {
        var evidence = new List<SearchIssueDraftEvidenceJsonResult>();
        var seen = new HashSet<string>(StringComparer.Ordinal);
        foreach (var result in queryResult.Results)
        {
            if (string.IsNullOrWhiteSpace(result.Path))
                continue;

            var line = GetSearchIssueDraftEvidenceLine(result);
            var key = $"{result.Path}\0{line.ToString(CultureInfo.InvariantCulture)}";
            if (!seen.Add(key))
                continue;

            var snippet = includeSnippets
                ? BuildSearchIssueDraftEvidenceSnippet(result)
                : string.Empty;
            if (includeSnippets && string.IsNullOrWhiteSpace(snippet))
                continue;

            evidence.Add(new SearchIssueDraftEvidenceJsonResult(result.Path, line, snippet));
            if (evidence.Count >= MaxIssueDraftEvidenceItems)
                break;
        }

        return evidence;
    }

    private static int GetSearchIssueDraftEvidenceLine(CompactSearchResult result)
    {
        if (result.MatchLines.Count > 0)
            return result.MatchLines[0];
        if (result.FocusLine.HasValue)
            return result.FocusLine.Value;
        if (result.SnippetStartLine > 0)
            return result.SnippetStartLine;
        return Math.Max(1, result.ChunkStartLine);
    }

    private static string BuildSearchIssueDraftEvidenceSnippet(CompactSearchResult result)
    {
        var snippetLines = result.Snippet.Split('\n');
        var targetLines = result.MatchLines.Count > 0
            ? result.MatchLines.Take(2).ToHashSet()
            : result.FocusLine.HasValue
                ? new HashSet<int> { result.FocusLine.Value }
                : [];
        var lines = new List<string>();

        if (targetLines.Count > 0)
        {
            for (var i = 0; i < snippetLines.Length; i++)
            {
                var absoluteLine = result.SnippetStartLine + i;
                if (targetLines.Contains(absoluteLine))
                    AddEvidenceSnippetLine(lines, snippetLines[i]);
            }
        }

        if (lines.Count == 0)
        {
            foreach (var line in snippetLines)
            {
                AddEvidenceSnippetLine(lines, line);
                if (lines.Count > 0)
                    break;
            }
        }

        return BoundSearchIssueDraftEvidenceSnippet(string.Join('\n', lines));
    }

    private static void AddEvidenceSnippetLine(List<string> lines, string line)
    {
        var trimmed = line.Trim();
        if (!string.IsNullOrWhiteSpace(trimmed))
            lines.Add(trimmed);
    }

    private static string BoundSearchIssueDraftEvidenceSnippet(string snippet)
    {
        if (snippet.Length <= MaxIssueDraftEvidenceSnippetLength)
            return snippet;

        return snippet[..MaxIssueDraftEvidenceSnippetLength].TrimEnd() + "...";
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
        IReadOnlyList<SearchIssueDraftEvidenceJsonResult> evidence,
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
        AppendSearchIssueDraftEvidence(sb, evidence);
        sb.AppendLine();
        AppendSearchIssueDraftTriageMetadata(sb, triage);
        sb.AppendLine();
        AppendSearchIssueDraftOmittedResults(sb, queryResult);
        sb.AppendLine();
        sb.AppendLine("## False-positive guidance");
        sb.AppendLine(queryResult.FalsePositiveGuidance);
        sb.AppendLine();
        if (queryResult.RiskEvidence.Count > 0)
        {
            sb.AppendLine("## Risk evidence");
            foreach (var riskEvidence in queryResult.RiskEvidence)
                sb.AppendLine($"- {riskEvidence}");
            sb.AppendLine();
        }

        if (queryResult.StringComparisonTaxonomy is not null)
        {
            AppendSearchIssueDraftStringComparisonTaxonomy(sb, queryResult.StringComparisonTaxonomy);
            sb.AppendLine();
        }

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
        sb.AppendLine($"- result_limit: `{queryResult.ResultLimit}`");
        sb.AppendLine($"- omitted_count: `{queryResult.OmittedCount}`");
        sb.AppendLine($"- minimum_omitted_result_count: `{queryResult.MinimumOmittedResultCount}`");
        sb.AppendLine($"- exact_substring: `{queryResult.ExactSubstring.ToString().ToLowerInvariant()}`");
        return sb.ToString().TrimEnd();
    }

    private static void AppendSearchIssueDraftEvidence(
        StringBuilder sb,
        IReadOnlyList<SearchIssueDraftEvidenceJsonResult> evidence)
    {
        sb.AppendLine("## Representative evidence");
        if (evidence.Count == 0)
        {
            sb.AppendLine("N/A");
            return;
        }

        foreach (var item in evidence)
        {
            sb.AppendLine($"- `{item.Path}:{item.Line.ToString(CultureInfo.InvariantCulture)}`");
            if (string.IsNullOrWhiteSpace(item.Snippet))
                continue;

            sb.AppendLine("```text");
            sb.AppendLine(item.Snippet);
            sb.AppendLine("```");
        }
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

    private static void AppendSearchIssueDraftStringComparisonTaxonomy(StringBuilder sb, SearchRecipeStringComparisonTaxonomyJsonResult taxonomy)
    {
        sb.AppendLine("## String-comparison taxonomy");
        sb.AppendLine(taxonomy.TriageGuidance);
        sb.AppendLine();
        sb.AppendLine("### Domain categories");
        foreach (var category in taxonomy.DomainCategories)
            sb.AppendLine($"- `{category.Name}`: {category.Description} Review: {category.ReviewGuidance}");
    }

    private static void AppendSearchIssueDraftTriageMetadata(StringBuilder sb, IssueDraftTriageMetadataJsonResult triage)
    {
        sb.AppendLine("## Triage metadata");
        sb.AppendLine($"- severity: `{triage.Severity}`");
        sb.AppendLine($"- confidence: `{triage.Confidence}`");
        sb.AppendLine($"- evidence_count: `{triage.EvidenceCount}`");
        sb.AppendLine($"- duplicate_guidance: {triage.DuplicateGuidance}");
    }

    private static void AppendSearchIssueDraftOmittedResults(
        StringBuilder sb,
        SearchRecipeQueryResultJsonResult queryResult)
    {
        sb.AppendLine("## Omitted results");
        sb.AppendLine($"- result_limit: `{queryResult.ResultLimit}`");
        sb.AppendLine($"- omitted_count: `{queryResult.OmittedCount}`");
        sb.AppendLine($"- minimum_omitted_result_count: `{queryResult.MinimumOmittedResultCount}`");
        sb.AppendLine($"- truncated: `{queryResult.Truncated.ToString().ToLowerInvariant()}`");
        if (!string.IsNullOrWhiteSpace(queryResult.NextCursor))
            sb.AppendLine($"- next_cursor: `{queryResult.NextCursor}`");
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

    private static string? FormatSearchGuardFilterScope(SearchGuardFilter guardFilter)
        => guardFilter.Scope switch
        {
            SearchGuardScope.Window => "window",
            SearchGuardScope.SameLine => "same_line",
            _ => null
        };

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
        IReadOnlyList<SearchIssueDraftEvidenceJsonResult> evidence,
        IssueDraftTriageMetadataJsonResult triage,
        QueryCommandOptions options)
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
        AppendSearchIssueDraftEvidence(sb, evidence);
        sb.AppendLine();
        AppendSearchIssueDraftTriageMetadata(sb, triage);
        sb.AppendLine();
        AppendSearchIssueDraftOmittedResults(sb, queryResult);
        sb.AppendLine();
        sb.AppendLine("## Review guidance");
        sb.AppendLine(queryResult.FalsePositiveGuidance);
        sb.AppendLine();
        sb.AppendLine("## Replay command");
        sb.AppendLine("```sh");
        sb.AppendLine(BuildAdHocSearchIssueDraftReplayCommand(options));
        sb.AppendLine("```");
        sb.AppendLine();
        sb.AppendLine("## Search metadata");
        sb.AppendLine("- draft_id: `search/ad-hoc`");
        sb.AppendLine($"- result_count: `{queryResult.Count}`");
        sb.AppendLine($"- result_limit: `{queryResult.ResultLimit}`");
        sb.AppendLine($"- omitted_count: `{queryResult.OmittedCount}`");
        sb.AppendLine($"- minimum_omitted_result_count: `{queryResult.MinimumOmittedResultCount}`");
        sb.AppendLine($"- exact_substring: `{queryResult.ExactSubstring.ToString().ToLowerInvariant()}`");
        return sb.ToString().TrimEnd();
    }

    private static string BuildAdHocSearchIssueDraftReplayCommand(QueryCommandOptions options)
    {
        var args = new List<string>
        {
            "cdidx",
            "search",
            options.Query!,
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
        foreach (var label in options.IssueLabels)
            AddReplayValueOption(args, "--issue-label", label);
        if (!string.IsNullOrWhiteSpace(options.IssueTitle))
            AddReplayValueOption(args, "--issue-title", options.IssueTitle);

        return string.Join(" ", args.Select(QuoteReplayShellArg));
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
            [.. query.RiskEvidence],
            ToSearchRecipeGuardFilterJsonResults(query.GuardFilters),
            query.Severity,
            [.. query.PathPatterns],
            [.. query.ExcludePaths],
            [.. query.MatchOrigins],
            [.. query.ExcludeOrigins],
            [.. query.ResultKinds],
            query.StringComparisonTaxonomy,
            query.BroadCatchTaxonomy,
            query.NullableContractTaxonomy,
            query.ExactSubstring)).ToList());

    private static string FormatSearchRecipeStringComparisonDomains(SearchRecipeStringComparisonTaxonomyJsonResult taxonomy)
        => string.Join(", ", taxonomy.DomainCategories.Select(category => category.Name));

    private static SearchRecipeCompactListItemJsonResult ToSearchRecipeCompactListItem(SearchAuditRecipe recipe, IReadOnlyList<SearchAuditRecipeQuery> queries) => new(
        recipe.Name,
        recipe.Description,
        recipe.DefaultScope,
        queries.Count,
        recipe.RecommendedLabels,
        [.. recipe.DefaultPathPatterns],
        [.. recipe.DefaultExcludePaths]);

    private static SearchRecipeCompactListItemJsonResult ToSearchRecipeCompactListItem(SearchRecipeListItemJsonResult recipe, IReadOnlyList<SearchRecipeQueryListItemJsonResult> queries) => new(
        recipe.Name,
        recipe.Description,
        recipe.DefaultScope,
        queries.Count,
        recipe.RecommendedLabels,
        recipe.DefaultPathPatterns,
        recipe.DefaultExcludePaths);

    private static List<SearchRecipeGuardFilterJsonResult> ToSearchRecipeGuardFilterJsonResults(IReadOnlyList<SearchGuardFilter> guardFilters)
        => guardFilters
            .Select(filter => new SearchRecipeGuardFilterJsonResult(
                filter.Role == SearchGuardRole.Require ? "require" : "reject",
                filter.Direction == SearchGuardDirection.Before ? "before" : "after",
                filter.Query,
                BuildSearchGuardReplayOptionName(filter),
                FormatSearchGuardFilterScope(filter)))
            .ToList();

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
            if (recipeQuery is { RiskEvidence.Count: > 0 })
                compact.RiskEvidence = [.. recipeQuery.RiskEvidence];

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

    private readonly record struct SearchLocationSpan(int Line, int Column, int Length);

    private static bool TryGetSearchLocationSpan(SearchMatchFacet facet, out SearchLocationSpan span)
    {
        if (facet.Line <= 0 || facet.Column <= 0)
        {
            span = default;
            return false;
        }

        span = new SearchLocationSpan(facet.Line, facet.Column, Math.Max(1, facet.Length));
        return true;
    }

    private static bool TryGetPrimarySearchLocation(SearchDisplayRow row, out SearchLocationSpan span)
    {
        var focusLine = row.Compact.FocusLine.GetValueOrDefault();
        var focusColumn = row.Compact.FocusColumn.GetValueOrDefault();
        if (focusLine > 0)
        {
            var focusedFacet = row.Compact.MatchFacets
                .Where(facet => facet.Line == focusLine && facet.Column > 0)
                .OrderBy(facet => focusColumn > 0 ? Math.Abs(facet.Column - focusColumn) : 0)
                .ThenBy(facet => facet.Column)
                .ThenByDescending(facet => facet.Length)
                .FirstOrDefault();
            if (focusedFacet != null && TryGetSearchLocationSpan(focusedFacet, out span))
                return true;

            if (row.Compact.MatchLines.Contains(focusLine))
            {
                span = new SearchLocationSpan(focusLine, Math.Max(1, focusColumn), 1);
                return true;
            }
        }

        foreach (var facet in row.Compact.MatchFacets)
        {
            if (TryGetSearchLocationSpan(facet, out span))
                return true;
        }

        foreach (var line in row.Compact.MatchLines)
        {
            if (line > 0)
            {
                span = new SearchLocationSpan(line, 1, 1);
                return true;
            }
        }

        span = default;
        return false;
    }

    private static IEnumerable<SearchLocationSpan> GetSearchLocationSpans(SearchDisplayRow row, bool includeAllMatches)
    {
        if (includeAllMatches)
        {
            var seen = new HashSet<string>(StringComparer.Ordinal);
            var emitted = false;
            foreach (var facet in row.Compact.MatchFacets)
            {
                if (!TryGetSearchLocationSpan(facet, out var span))
                    continue;
                if (!seen.Add(SearchFacetKey(span.Line, span.Column, span.Length)))
                    continue;

                emitted = true;
                yield return span;
            }

            if (emitted)
                yield break;

            foreach (var line in row.Compact.MatchLines)
            {
                if (line <= 0)
                    continue;
                var span = new SearchLocationSpan(line, 1, 1);
                if (!seen.Add(SearchFacetKey(span.Line, span.Column, span.Length)))
                    continue;

                emitted = true;
                yield return span;
            }

            if (emitted)
                yield break;
        }

        if (TryGetPrimarySearchLocation(row, out var primary))
            yield return primary;
    }

    private static IEnumerable<FormattedLocation> ToSearchFormattedLocations(SearchDisplayRow row, string query, bool useMatchLines)
    {
        var spans = GetSearchLocationSpans(row, useMatchLines).ToList();
        if (spans.Count == 0)
        {
            yield return new FormattedLocation(row.Result.Path, row.Result.StartLine, null, $"search match: {query}");
            yield break;
        }

        foreach (var span in spans)
            yield return new FormattedLocation(row.Result.Path, span.Line, span.Column, $"search match: {query}");
    }

    private static IEnumerable<LspLocation> ToSearchLspLocations(SearchDisplayRow row, bool useMatchLines)
    {
        var spans = GetSearchLocationSpans(row, useMatchLines).ToList();
        if (spans.Count == 0)
        {
            yield return ToLspLocation(row.Result);
            yield break;
        }

        foreach (var span in spans)
            yield return BuildLspLocation(row.Result.Path, span.Line, span.Column, span.Line, span.Column + span.Length);
    }

    private static IEnumerable<(string Path, int Line, int Column, string Message)> ToSearchQuickfixItems(SearchDisplayRow row, string query, bool useMatchLines)
    {
        var spans = GetSearchLocationSpans(row, useMatchLines).ToList();
        if (spans.Count == 0)
        {
            yield return (row.Result.Path, row.Result.StartLine, 1, $"search match: {query}");
            yield break;
        }

        foreach (var span in spans)
            yield return (row.Result.Path, span.Line, span.Column, $"search match: {query}");
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
        var first = results.FirstOrDefault();
        if (first != null)
            first.ExactSubstringHint = hint;
    }

    private static void WriteJsonStreamDone(int count, JsonSerializerOptions jsonOptions, bool interrupted = false, DbReader? reader = null)
    {
        var includeDiagnostics = HasReadOnlyFallbackDiagnostics(reader);
        Console.WriteLine(JsonSerializer.Serialize(
            new JsonStreamDoneResult(
                Done: !interrupted,
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

    private static int WriteCompactSearchResults(IEnumerable<CompactSearchResult> results, QueryCommandOptions options, JsonSerializerOptions jsonOptions)
    {
        using var writer = new StringWriter(CultureInfo.InvariantCulture);
        WriteCompactSearchResults(writer, results, jsonOptions);
        return WriteJsonObjectWithOptionalByteLimit(
            writer.ToString().TrimEnd('\r', '\n'),
            options,
            "compact search results",
            "Reduce --limit, --snippet-lines, or use `--json=ndjson --max-json-bytes` for streaming output.");
    }

    private static void WriteCompactSearchResults(TextWriter writer, IEnumerable<CompactSearchResult> results, JsonSerializerOptions jsonOptions)
    {
        var itemOptions = GetCompactJsonOptions(jsonOptions);
        var context = CliJsonSerializerContextFactory.Create(itemOptions);
        WriteJsonArray(
            writer,
            results,
            (writer, result) => writer.Write(JsonSerializer.Serialize(result, context.CompactSearchResult)),
            jsonOptions);
    }

    private static void WriteJsonArray<T>(IEnumerable<T> items, Action<TextWriter, T> writeItem, JsonSerializerOptions jsonOptions)
        => WriteJsonArray(Console.Out, items, writeItem, jsonOptions);

    private static void WriteJsonArray<T>(TextWriter writer, IEnumerable<T> items, Action<TextWriter, T> writeItem, JsonSerializerOptions jsonOptions)
    {
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
            var line = result.StartLine;
            var column = 1;
            if (TryGetPrimarySearchLocation(row, out var span))
            {
                line = span.Line;
                column = span.Column;
            }

            var values = new[]
            {
                result.Path,
                line.ToString(CultureInfo.InvariantCulture),
                column.ToString(CultureInfo.InvariantCulture),
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

    private static string FormatSearchVisibilitySuffix(string? visibility)
    {
        if (string.IsNullOrWhiteSpace(visibility)
            || string.Equals(visibility, "public", StringComparison.OrdinalIgnoreCase))
        {
            return string.Empty;
        }

        return $" [{visibility}]";
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

    private static void WriteDefinitionJsonResult(DefinitionResult result, QueryCommandOptions options, ExactQuerySignal? exactSignal, JsonSerializerOptions jsonOptions)
    {
        var payload = JsonSerializer.SerializeToNode(result, CliJsonSerializerContextFactory.Create(jsonOptions).DefinitionResult)!.AsObject();
        ApplyBodyModeDefinitionContentPolicy(payload, options);
        if (exactSignal.HasValue)
            AddExactJsonFields(payload, exactSignal.Value);
        Console.WriteLine(payload.ToJsonString(jsonOptions));
    }

    private static void ApplyBodyModeDefinitionContentPolicy(JsonObject payload, QueryCommandOptions options)
    {
        if (!options.IncludeBody)
            return;

        OmitDefinitionContent(payload, "body_content_field");
    }

    private static void ApplyInspectDefinitionContentPolicy(JsonObject payload, QueryCommandOptions options)
    {
        if (!payload.TryGetPropertyValue("definitions", out var definitionsNode) || definitionsNode is not JsonArray definitions)
            return;

        var reason = options.IncludeBody ? "body_content_field" : "inspect_body_not_requested";
        foreach (var definition in definitions.OfType<JsonObject>())
        {
            OmitDefinitionContent(definition, reason);
            if (!options.IncludeBody)
                OmitDefinitionBodyContent(definition);
        }
    }

    private static void OmitDefinitionContent(JsonObject definition, string reason)
    {
        if (!definition.Remove("content"))
            return;

        definition["content_omitted"] = true;
        definition["content_omitted_reason"] = reason;
    }

    private static void OmitDefinitionBodyContent(JsonObject definition)
    {
        definition.Remove("body_content");
        definition.Remove("body_content_start_line");
        definition.Remove("body_content_end_line");
        definition.Remove("body_content_next_start_line");
        definition.Remove("body_content_truncated");
        definition.Remove("body_requested_start_line");
        definition.Remove("body_requested_end_line");
        definition.Remove("body_effective_start_line");
        definition.Remove("body_effective_end_line");
        definition.Remove("body_content_truncation_reasons");
        definition.Remove("body_content_recovery");
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

    private sealed record FileListScopeFilters(
        IReadOnlyList<string> PathPatterns,
        IReadOnlyList<string> ExcludePaths,
        bool ExcludeTests);

    private static FileListScopeFilters BuildFilesScopeFilters(QueryCommandOptions options)
    {
        if (!options.ExcludeTests || options.PathPatterns.Count > 0)
        {
            return new(
                options.PathPatterns,
                options.ExcludePaths,
                options.ExcludeTests);
        }

        var pathPatterns = new List<string>(options.PathPatterns);
        AddDistinct(pathPatterns, SearchAuditRecipes.DefaultSourcePathPatterns);
        var excludePaths = new List<string>(options.ExcludePaths);
        AddDistinct(excludePaths, SearchAuditRecipes.DefaultSourceExcludePaths);
        return new(pathPatterns, excludePaths, ExcludeTests: true);
    }

    private static void AddFileCountBytesJsonFields(JsonObject payload, QueryCountResult counts)
    {
        payload["total_bytes"] = counts.TotalBytes ?? 0;
        payload["average_bytes"] = counts.AverageBytes ?? 0;
        payload["max_bytes"] = counts.MaxBytes ?? 0;
        payload["max_bytes_path"] = counts.MaxBytesPath;
        payload["bytes_authoritative"] = counts.BytesAuthoritative ?? true;
    }

    private static string FormatFileCountBytesSummary(QueryCountResult counts)
    {
        var totalBytes = counts.TotalBytes ?? 0;
        var averageBytes = counts.AverageBytes ?? 0;
        var maxBytes = counts.MaxBytes ?? 0;
        var maxPath = string.IsNullOrEmpty(counts.MaxBytesPath)
            ? "none"
            : counts.MaxBytesPath;
        var authority = (counts.BytesAuthoritative ?? true) ? "true" : "false";
        return string.Create(
            CultureInfo.InvariantCulture,
            $"{counts.Count} files, {totalBytes} bytes total, average {averageBytes:0.##} bytes, max {maxBytes} bytes ({maxPath}), bytes_authoritative: {authority}");
    }

    private static bool ShouldWriteBoundedDiscoveryJsonPayload(QueryCommandOptions options)
        => options.OutputFormat == OutputFormatCompact || options.SummaryOnly || options.MaxJsonBytes.HasValue;

    private static bool TryWriteDiscoveryOutputControlUsageError(string commandName, QueryCommandOptions options)
    {
        if (!options.SummaryOnly && !options.MaxJsonBytes.HasValue)
            return false;
        if (options.Json && options.OutputFormat is OutputFormatJson or OutputFormatCompact or OutputFormatCount)
            return false;

        var control = options.SummaryOnly ? "--summary-only" : "--max-json-bytes";
        WriteUsageError(
            $"{control} is only supported with {commandName} JSON, compact, or count output.",
            GetUsageLineOrThrow(commandName),
            $"Use `cdidx {commandName} --json {control}` or `cdidx {commandName} --format compact {control}`.");
        return true;
    }

    private static int WriteBoundedDiscoveryJsonPayload<T>(
        DbReader reader,
        QueryCommandOptions options,
        JsonSerializerOptions jsonOptions,
        string commandName,
        string resultsKey,
        IReadOnlyList<T> results,
        int totalCount,
        int fileCount,
        Func<T, JsonNode?> rowFactory,
        ExactQuerySignal? exactSignal = null)
    {
        var jsonNodeOptions = EnsureJsonNodeSerializerOptions(jsonOptions);
        var requestedRows = options.SummaryOnly ? 0 : results.Count;
        var json = BuildBoundedDiscoveryJson(requestedRows);
        if (!options.MaxJsonBytes.HasValue)
        {
            Console.WriteLine(json);
            return CommandExitCodes.Success;
        }

        if (JsonFitsByteLimit(json, options.MaxJsonBytes.Value))
        {
            Console.WriteLine(json);
            return CommandExitCodes.Success;
        }

        string? bestJson = null;
        var low = 0;
        var high = requestedRows;
        while (low <= high)
        {
            var mid = low + ((high - low) / 2);
            var candidate = BuildBoundedDiscoveryJson(mid);
            if (JsonFitsByteLimit(candidate, options.MaxJsonBytes.Value))
            {
                bestJson = candidate;
                low = mid + 1;
            }
            else
            {
                high = mid - 1;
            }
        }

        if (bestJson != null)
        {
            Console.WriteLine(bestJson);
            return CommandExitCodes.Success;
        }

        return WriteJsonObjectWithOptionalByteLimit(
            BuildBoundedDiscoveryJson(0),
            options,
            $"{commandName} compact",
            "Use --summary-only, reduce --limit, or increase --max-json-bytes.",
            commandName);

        string BuildBoundedDiscoveryJson(int emittedRows)
        {
            var payload = BuildBoundedDiscoveryPayload(
                reader,
                options,
                jsonOptions,
                resultsKey,
                results,
                totalCount,
                fileCount,
                Math.Clamp(emittedRows, 0, results.Count),
                rowFactory,
                exactSignal);
            return payload.ToJsonString(jsonNodeOptions);
        }
    }

    private static JsonObject BuildBoundedDiscoveryPayload<T>(
        DbReader reader,
        QueryCommandOptions options,
        JsonSerializerOptions jsonOptions,
        string resultsKey,
        IReadOnlyList<T> results,
        int totalCount,
        int fileCount,
        int emittedRows,
        Func<T, JsonNode?> rowFactory,
        ExactQuerySignal? exactSignal)
    {
        var omittedCount = Math.Max(0, totalCount - emittedRows);
        var rowLimitReached = totalCount > results.Count;
        var byteLimitReached = !options.SummaryOnly && options.MaxJsonBytes.HasValue && emittedRows < results.Count;
        var payload = new JsonObject
        {
            ["api_version"] = JsonOutputContract.ApiVersion,
            ["count"] = totalCount,
            ["file_count"] = fileCount,
            ["emitted_count"] = emittedRows,
            ["omitted_count"] = omittedCount,
            ["truncated"] = omittedCount > 0,
        };

        if (options.OutputFormat == OutputFormatCompact)
            payload["format"] = OutputFormatCompact;
        if (options.SummaryOnly)
            payload["summary_only"] = true;
        if (options.MaxJsonBytes.HasValue)
            payload["max_json_bytes"] = options.MaxJsonBytes.Value;
        if (rowLimitReached)
            payload["row_limit_reached"] = true;
        if (byteLimitReached)
            payload["byte_limit_reached"] = true;

        var omittedBy = new JsonArray();
        if (options.SummaryOnly && totalCount > 0)
            omittedBy.Add("summary_only");
        if (rowLimitReached)
            omittedBy.Add("limit");
        if (byteLimitReached)
            omittedBy.Add("max_json_bytes");
        if (omittedBy.Count > 0)
            payload["omitted_by"] = omittedBy;

        if (!options.SummaryOnly)
            payload[resultsKey] = BuildDiscoveryRows(results, emittedRows, rowFactory);
        if (exactSignal.HasValue)
            AddExactJsonFields(payload, exactSignal.Value);
        payload["query_context"] = BuildQueryContextJson(options, jsonOptions);
        AddFreshnessHint(payload, reader);
        return payload;
    }

    private static JsonArray BuildDiscoveryRows<T>(IReadOnlyList<T> results, int emittedRows, Func<T, JsonNode?> rowFactory)
    {
        var rows = new JsonArray();
        for (var i = 0; i < emittedRows && i < results.Count; i++)
            rows.Add(rowFactory(results[i]));
        return rows;
    }

    private static bool JsonFitsByteLimit(string json, int maxJsonBytes)
        => Encoding.UTF8.GetByteCount(json) + Environment.NewLine.Length <= maxJsonBytes;

    private static JsonNode? ToFileDiscoveryJsonNode(FileResult result, JsonSerializerOptions jsonOptions, bool compact)
    {
        if (!compact)
            return JsonSerializer.SerializeToNode(result, CliJsonSerializerContextFactory.Create(jsonOptions).FileResult);

        var row = new JsonObject
        {
            ["path"] = result.Path,
            ["lines"] = result.Lines,
            ["size"] = result.Size,
            ["symbol_count"] = result.SymbolCount,
            ["reference_count"] = result.ReferenceCount,
        };
        if (result.Lang != null)
            row["lang"] = result.Lang;
        return row;
    }

    private static JsonNode? ToSymbolDiscoveryJsonNode(SymbolResult result, JsonSerializerOptions jsonOptions, bool compact)
    {
        if (!compact)
            return JsonSerializer.SerializeToNode(result, CliJsonSerializerContextFactory.Create(jsonOptions).SymbolResult);

        var row = new JsonObject
        {
            ["path"] = result.Path,
            ["line"] = result.Line,
            ["start_line"] = result.StartLine,
            ["end_line"] = result.EndLine,
            ["kind"] = result.Kind,
            ["name"] = result.Name,
        };
        if (result.Lang != null)
            row["lang"] = result.Lang;
        if (result.ContainerName != null)
            row["container_name"] = result.ContainerName;
        if (result.Visibility != null)
            row["visibility"] = result.Visibility;
        if (result.SortMode != null)
            row["sort_mode"] = result.SortMode;
        if (result.ReferenceCount.HasValue)
            row["reference_count"] = result.ReferenceCount.Value;
        if (result.HotspotScore.HasValue)
            row["hotspot_score"] = result.HotspotScore.Value;
        if (result.RankingReferenceScore.HasValue)
            row["ranking_reference_score"] = result.RankingReferenceScore.Value;
        if (result.RankingHotspotScore.HasValue)
            row["ranking_hotspot_score"] = result.RankingHotspotScore.Value;
        if (result.GenericNamePenalty.HasValue)
            row["generic_name_penalty"] = result.GenericNamePenalty.Value;
        if (result.StructuralRankPenalty.HasValue)
            row["structural_rank_penalty"] = result.StructuralRankPenalty.Value;
        if (result.DefinitionSites.HasValue)
            row["definition_sites"] = result.DefinitionSites.Value;
        if (result.SizeLines.HasValue)
            row["size_lines"] = result.SizeLines.Value;
        if (result.ComplexityScore.HasValue)
            row["complexity_score"] = result.ComplexityScore.Value;
        return row;
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
            AddJsonByteLimitField(payload, options);
            return payload;
        }

        if (options.MapSections == null)
        {
            if (options.ContextAfterExplicit)
                payload["depth"] = options.ContextAfter;
            if (options.Compact && compactTruncation != null)
            {
                AddCompactJsonFields(payload, GetCompactSectionLimit(options), compactTruncation);
                payload["next_commands"] = BuildRepoMapNextCommands(options);
            }
            AddJsonByteLimitField(payload, options);
            return payload;
        }

        var keep = new HashSet<string>(RepoMapSummaryJsonProperties, StringComparer.Ordinal);
        foreach (var section in options.MapSections)
            AddRepoMapSectionJsonProperties(keep, section);

        KeepRepoMapJsonProperties(payload, keep);
        payload["sections"] = new JsonArray(options.MapSections.Select(section => JsonValue.Create(section)).ToArray<JsonNode?>());
        payload["section_properties"] = BuildRepoMapSectionProperties(options.MapSections);
        if (options.ContextAfterExplicit)
            payload["depth"] = options.ContextAfter;
        if (options.Compact && compactTruncation != null)
        {
            AddCompactJsonFields(payload, GetCompactSectionLimit(options), compactTruncation);
            payload["next_commands"] = BuildRepoMapNextCommands(options);
        }
        AddJsonByteLimitField(payload, options);
        return payload;
    }

    private static string BuildRepoMapIssueDraftsPayload(RepoMapResult map, QueryCommandOptions options, JsonSerializerOptions jsonOptions)
    {
        var candidates = map.LargestFiles
            .Where(IsRepoMapOversizedFileCandidate)
            .Select(BuildRepoMapIssueDraftJson)
            .ToArray();
        var sourceLimit = options.Compact ? GetCompactSourceLimit(GetCompactSectionLimit(options)) : options.Limit;
        var largestFilesTruncated = map.FileCount > map.LargestFiles.Count && map.LargestFiles.Count >= sourceLimit;
        var payload = new JsonObject
        {
            ["api_version"] = JsonOutputContract.ApiVersion,
            ["format"] = OutputFormatIssueDrafts,
            ["count"] = candidates.Length,
            ["issue_drafts"] = new JsonArray(candidates),
            ["groups"] = BuildRepoMapIssueDraftGroupsJson(candidates),
            ["thresholds"] = new JsonObject
            {
                ["line_threshold"] = MapIssueDraftLineThreshold,
                ["byte_threshold"] = MapIssueDraftByteThreshold,
            },
            ["truncation"] = new JsonObject
            {
                ["largest_files"] = new JsonObject
                {
                    ["source_section"] = "largest_files",
                    ["returned"] = map.LargestFiles.Count,
                    ["source_limit"] = sourceLimit,
                    ["total_files"] = map.FileCount,
                    ["truncated"] = largestFilesTruncated,
                },
            },
            ["query_context"] = BuildQueryContextJson(options, jsonOptions),
        };
        if (map.ProjectRoot != null)
            payload["project_root"] = map.ProjectRoot;
        if (map.GitHead != null)
            payload["git_head"] = map.GitHead;
        if (map.GitIsDirty != null)
            payload["git_is_dirty"] = map.GitIsDirty;
        if (map.IndexedHeadCommit != null)
            payload["indexed_head_commit"] = map.IndexedHeadCommit;
        if (map.WorktreeHeadChanged != null)
            payload["worktree_head_changed"] = map.WorktreeHeadChanged;
        AddJsonByteLimitField(payload, options);
        return payload.ToJsonString(GetJsonNodeSerializationOptions(jsonOptions));
    }

    private static JsonObject BuildRepoMapIssueDraftGroupsJson(IReadOnlyList<JsonObject> candidates)
    {
        var representativePaths = new JsonArray();
        foreach (var candidate in candidates.Take(DefaultCompactSectionLimit))
        {
            var path = candidate["candidate"]?["path"]?.GetValue<string>();
            if (path != null)
                representativePaths.Add(path);
        }

        return new JsonObject
        {
            ["oversized_file"] = new JsonObject
            {
                ["kind"] = "oversized_file",
                ["count"] = candidates.Count,
                ["source_section"] = "largest_files",
                ["representative_paths"] = representativePaths,
                ["representative_paths_truncated"] = candidates.Count > representativePaths.Count,
            },
        };
    }

    private static bool IsRepoMapOversizedFileCandidate(RepoFileSummaryResult file)
        => file.Lines >= MapIssueDraftLineThreshold || file.Size >= MapIssueDraftByteThreshold;

    private static JsonObject BuildRepoMapIssueDraftJson(RepoFileSummaryResult file)
    {
        var reasonTags = new JsonArray();
        if (file.Lines >= MapIssueDraftLineThreshold)
            reasonTags.Add("line_threshold_exceeded");
        if (file.Size >= MapIssueDraftByteThreshold)
            reasonTags.Add("byte_threshold_exceeded");

        return new JsonObject
        {
            ["kind"] = "oversized_file",
            ["title"] = $"Split oversized file: {file.Path}",
            ["body"] = BuildRepoMapIssueDraftBody(file, reasonTags),
            ["labels"] = new JsonArray("maintenance", "refactor"),
            ["candidate"] = new JsonObject
            {
                ["path"] = file.Path,
                ["lang"] = file.Lang,
                ["lines"] = file.Lines,
                ["size_bytes"] = file.Size,
                ["symbol_count"] = file.SymbolCount,
                ["reference_count"] = file.ReferenceCount,
                ["line_threshold"] = MapIssueDraftLineThreshold,
                ["byte_threshold"] = MapIssueDraftByteThreshold,
                ["line_threshold_exceeded"] = file.Lines >= MapIssueDraftLineThreshold,
                ["byte_threshold_exceeded"] = file.Size >= MapIssueDraftByteThreshold,
                ["reason_tags"] = reasonTags.DeepClone(),
                ["source_section"] = "largest_files",
            },
        };
    }

    private static string BuildRepoMapIssueDraftBody(RepoFileSummaryResult file, JsonArray reasonTags)
    {
        var reasons = string.Join(", ", reasonTags.Select(tag => tag?.GetValue<string>()).Where(tag => tag != null));
        var builder = new StringBuilder();
        builder.AppendLine("## Summary");
        builder.AppendLine();
        builder.AppendLine($"`{file.Path}` is an oversized maintenance candidate from `cdidx map --format issue-drafts`.");
        builder.AppendLine();
        builder.AppendLine("## Evidence");
        builder.AppendLine();
        builder.AppendLine($"- Lines: {file.Lines.ToString(CultureInfo.InvariantCulture)} (threshold: >= {MapIssueDraftLineThreshold.ToString(CultureInfo.InvariantCulture)})");
        builder.AppendLine($"- Size: {file.Size.ToString(CultureInfo.InvariantCulture)} bytes (threshold: >= {MapIssueDraftByteThreshold.ToString(CultureInfo.InvariantCulture)})");
        builder.AppendLine($"- Symbols: {file.SymbolCount.ToString(CultureInfo.InvariantCulture)}");
        builder.AppendLine($"- References: {file.ReferenceCount.ToString(CultureInfo.InvariantCulture)}");
        builder.AppendLine($"- Reason tags: {reasons}");
        builder.AppendLine();
        builder.AppendLine("## Checklist");
        builder.AppendLine();
        builder.AppendLine("- [ ] Identify cohesive regions, types, or command paths that can move together.");
        builder.AppendLine("- [ ] Preserve public behavior and CLI/MCP output contracts.");
        builder.AppendLine("- [ ] Add or keep focused tests for moved behavior.");
        return builder.ToString().TrimEnd();
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

    private static readonly IReadOnlyDictionary<string, string[]> RepoMapSectionJsonProperties = new Dictionary<string, string[]>(StringComparer.Ordinal)
    {
        ["languages"] = ["languages"],
        ["tree"] = ["modules"],
        ["hotspots"] = ["top_files", "symbol_rich_files", "reference_rich_files", "entrypoints"],
        ["metrics"] = ["largest_files"],
    };

    private static void KeepRepoMapJsonProperties(JsonObject payload, IReadOnlySet<string> keep)
    {
        foreach (var propertyName in payload.Select(property => property.Key).Where(key => !keep.Contains(key)).ToList())
            payload.Remove(propertyName);
    }

    private static void AddRepoMapSectionJsonProperties(HashSet<string> keep, string section)
    {
        if (!RepoMapSectionJsonProperties.TryGetValue(section, out var properties))
            return;

        foreach (var property in properties)
            keep.Add(property);
    }

    private static JsonObject BuildRepoMapSectionProperties(IEnumerable<string> sections)
    {
        var payload = new JsonObject();
        foreach (var section in sections)
        {
            if (!RepoMapSectionJsonProperties.TryGetValue(section, out var properties))
                continue;

            payload[section] = new JsonArray(properties.Select(property => JsonValue.Create(property)).ToArray<JsonNode?>());
        }

        return payload;
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
        => ApplyOutlineSymbolLimit(outline, sectionLimit);

    private static JsonObject ApplyOutlineSymbolLimit(OutlineResult outline, int sectionLimit)
    {
        var sections = new JsonObject();
        TruncateCompactSection(outline.Symbols, sectionLimit, sections, "symbols");
        return BuildCompactTruncationMetadata(sectionLimit, sections);
    }

    private static bool HasOutlineJsonControls(QueryCommandOptions options, IReadOnlyList<string> kindFilters)
        => options.OutlineFieldsExplicit
           || kindFilters.Count > 0
           || options.LimitExplicit
           || options.OutlineCursorOffset.HasValue
           || options.SortExplicit;

    private static bool TryParseOutlineSortMode(string value, out OutlineSortMode sortMode)
    {
        switch (value.Trim().ToLowerInvariant().Replace("_", "-"))
        {
            case "source":
            case "line":
            case "lines":
                sortMode = OutlineSortMode.Source;
                return true;
            case "name":
                sortMode = OutlineSortMode.Name;
                return true;
            case "kind":
                sortMode = OutlineSortMode.Kind;
                return true;
            case "references":
            case "reference":
            case "refs":
            case "ref":
                sortMode = OutlineSortMode.References;
                return true;
            case "size":
            case "span":
            case "spans":
                sortMode = OutlineSortMode.Size;
                return true;
            case "complexity":
                sortMode = OutlineSortMode.Complexity;
                return true;
            case "path":
                sortMode = OutlineSortMode.Path;
                return true;
            default:
                sortMode = OutlineSortMode.Source;
                return false;
        }
    }

    private static string FormatOutlineSortMode(OutlineSortMode sortMode)
        => sortMode switch
        {
            OutlineSortMode.Name => "name",
            OutlineSortMode.Kind => "kind",
            OutlineSortMode.References => "references",
            OutlineSortMode.Size => "size",
            OutlineSortMode.Complexity => "complexity",
            OutlineSortMode.Path => "path",
            _ => "source",
        };

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

    private static bool OutlineNeedsReferenceCounts(QueryCommandOptions options, OutlineSortMode sortMode)
        => sortMode is OutlineSortMode.References or OutlineSortMode.Complexity
           || (options.OutlineFieldsExplicit
               && (options.OutlineFields is null
                   || options.OutlineFields.Contains("reference_count", StringComparer.Ordinal)
                   || options.OutlineFields.Contains("complexity_score", StringComparer.Ordinal)));

    private static bool OutlineNeedsDerivedMetadata(QueryCommandOptions options, OutlineSortMode sortMode)
        => sortMode != OutlineSortMode.Source
           || options.SortExplicit
           || options.OutlineFieldsExplicit;

    private static List<OutlineSymbol> ApplyOutlineSort(IReadOnlyList<OutlineSymbol> symbols, OutlineSortMode sortMode, bool includeDerivedMetadata)
    {
        if (includeDerivedMetadata)
        {
            foreach (var symbol in symbols)
                ApplyOutlineSortMetadata(symbol, sortMode);
        }

        if (sortMode == OutlineSortMode.Source)
            return symbols.ToList();

        if (!includeDerivedMetadata)
        {
            foreach (var symbol in symbols)
                ApplyOutlineSortMetadata(symbol, sortMode);
        }

        return sortMode switch
        {
            OutlineSortMode.Name => symbols
                .OrderBy(symbol => symbol.Name, StringComparer.OrdinalIgnoreCase)
                .ThenBy(symbol => symbol.Line)
                .ThenBy(symbol => symbol.Kind, StringComparer.Ordinal)
                .ToList(),
            OutlineSortMode.Kind => symbols
                .OrderBy(symbol => symbol.Kind, StringComparer.Ordinal)
                .ThenByDescending(GetOutlineSizeLines)
                .ThenBy(symbol => symbol.Line)
                .ThenBy(symbol => symbol.Name, StringComparer.OrdinalIgnoreCase)
                .ToList(),
            OutlineSortMode.References => symbols
                .OrderByDescending(symbol => symbol.ReferenceCount ?? 0)
                .ThenByDescending(GetOutlineSizeLines)
                .ThenBy(symbol => symbol.Line)
                .ThenBy(symbol => symbol.Name, StringComparer.OrdinalIgnoreCase)
                .ToList(),
            OutlineSortMode.Size => symbols
                .OrderByDescending(GetOutlineSizeLines)
                .ThenBy(symbol => symbol.Line)
                .ThenBy(symbol => symbol.Name, StringComparer.OrdinalIgnoreCase)
                .ToList(),
            OutlineSortMode.Complexity => symbols
                .OrderByDescending(GetOutlineComplexityScore)
                .ThenByDescending(GetOutlineSizeLines)
                .ThenBy(symbol => symbol.Line)
                .ThenBy(symbol => symbol.Name, StringComparer.OrdinalIgnoreCase)
                .ToList(),
            OutlineSortMode.Path => symbols
                .OrderBy(symbol => symbol.Path, StringComparer.OrdinalIgnoreCase)
                .ThenBy(symbol => symbol.Line)
                .ThenBy(symbol => symbol.Name, StringComparer.OrdinalIgnoreCase)
                .ToList(),
            _ => symbols.ToList(),
        };
    }

    private static void ApplyOutlineSortMetadata(OutlineSymbol symbol, OutlineSortMode sortMode)
    {
        symbol.SortMode = FormatOutlineSortMode(sortMode);
        symbol.SizeLines = GetOutlineSizeLines(symbol);
        symbol.ComplexityScore = GetOutlineComplexityScore(symbol);
    }

    private static int GetOutlineSizeLines(OutlineSymbol symbol)
        => symbol.EndLine >= symbol.StartLine
            ? Math.Max(1, symbol.EndLine - symbol.StartLine + 1)
            : 1;

    private static double GetOutlineComplexityScore(OutlineSymbol symbol)
    {
        var visibilityBonus = symbol.Visibility switch
        {
            "public" or "pub" or "open" or "export" => 8.0,
            "protected" or "internal" or "protected internal" => 4.0,
            _ => 0.0,
        };
        var kindBonus = symbol.Kind is "class" or "struct" or "interface" or "enum" or "namespace" or "record"
            ? 6.0
            : 0.0;
        return (GetOutlineSizeLines(symbol) * 16.0) + ((symbol.ReferenceCount ?? 0) * 0.75) + visibilityBonus + kindBonus;
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
        OutlineSortMode sortMode,
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
            AddOutlinePagingJsonFields(payload, kindFilters, sortMode, options.SortExplicit, totalMatchingSymbols, offset, compactOutline.Symbols.Count, jsonOptions);
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
        AddOutlinePagingJsonFields(pagedPayload, kindFilters, sortMode, options.SortExplicit, totalMatchingSymbols, offset, pageSymbols.Count, jsonOptions);
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
        OutlineSortMode sortMode,
        bool sortExplicit,
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
        if (sortExplicit || sortMode != OutlineSortMode.Source)
            payload["sort"] = FormatOutlineSortMode(sortMode);
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
                case "sort_mode":
                    payload["sort_mode"] = symbol.SortMode;
                    break;
                case "reference_count":
                    payload["reference_count"] = symbol.ReferenceCount;
                    break;
                case "size_lines":
                    payload["size_lines"] = symbol.SizeLines;
                    break;
                case "complexity_score":
                    payload["complexity_score"] = symbol.ComplexityScore;
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

    private static void AddJsonByteLimitField(JsonObject payload, QueryCommandOptions options)
    {
        if (options.MaxJsonBytes.HasValue)
            payload["output_byte_limit"] = options.MaxJsonBytes.Value;
    }

    private static JsonArray BuildRepoMapNextCommands(QueryCommandOptions options)
    {
        var commands = new JsonArray
        {
            BuildRepoMapReplayCommand(options, ["--summary-only"]),
        };

        if (options.MapSections == null)
        {
            commands.Add(BuildRepoMapReplayCommand(options, ["--sections", "tree", "--limit", GetCompactSectionLimit(options).ToString(CultureInfo.InvariantCulture)]));
            commands.Add(BuildRepoMapReplayCommand(options, ["--sections", "hotspots", "--limit", GetCompactSectionLimit(options).ToString(CultureInfo.InvariantCulture)]));
        }
        else
        {
            commands.Add(BuildRepoMapReplayCommand(options, ["--sections", string.Join(',', options.MapSections), "--limit", GetCompactSectionLimit(options).ToString(CultureInfo.InvariantCulture)]));
        }

        return commands;
    }

    private static string BuildRepoMapReplayCommand(QueryCommandOptions options, string[] mapArgs)
    {
        var args = new List<string>
        {
            "cdidx",
            "map",
            options.Compact ? "--compact" : "--json",
        };
        args.AddRange(mapArgs);
        AddRepoMapReplayOptions(args, options);
        return string.Join(" ", args.Select(QuoteReplayShellArg));
    }

    private static void AddRepoMapReplayOptions(List<string> args, QueryCommandOptions options)
    {
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
        if (options.ContextAfterExplicit)
            AddReplayValueOption(args, "--depth", options.ContextAfter.ToString(CultureInfo.InvariantCulture));
        if (options.MinEntrypointConfidence > 0)
            AddReplayValueOption(args, "--min-entrypoint-confidence", options.MinEntrypointConfidence.ToString("0.###", CultureInfo.InvariantCulture));
        if (options.MaxJsonBytes.HasValue)
            AddReplayValueOption(args, "--max-json-bytes", options.MaxJsonBytes.Value.ToString(CultureInfo.InvariantCulture));
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
        var bodyContentPresent = options.IncludeBody && analysis.Definitions.Any(definition => definition.BodyContent != null);
        var bodyContentTruncated = options.IncludeBody && analysis.Definitions.Any(definition => definition.BodyContentTruncated);
        var nextStartLine = options.IncludeBody
            ? analysis.Definitions
                .Where(definition => definition.BodyContentNextStartLine.HasValue)
                .Select(definition => definition.BodyContentNextStartLine!.Value)
                .DefaultIfEmpty()
                .Min()
            : 0;

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

    // Preview option validation now lives in the command-specific unsupported-option allowlists.
    // Keep this shim so the existing call sites stay simple while the actual fail-closed logic
    // runs through ParseArgs() + TryWriteUnsupportedOptionError().
    // preview 系オプションの検証はコマンド別 allowlist に寄せたため、この shim は常に null を返す。
    private static string? ValidatePreviewOptions(string commandName, string[] args, bool allowMaxLineWidth, bool allowFocusOptions) => null;

    private static int ZeroResultExitCode(QueryCommandOptions options)
        => options.StrictNotFound ? CommandExitCodes.NotFound : CommandExitCodes.Success;

    private static int UnusedZeroResultExitCode(QueryCommandOptions options, UnusedDefaultSuppressionResult suppression)
        => !options.StrictNotFound && suppression.Applied && GetUnusedSuppressedCount(suppression) > 0
            ? CommandExitCodes.Success
            : ZeroResultExitCode(options);

    private static bool IsEmptySymbolAnalysis(SymbolAnalysisResult analysis)
        => analysis.File == null
           && analysis.Definitions.Count == 0
           && analysis.NearbySymbols.Count == 0
           && analysis.References.Count == 0
           && analysis.Callers.Count == 0
           && analysis.Callees.Count == 0;

    private static void WriteNumberedExcerpt(int startLine, string content, string indent = "")
    {
        var lines = content.Split('\n');
        for (int i = 0; i < lines.Length; i++)
            Console.WriteLine($"{indent}  {startLine + i,4}: {lines[i]}");
    }

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

        if (IsBareTokenSearch(options))
            CommandErrorWriter.WriteStderr($"Hint: {BareTokenAuthAuditHint}");

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

    private static bool IsBareTokenSearch(QueryCommandOptions options)
        => options.RecipeName == null
           && options.NamedSearchQueries.Count == 0
           && string.Equals(options.Query?.Trim(), "token", StringComparison.OrdinalIgnoreCase);

    private static SearchQueryHint? BuildBareTokenSearchHint(QueryCommandOptions options)
        => IsBareTokenSearch(options)
            ? new SearchQueryHint
            {
                Reason = "bare_token_query_auth_noise",
                SuggestedAction = BareTokenAuthAuditHint,
                Flag = "--recipe",
                McpArgument = "recipe",
            }
            : null;

    private static SearchQueryHint? BuildSearchPathGlobHint(DbReader reader, QueryCommandOptions options)
    {
        if (options.PathPatterns.Count != 1)
            return null;
        var pattern = options.PathPatterns[0].Replace('\\', '/').TrimEnd('/');
        if (pattern.Length == 0 || ContainsGlobMeta(pattern) || pattern.EndsWith("/**", StringComparison.Ordinal))
            return null;

        var anchoredMatches = reader.ListFiles(
            query: null,
            limit: 1,
            lang: options.Lang,
            pathPatterns: [pattern],
            excludePathPatterns: options.ExcludePaths,
            excludeTests: options.ExcludeTests,
            since: options.Since);
        if (anchoredMatches.Count > 0)
            return null;

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

    private static void AddBareTokenSearchHint(JsonObject payload, QueryCommandOptions options)
    {
        var hint = BuildBareTokenSearchHint(options);
        if (hint != null)
            payload["token_domain_hint"] = BuildSearchQueryHintJson(hint);
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
        if (options.MatchOrigins.Count > 0)
            query["match_origins"] = JsonSerializer.SerializeToNode(options.MatchOrigins, CliJsonSerializerContextFactory.Create(jsonOptions).ListString);
        if (options.ExcludeOrigins.Count > 0)
            query["exclude_origins"] = JsonSerializer.SerializeToNode(options.ExcludeOrigins, CliJsonSerializerContextFactory.Create(jsonOptions).ListString);
        if (options.ResultKinds.Count > 0)
            query["result_kinds"] = JsonSerializer.SerializeToNode(options.ResultKinds, CliJsonSerializerContextFactory.Create(jsonOptions).ListString);
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
        if (options.DependencyCycles)
            query["cycles"] = true;
        if (options.DependencySuppressNoise)
            query["suppress_noise"] = true;
        if (options.DependencySymbols.Count > 0)
            query["symbol"] = JsonSerializer.SerializeToNode(options.DependencySymbols, CliJsonSerializerContextFactory.Create(jsonOptions).ListString);
        if (options.DependencySymbolFamilies.Count > 0)
            query["symbol_family"] = JsonSerializer.SerializeToNode(options.DependencySymbolFamilies, CliJsonSerializerContextFactory.Create(jsonOptions).ListString);
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
            resultsKey: countOnly || queryOptions?.SummaryOnly == true ? null : "hotspots",
            includeFiles: countOnly,
            graphTableAvailable: graphAvailable,
            degraded: !graphAvailable,
            queryOptions: queryOptions,
            extraFields: static zeroPayload =>
            {
                zeroPayload["definition_site_total"] = 0;
                zeroPayload["grouped_by"] = HotspotsGroupedByNameKind;
            });
        AddHotspotsGroupingContractJsonFields(payload, HotspotsGroupedByNameKind, queryOptions, jsonOptions, countOnly);
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
        var field = FindStatusFieldExplanation(fieldName);
        if (field == null)
        {
            CommandErrorWriter.WriteStderr($"Error: unknown status field `{fieldName}`.");
            CommandErrorWriter.WriteStderr($"Hint: use one of: {string.Join(", ", StatusExplainFields.Select(f => f.FieldName))}.");
            return CommandExitCodes.UsageError;
        }

        Console.WriteLine($"{field.Label} ({field.FieldName})");
        Console.WriteLine();
        Console.WriteLine($"Ready: {field.ReadyText}");
        Console.WriteLine($"Degraded: {field.DegradedText}");
        Console.WriteLine($"Remediation: {field.Remediation}");
        return CommandExitCodes.Success;
    }

    private static int WriteStatusReadinessExplanationJson(string fieldName, JsonSerializerOptions jsonOptions)
    {
        var field = FindStatusFieldExplanation(fieldName);
        if (field == null)
            return CommandErrorWriter.WriteJsonOrHuman(
                true,
                jsonOptions,
                $"unknown status field `{fieldName}`.",
                CommandExitCodes.UsageError,
                $"use one of: {string.Join(", ", StatusExplainFields.Select(f => f.FieldName))}.",
                errorCode: CommandErrorCodes.UsageError,
                category: "usage");

        var knownFields = new JsonArray();
        foreach (var knownField in StatusExplainFields)
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
        CommandOutputWriter.WriteJsonNode(payload, jsonOptions);
        return CommandExitCodes.Success;
    }

    private static StatusFieldExplanation? FindStatusFieldExplanation(string fieldName)
        => StatusExplainFields.FirstOrDefault(
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
        "package",
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
        ["annotation", "attribute", "augmentation", "bcl_regex_without_timeout", "call", "consumes_hook", "dependency", "friend", "import", "instantiate", "razor_event_binding", "subscribe", "type_reference", "unsubscribe"];
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
            ["--limit"] = MaxQueryResultLimit,
            ["--max-results"] = MaxQueryResultLimit,
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
        ["--outline-only"] = "for inspect, return file, definitions, and nearby_symbols JSON only; add `--body` when definition body snippets are needed.",
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
        ["--snippet-lines"] = "pass an integer between 1 and 20, e.g. `--snippet-lines 8` (default 8); issue-draft output also accepts 0 for path/line-only evidence.",
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
    public bool JsonOutputFormatExplicit { get; init; }
    public string OutputFormat { get; init; } = "text";
    public int Limit { get; init; } = 20;
    public int? TotalLimit { get; init; }
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
    public string? SortValue { get; init; }
    public bool SortExplicit { get; init; }
    public List<string> ExtraNames { get; init; } = [];
    public List<string>? MapSections { get; init; }
    public bool SummaryOnly { get; init; }
    public bool MapSummaryOnly { get; init; }
    public bool DependencyCycles { get; init; }
    public bool DependencySuppressNoise { get; init; }
    public List<string> DependencySymbols { get; init; } = [];
    public List<string> DependencySymbolFamilies { get; init; } = [];
    public string? RecipeName { get; init; }
    public List<string> IncludeRecipeQueries { get; init; } = [];
    public List<string> ExcludeRecipeQueries { get; init; } = [];
    public bool ShowExcluded { get; init; }
    public bool ListRecipes { get; init; }
    public bool NamesOnly { get; init; }
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
    public bool SourceOnly { get; init; }
    public bool NoSemanticTokens { get; init; }
    public string? ParseError { get; init; }
}

public sealed record SearchNamedQuery(string Name, string Query);

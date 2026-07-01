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
    private static void AddJsonByteLimitField(JsonObject payload, QueryCommandOptions options)
    {
        if (options.MaxJsonBytes.HasValue)
            payload["output_byte_limit"] = options.MaxJsonBytes.Value;
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

    private static string FormatSamples(IReadOnlyList<string> samples)
        => samples.Count == 0 ? string.Empty : $" ({string.Join(", ", samples)})";

    private static string ShortSha(string? sha)
    {
        if (string.IsNullOrWhiteSpace(sha))
            return "<unknown>";
        return sha.Length <= 12 ? sha : sha[..12];
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

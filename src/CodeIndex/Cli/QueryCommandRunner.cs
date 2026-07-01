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

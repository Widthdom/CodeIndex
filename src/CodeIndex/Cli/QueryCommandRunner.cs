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
    internal const int MapIssueDraftLineThreshold = 800;
    internal const long MapIssueDraftByteThreshold = 64 * 1024;
    internal const int DefaultImpactLimit = 50;
    internal const int DefaultDependencyCycleGraphBudget = 10_000;
    internal const int MaxDependencyCycleGraphBudget = 1_000_000;
    internal const int DefaultDependencyCycleNodeLimit = 50;
    internal const int GraphLivenessLimitThreshold = 80;
    internal const string DependencyCycleDetectionMode = "deterministic_scc";
    internal const int MaxWorkspaceDependencyDatabaseCount = 8;
    internal const int MaxWorkspaceDependencyDatabasePairCount = MaxWorkspaceDependencyDatabaseCount * (MaxWorkspaceDependencyDatabaseCount - 1);
    internal const int FindAllCandidateFileLimit = 4096;
    internal const int FindAllLineScanLimit = 250_000;
    private const int MaxOutlineProjectionFieldsCsvLength = 256;
    private const int MaxOutlineProjectionFieldsCsvEntries = 16;
    // Cap OR-joined `symbols` names well below SQLite's 1000 expression-tree depth so oversized
    // batches fail fast with a clear usage error instead of a confusing SQLite exception.
    // OR 結合の `symbols` 名は SQLite の式木深さ上限 1000 を十分下回る値で頭打ちにし、
    // 大量バッチを SQLite 例外ではなく明確な usage error で早期に弾く。
    internal const int MaxSymbolQueryNames = 256;
    // Dependency filters are expanded into OR-joined SQLite predicates. Keep the combined
    // exact/family count comfortably below SQLite's expression-tree depth limit.
    // dependency filter は OR 結合の SQLite 条件へ展開されるため、exact/family の合計を
    // SQLite の式木深さ上限より十分小さい値に制限する。
    internal const int MaxDependencySymbolFilterCount = 256;
    internal const int MaxMapSectionsCsvLength = 256;
    internal const int MaxMapSectionsCsvEntries = 16;
    internal const int MaxInspectFieldsCsvLength = 256;
    internal const int MaxInspectFieldsCsvEntries = 16;
    internal const int MaxVisibilityFilterCsvLength = 256;
    internal const int MaxVisibilityFilterCsvEntries = 16;
    private const int MaxUnusedPaginationPages = 10;
    internal const int MaxUnusedPaginationFetchLimit = MaxQueryResultLimit * MaxUnusedPaginationPages + 1;
    internal const int MaxUnusedPaginationOffset = MaxUnusedPaginationFetchLimit - MaxQueryResultLimit - 1;
    private const int UnusedDefaultSuppressionOverfetchMultiplier = 6;
    internal const string HotspotsGroupedByNameKind = "name_kind";
    internal const string HotspotsGroupedBySymbol = "symbol";
    internal const string HotspotsGroupedByFile = "file";
    internal const string HotspotsGroupedByStatement = "statement";
    internal const string StatusCheckModeExplicit = "explicit";
    internal const string StatusCheckModeImpliedByStaleAfter = "implied_by_stale_after";
    public static string FormatReferenceRankMode(ReferenceRankMode mode) => mode switch
    {
        ReferenceRankMode.Count => "count",
        ReferenceRankMode.Kind => "kind",
        _ => "weighted",
    };

    internal static JsonObject BuildReferenceRankingRecipeJson(ReferenceRankMode mode)
    {
        var precedence = new JsonArray();
        foreach (var dimension in ReferenceRankRecipes.Get(mode))
            precedence.Add(ReferenceRankRecipes.Format(dimension));

        return new JsonObject
        {
            ["mode"] = FormatReferenceRankMode(mode),
            ["precedence"] = precedence,
        };
    }
}

public sealed class QueryCommandOptions
{
    internal QueryCommandInvocationContext InvocationContext { get; set; } = QueryCommandInvocationContext.Search;
    internal JsonSerializerOptions? InvocationJsonOptions { get; set; }
    internal bool InvocationMachineErrorOutputRequested { get; set; }
    public string DbPath { get; init; } = Path.Combine(".cdidx", "codeindex.db");
    public bool DbPathExplicit { get; init; }
    public bool ReadOnly { get; init; }
    public bool DryRun { get; init; }
    public bool ShowPaths { get; init; }
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
    public bool AllowUnknownLang { get; init; }
    public bool LanguageValidationError { get; init; }
    public string? Kind { get; init; }
    public string? UnusedBucket { get; init; }
    public string? MinUnusedConfidence { get; init; }
    public bool UnusedActionable { get; init; }
    public string? Severity { get; init; }
    public List<string> VisibilityFilters { get; init; } = [];
    public List<string> ExcludeVisibilityFilters { get; init; } = [];
    public string? Query { get; init; }
    public string? Selector { get; init; }
    public bool RawFts { get; init; }
    public bool IncludeBody { get; init; }
    public int? BodyStartLine { get; init; }
    public int? BodyLines { get; init; }
    public int? StartLine { get; init; }
    public int? EndLine { get; init; }
    public int ContextBefore { get; init; }
    public int ContextAfter { get; init; }
    public bool ContextAfterExplicit { get; init; }
    public int? SymmetricContext { get; init; }
    public int? ExplicitContextBefore { get; init; }
    public int? ExplicitContextAfter { get; init; }
    public bool ImpactDeprecatedDepthUsed { get; init; }
    public int? FocusLine { get; init; }
    public int? FocusColumn { get; init; }
    public int FocusLength { get; init; } = 1;
    public int SnippetLines { get; init; } = SearchSnippetFormatter.DefaultSnippetLines;
    internal bool SnippetLinesExplicit { get; init; }
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
    public bool GroupPartials { get; init; }
    public bool All { get; init; }
    public bool StrictNotFound { get; init; }
    public bool AllowPartial { get; init; }
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
    public bool TokenBoundary { get; init; }
    public bool CheckWorkspace { get; init; }
    public string? StatusCheckMode { get; init; }
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
    public bool GroupedPerFileLimitExplicit { get; init; }
    public int? SampleSize { get; init; }
    public int? RequestedMaxJsonBytes { get; init; }
    public int? MaxJsonBytes { get; init; }
    public bool RawBytes { get; init; }
    public bool RawKinds { get; init; }
    public bool IncludeQualifiedCommonCalls { get; init; }
    public bool IncludeMemberReads { get; init; }
    public bool Verbose { get; init; }
    public bool Profile { get; init; }
    public int? SlowQueryMs { get; init; }
    public bool Compact { get; init; }
    public List<string>? InspectFields { get; init; }
    internal ProjectionFieldValidationError? InspectFieldValidationError { get; init; }
    public double MinEntrypointConfidence { get; init; }
    public string? StatusExplainField { get; init; }
    public bool StatusLogPath { get; init; }
    public bool StatusConfig { get; init; }
    public bool? RedactPaths { get; init; }
    public ReferenceRankMode RankMode { get; init; } = ReferenceRankMode.Weighted;
    internal bool ReferenceRankingActive { get; set; }
    public SymbolSortMode SymbolSortMode { get; init; } = SymbolSortMode.Name;
    public string? SortValue { get; init; }
    public bool SortExplicit { get; init; }
    public List<string> ExtraNames { get; init; } = [];
    public List<string>? MapSections { get; init; }
    public bool SummaryOnly { get; init; }
    public bool MapSummaryOnly { get; init; }
    public bool DependencyCycles { get; init; }
    public int DependencyCycleGraphBudget { get; init; } = QueryCommandRunner.DefaultDependencyCycleGraphBudget;
    public bool IncludeAllDependencyCycleNodes { get; init; }
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
    public string IssueState { get; init; } = IssueDuplicatePreflight.DefaultIssueState;
    public string DuplicateConfidence { get; init; } = IssueDuplicatePreflight.DefaultDuplicateConfidence;
    public double DuplicateThreshold { get; init; } = IssueDuplicatePreflight.DefaultDuplicateThreshold;
    public bool DuplicatePreflightTuningExplicit { get; init; }
    public string? IssueTitle { get; init; }
    public List<string> IssueLabels { get; init; } = [];
    public SearchCursor? SearchCursor { get; init; }
    public int? UnusedCursorOffset { get; init; }
    public int? OutlineCursorOffset { get; init; }
    public string? CursorValue { get; init; }
    public DependencyCycleCursor? DependencyCycleCursor { get; init; }
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

public readonly record struct DependencyCycleCursor(int Offset, string Fingerprint);

internal enum RecipeReplayOutputCapability
{
    Default,
    ResultsOnlyNdjson,
}

internal sealed record QueryCommandInvocationContext(
    string CommandName,
    string UsageCommandName,
    string ValidationCommandName,
    bool RecipeNameIsPositional,
    bool StructuredMachineUsageErrors,
    bool SupportsRecipeResultsOnlyNdjson)
{
    internal static QueryCommandInvocationContext Search { get; } =
        new(
            "search",
            "search",
            "search",
            RecipeNameIsPositional: false,
            StructuredMachineUsageErrors: false,
            SupportsRecipeResultsOnlyNdjson: true);

    internal static QueryCommandInvocationContext Recipes { get; } =
        new(
            "recipes",
            "recipes",
            "recipes",
            RecipeNameIsPositional: false,
            StructuredMachineUsageErrors: false,
            SupportsRecipeResultsOnlyNdjson: false);

    internal static QueryCommandInvocationContext Audit { get; } =
        new(
            "audit",
            "audit",
            "search",
            RecipeNameIsPositional: true,
            StructuredMachineUsageErrors: true,
            SupportsRecipeResultsOnlyNdjson: false);

    internal string UsageLine =>
        ConsoleUi.GetUsageLine(UsageCommandName)
        ?? throw new InvalidOperationException($"Missing usage line for command '{UsageCommandName}'.");

    internal string RecipeDiscoveryCommand =>
        RecipeNameIsPositional ? "cdidx recipes" : "cdidx search --list-recipes";

    internal string RecipeSelectorSyntax =>
        RecipeNameIsPositional
            ? "cdidx audit <recipe>/<query>"
            : "cdidx search --recipe <recipe>/<query>";

    internal string RecipeCommandPrefix =>
        RecipeNameIsPositional ? "cdidx audit" : "cdidx search --recipe";

    internal string RecipeExecutionName =>
        RecipeNameIsPositional ? "audit" : "search --recipe";

    internal string RecipeCursorSelectorSyntax =>
        RecipeNameIsPositional ? "cdidx audit <recipe>/<query>" : "--recipe <recipe>/<query>";

    internal void AddRecipeCommandPrefix(
        List<string> args,
        string recipeSelector,
        RecipeReplayOutputCapability outputCapability = RecipeReplayOutputCapability.Default)
    {
        var replayContext = outputCapability == RecipeReplayOutputCapability.ResultsOnlyNdjson
            && !SupportsRecipeResultsOnlyNdjson
                ? Search
                : this;
        args.Add("cdidx");
        args.Add(replayContext.CommandName);
        if (!replayContext.RecipeNameIsPositional)
            args.Add("--recipe");
        args.Add(recipeSelector);
    }
}

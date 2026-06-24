using CodeIndex.Models;

namespace CodeIndex.Cli;

public static partial class QueryCommandRunner
{
    private static readonly List<string> SearchRecipeSupportedFormats = ["text", "json", "count", "compact", OutputFormatIssueDrafts];
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
}

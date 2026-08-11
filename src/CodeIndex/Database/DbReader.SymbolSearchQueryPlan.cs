namespace CodeIndex.Database;

public partial class DbReader
{
    private sealed record SymbolSearchQueryPlan
    {
        public IReadOnlyList<string>? Queries { get; init; }
        public int Limit { get; init; } = 20;
        public string? Kind { get; init; }
        public string? Lang { get; init; }
        public IReadOnlyList<string>? PathPatterns { get; init; }
        public IReadOnlyList<string>? ExcludePathPatterns { get; init; }
        public bool ExcludeTests { get; init; }
        public DateTime? Since { get; init; }
        public bool Exact { get; init; }
        public IReadOnlyList<string>? VisibilityFilters { get; init; }
        public IReadOnlyList<string>? ExcludeVisibilityFilters { get; init; }
        public SymbolSortMode SortMode { get; init; } = SymbolSortMode.Name;
        public int? StartLine { get; init; }
        public int? EndLine { get; init; }
        public bool GroupPartials { get; init; }
        public int Offset { get; init; }
    }

    private static class SymbolSearchQueryPlanBuilder
    {
        public static SymbolSearchQueryPlan Build(SymbolSearchQueryPlan source)
        {
            var lang = NormalizeQueryLanguage(source.Lang);
            return source with
            {
                Lang = lang,
                Queries = NormalizeSymbolSearchQueries(source.Queries, lang, source.Exact),
            };
        }
    }
}

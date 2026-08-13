namespace CodeIndex.Database;

public partial class DbReader
{
    public int CountSearchSymbols(IReadOnlyList<string>? queries, int limit = 20, string? kind = null, string? lang = null, IReadOnlyList<string>? pathPatterns = null, IReadOnlyList<string>? excludePathPatterns = null, bool excludeTests = false, DateTime? since = null, bool exact = false, IReadOnlyList<string>? visibilityFilters = null, IReadOnlyList<string>? excludeVisibilityFilters = null)
    {
        var plan = SymbolSearchQueryPlanBuilder.Build(new SymbolSearchQueryPlan
        {
            Queries = queries,
            Limit = limit,
            Kind = kind,
            Lang = lang,
            PathPatterns = pathPatterns,
            ExcludePathPatterns = excludePathPatterns,
            ExcludeTests = excludeTests,
            Since = since,
            Exact = exact,
            VisibilityFilters = visibilityFilters,
            ExcludeVisibilityFilters = excludeVisibilityFilters,
        });
        return ExecuteBoundedSymbolSearch(plan);
    }

    private int ExecuteBoundedSymbolSearch(SymbolSearchQueryPlan plan)
    {
        if (plan.Queries != null
            && (plan.Queries.Count > 1
                || string.Equals(plan.Lang, "markdown", StringComparison.Ordinal)))
        {
            return SearchSymbols(
                plan.Queries,
                plan.Limit,
                plan.Kind,
                plan.Lang,
                plan.PathPatterns,
                plan.ExcludePathPatterns,
                plan.ExcludeTests,
                plan.Since,
                plan.Exact,
                plan.VisibilityFilters,
                plan.ExcludeVisibilityFilters).Count;
        }

        using var cmd = _conn.CreateCommand();

        var innerSql = @"
            SELECT 1
            FROM symbols s
            JOIN files f ON s.file_id = f.id
            WHERE 1=1";

        innerSql += SymbolSearchQueryPredicateBuilder.BuildBounded(this, plan);
        SymbolSearchQueryPredicateBuilder.AppendFilters(this, ref innerSql, plan, includeLineRange: false);
        innerSql += " LIMIT @limit";

        cmd.CommandText = $"SELECT COUNT(*) FROM ({innerSql})";
        SymbolSearchQueryBinder.BindBoundedQuery(this, cmd, plan);
        SymbolSearchQueryBinder.BindFilters(this, cmd, plan, includeLineRange: false);
        SqliteCommandPolicy.Add(cmd, "@limit", plan.Limit);

        var raw = cmd.ExecuteScalar();
        return raw is long l ? (int)l : Convert.ToInt32(raw);
    }
}

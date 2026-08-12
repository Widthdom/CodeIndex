namespace CodeIndex.Database;

public partial class DbReader
{
    public QueryCountResult CountSearchSymbolsTotal(string? query = null, string? kind = null, string? lang = null, IReadOnlyList<string>? pathPatterns = null, IReadOnlyList<string>? excludePathPatterns = null, bool excludeTests = false, DateTime? since = null, bool exact = false, IReadOnlyList<string>? visibilityFilters = null, IReadOnlyList<string>? excludeVisibilityFilters = null, bool groupPartials = false)
    {
        return CountSearchSymbolsTotal(query == null ? null : new[] { NormalizeSymbolSearchQueryForSymbolSearch(query, lang, exact) ?? query ?? string.Empty }, kind, lang, pathPatterns, excludePathPatterns, excludeTests, since, exact, visibilityFilters, excludeVisibilityFilters, groupPartials);
    }

    public QueryCountResult CountSearchSymbolsTotal(IReadOnlyList<string>? queries, string? kind = null, string? lang = null, IReadOnlyList<string>? pathPatterns = null, IReadOnlyList<string>? excludePathPatterns = null, bool excludeTests = false, DateTime? since = null, bool exact = false, IReadOnlyList<string>? visibilityFilters = null, IReadOnlyList<string>? excludeVisibilityFilters = null, bool groupPartials = false)
    {
        var plan = SymbolSearchQueryPlanBuilder.Build(new SymbolSearchQueryPlan
        {
            Queries = queries,
            Kind = kind,
            Lang = lang,
            PathPatterns = pathPatterns,
            ExcludePathPatterns = excludePathPatterns,
            ExcludeTests = excludeTests,
            Since = since,
            Exact = exact,
            VisibilityFilters = visibilityFilters,
            ExcludeVisibilityFilters = excludeVisibilityFilters,
            GroupPartials = groupPartials,
        });
        return ExecuteTotalSymbolSearch(plan);
    }

    private QueryCountResult ExecuteTotalSymbolSearch(SymbolSearchQueryPlan plan)
    {
        if (plan.GroupPartials)
        {
            EnsureCSharpCallableTypeKinds(
                plan.Lang,
                plan.Queries,
                plan.Exact,
                plan.Kind);
        }
        using var cmd = _conn.CreateCommand();

        var logicalPartialKeySql = LogicalPartialQuerySql.BuildKey(
            this,
            GetSymbolColumnSql("signature"),
            GetSymbolColumnSql("container_name"),
            GetSymbolColumnSql("container_qualified_name"),
            GetSymbolColumnSql("family_key"),
            GetSymbolColumnSql("return_type"));
        var countSql = plan.GroupPartials
            ? $"COUNT(DISTINCT ({logicalPartialKeySql}))"
            : "COUNT(*)";
        var sql = $@"
            SELECT {countSql}, COUNT(DISTINCT f.path)
            FROM symbols s
            JOIN files f ON s.file_id = f.id
            WHERE 1=1";

        sql += SymbolSearchQueryPredicateBuilder.BuildFull(this, plan);
        SymbolSearchQueryPredicateBuilder.AppendFilters(this, ref sql, plan, includeLineRange: false);

        cmd.CommandText = sql;
        SymbolSearchQueryBinder.BindFullQueries(this, cmd, plan);
        SymbolSearchQueryBinder.BindFilters(this, cmd, plan, includeLineRange: false);

        using var reader = cmd.ExecuteTrackedReader();
        return reader.TrackedRead()
            ? new QueryCountResult(reader.GetInt32(0), reader.GetInt32(1))
            : new QueryCountResult(0, 0);
    }
}

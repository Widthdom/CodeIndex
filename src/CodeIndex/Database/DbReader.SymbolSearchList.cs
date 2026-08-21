namespace CodeIndex.Database;

public partial class DbReader
{
    /// <summary>
    /// Search symbols by one or more name patterns (OR-joined). Empty/null list returns all symbols matching other filters.
    /// When <paramref name="exact"/> is true, names are matched case-insensitively for equality instead of substring.
    /// 複数名前パターン（OR結合）でシンボルを検索。空/null なら他フィルタに一致する全シンボルを返す。
    /// <paramref name="exact"/> が true の場合、部分一致ではなく大文字小文字を無視した完全一致になる。
    /// </summary>
    public List<SymbolResult> SearchSymbols(IReadOnlyList<string>? queries, int limit = 20, string? kind = null, string? lang = null, IReadOnlyList<string>? pathPatterns = null, IReadOnlyList<string>? excludePathPatterns = null, bool excludeTests = false, DateTime? since = null, bool exact = false, IReadOnlyList<string>? visibilityFilters = null, IReadOnlyList<string>? excludeVisibilityFilters = null, SymbolSortMode sortMode = SymbolSortMode.Name, int? startLine = null, int? endLine = null, bool groupPartials = false, int offset = 0, string? partialFamilyId = null, int familyMemberOffset = 0)
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
            SortMode = sortMode,
            StartLine = startLine,
            EndLine = endLine,
            GroupPartials = groupPartials,
            Offset = offset,
            PartialFamilyId = partialFamilyId,
            FamilyMemberOffset = Math.Max(0, familyMemberOffset),
        });
        return ExecuteSymbolSearchList(plan);
    }

    private List<SymbolResult> ExecuteSymbolSearchList(SymbolSearchQueryPlan plan)
    {
        if (plan.Queries is { Count: > 1 })
            return ExecuteMultiQuerySymbolSearch(plan);

        if (plan.GroupPartials)
        {
            EnsureCSharpCallableTypeKinds(
                plan.Lang,
                plan.Queries,
                plan.Exact,
                plan.Kind);
        }
        using var cmd = _conn.CreateCommand();

        var sql = BuildSymbolSearchListSql(plan);
        cmd.CommandText = sql;
        SymbolSearchQueryBinder.BindFullQueries(this, cmd, plan);
        SymbolSearchQueryBinder.BindListOrdering(cmd, plan);
        SymbolSearchQueryBinder.BindFilters(this, cmd, plan, includeLineRange: true);
        SqliteCommandPolicy.Add(cmd, "@limit", plan.Limit);
        SqliteCommandPolicy.Add(cmd, "@offset", Math.Max(0, plan.Offset));
        if (plan.GroupPartials)
        {
            SqliteCommandPolicy.Add(cmd, "@familyMemberOffset", plan.FamilyMemberOffset);
            if (plan.PartialFamilyId is not null)
                SqliteCommandPolicy.Add(cmd, "@partialFamilyId", plan.PartialFamilyId);
        }

        using var reader = cmd.ExecuteTrackedReader();
        return SymbolSearchRowProjector.ReadAll(reader, plan);
    }
}

namespace CodeIndex.Database;

public partial class DbReader
{
    // Run one search per name so a common earlier-sorting name cannot starve another
    // query, then round-robin under the original global limit/offset contract.
    private List<SymbolResult> ExecuteMultiQuerySymbolSearch(SymbolSearchQueryPlan plan)
    {
        var queries = plan.Queries!;
        var requestedPrefix = checked(plan.Limit + Math.Max(0, plan.Offset));
        var perName = new List<List<SymbolResult>>(queries.Count);
        foreach (var query in queries)
        {
            perName.Add(SearchSymbols(
                new[] { query! },
                requestedPrefix,
                plan.Kind,
                plan.Lang,
                plan.PathPatterns,
                plan.ExcludePathPatterns,
                plan.ExcludeTests,
                plan.Since,
                plan.Exact,
                plan.VisibilityFilters,
                plan.ExcludeVisibilityFilters,
                plan.SortMode,
                plan.StartLine,
                plan.EndLine,
                plan.GroupPartials));
        }

        var seen = new HashSet<long?>();
        var merged = new List<SymbolResult>();
        var cursors = new int[perName.Count];
        bool advanced;
        do
        {
            advanced = false;
            for (var index = 0; index < perName.Count && merged.Count < requestedPrefix; index++)
            {
                while (cursors[index] < perName[index].Count)
                {
                    var result = perName[index][cursors[index]++];
                    if (seen.Add(result.SymbolId))
                    {
                        merged.Add(result);
                        advanced = true;
                        break;
                    }
                }
            }
        } while (advanced && merged.Count < requestedPrefix);

        return merged
            .Skip(Math.Max(0, plan.Offset))
            .Take(plan.Limit)
            .ToList();
    }
}

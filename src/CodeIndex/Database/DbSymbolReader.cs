using System.Globalization;
using System.Text;
using System.Text.RegularExpressions;
using CodeIndex.Indexer;
using Microsoft.Data.Sqlite;
using Regex = CodeIndex.Indexer.BoundedRegex;

namespace CodeIndex.Database;

/// <summary>
/// Symbol query operations: search, definitions, outline, analyze (partial class split from DbReader.cs).
/// シンボルクエリ操作: 検索、定義、アウトライン、分析（DbReader.csからのpartial class分割）。
/// </summary>
public partial class DbReader
{
    private const string SymbolLanguageFileIdFilter = " AND s.file_id IN (SELECT id FROM files WHERE lang = @lang)";
    private void AppendVisibilityFilters(ref string sql, IReadOnlyList<string>? visibilityFilters, IReadOnlyList<string>? excludeVisibilityFilters)
    {
        var expandedVisibilityFilters = visibilityFilters is { Count: > 0 } ? ExpandVisibilityFilterValues(visibilityFilters) : null;
        var expandedExcludeVisibilityFilters = excludeVisibilityFilters is { Count: > 0 } ? ExpandVisibilityFilterValues(excludeVisibilityFilters) : null;
        EnsureVisibilityFilterParameterBudget(expandedVisibilityFilters, expandedExcludeVisibilityFilters);

        if (expandedVisibilityFilters is { Count: > 0 })
            sql += $" AND lower({GetSymbolColumnSql("visibility", "''")}) IN ({SqliteDynamicSql.BuildParameterList("visibility", expandedVisibilityFilters.Count)})";
        if (expandedExcludeVisibilityFilters is { Count: > 0 })
            sql += $" AND lower({GetSymbolColumnSql("visibility", "''")}) NOT IN ({SqliteDynamicSql.BuildParameterList("excludeVisibility", expandedExcludeVisibilityFilters.Count)})";
    }

    private static void AddVisibilityFilterParameters(SqliteCommand cmd, IReadOnlyList<string>? visibilityFilters, IReadOnlyList<string>? excludeVisibilityFilters)
    {
        var expandedVisibilityFilters = visibilityFilters is { Count: > 0 } ? ExpandVisibilityFilterValues(visibilityFilters) : null;
        var expandedExcludeVisibilityFilters = excludeVisibilityFilters is { Count: > 0 } ? ExpandVisibilityFilterValues(excludeVisibilityFilters) : null;
        EnsureVisibilityFilterParameterBudget(expandedVisibilityFilters, expandedExcludeVisibilityFilters);

        if (expandedVisibilityFilters is { Count: > 0 })
            SqliteDynamicSql.AddParameters(cmd, "visibility", expandedVisibilityFilters, SqliteType.Text, "visibility filters");

        if (expandedExcludeVisibilityFilters is { Count: > 0 })
            SqliteDynamicSql.AddParameters(cmd, "excludeVisibility", expandedExcludeVisibilityFilters, SqliteType.Text, "visibility filters");
    }

    private static void EnsureVisibilityFilterParameterBudget(IReadOnlyCollection<string>? visibilityFilters, IReadOnlyCollection<string>? excludeVisibilityFilters)
        => SqliteDynamicSql.EnsureParameterBudget((visibilityFilters?.Count ?? 0) + (excludeVisibilityFilters?.Count ?? 0), "visibility filters");

    private static List<string> ExpandVisibilityFilterValues(IReadOnlyList<string> filters)
    {
        var expanded = new List<string>();
        foreach (var filter in filters)
        {
            string[] aliases = filter switch
            {
                "public" => ["public", "pub", "open", "export"],
                "private" => ["private", "fileprivate"],
                _ => [filter],
            };

            foreach (var alias in aliases)
            {
                if (!expanded.Contains(alias, StringComparer.Ordinal))
                    expanded.Add(alias);
            }
        }

        return expanded;
    }
}

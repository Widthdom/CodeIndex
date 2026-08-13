using CodeIndex.Indexer;
using CodeIndex.Models;
using Microsoft.Data.Sqlite;
using System.Text;

namespace CodeIndex.Database;

public partial class DbReader
{
    private DependencySqlFragment BuildDependencyGraphLanguagePredicate(string fileAlias, string parameterPrefix)
    {
        var supportedLanguages = GetWorkspaceSupportedReferenceLanguages()
            .OrderBy(language => language, StringComparer.Ordinal)
            .ToList();
        if (supportedLanguages.Count == 0)
            return new DependencySqlFragment("1 = 0", Array.Empty<DependencyQueryParameter>());

        var parameters = new DependencyQueryParameter[supportedLanguages.Count];
        for (var i = 0; i < supportedLanguages.Count; i++)
        {
            var parameterName = $"@{parameterPrefix}{i}";
            parameters[i] = DependencyQueryParameter.Text(parameterName, supportedLanguages[i]);
        }

        return new DependencySqlFragment(
            BuildGraphSupportedLanguagePredicateSql(supportedLanguages, fileAlias, parameterPrefix),
            parameters);
    }

    private static DependencySqlFragment BuildDependencySymbolFilter(
        string symbolSql,
        IReadOnlyList<string>? dependencySymbols,
        IReadOnlyList<string>? dependencySymbolFamilies,
        bool suppressDependencyNoise,
        string parameterPrefix,
        string? filterScopeSql = null)
    {
        var sql = new StringBuilder();
        var parameters = new List<DependencyQueryParameter>();
        AppendRequestedDependencySymbols(
            sql,
            parameters,
            symbolSql,
            dependencySymbols,
            dependencySymbolFamilies,
            parameterPrefix,
            filterScopeSql);
        AppendDependencyNoiseFilter(
            sql,
            parameters,
            symbolSql,
            suppressDependencyNoise,
            parameterPrefix,
            filterScopeSql);
        return new DependencySqlFragment(sql.ToString(), parameters.ToArray());
    }

    private static void AppendRequestedDependencySymbols(
        StringBuilder sql,
        List<DependencyQueryParameter> parameters,
        string symbolSql,
        IReadOnlyList<string>? dependencySymbols,
        IReadOnlyList<string>? dependencySymbolFamilies,
        string parameterPrefix,
        string? filterScopeSql)
    {
        if (dependencySymbols is not { Count: > 0 } && dependencySymbolFamilies is not { Count: > 0 })
            return;

        var predicates = new List<string>((dependencySymbols?.Count ?? 0) + (dependencySymbolFamilies?.Count ?? 0));
        if (dependencySymbols != null)
        {
            for (var i = 0; i < dependencySymbols.Count; i++)
            {
                var parameterName = $"@{parameterPrefix}Symbol{i}";
                predicates.Add($"({symbolSql}) = {parameterName}");
                parameters.Add(DependencyQueryParameter.Text(parameterName, dependencySymbols[i]));
            }
        }
        if (dependencySymbolFamilies != null)
        {
            for (var i = 0; i < dependencySymbolFamilies.Count; i++)
            {
                var parameterName = $"@{parameterPrefix}Family{i}";
                predicates.Add($"({symbolSql}) GLOB {parameterName}");
                parameters.Add(DependencyQueryParameter.Text(
                    parameterName,
                    EscapeSqliteGlobLiteral(dependencySymbolFamilies[i]) + "*"));
            }
        }

        AppendScopedDependencyPredicate(sql, "(" + string.Join(" OR ", predicates) + ")", filterScopeSql);
    }

    private static void AppendDependencyNoiseFilter(
        StringBuilder sql,
        List<DependencyQueryParameter> parameters,
        string symbolSql,
        bool suppressDependencyNoise,
        string parameterPrefix,
        string? filterScopeSql)
    {
        if (!suppressDependencyNoise)
            return;

        var parameterNames = new string[DependencyNoiseProfile.SymbolNames.Length];
        for (var i = 0; i < DependencyNoiseProfile.SymbolNames.Length; i++)
        {
            var parameterName = $"@{parameterPrefix}Noise{i}";
            parameterNames[i] = parameterName;
            parameters.Add(DependencyQueryParameter.Text(parameterName, DependencyNoiseProfile.SymbolNames[i]));
        }
        AppendScopedDependencyPredicate(
            sql,
            $"({symbolSql}) COLLATE NOCASE NOT IN ({string.Join(", ", parameterNames)})",
            filterScopeSql);
    }

    internal static void AppendDependencySymbolFilter(
        SqliteCommand cmd,
        ref string sql,
        string symbolSql,
        IReadOnlyList<string>? dependencySymbols,
        IReadOnlyList<string>? dependencySymbolFamilies,
        bool suppressDependencyNoise,
        string parameterPrefix,
        string? filterScopeSql = null)
    {
        var fragment = BuildDependencySymbolFilter(
            symbolSql,
            dependencySymbols,
            dependencySymbolFamilies,
            suppressDependencyNoise,
            parameterPrefix,
            filterScopeSql);
        sql += fragment.Sql;
        BindDependencyQueryParameters(cmd, fragment.Parameters);
    }

    private static void AppendScopedDependencyPredicate(StringBuilder sql, string predicate, string? filterScopeSql)
        => sql.Append(filterScopeSql == null
            ? " AND " + predicate
            : $" AND (NOT ({filterScopeSql}) OR {predicate})");

    private static void BindDependencyQueryParameters(
        SqliteCommand command,
        IReadOnlyList<DependencyQueryParameter> parameters)
    {
        for (var i = 0; i < parameters.Count; i++)
        {
            var parameter = parameters[i];
            switch (parameter.Kind)
            {
                case DependencyQueryParameterKind.Text:
                    SqliteCommandPolicy.Add(command, parameter.Name, parameter.TextValue!);
                    break;
                case DependencyQueryParameterKind.Int32:
                    SqliteCommandPolicy.Add(command, parameter.Name, parameter.Int32Value);
                    break;
                default:
                    throw new InvalidOperationException($"Unsupported dependency parameter kind: {parameter.Kind}");
            }
        }
    }

    private static string EscapeSqliteGlobLiteral(string value)
        => value
            .Replace("[", "[[]", StringComparison.Ordinal)
            .Replace("*", "[*]", StringComparison.Ordinal)
            .Replace("?", "[?]", StringComparison.Ordinal);

    private static string DependencyTestPathCondition(string pathSql)
        => "(" + TestPathCondition.Replace("f.path", pathSql) + $" OR lower({pathSql}) LIKE '%.test%/%')";

    private string BuildDependencyGeneratedFilter(string fileAlias)
        => !IncludeGeneratedScope.Value && _fileColumns.Contains("generated")
            ? $" AND COALESCE({fileAlias}.generated, 0) = 0"
            : string.Empty;
}

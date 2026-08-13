using System.Text;
using Microsoft.Data.Sqlite;

namespace CodeIndex.Database;

public partial class DbReader
{
    private readonly record struct SymbolHotspotCandidatePlan(
        string Sql,
        List<string> GraphLanguages,
        List<string> HotspotFamilyLanguages,
        int? CandidateLimit);

    private SymbolHotspotCandidatePlan BuildSymbolHotspotCandidatePlan(
        int? resultLimit,
        string? kind,
        string? lang,
        IReadOnlyList<string>? pathPatterns,
        IReadOnlyList<string>? excludePathPatterns,
        bool excludeTests,
        IReadOnlyList<string>? visibilityFilters,
        IReadOnlyList<string>? excludeVisibilityFilters)
    {
        // Ambiguity is computed from the visibility-filtered language/kind candidate set
        // before path and test filters. This prevents an out-of-scope duplicate from
        // promoting a same-name symbol to codebase-wide counting. Cross-file grouping is
        // allowed only for authoritative family keys in fully-ready languages.
        // 曖昧性は visibility / language / kind 適用後、path / test 適用前に判定する。
        // scope 外の同名定義による誤った全体集計を防ぎ、cross-file 集約は ready な family に限る。
        var graphLanguages = new List<string>(GetWorkspaceSupportedReferenceLanguages());
        var hotspotFamilyLanguages = new List<string>(_hotspotFamilyReadyLanguages);
        hotspotFamilyLanguages.Sort(StringComparer.Ordinal);
        var familyLanguageConditionSql = hotspotFamilyLanguages.Count > 0
            ? $"f.lang IN ({BuildIndexedParameterList("hotspotFamilyLang", hotspotFamilyLanguages.Count)})"
            : "0";
        var familyKeySql = GetSymbolColumnSql("family_key");
        var familyTargetKeySql = hotspotFamilyLanguages.Count > 0
            ? $@"CASE
                    WHEN {familyLanguageConditionSql}
                     AND COALESCE({familyKeySql}, '') <> ''
                        THEN 'family|' || COALESCE(f.lang, '') || '|' || COALESCE(s.kind, '') || '|' || {familyKeySql}
                    ELSE NULL
                END"
            : "NULL";
        var containerQualifiedNameSql = GetSymbolColumnSql("container_qualified_name");
        var containerTargetKeySql = $@"CASE
                    WHEN COALESCE({containerQualifiedNameSql}, '') <> ''
                        THEN 'container|' || CAST(s.file_id AS TEXT) || '|' || COALESCE(s.kind, '') || '|' || {containerQualifiedNameSql}
                    ELSE NULL
                END";
        var candidateLimit = GetBoundedHotspotCandidateLimit(resultLimit);
        var sql = new StringBuilder(capacity: 5_000);
        sql.Append($@"
            WITH {BuildBoundedHotspotCandidatePrefix(candidateLimit)}all_candidate_symbols AS MATERIALIZED (
                SELECT s.id, s.file_id, s.name, s.kind, f.path, f.lang, s.line,
                       {GetSymbolColumnSql("visibility")} AS visibility,
                       {GetSymbolColumnSql("container_name")} AS container_name,
                       CASE
                           WHEN {familyTargetKeySql} IS NOT NULL
                               THEN {familyTargetKeySql}
                           WHEN {containerTargetKeySql} IS NOT NULL
                               THEN {containerTargetKeySql}
                           ELSE 'file|' || CAST(s.file_id AS TEXT)
                       END AS logical_target_key,
                       COALESCE({familyTargetKeySql}, {containerTargetKeySql}) AS count_safe_key
                FROM symbols s
                JOIN files f ON s.file_id = f.id
                WHERE s.kind NOT IN ('import', 'namespace')");
        sql.Append(BuildCSharpHotspotFunctionDefinitionGateSql());
        sql.Append(BuildBoundedHotspotSymbolPredicate(candidateLimit));
        AppendSymbolHotspotCandidateFilters(
            sql,
            graphLanguages,
            kind,
            lang,
            visibilityFilters,
            excludeVisibilityFilters);
        sql.Append(@"
            ),
            name_cardinality AS (
                SELECT lang,
                       name,
                       COUNT(*) AS defs,
                       COUNT(DISTINCT logical_target_key) AS target_groups,
                       COUNT(DISTINCT count_safe_key) AS count_safe_groups,
                       COUNT(count_safe_key) AS count_safe_defs
                FROM all_candidate_symbols
                GROUP BY lang, name
            ),
            filtered_candidates AS MATERIALIZED (
                SELECT id,
                       file_id,
                       name,
                       kind,
                       path,
                       lang,
                       line,
                       visibility,
                       container_name,
                       logical_target_key
                FROM all_candidate_symbols
                WHERE 1 = 1");
        AppendSymbolHotspotPathFilters(
            sql,
            pathPatterns,
            excludePathPatterns,
            excludeTests);
        sql.Append(@"
            ),");
        return new SymbolHotspotCandidatePlan(
            sql.ToString(),
            graphLanguages,
            hotspotFamilyLanguages,
            candidateLimit);
    }

    private void AppendSymbolHotspotCandidateFilters(
        StringBuilder sql,
        IReadOnlyList<string> graphLanguages,
        string? kind,
        string? lang,
        IReadOnlyList<string>? visibilityFilters,
        IReadOnlyList<string>? excludeVisibilityFilters)
    {
        var filterSql = string.Empty;
        if (lang != null)
            filterSql += SymbolLanguageFileIdFilter;
        else
            filterSql += $" AND f.lang IN ({BuildIndexedParameterList("gl", graphLanguages.Count)})";
        if (kind != null)
            filterSql += " AND s.kind = @kind";
        AppendVisibilityFilters(ref filterSql, visibilityFilters, excludeVisibilityFilters);
        sql.Append(filterSql);
    }

    private string BuildCSharpHotspotFunctionDefinitionGateSql() =>
        _symbolColumns.Contains("body_start_line")
        && _symbolColumns.Contains("body_end_line")
        && _symbolColumns.Contains("signature")
        && _symbolColumns.Contains("container_kind")
            ? @"
                  AND NOT (
                      f.lang = 'csharp'
                      AND s.kind = 'function'
                      AND s.container_kind = 'function'
                      AND (
                          (s.body_start_line IS NULL AND s.body_end_line IS NULL)
                          OR (s.container_kind = 'function' AND COALESCE(s.signature, '') LIKE '%.' || s.name || '(%')
                      )
                  )"
            : string.Empty;

    private static void AppendSymbolHotspotPathFilters(
        StringBuilder sql,
        IReadOnlyList<string>? pathPatterns,
        IReadOnlyList<string>? excludePathPatterns,
        bool excludeTests)
    {
        if (pathPatterns is { Count: > 0 })
        {
            sql.Append(" AND (");
            for (var index = 0; index < pathPatterns.Count; index++)
            {
                if (index > 0)
                    sql.Append(" OR ");
                sql.Append(BuildPathColumnFilterPredicate("path", "pathPattern", index, pathPatterns[index]));
            }
            sql.Append(')');
        }
        if (excludePathPatterns != null)
        {
            for (var index = 0; index < excludePathPatterns.Count; index++)
            {
                sql.Append(" AND NOT ");
                sql.Append(BuildPathColumnFilterPredicate(
                    "path",
                    "excludePathPattern",
                    index,
                    excludePathPatterns[index]));
            }
        }
        if (excludeTests)
        {
            sql.Append(" AND NOT ");
            sql.Append(TestPathCondition.Replace("f.path", "path", StringComparison.Ordinal));
        }
    }

    private static string BuildIndexedParameterList(string prefix, int count)
    {
        var parameters = new StringBuilder(count * (prefix.Length + 4));
        for (var index = 0; index < count; index++)
        {
            if (index > 0)
                parameters.Append(',');
            parameters.Append('@');
            parameters.Append(prefix);
            parameters.Append(index);
        }
        return parameters.ToString();
    }

    private static void AddSymbolHotspotParameters(
        SqliteCommand command,
        SymbolHotspotRowsQuery query,
        int? limit,
        string? kind,
        string? lang,
        IReadOnlyList<string>? pathPatterns,
        IReadOnlyList<string>? excludePathPatterns,
        IReadOnlyList<string>? visibilityFilters,
        IReadOnlyList<string>? excludeVisibilityFilters)
    {
        if (limit.HasValue)
            SqliteCommandPolicy.Add(command, "@limit", limit.Value);
        var candidatePlan = query.CandidatePlan;
        if (candidatePlan.CandidateLimit.HasValue)
        {
            SqliteCommandPolicy.Add(
                command,
                "@candidateReferenceLimit",
                candidatePlan.CandidateLimit.Value);
        }
        if (lang != null)
            SqliteCommandPolicy.Add(command, "@lang", lang);
        else
        {
            for (var index = 0; index < candidatePlan.GraphLanguages.Count; index++)
                SqliteCommandPolicy.Add(command, $"@gl{index}", candidatePlan.GraphLanguages[index]);
        }
        if (kind != null)
            SqliteCommandPolicy.Add(command, "@kind", kind);
        AddPathFilterParameters(command, pathPatterns, excludePathPatterns);
        AddVisibilityFilterParameters(command, visibilityFilters, excludeVisibilityFilters);
        for (var index = 0; index < candidatePlan.HotspotFamilyLanguages.Count; index++)
        {
            SqliteCommandPolicy.Add(
                command,
                $"@hotspotFamilyLang{index}",
                candidatePlan.HotspotFamilyLanguages[index]);
        }
    }
}

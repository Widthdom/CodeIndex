using System.Globalization;
using System.Text;
using System.Text.RegularExpressions;
using CodeIndex.Indexer;
using Microsoft.Data.Sqlite;
using Regex = CodeIndex.Indexer.BoundedRegex;

namespace CodeIndex.Database;

public partial class DbReader
{
    private SymbolHotspotRowsQuery BuildGroupedSymbolHotspotRowsQuery(string? kind, string? lang, IReadOnlyList<string>? pathPatterns, IReadOnlyList<string>? excludePathPatterns, bool excludeTests, IReadOnlyList<string>? visibilityFilters, IReadOnlyList<string>? excludeVisibilityFilters)
    {
        var containerNameSql = GetSymbolColumnSql("container_name");
        var containerQualifiedNameSql = GetSymbolColumnSql("container_qualified_name");
        var familyKeySql = GetSymbolColumnSql("family_key");
        var hotspotFamilyLangs = _hotspotFamilyReadyLanguages
            .OrderBy(value => value, StringComparer.Ordinal)
            .ToList();
        var familyLangConditionSql = hotspotFamilyLangs.Count > 0
            ? $"f.lang IN ({string.Join(",", hotspotFamilyLangs.Select((_, i) => $"@hotspotFamilyLang{i}"))})"
            : "0";
        var familyTargetKeySql = hotspotFamilyLangs.Count > 0
            ? $@"CASE
                    WHEN {familyLangConditionSql}
                     AND COALESCE({familyKeySql}, '') <> ''
                        THEN 'family|' || COALESCE(f.lang, '') || '|' || COALESCE(s.kind, '') || '|' || {familyKeySql}
                    ELSE NULL
                END"
            : "NULL";
        var containerTargetKeySql = $@"CASE
                    WHEN COALESCE({containerQualifiedNameSql}, '') <> ''
                        THEN 'container|' || CAST(s.file_id AS TEXT) || '|' || COALESCE(s.kind, '') || '|' || {containerQualifiedNameSql}
                    ELSE NULL
                END";
        var csharpFunctionDefinitionGateSql = _symbolColumns.Contains("body_start_line")
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
        var graphLangs = ReferenceExtractor.GetSupportedLanguages().ToList();
        var sql = $@"
            WITH all_candidate_symbols AS (
                SELECT s.id, s.file_id, s.name, s.kind, f.path, f.lang, s.line,
                       {GetSymbolColumnSql("visibility")} AS visibility,
                       {containerNameSql} AS container_name,
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
                WHERE s.kind NOT IN ('import', 'namespace')" + csharpFunctionDefinitionGateSql;

        if (lang != null)
            sql += SymbolLanguageFileIdFilter;
        else
            sql += $" AND f.lang IN ({string.Join(",", graphLangs.Select((_, i) => $"@gl{i}"))})";
        if (kind != null)
            sql += " AND s.kind = @kind";
        AppendVisibilityFilters(ref sql, visibilityFilters, excludeVisibilityFilters);

        sql += @"
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
            filtered_candidates AS (
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
                WHERE 1 = 1";
        if (pathPatterns is { Count: > 0 })
        {
            var ors = new List<string>(pathPatterns.Count);
            for (int i = 0; i < pathPatterns.Count; i++)
                ors.Add(BuildPathColumnFilterPredicate("path", "pathPattern", i, pathPatterns[i]));
            sql += " AND (" + string.Join(" OR ", ors) + ")";
        }
        if (excludePathPatterns != null)
        {
            for (int i = 0; i < excludePathPatterns.Count; i++)
                sql += $" AND NOT {BuildPathColumnFilterPredicate("path", "excludePathPattern", i, excludePathPatterns[i])}";
        }
        if (excludeTests)
            sql += $" AND NOT {TestPathCondition.Replace("f.path", "path")}";
        sql += @"
            ),
            logical_references AS (
                SELECT sr.file_id,
                       rf.lang,
                       " + BuildLogicalReferenceNameExpr("rf.lang", "sr.symbol_name", ReferenceContextSql("sr"), "sr.container_name", "sr.column_number") + @" AS symbol_name,
                       " + BuildLogicalReferenceSegmentCountExpr("rf.lang", "sr.symbol_name", ReferenceContextSql("sr"), "sr.container_name", "sr.column_number") + @" AS symbol_segment_count,
                       sr.line,
                       sr.column_number,
                       " + GetLogicalReferenceKindSql("sr.reference_kind") + @" AS logical_reference_kind,
                       " + GetHotspotReferenceWeightSql("sr.reference_kind") + @" AS reference_weight
                FROM symbol_references sr
                JOIN files rf ON rf.id = sr.file_id" + ReferenceLineJoinSql("sr") + @"
                WHERE sr.reference_kind IN " + CallGraphReferenceKindsSql + @"
                GROUP BY rf.lang, sr.file_id, symbol_name, symbol_segment_count, sr.line, sr.column_number, logical_reference_kind
            ),
            global_reference_counts AS (
                SELECT lang,
                       symbol_name,
                       symbol_segment_count,
                       COUNT(*) AS ref_count,
                       SUM(reference_weight) AS ref_score
                FROM logical_references
                GROUP BY lang, symbol_name, symbol_segment_count
            ),
            file_target_cardinality AS (
                SELECT lang,
                       file_id,
                       name,
                       kind,
                       COUNT(DISTINCT logical_target_key) AS target_count
                FROM filtered_candidates
                GROUP BY lang, file_id, name, kind
            ),
            conservative_target_files AS (
                SELECT DISTINCT lang,
                       file_id,
                       name,
                       kind,
                       logical_target_key
                FROM filtered_candidates
            ),
            file_reference_counts AS (
                SELECT lang,
                       file_id,
                       symbol_name,
                       symbol_segment_count,
                       COUNT(*) AS ref_count,
                       SUM(reference_weight) AS ref_score
                FROM logical_references
                GROUP BY lang, file_id, symbol_name, symbol_segment_count
            ),
            conservative_reference_counts AS (
                SELECT ctf.logical_target_key,
                       ctf.name,
                       ctf.kind,
                       SUM(COALESCE(frc.ref_count, 0)) AS ref_count,
                       SUM(COALESCE(frc.ref_score, 0.0)) AS ref_score
                FROM conservative_target_files ctf
                JOIN file_target_cardinality ftc
                  ON ftc.lang = ctf.lang
                 AND ftc.file_id = ctf.file_id
                 AND ftc.name = ctf.name
                 AND ftc.kind = ctf.kind
                 AND ftc.target_count = 1
                LEFT JOIN file_reference_counts frc
                  ON frc.lang = ctf.lang
                 AND frc.file_id = ctf.file_id
                 AND (
                        (ctf.lang != 'sql' AND frc.symbol_name = ctf.name)
                     OR (ctf.lang = 'sql' AND (
                            (frc.symbol_segment_count = sql_segment_count(ctf.name) AND frc.symbol_name = sql_normalize_name(ctf.name) COLLATE NOCASE)
                         OR (frc.symbol_segment_count = 1 AND frc.symbol_name = sql_leaf_name(ctf.name) COLLATE NOCASE)
                     ))
                 )
                GROUP BY ctf.logical_target_key, ctf.name, ctf.kind
            ),
            site_reference_counts AS (
                SELECT fc.id AS symbol_id,
                       CASE
                           WHEN (fc.lang != 'csharp' OR fc.kind != 'property')
                             AND (
                                 nc.defs = 1
                                 OR (nc.count_safe_defs = nc.defs AND nc.count_safe_groups = 1)
                             )
                               THEN COALESCE(grc.ref_count, 0)
                           ELSE COALESCE(crc.ref_count, 0)
                       END AS ref_count,
                       CASE
                           WHEN (fc.lang != 'csharp' OR fc.kind != 'property')
                             AND (
                                 nc.defs = 1
                                 OR (nc.count_safe_defs = nc.defs AND nc.count_safe_groups = 1)
                             )
                               THEN COALESCE(grc.ref_score, 0.0)
                           ELSE COALESCE(crc.ref_score, 0.0)
                       END AS ref_score
                FROM filtered_candidates fc
                JOIN name_cardinality nc
                  ON nc.lang = fc.lang
                 AND nc.name = fc.name
                LEFT JOIN global_reference_counts grc
                  ON grc.lang = fc.lang
                 AND (
                        (fc.lang != 'sql' AND grc.symbol_name = fc.name)
                     OR (fc.lang = 'sql' AND (
                            (grc.symbol_segment_count = sql_segment_count(fc.name) AND grc.symbol_name = sql_normalize_name(fc.name) COLLATE NOCASE)
                         OR (grc.symbol_segment_count = 1 AND grc.symbol_name = sql_leaf_name(fc.name) COLLATE NOCASE)
                     ))
                 )
                LEFT JOIN conservative_reference_counts crc
                  ON crc.logical_target_key = fc.logical_target_key
                 AND crc.name = fc.name
                 AND crc.kind = fc.kind
            ),
            hotspot_sites AS (
                SELECT fc.id AS symbol_id,
                       fc.name,
                       fc.kind,
                       fc.path,
                       fc.lang,
                       fc.line,
                       fc.visibility,
                       fc.container_name,
                       fc.logical_target_key,
                       src.ref_count,
                       src.ref_score
                FROM filtered_candidates fc
                JOIN site_reference_counts src ON src.symbol_id = fc.id
                WHERE src.ref_count > 0
            ),
            ranked_sites AS (
                SELECT hs.*,
                       ROW_NUMBER() OVER (
                           PARTITION BY hs.name, hs.kind
                           ORDER BY hs.path, hs.line, COALESCE(hs.container_name, ''), COALESCE(hs.visibility, '')
                       ) AS rep_rank
                FROM hotspot_sites hs
            ),
            grouped_reference_counts AS (
                SELECT hs.name,
                       hs.kind,
                       SUM(hs.ref_count) AS ref_count,
                       SUM(hs.ref_score) AS ref_score
                FROM (
                    SELECT DISTINCT name,
                           kind,
                           logical_target_key,
                           ref_count,
                           ref_score
                    FROM hotspot_sites
                ) hs
                GROUP BY hs.name, hs.kind
            ),
            grouped AS (
                SELECT hs.name,
                       hs.kind,
                       MAX(grc.ref_count) AS ref_count,
                       MAX(grc.ref_score) AS ref_score,
                       COUNT(*) AS definition_sites
                FROM ranked_sites hs
                JOIN grouped_reference_counts grc
                  ON grc.name = hs.name
                 AND grc.kind = hs.kind
                GROUP BY hs.name, hs.kind
            )";

        return new SymbolHotspotRowsQuery(sql, graphLangs, hotspotFamilyLangs);
    }

    public HotspotCountResult CountGroupedSymbolHotspots(string? kind, string? lang, IReadOnlyList<string>? pathPatterns, IReadOnlyList<string>? excludePathPatterns, bool excludeTests, IReadOnlyList<string>? visibilityFilters = null, IReadOnlyList<string>? excludeVisibilityFilters = null)
    {
        if (!_hasReferencesTable) return new HotspotCountResult(0, 0);
        var query = BuildGroupedSymbolHotspotRowsQuery(kind, lang, pathPatterns, excludePathPatterns, excludeTests, visibilityFilters, excludeVisibilityFilters);
        var sql = query.Sql + @"
            SELECT COUNT(*),
                   (SELECT COUNT(DISTINCT path) FROM ranked_sites),
                   COALESCE(SUM(definition_sites), 0)
            FROM grouped";

        using var cmd = _conn.CreateCommand();
        cmd.CommandText = sql;
        AddSymbolHotspotParameters(cmd, query, limit: null, kind, lang, pathPatterns, excludePathPatterns, visibilityFilters, excludeVisibilityFilters);
        return ExecuteHotspotCountSummary(cmd);
    }

    /// <summary>
    /// Return grouped hotspot rows collapsed by (name, kind) after the full filtered site set
    /// has been considered, keeping the representative site deterministic.
    /// フィルタ済みの全 definition site を見た上で、(name, kind) 単位に hotspot を集約して返す。
    /// 代表 site は決定的な順序で選ぶ。
    /// </summary>
    public List<GroupedHotspotResult> GetGroupedSymbolHotspots(int limit, string? kind, string? lang, IReadOnlyList<string>? pathPatterns, IReadOnlyList<string>? excludePathPatterns, bool excludeTests, IReadOnlyList<string>? visibilityFilters = null, IReadOnlyList<string>? excludeVisibilityFilters = null)
    {
        if (!_hasReferencesTable) return [];

        var query = BuildGroupedSymbolHotspotRowsQuery(kind, lang, pathPatterns, excludePathPatterns, excludeTests, visibilityFilters, excludeVisibilityFilters);
        var genericNamePenaltySql = GetGenericHotspotNamePenaltySql("g.name");
        var sql = query.Sql + @"
            SELECT g.name, g.kind, g.ref_count, g.ref_score,
                   (g.ref_score * (" + genericNamePenaltySql + @")) AS ranking_score,
                   (" + genericNamePenaltySql + @") AS generic_name_penalty,
                   g.definition_sites,
                   rep.path, rep.lang, rep.line, rep.visibility, rep.container_name,
                   (
                       SELECT GROUP_CONCAT(path, char(10))
                       FROM (
                           SELECT DISTINCT hs2.path AS path
                           FROM ranked_sites hs2
                           WHERE hs2.name = g.name
                             AND hs2.kind = g.kind
                           ORDER BY path
                           LIMIT @groupedPathSampleLimit
                       )
                   ) AS grouped_paths
            FROM grouped g
            JOIN ranked_sites rep
              ON rep.name = g.name
             AND rep.kind = g.kind
             AND rep.rep_rank = 1
            ORDER BY ranking_score DESC, g.ref_score DESC, g.ref_count DESC, g.name, g.kind
            LIMIT @limit";

        using var cmd = _conn.CreateCommand();
        cmd.CommandText = sql;
        AddSymbolHotspotParameters(cmd, query, limit, kind, lang, pathPatterns, excludePathPatterns, visibilityFilters, excludeVisibilityFilters);
        SqliteCommandPolicy.Add(cmd, "@groupedPathSampleLimit", GroupedHotspotPathSampleLimit + 1);

        var results = new List<GroupedHotspotResult>();
        using var reader = cmd.ExecuteTrackedReader();
        while (reader.TrackedRead())
        {
            var paths = GetNullableString(reader, 12)?
                .Split('\n', StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries)
                .ToList() ?? [];
            var pathsTruncated = paths.Count > GroupedHotspotPathSampleLimit;
            if (pathsTruncated)
                paths = paths.Take(GroupedHotspotPathSampleLimit).ToList();
            results.Add(new GroupedHotspotResult
            {
                Symbol = new SymbolResult
                {
                    Name = reader.GetString(0),
                    Kind = reader.GetString(1),
                    Path = reader.GetString(7),
                    Lang = GetNullableString(reader, 8),
                    Line = reader.GetInt32(9),
                    Visibility = GetNullableString(reader, 10),
                    ContainerName = GetNullableString(reader, 11),
                },
                ReferenceCount = reader.GetInt32(2),
                ReferenceScore = reader.GetDouble(3),
                RankingScore = reader.GetDouble(4),
                GenericNamePenalty = reader.GetDouble(5),
                DefinitionSites = reader.GetInt32(6),
                Paths = paths,
                PathsTruncated = pathsTruncated,
            });
        }

        PopulateGroupedHotspotDefinitionSiteDetails(results, query, kind, lang, pathPatterns, excludePathPatterns, visibilityFilters, excludeVisibilityFilters);
        return results;
    }

    private void PopulateGroupedHotspotDefinitionSiteDetails(
        List<GroupedHotspotResult> results,
        SymbolHotspotRowsQuery query,
        string? kind,
        string? lang,
        IReadOnlyList<string>? pathPatterns,
        IReadOnlyList<string>? excludePathPatterns,
        IReadOnlyList<string>? visibilityFilters,
        IReadOnlyList<string>? excludeVisibilityFilters)
    {
        if (results.Count == 0)
            return;

        var groupPredicates = new List<string>(results.Count);
        for (var i = 0; i < results.Count; i++)
            groupPredicates.Add($"(hs.name = @detailName{i} AND hs.kind = @detailKind{i})");

        using var cmd = _conn.CreateCommand();
        cmd.CommandText = query.Sql + $@"
            SELECT hs.name,
                   hs.kind,
                   hs.path,
                   hs.lang,
                   hs.line,
                   hs.visibility,
                   hs.container_name,
                   hs.logical_target_key
            FROM ranked_sites hs
            WHERE {string.Join(" OR ", groupPredicates)}
            ORDER BY hs.name, hs.kind, hs.path, hs.line, COALESCE(hs.container_name, ''), COALESCE(hs.visibility, '')";
        AddSymbolHotspotParameters(cmd, query, limit: null, kind, lang, pathPatterns, excludePathPatterns, visibilityFilters, excludeVisibilityFilters);
        for (var i = 0; i < results.Count; i++)
        {
            SqliteCommandPolicy.Add(cmd, $"@detailName{i}", results[i].Symbol.Name);
            SqliteCommandPolicy.Add(cmd, $"@detailKind{i}", results[i].Symbol.Kind);
        }

        var byGroup = results.ToDictionary(result => GetGroupedHotspotKey(result.Symbol.Name, result.Symbol.Kind), result => result, StringComparer.Ordinal);
        using var reader = cmd.ExecuteTrackedReader();
        while (reader.TrackedRead())
        {
            var key = GetGroupedHotspotKey(reader.GetString(0), reader.GetString(1));
            if (!byGroup.TryGetValue(key, out var group))
                continue;
            group.DefinitionSiteDetails.Add(new GroupedHotspotDefinitionSite
            {
                Path = reader.GetString(2),
                Lang = GetNullableString(reader, 3),
                Line = reader.GetInt32(4),
                Visibility = GetNullableString(reader, 5),
                Container = GetNullableString(reader, 6),
                LogicalTargetKey = GetNullableString(reader, 7),
            });
        }
    }

    private static string GetGroupedHotspotKey(string name, string kind)
        => name + "\0" + kind;
}

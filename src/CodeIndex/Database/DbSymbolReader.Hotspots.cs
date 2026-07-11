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

    private const string GenericHotspotNamePenaltySqlLiteral = "0.35";
    private const string GenericHotspotNamesSql = "('add','append','build','call','combine','convert','create','execute','get','getstring','getvalue','getvalues','handle','invoke','load','parse','process','read','resolve','run','set','start','stop','tolist','tostring','tryparse','update','write')";

    private const int GroupedHotspotPathSampleLimit = 20;

    private static string GetHotspotReferenceWeightSql(string referenceKindSql) => $@"
        CASE {referenceKindSql}
            WHEN 'call' THEN 1.0
            WHEN 'instantiate' THEN 1.0
            WHEN 'subscribe' THEN 0.3
            WHEN 'friend' THEN 0.3
            WHEN 'type_reference' THEN 0.1
            ELSE 0.0
        END";

    private static string GetGenericHotspotNamePenaltySql(string nameSql)
        => $"CASE WHEN lower({nameSql}) IN {GenericHotspotNamesSql} THEN {GenericHotspotNamePenaltySqlLiteral} ELSE 1.0 END";

    /// <summary>
    /// Find symbols with the most references (hotspots — heavily used code).
    /// Counts total reference volume across the codebase for names that stay unambiguous within
    /// the active language/kind candidate set. Path and test filters only decide which logical
    /// target rows are returned, not whether a name is considered globally ambiguous. When
    /// multiple logical targets still share the same name, falls back to conservative in-target
    /// file counts; rows that collapse to one logical target family (same container or top-level
    /// file) are grouped because bare-name references cannot disambiguate the true target symbol.
    /// 最も多く参照されるシンボルを検索する（ホットスポット — 多用されるコード）。
    /// active な言語/種別候補集合の中で名前が曖昧でないシンボルは codebase 全体の参照数を数える。
    /// path/test フィルタは返す logical target 行だけを絞り、名前の曖昧性判定には使わない。
    /// 複数の logical target が同名を共有する場合は bare-name 参照で真の対象を特定できないため
    /// 保守的な in-target file 件数へフォールバックし、1 つの logical target family に収まる行は集約する。
    /// </summary>
    public List<SymbolHotspotResult> GetSymbolHotspots(int limit, string? kind, string? lang, IReadOnlyList<string>? pathPatterns, IReadOnlyList<string>? excludePathPatterns, bool excludeTests, IReadOnlyList<string>? visibilityFilters = null, IReadOnlyList<string>? excludeVisibilityFilters = null)
    {
        if (!_hasReferencesTable) return [];
        var query = BuildSymbolHotspotRowsQuery(kind, lang, pathPatterns, excludePathPatterns, excludeTests, visibilityFilters, excludeVisibilityFilters);
        var genericNamePenaltySql = GetGenericHotspotNamePenaltySql("gr.name");
        var sql = query.Sql + @"
            SELECT gr.name, rc.ref_count, rc.ref_score,
                   (rc.ref_score * (" + genericNamePenaltySql + @")) AS ranking_score,
                   (" + genericNamePenaltySql + @") AS generic_name_penalty,
                   gr.kind, gr.path, gr.lang, gr.line,
                   gr.visibility, gr.container_name
            FROM grouped_rows gr
            JOIN reference_counts rc ON rc.symbol_id = gr.symbol_id
            WHERE rc.ref_count > 0
            ORDER BY ranking_score DESC,
                     rc.ref_score DESC,
                     rc.ref_count DESC,
                     gr.path COLLATE BINARY ASC,
                     gr.line ASC,
                     gr.name COLLATE BINARY ASC,
                     gr.kind COLLATE BINARY ASC,
                     gr.symbol_id ASC
            LIMIT @limit";

        using var cmd = _conn.CreateCommand();
        cmd.CommandText = sql;
        AddSymbolHotspotParameters(cmd, query, limit, kind, lang, pathPatterns, excludePathPatterns, visibilityFilters, excludeVisibilityFilters);

        var results = new List<SymbolHotspotResult>();
        using var reader = cmd.ExecuteTrackedReader();
        while (reader.TrackedRead())
        {
            results.Add(new SymbolHotspotResult
            {
                Symbol = new SymbolResult
                {
                    Name = reader.GetString(0),
                    Kind = reader.GetString(5),
                    Path = reader.GetString(6),
                    Lang = GetNullableString(reader, 7),
                    Line = reader.GetInt32(8),
                    Visibility = GetNullableString(reader, 9),
                    ContainerName = GetNullableString(reader, 10),
                },
                ReferenceCount = reader.GetInt32(1),
                ReferenceScore = reader.GetDouble(2),
                RankingScore = reader.GetDouble(3),
                GenericNamePenalty = reader.GetDouble(4),
            });
        }
        return results;
    }

    public List<FileHotspotResult> GetFileSymbolHotspots(int limit, string? kind, string? lang, IReadOnlyList<string>? pathPatterns, IReadOnlyList<string>? excludePathPatterns, bool excludeTests, IReadOnlyList<string>? visibilityFilters = null, IReadOnlyList<string>? excludeVisibilityFilters = null)
    {
        if (!_hasReferencesTable) return [];
        var query = BuildSymbolHotspotRowsQuery(kind, lang, pathPatterns, excludePathPatterns, excludeTests, visibilityFilters, excludeVisibilityFilters);
        var genericNamePenaltySql = GetGenericHotspotNamePenaltySql("gr.name");
        var fileSymbolCountSql = "MAX(fsc.symbol_count)";
        var structuralRankPenaltySql = GetFileHotspotStructuralRankPenaltySql("COUNT(*)");
        var sql = query.Sql + @"
            SELECT gr.path,
                   gr.lang,
                   SUM(rc.ref_count) AS ref_count,
                   SUM(rc.ref_score) AS ref_score,
                   (SUM(rc.ref_score * (" + genericNamePenaltySql + @")) * (" + structuralRankPenaltySql + @")) AS ranking_score,
                   CASE
                       WHEN SUM(rc.ref_score) > 0
                           THEN SUM(rc.ref_score * (" + genericNamePenaltySql + @")) / SUM(rc.ref_score)
                       ELSE 1.0
                   END AS generic_name_penalty,
                   (" + structuralRankPenaltySql + @") AS structural_rank_penalty,
                   " + fileSymbolCountSql + @" AS symbol_count
            FROM grouped_rows gr
            JOIN reference_counts rc ON rc.symbol_id = gr.symbol_id
            JOIN file_symbol_counts fsc
              ON fsc.path = gr.path
             AND fsc.lang_key = COALESCE(gr.lang, '')
            WHERE rc.ref_count > 0
            GROUP BY gr.path, gr.lang
            ORDER BY ranking_score DESC,
                     ref_score DESC,
                     ref_count DESC,
                     gr.path COLLATE BINARY ASC
            LIMIT @limit";

        using var cmd = _conn.CreateCommand();
        cmd.CommandText = sql;
        AddSymbolHotspotParameters(cmd, query, limit, kind, lang, pathPatterns, excludePathPatterns, visibilityFilters, excludeVisibilityFilters);

        var results = new List<FileHotspotResult>();
        using var reader = cmd.ExecuteTrackedReader();
        while (reader.TrackedRead())
        {
            results.Add(new FileHotspotResult
            {
                Path = reader.GetString(0),
                Lang = GetNullableString(reader, 1),
                ReferenceCount = reader.GetInt32(2),
                ReferenceScore = reader.GetDouble(3),
                RankingScore = reader.GetDouble(4),
                GenericNamePenalty = reader.GetDouble(5),
                StructuralRankPenalty = reader.GetDouble(6),
                SymbolCount = reader.GetInt32(7),
            });
        }
        return results;
    }

    private static string GetFileHotspotStructuralRankPenaltySql(string symbolCountSql)
        => $@"CASE
                WHEN {symbolCountSql} <= 2 THEN 0.1
                WHEN {symbolCountSql} <= 8 THEN 0.35
                ELSE 1.0
            END";

    public HotspotCountResult CountSymbolHotspots(string? kind, string? lang, IReadOnlyList<string>? pathPatterns, IReadOnlyList<string>? excludePathPatterns, bool excludeTests, IReadOnlyList<string>? visibilityFilters = null, IReadOnlyList<string>? excludeVisibilityFilters = null)
    {
        if (!_hasReferencesTable) return new HotspotCountResult(0, 0);
        var query = BuildSymbolHotspotRowsQuery(kind, lang, pathPatterns, excludePathPatterns, excludeTests, visibilityFilters, excludeVisibilityFilters);
        var sql = query.Sql + @"
            SELECT COUNT(*),
                   COUNT(DISTINCT gr.path)
            FROM grouped_rows gr
            JOIN reference_counts rc ON rc.symbol_id = gr.symbol_id
            WHERE rc.ref_count > 0";

        using var cmd = _conn.CreateCommand();
        cmd.CommandText = sql;
        AddSymbolHotspotParameters(cmd, query, limit: null, kind, lang, pathPatterns, excludePathPatterns, visibilityFilters, excludeVisibilityFilters);
        return ExecuteHotspotCountSummary(cmd);
    }

    public HotspotCountResult CountFileSymbolHotspots(string? kind, string? lang, IReadOnlyList<string>? pathPatterns, IReadOnlyList<string>? excludePathPatterns, bool excludeTests, IReadOnlyList<string>? visibilityFilters = null, IReadOnlyList<string>? excludeVisibilityFilters = null)
    {
        if (!_hasReferencesTable) return new HotspotCountResult(0, 0);
        var query = BuildSymbolHotspotRowsQuery(kind, lang, pathPatterns, excludePathPatterns, excludeTests, visibilityFilters, excludeVisibilityFilters);
        var sql = query.Sql + @"
            SELECT COUNT(*),
                   COUNT(*)
            FROM (
                SELECT gr.path,
                       gr.lang
                FROM grouped_rows gr
                JOIN reference_counts rc ON rc.symbol_id = gr.symbol_id
                WHERE rc.ref_count > 0
                GROUP BY gr.path, gr.lang
            ) file_groups";

        using var cmd = _conn.CreateCommand();
        cmd.CommandText = sql;
        AddSymbolHotspotParameters(cmd, query, limit: null, kind, lang, pathPatterns, excludePathPatterns, visibilityFilters, excludeVisibilityFilters);
        return ExecuteHotspotCountSummary(cmd);
    }

    private SymbolHotspotRowsQuery BuildSymbolHotspotRowsQuery(string? kind, string? lang, IReadOnlyList<string>? pathPatterns, IReadOnlyList<string>? excludePathPatterns, bool excludeTests, IReadOnlyList<string>? visibilityFilters, IReadOnlyList<string>? excludeVisibilityFilters)
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
        // Ambiguity is computed from the unscoped language/kind candidate set so `--path`
        // cannot hide an out-of-scope duplicate and accidentally promote a same-name symbol
        // back to codebase-wide counting. Cross-file grouping is allowed only when the
        // extractor persisted an authoritative family key on a DB that is stamped as fully
        // current for hotspot-family semantics (currently partial-type families). Same-file
        // same-container overloads can still share one conservative target key, but only
        // unique names or authoritative families may promote to codebase-wide counts.
        // 曖昧性は path 非依存の候補集合で判定し、`--path` で隠れた重複定義が一意扱いに
        // 戻ってしまうことを防ぐ。cross-file の集約は current な hotspot-family semantics で
        // fully-ready と判定された DB 上の正式な family key のみに限定し、same-file の
        // same-container overload は保守的な target として扱いつつ、codebase-wide 集計への
        // 昇格は一意名か authoritative family のみに限定する。
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

        var graphLangs = ReferenceExtractor.GetSupportedLanguages().ToList();
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
        if (pathPatterns != null && pathPatterns.Count > 0)
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
            grouped_candidates AS (
                SELECT MIN(id) AS symbol_id,
                       name,
                       kind,
                       logical_target_key
                FROM filtered_candidates
                GROUP BY logical_target_key, name, kind
            ),
            grouped_metadata AS (
                SELECT logical_target_key,
                       name,
                       kind,
                       CASE
                           WHEN COUNT(DISTINCT COALESCE(visibility, '')) = 1 THEN MIN(visibility)
                           ELSE NULL
                       END AS visibility,
                       CASE
                           WHEN COUNT(DISTINCT COALESCE(container_name, '')) = 1 THEN MIN(container_name)
                           ELSE NULL
                       END AS container_name
                FROM filtered_candidates
                GROUP BY logical_target_key, name, kind
            ),
            grouped_rows AS (
                SELECT gc.symbol_id,
                       gc.name,
                       gc.kind,
                       fc.path,
                       fc.lang,
                       fc.line,
                       gm.visibility,
                       gm.container_name,
                       gc.logical_target_key
                FROM grouped_candidates gc
                JOIN filtered_candidates fc ON fc.id = gc.symbol_id
                JOIN grouped_metadata gm
                 ON gm.logical_target_key = gc.logical_target_key
                 AND gm.name = gc.name
                 AND gm.kind = gc.kind
            ),
            logical_references AS (
                SELECT sr.file_id,
                       rf.lang,
                       sr.symbol_name AS raw_symbol_name,
                       " + BuildLogicalReferenceNameExpr("rf.lang", "sr.symbol_name", ReferenceContextSql("sr"), "sr.container_name", "sr.column_number") + @" AS symbol_name,
                       " + BuildLogicalReferenceSegmentCountExpr("rf.lang", "sr.symbol_name", ReferenceContextSql("sr"), "sr.container_name", "sr.column_number") + @" AS symbol_segment_count,
                       " + BuildLogicalReferenceLeafFallbackAllowedExpr("rf.lang", "sr.symbol_name", ReferenceContextSql("sr"), "sr.container_name", "sr.column_number") + @" AS allow_leaf_fallback,
                       sr.line,
                       sr.column_number,
                       " + GetLogicalReferenceKindSql("sr.reference_kind") + @" AS logical_reference_kind,
                       " + GetHotspotReferenceWeightSql("sr.reference_kind") + @" AS reference_weight
                FROM symbol_references sr
                JOIN files rf ON rf.id = sr.file_id" + ReferenceLineJoinSql("sr") + @"
                WHERE sr.reference_kind IN " + CallGraphReferenceKindsSql + @"
                GROUP BY rf.lang, sr.file_id, raw_symbol_name, symbol_name, symbol_segment_count, allow_leaf_fallback, sr.line, sr.column_number, logical_reference_kind
            ),
            global_exact_reference_counts AS (
                SELECT lang,
                       symbol_name,
                       symbol_segment_count,
                       COUNT(*) AS ref_count,
                       SUM(reference_weight) AS ref_score
                FROM logical_references
                GROUP BY lang, symbol_name, symbol_segment_count
            ),
            global_leaf_reference_counts AS (
                SELECT lang,
                       raw_symbol_name,
                       symbol_name AS resolved_symbol_name,
                       symbol_segment_count AS resolved_symbol_segment_count,
                       COUNT(*) AS ref_count,
                       SUM(reference_weight) AS ref_score
                FROM logical_references
                WHERE allow_leaf_fallback = 1
                GROUP BY lang, raw_symbol_name, resolved_symbol_name, resolved_symbol_segment_count
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
            file_reference_counts_exact AS (
                SELECT lang,
                       file_id,
                       symbol_name,
                       symbol_segment_count,
                       COUNT(*) AS ref_count,
                       SUM(reference_weight) AS ref_score
                FROM logical_references
                GROUP BY lang, file_id, symbol_name, symbol_segment_count
            ),
            file_reference_counts_leaf AS (
                SELECT lang,
                       file_id,
                       raw_symbol_name,
                       symbol_name AS resolved_symbol_name,
                       symbol_segment_count AS resolved_symbol_segment_count,
                       COUNT(*) AS ref_count,
                       SUM(reference_weight) AS ref_score
                FROM logical_references
                WHERE allow_leaf_fallback = 1
                GROUP BY lang, file_id, raw_symbol_name, resolved_symbol_name, resolved_symbol_segment_count
            ),
            conservative_reference_counts AS (
                SELECT ctf.logical_target_key,
                       ctf.name,
                       ctf.kind,
                       SUM(COALESCE(frc_exact.ref_count, 0) + COALESCE(frc_leaf.ref_count, 0)) AS ref_count,
                       SUM(COALESCE(frc_exact.ref_score, 0.0) + COALESCE(frc_leaf.ref_score, 0.0)) AS ref_score
                FROM conservative_target_files ctf
                JOIN file_target_cardinality ftc
                  ON ftc.lang = ctf.lang
                 AND ftc.file_id = ctf.file_id
                 AND ftc.name = ctf.name
                 AND ftc.kind = ctf.kind
                 AND ftc.target_count = 1
                LEFT JOIN file_reference_counts_exact frc_exact
                  ON frc_exact.lang = ctf.lang
                 AND frc_exact.file_id = ctf.file_id
                 AND (
                         (ctf.lang != 'sql' AND frc_exact.symbol_name = ctf.name)
                      OR (ctf.lang = 'sql' AND (
                             (frc_exact.symbol_segment_count = sql_segment_count(ctf.name) AND frc_exact.symbol_name = sql_normalize_name(ctf.name) COLLATE NOCASE)
                      ))
                  )
                LEFT JOIN file_reference_counts_leaf frc_leaf
                  ON frc_leaf.lang = ctf.lang
                 AND frc_leaf.file_id = ctf.file_id
                 AND ctf.lang = 'sql'
                 AND sql_segment_count(ctf.name) > 1
                 AND frc_leaf.raw_symbol_name = sql_leaf_name(ctf.name) COLLATE NOCASE
                 AND NOT EXISTS (
                        SELECT 1
                        FROM filtered_candidates fc_resolved
                        WHERE fc_resolved.lang = ctf.lang
                          AND sql_segment_count(fc_resolved.name) = frc_leaf.resolved_symbol_segment_count
                          AND sql_normalize_name(fc_resolved.name) = frc_leaf.resolved_symbol_name COLLATE NOCASE
                    )
                 AND NOT EXISTS (
                        SELECT 1
                        FROM filtered_candidates fc_exact
                        WHERE fc_exact.lang = ctf.lang
                          AND sql_segment_count(fc_exact.name) = 1
                          AND sql_normalize_name(fc_exact.name) = frc_leaf.raw_symbol_name COLLATE NOCASE
                    )
                GROUP BY ctf.logical_target_key, ctf.name, ctf.kind
            ),
            reference_counts AS (
                SELECT gr.symbol_id,
                       CASE
                            WHEN (gr.lang != 'csharp' OR gr.kind != 'property')
                              AND (
                                  nc.defs = 1
                                  OR (nc.count_safe_defs = nc.defs AND nc.count_safe_groups = 1)
                              )
                                THEN COALESCE(gerc.ref_count, 0) + COALESCE(glrc.ref_count, 0)
                            ELSE COALESCE(crc.ref_count, 0)
                        END AS ref_count,
                       CASE
                            WHEN (gr.lang != 'csharp' OR gr.kind != 'property')
                              AND (
                                  nc.defs = 1
                                  OR (nc.count_safe_defs = nc.defs AND nc.count_safe_groups = 1)
                              )
                                THEN COALESCE(gerc.ref_score, 0.0) + COALESCE(glrc.ref_score, 0.0)
                            ELSE COALESCE(crc.ref_score, 0.0)
                        END AS ref_score
                FROM grouped_rows gr
                JOIN name_cardinality nc
                  ON nc.lang = gr.lang
                  AND nc.name = gr.name
                LEFT JOIN global_exact_reference_counts gerc
                  ON gerc.lang = gr.lang
                 AND (
                         (gr.lang != 'sql' AND gerc.symbol_name = gr.name)
                      OR (gr.lang = 'sql' AND (
                             (gerc.symbol_segment_count = sql_segment_count(gr.name) AND gerc.symbol_name = sql_normalize_name(gr.name) COLLATE NOCASE)
                      ))
                  )
                LEFT JOIN global_leaf_reference_counts glrc
                  ON glrc.lang = gr.lang
                 AND gr.lang = 'sql'
                 AND sql_segment_count(gr.name) > 1
                 AND glrc.raw_symbol_name = sql_leaf_name(gr.name) COLLATE NOCASE
                 AND NOT EXISTS (
                        SELECT 1
                        FROM filtered_candidates fc_resolved
                        WHERE fc_resolved.lang = gr.lang
                          AND sql_segment_count(fc_resolved.name) = glrc.resolved_symbol_segment_count
                          AND sql_normalize_name(fc_resolved.name) = glrc.resolved_symbol_name COLLATE NOCASE
                    )
                 AND NOT EXISTS (
                        SELECT 1
                        FROM filtered_candidates fc_exact
                        WHERE fc_exact.lang = gr.lang
                          AND sql_segment_count(fc_exact.name) = 1
                          AND sql_normalize_name(fc_exact.name) = glrc.raw_symbol_name COLLATE NOCASE
                    )
                LEFT JOIN conservative_reference_counts crc
                  ON crc.logical_target_key = gr.logical_target_key
                 AND crc.name = gr.name
                 AND crc.kind = gr.kind
            ),
            file_symbol_counts AS (
                SELECT path,
                       COALESCE(lang, '') AS lang_key,
                       COUNT(*) AS symbol_count
                FROM filtered_candidates
                GROUP BY path, COALESCE(lang, '')
            )";
        return new SymbolHotspotRowsQuery(sql, graphLangs, hotspotFamilyLangs);
    }

    private static void AddSymbolHotspotParameters(SqliteCommand command, SymbolHotspotRowsQuery query, int? limit, string? kind, string? lang, IReadOnlyList<string>? pathPatterns, IReadOnlyList<string>? excludePathPatterns, IReadOnlyList<string>? visibilityFilters, IReadOnlyList<string>? excludeVisibilityFilters)
    {
        if (limit.HasValue)
            SqliteCommandPolicy.Add(command, "@limit", limit.Value);
        if (lang != null)
            SqliteCommandPolicy.Add(command, "@lang", lang);
        else
        {
            for (int i = 0; i < query.GraphLanguages.Count; i++)
                SqliteCommandPolicy.Add(command, $"@gl{i}", query.GraphLanguages[i]);
        }
        if (kind != null)
            SqliteCommandPolicy.Add(command, "@kind", kind);
        AddPathFilterParameters(command, pathPatterns, excludePathPatterns);
        AddVisibilityFilterParameters(command, visibilityFilters, excludeVisibilityFilters);
        for (int i = 0; i < query.HotspotFamilyLanguages.Count; i++)
            SqliteCommandPolicy.Add(command, $"@hotspotFamilyLang{i}", query.HotspotFamilyLanguages[i]);
    }

    private sealed record SymbolHotspotRowsQuery(string Sql, List<string> GraphLanguages, List<string> HotspotFamilyLanguages);

    private static HotspotCountResult ExecuteHotspotCountSummary(SqliteCommand command)
    {
        using var reader = command.ExecuteTrackedReader();
        if (!reader.TrackedRead())
            return new HotspotCountResult(0, 0);

        var count = Convert.ToInt32(reader.GetValue(0));
        var fileCount = Convert.ToInt32(reader.GetValue(1));
        var definitionSiteTotal = reader.FieldCount > 2 && !reader.IsDBNull(2)
            ? Convert.ToInt32(reader.GetValue(2))
            : 0;
        return new HotspotCountResult(count, fileCount, definitionSiteTotal);
    }


}

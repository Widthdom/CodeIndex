using System.Globalization;
using System.Text;
using System.Text.RegularExpressions;
using CodeIndex.Indexer;
using Microsoft.Data.Sqlite;
using Regex = CodeIndex.Indexer.BoundedRegex;

namespace CodeIndex.Database;

public partial class DbReader
{
    private bool CanUseCSharpIdentityHotspotCounts()
        => HasCurrentReferenceIdentityContractForRead()
           && _referenceColumns.Contains("target_symbol_id")
           && _referenceColumns.Contains("resolution_state")
           && HasTable("symbol_reference_candidates");

    private string BuildCSharpIdentityHotspotReferenceCountsSql()
    {
        if (!CanUseCSharpIdentityHotspotCounts())
        {
            return @"
            csharp_identity_reference_counts AS (
                SELECT NULL AS logical_target_key,
                       NULL AS target_name,
                       NULL AS target_kind,
                       0 AS ref_count,
                       0.0 AS ref_score
                WHERE 1 = 0
            ),";
        }

        var logicalKindSql = GetLogicalReferenceKindSql("identity_reference.reference_kind");
        var referenceWeightSql = GetHotspotReferenceWeightSql("identity_reference.reference_kind");
        return $@"
            csharp_identity_reference_targets AS MATERIALIZED (
                SELECT resolved_reference.id AS reference_id,
                       resolved_target.logical_target_key,
                       resolved_target.name AS target_name,
                       resolved_target.kind AS target_kind
                FROM symbol_references resolved_reference
                JOIN csharp_identity_candidate_symbols resolved_target
                  ON resolved_target.id = resolved_reference.target_symbol_id
                WHERE resolved_reference.resolution_state = 'resolved'
                  AND resolved_target.lang = 'csharp'

                UNION ALL

                SELECT grouped_reference.id AS reference_id,
                       MIN(grouped_target.logical_target_key) AS logical_target_key,
                       MIN(grouped_target.name) AS target_name,
                       MIN(grouped_target.kind) AS target_kind
                FROM symbol_references grouped_reference
                JOIN symbol_reference_candidates grouped_candidate
                  ON grouped_candidate.reference_id = grouped_reference.id
                JOIN csharp_identity_candidate_symbols grouped_target
                  ON grouped_target.id = grouped_candidate.symbol_id
                WHERE grouped_reference.resolution_state IN ('resolved_group', 'ambiguous')
                  AND grouped_target.lang = 'csharp'
                GROUP BY grouped_reference.id
                HAVING COUNT(DISTINCT grouped_target.logical_target_key) = 1
                   AND COUNT(DISTINCT grouped_target.name) = 1
                   AND COUNT(DISTINCT grouped_target.kind) = 1
            ),
            csharp_identity_reference_sites AS MATERIALIZED (
                SELECT identity_target.logical_target_key,
                       identity_target.target_name,
                       identity_target.target_kind,
                       identity_reference.file_id,
                       identity_reference.line,
                       identity_reference.column_number,
                       {logicalKindSql} AS logical_reference_kind,
                       MAX({referenceWeightSql}) AS reference_score
                FROM symbol_references identity_reference
                JOIN files identity_source_file
                  ON identity_source_file.id = identity_reference.file_id
                JOIN csharp_identity_reference_targets identity_target
                  ON identity_target.reference_id = identity_reference.id
                WHERE identity_source_file.lang = 'csharp'
                  AND identity_reference.reference_kind IN {CallGraphReferenceKindsSql}
                GROUP BY identity_target.logical_target_key,
                         identity_target.target_name,
                         identity_target.target_kind,
                         identity_reference.file_id,
                         identity_reference.line,
                         identity_reference.column_number,
                         logical_reference_kind
            ),
            csharp_identity_reference_counts AS (
                SELECT logical_target_key,
                       target_name,
                       target_kind,
                       COUNT(*) AS ref_count,
                       SUM(reference_score) AS ref_score
                FROM csharp_identity_reference_sites
                GROUP BY logical_target_key, target_name, target_kind
            ),";
    }

    private SymbolHotspotRowsQuery BuildGroupedSymbolHotspotRowsQuery(int? resultLimit, string? kind, string? lang, IReadOnlyList<string>? pathPatterns, IReadOnlyList<string>? excludePathPatterns, bool excludeTests, IReadOnlyList<string>? visibilityFilters, IReadOnlyList<string>? excludeVisibilityFilters)
    {
        var candidatePlan = BuildSymbolHotspotCandidatePlan(
            resultLimit,
            kind,
            lang,
            pathPatterns,
            excludePathPatterns,
            excludeTests,
            visibilityFilters,
            excludeVisibilityFilters);
        var csharpIdentityCountSql = CanUseCSharpIdentityHotspotCounts()
            ? "WHEN fc.lang = 'csharp' AND fc.kind IN ('function', 'test.method', 'property') THEN COALESCE(circ.ref_count, 0)"
            : string.Empty;
        var csharpIdentityScoreSql = CanUseCSharpIdentityHotspotCounts()
            ? "WHEN fc.lang = 'csharp' AND fc.kind IN ('function', 'test.method', 'property') THEN COALESCE(circ.ref_score, 0.0)"
            : string.Empty;
        var sql = candidatePlan.Sql + @"
            logical_references AS MATERIALIZED (
                " + BuildHotspotLogicalReferenceRowsSql(includeLeafMetadata: false, boundedCandidates: candidatePlan.CandidateLimit.HasValue) + @"
            )," + BuildCSharpIdentityHotspotReferenceCountsSql() + @"
            file_reference_counts AS MATERIALIZED (
                SELECT lang,
                       file_id,
                       symbol_name,
                       symbol_segment_count,
                       SUM(reference_count) AS ref_count,
                       SUM(reference_score) AS ref_score
                FROM logical_references
                GROUP BY lang, file_id, symbol_name, symbol_segment_count
            ),
            global_reference_counts AS (
                SELECT lang,
                       symbol_name,
                       symbol_segment_count,
                       SUM(ref_count) AS ref_count,
                       SUM(ref_score) AS ref_score
                FROM file_reference_counts
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
            conservative_reference_counts AS (
                SELECT ctf.logical_target_key,
                       ctf.name,
                       ctf.kind,
                       SUM(
                           COALESCE(frc_non_sql.ref_count, 0)
                           + COALESCE(frc_sql_exact.ref_count, 0)
                           + COALESCE(frc_sql_leaf.ref_count, 0)) AS ref_count,
                       SUM(
                           COALESCE(frc_non_sql.ref_score, 0.0)
                           + COALESCE(frc_sql_exact.ref_score, 0.0)
                           + COALESCE(frc_sql_leaf.ref_score, 0.0)) AS ref_score
                FROM conservative_target_files ctf
                JOIN file_target_cardinality ftc
                  ON ftc.lang = ctf.lang
                 AND ftc.file_id = ctf.file_id
                 AND ftc.name = ctf.name
                 AND ftc.kind = ctf.kind
                 AND ftc.target_count = 1
                LEFT JOIN file_reference_counts frc_non_sql
                  ON ctf.lang != 'sql'
                 AND frc_non_sql.lang = ctf.lang
                 AND frc_non_sql.file_id = ctf.file_id
                 AND frc_non_sql.symbol_name = ctf.name
                LEFT JOIN file_reference_counts frc_sql_exact
                  ON ctf.lang = 'sql'
                 AND frc_sql_exact.lang = ctf.lang
                 AND frc_sql_exact.file_id = ctf.file_id
                 AND frc_sql_exact.symbol_segment_count = sql_segment_count(ctf.name)
                 AND frc_sql_exact.symbol_name = sql_normalize_name(ctf.name) COLLATE NOCASE
                LEFT JOIN file_reference_counts frc_sql_leaf
                  ON ctf.lang = 'sql'
                 AND sql_segment_count(ctf.name) > 1
                 AND frc_sql_leaf.lang = ctf.lang
                 AND frc_sql_leaf.file_id = ctf.file_id
                 AND frc_sql_leaf.symbol_segment_count = 1
                 AND frc_sql_leaf.symbol_name = sql_leaf_name(ctf.name) COLLATE NOCASE
                GROUP BY ctf.logical_target_key, ctf.name, ctf.kind
            ),
            site_reference_counts AS (
                SELECT fc.id AS symbol_id,
                       CASE
                           " + csharpIdentityCountSql + @"
                           WHEN (fc.lang != 'csharp' OR fc.kind != 'property')
                             AND (
                                 nc.defs = 1
                                 OR (nc.count_safe_defs = nc.defs AND nc.count_safe_groups = 1)
                             )
                               THEN COALESCE(grc_non_sql.ref_count, 0)
                                  + COALESCE(grc_sql_exact.ref_count, 0)
                                  + COALESCE(grc_sql_leaf.ref_count, 0)
                           ELSE COALESCE(crc.ref_count, 0)
                       END AS ref_count,
                       CASE
                           " + csharpIdentityScoreSql + @"
                           WHEN (fc.lang != 'csharp' OR fc.kind != 'property')
                             AND (
                                 nc.defs = 1
                                 OR (nc.count_safe_defs = nc.defs AND nc.count_safe_groups = 1)
                             )
                               THEN COALESCE(grc_non_sql.ref_score, 0.0)
                                  + COALESCE(grc_sql_exact.ref_score, 0.0)
                                  + COALESCE(grc_sql_leaf.ref_score, 0.0)
                           ELSE COALESCE(crc.ref_score, 0.0)
                       END AS ref_score
                FROM filtered_candidates fc
                JOIN name_cardinality nc
                  ON nc.lang = fc.lang
                 AND nc.name = fc.name
                LEFT JOIN global_reference_counts grc_non_sql
                  ON fc.lang != 'sql'
                 AND grc_non_sql.lang = fc.lang
                 AND grc_non_sql.symbol_name = fc.name
                LEFT JOIN global_reference_counts grc_sql_exact
                  ON fc.lang = 'sql'
                 AND grc_sql_exact.lang = fc.lang
                 AND grc_sql_exact.symbol_segment_count = sql_segment_count(fc.name)
                 AND grc_sql_exact.symbol_name = sql_normalize_name(fc.name) COLLATE NOCASE
                LEFT JOIN global_reference_counts grc_sql_leaf
                  ON fc.lang = 'sql'
                 AND sql_segment_count(fc.name) > 1
                 AND grc_sql_leaf.lang = fc.lang
                 AND grc_sql_leaf.symbol_segment_count = 1
                 AND grc_sql_leaf.symbol_name = sql_leaf_name(fc.name) COLLATE NOCASE
                LEFT JOIN conservative_reference_counts crc
                  ON crc.logical_target_key = fc.logical_target_key
                 AND crc.name = fc.name
                 AND crc.kind = fc.kind
                LEFT JOIN csharp_identity_reference_counts circ
                  ON circ.logical_target_key = fc.logical_target_key
                 AND circ.target_name = fc.name
                 AND circ.target_kind = fc.kind
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

        return new SymbolHotspotRowsQuery(sql, candidatePlan);
    }

    public HotspotCountResult CountGroupedSymbolHotspots(string? kind, string? lang, IReadOnlyList<string>? pathPatterns, IReadOnlyList<string>? excludePathPatterns, bool excludeTests, IReadOnlyList<string>? visibilityFilters = null, IReadOnlyList<string>? excludeVisibilityFilters = null)
        => RunInReadSnapshot(() => CountGroupedSymbolHotspotsCore(kind, lang, pathPatterns, excludePathPatterns, excludeTests, visibilityFilters, excludeVisibilityFilters));

    private HotspotCountResult CountGroupedSymbolHotspotsCore(string? kind, string? lang, IReadOnlyList<string>? pathPatterns, IReadOnlyList<string>? excludePathPatterns, bool excludeTests, IReadOnlyList<string>? visibilityFilters, IReadOnlyList<string>? excludeVisibilityFilters)
    {
        if (!_hasReferencesTable) return new HotspotCountResult(0, 0);
        var query = BuildGroupedSymbolHotspotRowsQuery(null, kind, lang, pathPatterns, excludePathPatterns, excludeTests, visibilityFilters, excludeVisibilityFilters);
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
    public List<GroupedHotspotResult> GetGroupedSymbolHotspots(int limit, string? kind, string? lang, IReadOnlyList<string>? pathPatterns, IReadOnlyList<string>? excludePathPatterns, bool excludeTests, IReadOnlyList<string>? visibilityFilters = null, IReadOnlyList<string>? excludeVisibilityFilters = null, int offset = 0)
        => RunInReadSnapshot(() => GetGroupedSymbolHotspotsCore(limit, kind, lang, pathPatterns, excludePathPatterns, excludeTests, visibilityFilters, excludeVisibilityFilters, offset));

    private List<GroupedHotspotResult> GetGroupedSymbolHotspotsCore(int limit, string? kind, string? lang, IReadOnlyList<string>? pathPatterns, IReadOnlyList<string>? excludePathPatterns, bool excludeTests, IReadOnlyList<string>? visibilityFilters, IReadOnlyList<string>? excludeVisibilityFilters, int offset)
    {
        if (!_hasReferencesTable) return [];

        var query = BuildGroupedSymbolHotspotRowsQuery(checked(limit + Math.Max(0, offset)), kind, lang, pathPatterns, excludePathPatterns, excludeTests, visibilityFilters, excludeVisibilityFilters);
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
            LIMIT @limit OFFSET @offset";

        using var cmd = _conn.CreateCommand();
        cmd.CommandText = sql;
        AddSymbolHotspotParameters(cmd, query, limit, kind, lang, pathPatterns, excludePathPatterns, visibilityFilters, excludeVisibilityFilters);
        SqliteCommandPolicy.Add(cmd, "@offset", Math.Max(0, offset));
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

    internal static string GetHotspotReferenceWeightSql(string referenceKindSql) => $@"
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

    // A limited hotspot query first reads a fixed-size, index-ordered candidate frontier.
    // The expensive symbol ambiguity and logical-target joins then operate only on names
    // reachable from that frontier. Count queries intentionally pass no limit and retain
    // authoritative full-set semantics.
    // limit query は index 順の固定上限 frontier を先に選び、重い曖昧性判定を候補名に限定する。
    private const int MinimumBoundedHotspotCandidateCount = 512;
    private const int MaximumBoundedHotspotCandidateCount = 4096;
    private const int BoundedHotspotCandidatesPerResult = 64;

    private int? GetBoundedHotspotCandidateLimit(
        int? resultLimit,
        string? kind,
        string? lang)
    {
        if (!_hasHotspotReferenceCountsTable || !resultLimit.HasValue)
            return null;

        // The persisted name frontier is not authoritative for current C# callable
        // hotspots, whose final rank is based on resolved logical target identities.
        // Disable the legacy frontier whenever that identity-ranked result set can be
        // selected; otherwise unresolved high-volume names can evict real targets.
        // current C# callable hotspot の最終順位は resolved logical target identity
        // に基づくため、旧 name frontier は使わず実 target の取りこぼしを防ぐ。
        var canSelectCSharpCallable = (lang == null || lang == "csharp")
            && (kind == null || kind is "function" or "test.method" or "property");
        if (CanUseCSharpIdentityHotspotCounts() && canSelectCSharpCallable)
            return null;

        return Math.Clamp(
            resultLimit.Value * BoundedHotspotCandidatesPerResult,
            MinimumBoundedHotspotCandidateCount,
            MaximumBoundedHotspotCandidateCount);
    }

    private static string BuildBoundedHotspotCandidatePrefix(int? candidateLimit)
        => candidateLimit.HasValue
            ? @"
            bounded_reference_names AS MATERIALIZED (
                SELECT lang,
                       raw_symbol_name,
                       symbol_name,
                       symbol_segment_count,
                       allow_leaf_fallback
                FROM hotspot_reference_counts
                ORDER BY reference_score DESC,
                         reference_count DESC,
                         lang,
                         symbol_name,
                         symbol_segment_count,
                         raw_symbol_name
                LIMIT @candidateReferenceLimit
            ),
            "
            : string.Empty;

    private static string BuildBoundedHotspotSymbolPredicate(int? candidateLimit)
        => candidateLimit.HasValue
            ? @"
                  AND EXISTS (
                      SELECT 1
                      FROM bounded_reference_names brn
                      WHERE brn.lang = f.lang
                        AND (
                            (f.lang != 'sql' AND brn.symbol_name = s.name)
                            OR (f.lang = 'sql' AND (
                                (brn.symbol_segment_count = sql_segment_count(s.name)
                                 AND brn.symbol_name = sql_normalize_name(s.name) COLLATE NOCASE)
                                OR (sql_segment_count(s.name) > 1
                                    AND brn.allow_leaf_fallback = 1
                                    AND brn.raw_symbol_name = sql_leaf_name(s.name) COLLATE NOCASE)
                            ))
                        )
                  )"
            : string.Empty;

    private string BuildHotspotLogicalReferenceRowsSql(bool includeLeafMetadata, bool boundedCandidates = false)
    {
        if (_hasHotspotReferenceCountsTable)
        {
            var boundedWhere = boundedCandidates
                ? @"
                    WHERE EXISTS (
                        SELECT 1
                        FROM bounded_reference_names brn
                        WHERE brn.lang = hrc.lang
                          AND (
                              (brn.symbol_name = hrc.symbol_name
                               AND brn.symbol_segment_count = hrc.symbol_segment_count)
                              OR (hrc.allow_leaf_fallback = 1
                                  AND brn.raw_symbol_name = hrc.raw_symbol_name)
                          )
                    )"
                : string.Empty;
            return includeLeafMetadata
                ? @"SELECT file_id,
                           lang,
                           raw_symbol_name,
                           symbol_name,
                           symbol_segment_count,
                           allow_leaf_fallback,
                           reference_count,
                           reference_score
                    FROM hotspot_reference_counts hrc" + boundedWhere
                : @"SELECT file_id,
                           lang,
                           symbol_name,
                           symbol_segment_count,
                           reference_count,
                           reference_score
                    FROM hotspot_reference_counts hrc" + boundedWhere;
        }

        var nonSqlNameSql = @"CASE
                WHEN rf.lang = 'markdown' AND instr(sr.symbol_name, '#') > 0
                    THEN substr(sr.symbol_name, 1, instr(sr.symbol_name, '#') - 1)
                ELSE sr.symbol_name
            END";
        var contextSql = ReferenceContextSql("sr");
        var referenceLineJoinSql = ReferenceLineJoinSql("sr");
        var logicalKindSql = GetLogicalReferenceKindSql("sr.reference_kind");
        var referenceWeightSql = GetHotspotReferenceWeightSql("sr.reference_kind");
        var sqlNameSql = BuildLogicalReferenceNameExpr("rf.lang", "sr.symbol_name", contextSql, "sr.container_name", "sr.column_number");
        var sqlSegmentCountSql = BuildLogicalReferenceSegmentCountExpr("rf.lang", "sr.symbol_name", contextSql, "sr.container_name", "sr.column_number");
        // Raw spellings are metadata, not part of logical-site identity. Pick one
        // deterministic spelling after grouping by the resolved site so aliases that
        // canonicalize to the same location are counted once in aggregate and fallback paths.
        // raw spelling は logical-site identity ではないため、resolved site ごとに1つ選ぶ。
        var rawNameProjection = includeLeafMetadata ? "MIN(sr.symbol_name) AS raw_symbol_name," : string.Empty;
        var nonSqlLeafProjection = includeLeafMetadata ? "0 AS allow_leaf_fallback," : string.Empty;
        var sqlLeafProjection = includeLeafMetadata
            ? "MAX(" + BuildLogicalReferenceLeafFallbackAllowedExpr("rf.lang", "sr.symbol_name", contextSql, "sr.container_name", "sr.column_number") + ") AS allow_leaf_fallback,"
            : string.Empty;

        return $@"
            SELECT sr.file_id,
                   rf.lang,
                   {rawNameProjection}
                   {nonSqlNameSql} AS symbol_name,
                   1 AS symbol_segment_count,
                   {nonSqlLeafProjection}
                   1 AS reference_count,
                   {referenceWeightSql} AS reference_score
            FROM symbol_references sr
            JOIN files rf ON rf.id = sr.file_id
            WHERE sr.reference_kind IN {CallGraphReferenceKindsSql}
              AND sr.symbol_name IS NOT NULL
              AND sr.symbol_name <> ''
              AND rf.lang != 'sql'
            GROUP BY rf.lang,
                     sr.file_id,
                     {nonSqlNameSql},
                     symbol_segment_count,
                     sr.line,
                     sr.column_number,
                     {logicalKindSql}

            UNION ALL

            SELECT sr.file_id,
                   rf.lang,
                   {rawNameProjection}
                   {sqlNameSql} AS symbol_name,
                   {sqlSegmentCountSql} AS symbol_segment_count,
                   {sqlLeafProjection}
                   1 AS reference_count,
                   {referenceWeightSql} AS reference_score
            FROM symbol_references sr
            JOIN files rf ON rf.id = sr.file_id{referenceLineJoinSql}
            WHERE sr.reference_kind IN {CallGraphReferenceKindsSql}
              AND sr.symbol_name IS NOT NULL
              AND sr.symbol_name <> ''
              AND rf.lang = 'sql'
            GROUP BY rf.lang,
                     sr.file_id,
                     {sqlNameSql},
                     {sqlSegmentCountSql},
                     sr.line,
                     sr.column_number,
                     {logicalKindSql}";
    }

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
    public List<SymbolHotspotResult> GetSymbolHotspots(int limit, string? kind, string? lang, IReadOnlyList<string>? pathPatterns, IReadOnlyList<string>? excludePathPatterns, bool excludeTests, IReadOnlyList<string>? visibilityFilters = null, IReadOnlyList<string>? excludeVisibilityFilters = null, int offset = 0)
        => RunInReadSnapshot(() => GetSymbolHotspotsCore(limit, kind, lang, pathPatterns, excludePathPatterns, excludeTests, visibilityFilters, excludeVisibilityFilters, offset));

    private List<SymbolHotspotResult> GetSymbolHotspotsCore(int limit, string? kind, string? lang, IReadOnlyList<string>? pathPatterns, IReadOnlyList<string>? excludePathPatterns, bool excludeTests, IReadOnlyList<string>? visibilityFilters, IReadOnlyList<string>? excludeVisibilityFilters, int offset)
    {
        if (!_hasReferencesTable) return [];
        var query = BuildSymbolHotspotRowsQuery(checked(limit + Math.Max(0, offset)), kind, lang, pathPatterns, excludePathPatterns, excludeTests, visibilityFilters, excludeVisibilityFilters);
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
            LIMIT @limit OFFSET @offset";

        using var cmd = _conn.CreateCommand();
        cmd.CommandText = sql;
        AddSymbolHotspotParameters(cmd, query, limit, kind, lang, pathPatterns, excludePathPatterns, visibilityFilters, excludeVisibilityFilters);
        SqliteCommandPolicy.Add(cmd, "@offset", Math.Max(0, offset));

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

    public List<FileHotspotResult> GetFileSymbolHotspots(int limit, string? kind, string? lang, IReadOnlyList<string>? pathPatterns, IReadOnlyList<string>? excludePathPatterns, bool excludeTests, IReadOnlyList<string>? visibilityFilters = null, IReadOnlyList<string>? excludeVisibilityFilters = null, int offset = 0)
        => RunInReadSnapshot(() => GetFileSymbolHotspotsCore(limit, kind, lang, pathPatterns, excludePathPatterns, excludeTests, visibilityFilters, excludeVisibilityFilters, offset));

    private List<FileHotspotResult> GetFileSymbolHotspotsCore(int limit, string? kind, string? lang, IReadOnlyList<string>? pathPatterns, IReadOnlyList<string>? excludePathPatterns, bool excludeTests, IReadOnlyList<string>? visibilityFilters, IReadOnlyList<string>? excludeVisibilityFilters, int offset)
    {
        if (!_hasReferencesTable) return [];
        var query = BuildSymbolHotspotRowsQuery(checked(limit + Math.Max(0, offset)), kind, lang, pathPatterns, excludePathPatterns, excludeTests, visibilityFilters, excludeVisibilityFilters);
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
            LIMIT @limit OFFSET @offset";

        using var cmd = _conn.CreateCommand();
        cmd.CommandText = sql;
        AddSymbolHotspotParameters(cmd, query, limit, kind, lang, pathPatterns, excludePathPatterns, visibilityFilters, excludeVisibilityFilters);
        SqliteCommandPolicy.Add(cmd, "@offset", Math.Max(0, offset));

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
        reader.Dispose();
        PopulateBoundedFileHotspotSymbolCounts(results, kind, visibilityFilters, excludeVisibilityFilters);
        return results;
    }

    private void PopulateBoundedFileHotspotSymbolCounts(
        List<FileHotspotResult> results,
        string? kind,
        IReadOnlyList<string>? visibilityFilters,
        IReadOnlyList<string>? excludeVisibilityFilters)
    {
        if (results.Count == 0)
            return;

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
                      OR COALESCE(s.signature, '') LIKE '%.' || s.name || '(%'
                  )
              )"
            : string.Empty;
        var pathParameters = results.Select((_, i) => $"@fileHotspotPath{i}").ToList();
        var sql = $@"
            SELECT f.path, COUNT(*)
            FROM symbols s
            JOIN files f ON f.id = s.file_id
            WHERE f.path IN ({string.Join(",", pathParameters)})
              AND s.kind NOT IN ('import', 'namespace')" + csharpFunctionDefinitionGateSql;
        if (kind != null)
            sql += " AND s.kind = @kind";
        AppendVisibilityFilters(ref sql, visibilityFilters, excludeVisibilityFilters);
        sql += " GROUP BY f.path";

        using var cmd = _conn.CreateCommand();
        cmd.CommandText = sql;
        for (var i = 0; i < results.Count; i++)
            SqliteCommandPolicy.Add(cmd, pathParameters[i], results[i].Path);
        if (kind != null)
            SqliteCommandPolicy.Add(cmd, "@kind", kind);
        AddVisibilityFilterParameters(cmd, visibilityFilters, excludeVisibilityFilters);
        var byPath = results.ToDictionary(result => result.Path, StringComparer.Ordinal);
        using var countReader = cmd.ExecuteTrackedReader();
        while (countReader.TrackedRead())
        {
            if (byPath.TryGetValue(countReader.GetString(0), out var result))
                result.SymbolCount = countReader.GetInt32(1);
        }
    }

    private static string GetFileHotspotStructuralRankPenaltySql(string symbolCountSql)
        => $@"CASE
                WHEN {symbolCountSql} <= 2 THEN 0.1
                WHEN {symbolCountSql} <= 8 THEN 0.35
                ELSE 1.0
            END";

    public HotspotCountResult CountSymbolHotspots(string? kind, string? lang, IReadOnlyList<string>? pathPatterns, IReadOnlyList<string>? excludePathPatterns, bool excludeTests, IReadOnlyList<string>? visibilityFilters = null, IReadOnlyList<string>? excludeVisibilityFilters = null)
        => RunInReadSnapshot(() => CountSymbolHotspotsCore(kind, lang, pathPatterns, excludePathPatterns, excludeTests, visibilityFilters, excludeVisibilityFilters));

    private HotspotCountResult CountSymbolHotspotsCore(string? kind, string? lang, IReadOnlyList<string>? pathPatterns, IReadOnlyList<string>? excludePathPatterns, bool excludeTests, IReadOnlyList<string>? visibilityFilters, IReadOnlyList<string>? excludeVisibilityFilters)
    {
        if (!_hasReferencesTable) return new HotspotCountResult(0, 0);
        var query = BuildSymbolHotspotRowsQuery(null, kind, lang, pathPatterns, excludePathPatterns, excludeTests, visibilityFilters, excludeVisibilityFilters);
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
        => RunInReadSnapshot(() => CountFileSymbolHotspotsCore(kind, lang, pathPatterns, excludePathPatterns, excludeTests, visibilityFilters, excludeVisibilityFilters));

    private HotspotCountResult CountFileSymbolHotspotsCore(string? kind, string? lang, IReadOnlyList<string>? pathPatterns, IReadOnlyList<string>? excludePathPatterns, bool excludeTests, IReadOnlyList<string>? visibilityFilters, IReadOnlyList<string>? excludeVisibilityFilters)
    {
        if (!_hasReferencesTable) return new HotspotCountResult(0, 0);
        var query = BuildSymbolHotspotRowsQuery(null, kind, lang, pathPatterns, excludePathPatterns, excludeTests, visibilityFilters, excludeVisibilityFilters);
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

    private SymbolHotspotRowsQuery BuildSymbolHotspotRowsQuery(int? resultLimit, string? kind, string? lang, IReadOnlyList<string>? pathPatterns, IReadOnlyList<string>? excludePathPatterns, bool excludeTests, IReadOnlyList<string>? visibilityFilters, IReadOnlyList<string>? excludeVisibilityFilters)
    {
        var candidatePlan = BuildSymbolHotspotCandidatePlan(
            resultLimit,
            kind,
            lang,
            pathPatterns,
            excludePathPatterns,
            excludeTests,
            visibilityFilters,
            excludeVisibilityFilters);
        var csharpIdentityCountSql = CanUseCSharpIdentityHotspotCounts()
            ? "WHEN gr.lang = 'csharp' AND gr.kind IN ('function', 'test.method', 'property') THEN COALESCE(circ.ref_count, 0)"
            : string.Empty;
        var csharpIdentityScoreSql = CanUseCSharpIdentityHotspotCounts()
            ? "WHEN gr.lang = 'csharp' AND gr.kind IN ('function', 'test.method', 'property') THEN COALESCE(circ.ref_score, 0.0)"
            : string.Empty;
        var sql = candidatePlan.Sql + @"
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
            logical_references AS MATERIALIZED (
                " + BuildHotspotLogicalReferenceRowsSql(includeLeafMetadata: true, boundedCandidates: candidatePlan.CandidateLimit.HasValue) + @"
            )," + BuildCSharpIdentityHotspotReferenceCountsSql() + @"
            file_reference_counts_exact AS MATERIALIZED (
                SELECT lang,
                       file_id,
                       symbol_name,
                       symbol_segment_count,
                       SUM(reference_count) AS ref_count,
                       SUM(reference_score) AS ref_score
                FROM logical_references
                GROUP BY lang, file_id, symbol_name, symbol_segment_count
            ),
            global_exact_reference_counts AS (
                SELECT lang,
                       symbol_name,
                       symbol_segment_count,
                       SUM(ref_count) AS ref_count,
                       SUM(ref_score) AS ref_score
                FROM file_reference_counts_exact
                GROUP BY lang, symbol_name, symbol_segment_count
            ),
            file_reference_counts_leaf AS MATERIALIZED (
                SELECT lang,
                       file_id,
                       raw_symbol_name,
                       symbol_name AS resolved_symbol_name,
                       symbol_segment_count AS resolved_symbol_segment_count,
                       SUM(reference_count) AS ref_count,
                       SUM(reference_score) AS ref_score
                FROM logical_references
                WHERE allow_leaf_fallback = 1
                GROUP BY lang, file_id, raw_symbol_name, resolved_symbol_name, resolved_symbol_segment_count
            ),
            global_leaf_reference_counts AS (
                SELECT lang,
                       raw_symbol_name,
                       resolved_symbol_name,
                       resolved_symbol_segment_count,
                       SUM(ref_count) AS ref_count,
                       SUM(ref_score) AS ref_score
                FROM file_reference_counts_leaf
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
            conservative_reference_counts AS (
                SELECT ctf.logical_target_key,
                       ctf.name,
                       ctf.kind,
                       SUM(
                           COALESCE(frc_non_sql.ref_count, 0)
                           + COALESCE(frc_sql_exact.ref_count, 0)
                           + COALESCE(frc_leaf.ref_count, 0)) AS ref_count,
                       SUM(
                           COALESCE(frc_non_sql.ref_score, 0.0)
                           + COALESCE(frc_sql_exact.ref_score, 0.0)
                           + COALESCE(frc_leaf.ref_score, 0.0)) AS ref_score
                FROM conservative_target_files ctf
                JOIN file_target_cardinality ftc
                  ON ftc.lang = ctf.lang
                 AND ftc.file_id = ctf.file_id
                 AND ftc.name = ctf.name
                 AND ftc.kind = ctf.kind
                 AND ftc.target_count = 1
                LEFT JOIN file_reference_counts_exact frc_non_sql
                  ON ctf.lang != 'sql'
                 AND frc_non_sql.lang = ctf.lang
                 AND frc_non_sql.file_id = ctf.file_id
                 AND frc_non_sql.symbol_name = ctf.name
                LEFT JOIN file_reference_counts_exact frc_sql_exact
                  ON ctf.lang = 'sql'
                 AND frc_sql_exact.lang = ctf.lang
                 AND frc_sql_exact.file_id = ctf.file_id
                 AND frc_sql_exact.symbol_segment_count = sql_segment_count(ctf.name)
                 AND frc_sql_exact.symbol_name = sql_normalize_name(ctf.name) COLLATE NOCASE
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
                            " + csharpIdentityCountSql + @"
                            WHEN (gr.lang != 'csharp' OR gr.kind != 'property')
                              AND (
                                  nc.defs = 1
                                  OR (nc.count_safe_defs = nc.defs AND nc.count_safe_groups = 1)
                              )
                                THEN COALESCE(gerc_non_sql.ref_count, 0)
                                   + COALESCE(gerc_sql_exact.ref_count, 0)
                                   + COALESCE(glrc.ref_count, 0)
                            ELSE COALESCE(crc.ref_count, 0)
                        END AS ref_count,
                       CASE
                            " + csharpIdentityScoreSql + @"
                            WHEN (gr.lang != 'csharp' OR gr.kind != 'property')
                              AND (
                                  nc.defs = 1
                                  OR (nc.count_safe_defs = nc.defs AND nc.count_safe_groups = 1)
                              )
                                THEN COALESCE(gerc_non_sql.ref_score, 0.0)
                                   + COALESCE(gerc_sql_exact.ref_score, 0.0)
                                   + COALESCE(glrc.ref_score, 0.0)
                            ELSE COALESCE(crc.ref_score, 0.0)
                        END AS ref_score
                FROM grouped_rows gr
                JOIN name_cardinality nc
                  ON nc.lang = gr.lang
                  AND nc.name = gr.name
                LEFT JOIN global_exact_reference_counts gerc_non_sql
                  ON gr.lang != 'sql'
                 AND gerc_non_sql.lang = gr.lang
                 AND gerc_non_sql.symbol_name = gr.name
                LEFT JOIN global_exact_reference_counts gerc_sql_exact
                  ON gr.lang = 'sql'
                 AND gerc_sql_exact.lang = gr.lang
                 AND gerc_sql_exact.symbol_segment_count = sql_segment_count(gr.name)
                 AND gerc_sql_exact.symbol_name = sql_normalize_name(gr.name) COLLATE NOCASE
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
                LEFT JOIN csharp_identity_reference_counts circ
                  ON circ.logical_target_key = gr.logical_target_key
                 AND circ.target_name = gr.name
                 AND circ.target_kind = gr.kind
            ),
            file_symbol_counts AS (
                SELECT path,
                       COALESCE(lang, '') AS lang_key,
                       COUNT(*) AS symbol_count
                FROM filtered_candidates
                GROUP BY path, COALESCE(lang, '')
            )";
        return new SymbolHotspotRowsQuery(sql, candidatePlan);
    }

    private sealed record SymbolHotspotRowsQuery(
        string Sql,
        SymbolHotspotCandidatePlan CandidatePlan);

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

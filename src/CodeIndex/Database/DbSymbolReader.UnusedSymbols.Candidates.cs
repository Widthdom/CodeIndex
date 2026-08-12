using System.Text;
using Microsoft.Data.Sqlite;

namespace CodeIndex.Database;

public partial class DbReader
{
    private sealed class UnusedCandidateSymbol
    {
        public long FileId { get; init; }
        public string Path { get; init; } = string.Empty;
        public string? Lang { get; init; }
        public string Kind { get; init; } = string.Empty;
        public string Name { get; init; } = string.Empty;
        public int Line { get; init; }
        public int StartLine { get; init; }
        public int EndLine { get; init; }
        public string? Signature { get; init; }
        public string? Visibility { get; init; }
        public string? ReturnType { get; init; }
        public string? ContainerKind { get; init; }
        public string? ContainerName { get; init; }
        public string? ContainerQualifiedName { get; init; }
        public bool IsPublicOrExported { get; init; }
        public bool IsReflectionOrConfigSuspect { get; init; }
    }

    private readonly record struct UnusedCandidateScope(
        string? Kind,
        string? Lang,
        IReadOnlyList<string>? PathPatterns,
        IReadOnlyList<string>? ExcludePathPatterns,
        bool ExcludeTests,
        IReadOnlyList<string>? VisibilityFilters,
        IReadOnlyList<string>? ExcludeVisibilityFilters);

    private readonly record struct UnusedCandidateQueryPlan(
        string Sql,
        IReadOnlyList<string> GraphLanguages);

    private readonly record struct UnusedCandidateBucketSql(
        string IsPublicOrExported,
        string IsReflectionOrConfigSuspect,
        string ProvisionalOrder);

    private const string UnusedCandidateColumns =
        "file_id, path, lang, kind, name, line, start_line, end_line, signature, visibility, " +
        "return_type, container_kind, container_name, container_qualified_name, " +
        "is_public_or_exported, is_reflection_or_config_suspect, provisional_bucket_order";

    private List<UnusedSymbolResult> FetchUnusedCandidates(
        int fetchLimit,
        int provisionalBucketOrder,
        int offset,
        string? kind,
        string? lang,
        IReadOnlyList<string>? pathPatterns,
        IReadOnlyList<string>? excludePathPatterns,
        bool excludeTests,
        IReadOnlyList<string>? visibilityFilters = null,
        IReadOnlyList<string>? excludeVisibilityFilters = null)
    {
        var scope = new UnusedCandidateScope(
            kind, lang, pathPatterns, excludePathPatterns, excludeTests,
            visibilityFilters, excludeVisibilityFilters);
        var plan = BuildUnusedCandidatePageQuery(scope, resolveSqlReferences: true);
        using var cmd = _conn.CreateCommand();
        cmd.CommandText = plan.Sql;
        AddUnusedCandidatePageParameters(cmd, plan, scope, provisionalBucketOrder, fetchLimit, offset);

        var results = new List<UnusedSymbolResult>();
        using var reader = cmd.ExecuteTrackedReader();
        while (reader.TrackedRead())
            results.Add(CreateUnusedSymbolResult(ReadUnusedCandidate(reader)));
        return results;
    }

    private List<UnusedCandidateSymbol> FetchUnusedCandidateSymbols(
        int fetchLimit,
        int offset,
        int provisionalBucketOrder,
        string? kind,
        string? lang,
        IReadOnlyList<string>? pathPatterns,
        IReadOnlyList<string>? excludePathPatterns,
        bool excludeTests,
        IReadOnlyList<string>? visibilityFilters = null,
        IReadOnlyList<string>? excludeVisibilityFilters = null)
    {
        var scope = new UnusedCandidateScope(
            kind, lang, pathPatterns, excludePathPatterns, excludeTests,
            visibilityFilters, excludeVisibilityFilters);
        var plan = BuildUnusedCandidatePageQuery(scope, resolveSqlReferences: false);
        using var cmd = _conn.CreateCommand();
        cmd.CommandText = plan.Sql;
        AddUnusedCandidatePageParameters(cmd, plan, scope, provisionalBucketOrder, fetchLimit, offset);

        var candidates = new List<UnusedCandidateSymbol>();
        using var reader = cmd.ExecuteTrackedReader();
        while (reader.TrackedRead())
            candidates.Add(ReadUnusedCandidate(reader));
        return candidates;
    }

    private QueryCountResult CountUnusedCandidates(UnusedCandidateScope scope)
    {
        var plan = BuildUnusedCandidateCountQuery(scope);
        using var cmd = _conn.CreateCommand();
        cmd.CommandText = plan.Sql;
        AddUnusedCandidateScopeParameters(cmd, plan, scope);

        using var reader = cmd.ExecuteTrackedReader();
        if (!reader.TrackedRead())
            return new QueryCountResult(0, 0);
        return new QueryCountResult(
            reader.GetInt32(0),
            reader.GetInt32(1),
            reader.FieldCount > 2
            && !reader.IsDBNull(2)
            && Convert.ToInt32(reader.GetValue(2)) != 0);
    }

    private UnusedCandidateQueryPlan BuildUnusedCandidatePageQuery(
        UnusedCandidateScope scope,
        bool resolveSqlReferences)
    {
        var graphLanguages = GetUnusedCandidateGraphLanguages(scope.Lang, resolveSqlReferences);
        var bucketSql = BuildUnusedCandidateBucketSql();
        var sql = new StringBuilder(capacity: resolveSqlReferences ? 5_000 : 3_000);
        if (resolveSqlReferences)
            sql.Append("\n            WITH unused_candidates AS (\n                SELECT ");
        else
            sql.Append("\n            SELECT ");
        sql.Append(BuildUnusedCandidateProjectionSql(bucketSql));
        AppendUnusedCandidateSourceSql(sql, scope, graphLanguages, resolveSqlReferences);

        if (resolveSqlReferences)
        {
            sql.Append("\n            )\n            SELECT ");
            sql.Append(UnusedCandidateColumns);
            sql.Append("\n            FROM unused_candidates\n            WHERE provisional_bucket_order = @bucketOrder");
        }
        else
        {
            sql.Append(" AND (");
            sql.Append(bucketSql.ProvisionalOrder);
            sql.Append(") = @bucketOrder");
        }
        sql.Append("\n            ORDER BY path, line, name\n            LIMIT @limit OFFSET @offset");
        return new UnusedCandidateQueryPlan(sql.ToString(), graphLanguages);
    }

    private UnusedCandidateQueryPlan BuildUnusedCandidateCountQuery(UnusedCandidateScope scope)
    {
        var graphLanguages = GetUnusedCandidateGraphLanguages(scope.Lang, resolveSqlReferences: true);
        var sql = new StringBuilder(capacity: 4_000);
        sql.Append(@"
            SELECT COUNT(*), COUNT(DISTINCT f.path), MAX(CASE WHEN f.lang = 'sql' THEN 1 ELSE 0 END)");
        AppendUnusedCandidateSourceSql(sql, scope, graphLanguages, resolveSqlReferences: true);
        return new UnusedCandidateQueryPlan(sql.ToString(), graphLanguages);
    }

    private IReadOnlyList<string> GetUnusedCandidateGraphLanguages(
        string? lang,
        bool resolveSqlReferences)
    {
        var supportedLanguages = GetWorkspaceSupportedReferenceLanguages();
        if (lang != null)
            return Array.Empty<string>();

        var graphLanguages = new List<string>();
        foreach (var language in supportedLanguages)
        {
            if (resolveSqlReferences || !IsSqlLanguage(language))
                graphLanguages.Add(language);
        }
        return graphLanguages;
    }

    private void AppendUnusedCandidateSourceSql(
        StringBuilder sql,
        UnusedCandidateScope scope,
        IReadOnlyList<string> graphLanguages,
        bool resolveSqlReferences)
    {
        sql.Append(@"
            FROM symbols s
            JOIN files f ON s.file_id = f.id
            WHERE s.kind NOT IN ('import', 'namespace')");
        sql.Append(BuildUnusedReferenceAbsenceSql(resolveSqlReferences));
        if (resolveSqlReferences && _hasChunksTable && HasTable("chunks"))
        {
            var visibilitySql = $"lower({GetSymbolColumnSql("visibility", "''")})";
            sql.Append(BuildSameFilePrivateUseExclusionSql(
                "s", "f", visibilitySql,
                GetSymbolColumnSql("start_line", "s.line"),
                GetSymbolColumnSql("end_line", "s.line")));
            sql.Append(BuildCSharpPartialContainingTypeUseExclusionSql("s", "f", visibilitySql));
        }
        sql.Append("\n              AND ");
        sql.Append(BuildAmbiguousCSharpEnumMemberExclusionSql(
            "s", "f", scope.PathPatterns, scope.ExcludePathPatterns, scope.ExcludeTests));
        AppendUnusedCandidateScopeSql(sql, scope, graphLanguages);
    }

    private string BuildUnusedReferenceAbsenceSql(bool resolveSqlReferences)
    {
        if (!resolveSqlReferences)
        {
            return @"
              AND NOT EXISTS (
                  SELECT 1
                  FROM symbol_references sr
                  WHERE sr.symbol_name IS NOT NULL
                    AND sr.symbol_name <> ''
                    AND sr.symbol_name = s.name
              )";
        }

        var referenceContextSql = ReferenceContextSql("sr");
        return @"
              AND NOT EXISTS (
                  SELECT 1
                  FROM symbol_references sr
                  JOIN files rf ON rf.id = sr.file_id" + ReferenceLineJoinSql("sr") + @"
                  WHERE sr.symbol_name = s.name
                     OR (f.lang = 'sql' AND rf.lang = 'sql' AND (
                            (sql_resolve_reference_segment_count_at(sr.symbol_name, " + referenceContextSql + @", sr.container_name, sr.column_number) = sql_segment_count(s.name)
                             AND sql_reference_matches_target_at(sr.symbol_name, " + referenceContextSql + @", sr.container_name, sr.column_number, s.name) = 1)
                         OR (sql_segment_count(sr.symbol_name) = 1
                            AND sql_allow_leaf_fallback_at(sr.symbol_name, " + referenceContextSql + @", sr.container_name, sr.column_number) = 1
                            AND sr.symbol_name = sql_leaf_name(s.name) COLLATE NOCASE
                            AND NOT EXISTS (
                                    SELECT 1
                                    FROM symbols s_exact
                                    JOIN files f_exact ON f_exact.id = s_exact.file_id
                                    WHERE f_exact.lang = 'sql'
                                      AND sql_segment_count(s_exact.name) = sql_resolve_reference_segment_count_at(sr.symbol_name, " + referenceContextSql + @", sr.container_name, sr.column_number)
                                      AND sql_reference_matches_target_at(sr.symbol_name, " + referenceContextSql + @", sr.container_name, sr.column_number, s_exact.name) = 1
                                ))
                     ))
              )";
    }

    private void AppendUnusedCandidateScopeSql(
        StringBuilder sql,
        UnusedCandidateScope scope,
        IReadOnlyList<string> graphLanguages)
    {
        var filters = string.Empty;
        if (scope.Lang != null)
            filters += SymbolLanguageFileIdFilter;
        else
            filters += $" AND f.lang IN ({string.Join(",", graphLanguages.Select((_, i) => $"@gl{i}"))})";
        if (scope.Kind != null)
            filters += " AND s.kind = @kind";
        AppendPathFilters(
            ref filters,
            scope.PathPatterns,
            scope.ExcludePathPatterns,
            scope.ExcludeTests);
        AppendVisibilityFilters(
            ref filters,
            scope.VisibilityFilters,
            scope.ExcludeVisibilityFilters);
        sql.Append(filters);
    }

    private static void AddUnusedCandidatePageParameters(
        SqliteCommand command,
        UnusedCandidateQueryPlan plan,
        UnusedCandidateScope scope,
        int provisionalBucketOrder,
        int fetchLimit,
        int offset)
    {
        SqliteCommandPolicy.Add(command, "@bucketOrder", provisionalBucketOrder);
        SqliteCommandPolicy.Add(command, "@limit", fetchLimit);
        SqliteCommandPolicy.Add(command, "@offset", offset);
        AddUnusedCandidateScopeParameters(command, plan, scope);
    }

    private static void AddUnusedCandidateScopeParameters(
        SqliteCommand command,
        UnusedCandidateQueryPlan plan,
        UnusedCandidateScope scope)
    {
        if (scope.Lang != null)
            SqliteCommandPolicy.Add(command, "@lang", scope.Lang);
        else
        {
            for (var index = 0; index < plan.GraphLanguages.Count; index++)
                SqliteCommandPolicy.Add(command, $"@gl{index}", plan.GraphLanguages[index]);
        }
        if (scope.Kind != null)
            SqliteCommandPolicy.Add(command, "@kind", scope.Kind);
        AddPathFilterParameters(command, scope.PathPatterns, scope.ExcludePathPatterns);
        AddVisibilityFilterParameters(
            command,
            scope.VisibilityFilters,
            scope.ExcludeVisibilityFilters);
    }

    private string BuildUnusedCandidateProjectionSql(UnusedCandidateBucketSql bucketSql) => $@"s.file_id, f.path, f.lang, s.kind, s.name, s.line,
                   {GetSymbolColumnSql("start_line", "s.line")} AS start_line,
                   {GetSymbolColumnSql("end_line", "s.line")} AS end_line,
                   {GetSymbolColumnSql("signature")} AS signature,
                   {GetSymbolColumnSql("visibility")} AS visibility,
                   {GetSymbolColumnSql("return_type")} AS return_type,
                   {GetSymbolColumnSql("container_kind")} AS container_kind,
                   {GetSymbolColumnSql("container_name")} AS container_name,
                   {GetSymbolColumnSql("container_qualified_name", GetSymbolColumnSql("container_name", "''"))} AS container_qualified_name,
                   CASE WHEN {bucketSql.IsPublicOrExported} THEN 1 ELSE 0 END AS is_public_or_exported,
                   CASE WHEN {bucketSql.IsReflectionOrConfigSuspect} THEN 1 ELSE 0 END AS is_reflection_or_config_suspect,
                   {bucketSql.ProvisionalOrder} AS provisional_bucket_order";

    private UnusedCandidateBucketSql BuildUnusedCandidateBucketSql()
    {
        var visibilitySql = $"lower({GetSymbolColumnSql("visibility", "''")})";
        var signatureSql = $"lower({GetSymbolColumnSql("signature", "''")})";
        var isPublicOrExportedSql = $"{visibilitySql} IN ('public', 'open', 'pub', 'export')";
        var reflectionOrConfigSql = $@"(
                {isPublicOrExportedSql}
                AND s.kind = 'property'
                AND (
                    lower(f.path) LIKE 'config/%'
                    OR lower(f.path) LIKE '%/config/%'
                    OR lower(f.path) LIKE 'settings/%'
                    OR lower(f.path) LIKE '%/settings/%'
                    OR lower(f.path) LIKE 'options/%'
                    OR lower(f.path) LIKE '%/options/%'
                    OR {signatureSql} LIKE '%iconfiguration%'
                    OR {signatureSql} LIKE '%configurationsection%'
                    OR {signatureSql} LIKE '%ioptions%'
                    OR {signatureSql} LIKE '%options<%'
                )
            )";
        var provisionalOrderSql = $@"CASE
                WHEN {reflectionOrConfigSql} THEN 3
                WHEN {isPublicOrExportedSql} THEN 2
                WHEN {visibilitySql} IN ('private', 'fileprivate') THEN 0
                ELSE 1
            END";
        return new UnusedCandidateBucketSql(
            isPublicOrExportedSql,
            reflectionOrConfigSql,
            provisionalOrderSql);
    }

    private static UnusedCandidateSymbol ReadUnusedCandidate(SqliteDataReader reader) => new()
    {
        FileId = reader.GetInt64(0),
        Path = reader.GetString(1),
        Lang = GetNullableString(reader, 2),
        Kind = reader.GetString(3),
        Name = reader.GetString(4),
        Line = reader.GetInt32(5),
        StartLine = GetInt32OrFallback(reader, 6, 5),
        EndLine = GetInt32OrFallback(reader, 7, 5),
        Signature = GetNullableString(reader, 8),
        Visibility = GetNullableString(reader, 9),
        ReturnType = GetNullableString(reader, 10),
        ContainerKind = GetNullableString(reader, 11),
        ContainerName = GetNullableString(reader, 12),
        ContainerQualifiedName = GetNullableString(reader, 13),
        IsPublicOrExported = reader.GetInt32(14) != 0,
        IsReflectionOrConfigSuspect = reader.GetInt32(15) != 0,
    };

    private string BuildAmbiguousCSharpEnumMemberExclusionSql(
        string symbolAlias,
        string fileAlias,
        IReadOnlyList<string>? pathPatterns,
        IReadOnlyList<string>? excludePathPatterns,
        bool excludeTests)
    {
        var symbolContainerKindSql = GetSymbolColumnSql("container_kind", "''", symbolAlias);
        var symbolContainerNameSql = GetSymbolColumnSql("container_name", "''", symbolAlias);
        var symbolContainerQualifiedNameSql = GetSymbolColumnSql("container_qualified_name", symbolContainerNameSql, symbolAlias);
        var peerContainerKindSql = GetSymbolColumnSql("container_kind", "''", "s_peer");
        var peerContainerNameSql = GetSymbolColumnSql("container_name", "''", "s_peer");
        var peerContainerQualifiedNameSql = GetSymbolColumnSql("container_qualified_name", peerContainerNameSql, "s_peer");
        var peerPathFiltersSql = BuildPathFiltersSql("f_peer", pathPatterns, excludePathPatterns, excludeTests);

        return $@"
                NOT (
                    {fileAlias}.lang = 'csharp'
                    AND {symbolAlias}.kind = 'enum'
                    AND {symbolContainerKindSql} = 'enum'
                    AND EXISTS (
                        SELECT 1
                        FROM symbols s_peer
                        JOIN files f_peer ON f_peer.id = s_peer.file_id
                        WHERE f_peer.lang = 'csharp'
                          {peerPathFiltersSql}
                          AND s_peer.kind = 'enum'
                          AND {peerContainerKindSql} = 'enum'
                          AND s_peer.name = {symbolAlias}.name
                          AND {peerContainerQualifiedNameSql} <> {symbolContainerQualifiedNameSql}
                    )
                )";
    }
}

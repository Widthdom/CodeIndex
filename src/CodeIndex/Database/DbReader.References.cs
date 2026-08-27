using CodeIndex.Indexer;
using Microsoft.Data.Sqlite;

namespace CodeIndex.Database;

public partial class DbReader
{
    private const int CSharpUsingStaticReferenceFilterChunkSize = 64;
    private const int CSharpUsingStaticReferenceFilterMaxRawLimit = 65536;
    private sealed record SearchReferenceRawRow(string Path, string? Lang, string SymbolName, string ReferenceKind, int Line, int Column, string Context, string? ContainerKind, string? ContainerName, bool IsSelfReference, bool IsMutualRecursion, long? TargetSymbolId, string? TargetSymbolKey, string? ResolutionState, int ResolutionCandidateCount, int? SpanLength);
    private static bool IncludeAmbiguousMSourceForIdentityTarget(string? language, long? targetSymbolId) =>
        targetSymbolId != null && language is "matlab" or "objc";

    internal sealed record ReferencePositionCandidate(SymbolResult Definition, bool Authoritative);
    internal sealed record ReferencePositionResolution(
        bool IdentityAvailable,
        bool CandidatesTruncated,
        IReadOnlyList<ReferencePositionCandidate> Candidates,
        bool ExplicitNegativeEvidence = false);

    /// <summary>
    /// Search indexed references such as call sites.
    /// 呼び出し箇所などのインデックス済み参照を検索する。
    /// </summary>
    public List<ReferenceResult> SearchReferences(string? query = null, int limit = 20, string? lang = null, string? referenceKind = null, IReadOnlyList<string>? pathPatterns = null, IReadOnlyList<string>? excludePathPatterns = null, bool excludeTests = false, bool exact = false, int maxLineWidth = LineWidthFormatter.DefaultMaxLineWidth, bool excludeSelfReferences = false, int offset = 0, bool includeQualifiedCommonCalls = false)
    {
        maxLineWidth = LineWidthFormatter.ClampMaxLineWidth(maxLineWidth);
        lang = NormalizeQueryLanguage(lang);
        query = NormalizeSymbolSearchQuery(query, lang, exact) ?? query ?? string.Empty;
        if (!_hasReferencesTable)
            return new List<ReferenceResult>();

        if (!ShouldApplyCSharpUsingStaticConstantPatternReferenceFilter(lang, referenceKind, exact))
            return SearchReferencesCore(query, limit, lang, referenceKind, pathPatterns, excludePathPatterns, excludeTests, exact, offset, maxLineWidth, excludeSelfReferences, includeQualifiedCommonCalls);

        var rawLimit = Math.Max(limit, CSharpUsingStaticReferenceFilterChunkSize);
        var rawOffset = 0;
        var acceptedBeforePage = Math.Max(0, offset);
        var accepted = 0;
        var filtered = new List<ReferenceResult>();
        while (filtered.Count < limit)
        {
            var rawResults = SearchReferencesCore(query, rawLimit, lang, referenceKind, pathPatterns, excludePathPatterns, excludeTests, exact, rawOffset, maxLineWidth, excludeSelfReferences, includeQualifiedCommonCalls);
            if (rawResults.Count == 0)
                break;

            foreach (var result in rawResults)
            {
                if (ShouldSuppressCSharpUsingStaticConstantPatternReference(result))
                    continue;

                if (accepted < acceptedBeforePage)
                {
                    accepted++;
                    continue;
                }

                accepted++;
                filtered.Add(result);
                if (filtered.Count >= limit)
                    break;
            }

            if (rawResults.Count < rawLimit || filtered.Count >= limit)
                break;

            rawOffset += rawResults.Count;
            rawLimit = Math.Min(rawLimit * 2, CSharpUsingStaticReferenceFilterMaxRawLimit);
        }

        return filtered.Count <= limit ? filtered : filtered.Take(limit).ToList();
    }

    private List<ReferenceResult> SearchReferencesCore(string? query, int limit, string? lang, string? referenceKind, IReadOnlyList<string>? pathPatterns, IReadOnlyList<string>? excludePathPatterns, bool excludeTests, bool exact, int offset, int maxLineWidth, bool excludeSelfReferences, bool includeQualifiedCommonCalls, long? targetSymbolId = null, bool requireAuthoritativeIdentity = false)
    {
        using var cmd = CreateSearchReferencesCommandCore(query, limit, lang, referenceKind, pathPatterns, excludePathPatterns, excludeTests, exact, offset, includeOrdering: true, excludeSelfReferences, includeQualifiedCommonCalls, targetSymbolId, requireAuthoritativeIdentity);
        var results = new List<ReferenceResult>();
        using var reader = cmd.ExecuteTrackedReader();
        while (reader.TrackedRead())
        {
            var row = ReadSearchReferenceRawRow(reader);
            var clampedContext = LineWidthFormatter.ClampLine(row.Context, maxLineWidth, row.Column, query?.Length ?? 1);
            results.Add(new ReferenceResult
            {
                Path = row.Path,
                Lang = row.Lang,
                SymbolName = row.SymbolName,
                ReferenceKind = row.ReferenceKind,
                Line = row.Line,
                Column = row.Column,
                SpanLength = row.SpanLength,
                RawContext = row.Context,
                Context = clampedContext.Text,
                ContextTruncated = clampedContext.Truncated,
                ContainerKind = row.ContainerKind,
                ContainerName = row.ContainerName,
                IsSelfReference = row.IsSelfReference,
                IsMutualRecursion = row.IsMutualRecursion,
                TargetSymbolId = row.TargetSymbolId,
                TargetSymbolKey = row.TargetSymbolKey,
                ResolutionState = row.ResolutionState,
                ResolutionCandidateCount = row.ResolutionCandidateCount,
            });
        }
        return results;
    }

    internal IReadOnlyList<long> GetReferenceGraphIdentityCandidates(
        string query,
        string? lang,
        string? referenceKind,
        IReadOnlyList<string>? pathPatterns,
        IReadOnlyList<string>? excludePathPatterns,
        bool excludeTests,
        bool exact,
        bool includeQualifiedCommonCalls)
    {
        if (string.IsNullOrWhiteSpace(query) || !_hasReferencesTable)
            return [];

        lang = NormalizeQueryLanguage(lang);
        query = NormalizeSymbolSearchQuery(query, lang, exact) ?? query;
        using var command = CreateSearchReferencesCommandCore(
            query,
            limit: 0,
            lang,
            referenceKind,
            pathPatterns,
            excludePathPatterns,
            excludeTests,
            exact,
            offset: 0,
            includeOrdering: false,
            excludeSelfReferences: false,
            includeQualifiedCommonCalls,
            targetSymbolId: null);
        command.CommandText = $@"
            SELECT DISTINCT CAST(identity.value AS INTEGER) AS symbol_id
            FROM ({command.CommandText}) AS graph_rows
            JOIN json_each(
                CASE
                    WHEN graph_rows.root_symbol_ids IS NULL OR graph_rows.root_symbol_ids = '' THEN '[]'
                    ELSE '[' || graph_rows.root_symbol_ids || ']'
                END
            ) AS identity
            WHERE CAST(identity.value AS INTEGER) > 0
            ORDER BY symbol_id
            LIMIT @graphIdentityLimit";
        SqliteCommandPolicy.Add(command, "@graphIdentityLimit", GraphIdentityCandidateLimit + 1);

        var symbolIds = new List<long>();
        using var reader = command.ExecuteTrackedReader();
        while (reader.TrackedRead())
            symbolIds.Add(reader.GetInt64(0));
        return symbolIds;
    }

    internal List<ReferenceResult> SearchReferencesForCandidate(
        DefinitionResult definition,
        int limit,
        IReadOnlyList<string>? pathPatterns,
        IReadOnlyList<string>? excludePathPatterns,
        bool excludeTests,
        int maxLineWidth,
        int offset = 0,
        string? referenceKind = null,
        bool excludeSelfReferences = false,
        bool includeQualifiedCommonCalls = false,
        bool requireAuthoritativeIdentity = false)
    {
        if (definition.SymbolId is not long symbolId || !HasTable("symbol_reference_candidates"))
            return [];

        return SearchReferencesCore(
            definition.Name,
            limit,
            definition.Lang,
            referenceKind,
            pathPatterns,
            excludePathPatterns,
            excludeTests,
            exact: true,
            offset,
            maxLineWidth,
            excludeSelfReferences,
            includeQualifiedCommonCalls,
            targetSymbolId: symbolId,
            requireAuthoritativeIdentity: requireAuthoritativeIdentity);
    }

    internal QueryCountResult CountSearchReferencesForCandidate(
        DefinitionResult definition,
        IReadOnlyList<string>? pathPatterns,
        IReadOnlyList<string>? excludePathPatterns,
        bool excludeTests,
        string? referenceKind = null,
        bool excludeSelfReferences = false,
        bool includeQualifiedCommonCalls = false,
        bool requireAuthoritativeIdentity = false)
    {
        if (definition.SymbolId is not long symbolId || !HasTable("symbol_reference_candidates"))
            return new QueryCountResult(0, 0);

        using var cmd = CreateSearchReferencesCommandCore(
            definition.Name,
            limit: int.MaxValue,
            definition.Lang,
            referenceKind,
            pathPatterns,
            excludePathPatterns,
            excludeTests,
            exact: true,
            offset: 0,
            includeOrdering: false,
            excludeSelfReferences,
            includeQualifiedCommonCalls,
            targetSymbolId: symbolId,
            requireAuthoritativeIdentity: requireAuthoritativeIdentity);
        cmd.CommandText = $"SELECT COUNT(*), COUNT(DISTINCT path), MAX(CASE WHEN lang = 'sql' THEN 1 ELSE 0 END) FROM ({cmd.CommandText})";
        return ExecuteCountSummary(cmd);
    }

    internal ReferencePositionResolution GetReferencePositionResolution(
        string path,
        string symbolName,
        int line,
        int column,
        int maxCandidates)
    {
        if (maxCandidates <= 0 ||
            !_hasReferencesTable ||
            !_referenceIdentityContractCurrent ||
            !HasTable("symbol_reference_candidates"))
        {
            return new ReferencePositionResolution(false, false, []);
        }

        EnsureCSharpCallableTypeKinds(candidateQueries: [symbolName], exact: true);
        using var txn = _conn.BeginTransaction(deferred: true);
        using var negativeEvidenceCmd = _conn.CreateCommand();
        negativeEvidenceCmd.CommandText = """
            SELECT EXISTS (
                SELECT 1
                FROM symbol_references AS r
                JOIN files AS source_file ON source_file.id = r.file_id
                WHERE source_file.path = @path
                  AND r.symbol_name = @symbolName COLLATE NOCASE
                  AND r.line = @line
                  AND r.column_number = @column
                  AND r.target_qualifier IN (
                      char(31) || 'csharp_value_callable',
                      char(31) || 'csharp_local_uncertain'
                  )
            )
            """;
        SqliteCommandPolicy.Add(negativeEvidenceCmd, "@path", path);
        SqliteCommandPolicy.Add(negativeEvidenceCmd, "@symbolName", symbolName);
        SqliteCommandPolicy.Add(negativeEvidenceCmd, "@line", line);
        SqliteCommandPolicy.Add(negativeEvidenceCmd, "@column", column);
        var explicitNegativeEvidence = Convert.ToInt32(
            negativeEvidenceCmd.ExecuteScalar(),
            System.Globalization.CultureInfo.InvariantCulture) != 0;

        using var cmd = _conn.CreateCommand();
        var startLineSql = GetSymbolColumnSql("start_line", "s.line");
        var endLineSql = GetSymbolColumnSql("end_line", "s.line");
        var signatureSql = GetSymbolColumnSql("signature");
        var logicalPartialKeySql = LogicalPartialSymbolGrouper.BuildSqlKeyExpression(
            "target_file.lang",
            "s.kind",
            "s.name",
            "s.id",
            "target_file.path",
            signatureSql,
            GetSymbolColumnSql("container_name"),
            GetSymbolColumnSql("container_qualified_name"),
            GetSymbolColumnSql("family_key"),
            GetSymbolColumnSql("return_type"),
            GetSymbolColumnSql("is_partial_declaration"),
            _hotspotFamilyReadyLanguages.Contains("csharp"));
        cmd.CommandText = $@"
            SELECT target_file.path,
                   target_file.lang,
                   s.kind,
                   {GetSymbolColumnSql("sub_kind")} AS sub_kind,
                   s.name,
                   s.line,
                   {startLineSql} AS start_line,
                   {GetSymbolColumnSql("start_column")} AS start_column,
                   {endLineSql} AS end_line,
                   {GetSymbolColumnSql("body_start_line")} AS body_start_line,
                   {GetSymbolColumnSql("body_end_line")} AS body_end_line,
                   {signatureSql} AS signature,
                   {GetSymbolColumnSql("container_kind")} AS container_kind,
                   {GetSymbolColumnSql("container_name")} AS container_name,
                   {GetSymbolColumnSql("container_qualified_name")} AS container_qualified_name,
                   {GetSymbolColumnSql("visibility")} AS visibility,
                   {GetSymbolColumnSql("return_type")} AS return_type,
                   s.id AS symbol_id,
                   {logicalPartialKeySql} AS logical_partial_key,
                   MAX(CASE WHEN r.target_symbol_id = s.id THEN 1 ELSE 0 END) AS authoritative,
                   MIN(candidate.scope_rank) AS scope_rank
            FROM symbol_references AS r
            JOIN files AS source_file ON source_file.id = r.file_id
            JOIN symbol_reference_candidates AS candidate ON candidate.reference_id = r.id
            JOIN symbols AS s ON s.id = candidate.symbol_id
            JOIN files AS target_file ON target_file.id = s.file_id
            WHERE source_file.path = @path
              AND r.symbol_name = @symbolName COLLATE NOCASE
              AND r.line = @line
              AND r.column_number = @column
            GROUP BY s.id
            ORDER BY authoritative DESC, scope_rank, s.id
            LIMIT @limit";
        SqliteCommandPolicy.Add(cmd, "@path", path);
        SqliteCommandPolicy.Add(cmd, "@symbolName", symbolName);
        SqliteCommandPolicy.Add(cmd, "@line", line);
        SqliteCommandPolicy.Add(cmd, "@column", column);
        SqliteCommandPolicy.Add(cmd, "@limit", checked(maxCandidates + 1));

        var candidates = new List<ReferencePositionCandidate>();
        using var reader = cmd.ExecuteTrackedReader();
        while (reader.TrackedRead())
        {
            var name = reader.GetString(4);
            var signature = GetNullableString(reader, 11);
            candidates.Add(new ReferencePositionCandidate(
                new SymbolResult
                {
                    Path = reader.GetString(0),
                    Lang = GetNullableString(reader, 1),
                    Kind = reader.GetString(2),
                    SubKind = GetNullableString(reader, 3),
                    Name = name,
                    Line = reader.GetInt32(5),
                    StartLine = GetInt32OrFallback(reader, 6, 5),
                    StartColumn = ResolveSymbolIdentifierStartColumn(
                        GetNullableInt32(reader, 7),
                        signature,
                        name,
                        reader.GetString(2)),
                    EndLine = GetInt32OrFallback(reader, 8, 5),
                    BodyStartLine = GetNullableInt32(reader, 9),
                    BodyEndLine = GetNullableInt32(reader, 10),
                    Signature = signature,
                    ContainerKind = GetNullableString(reader, 12),
                    ContainerName = GetNullableString(reader, 13),
                    ContainerQualifiedName = GetNullableString(reader, 14),
                    Visibility = GetNullableString(reader, 15),
                    ReturnType = GetNullableString(reader, 16),
                    SymbolId = reader.GetInt64(17),
                    LogicalPartialKey = GetNullableString(reader, 18),
                },
                reader.GetInt32(19) != 0));
        }

        var truncated = candidates.Count > maxCandidates;
        if (truncated)
            candidates.RemoveRange(maxCandidates, candidates.Count - maxCandidates);
        txn.Commit();
        return new ReferencePositionResolution(
            true,
            truncated,
            candidates,
            explicitNegativeEvidence);
    }

    private SqliteCommand CreateSearchReferencesCommand(string? query, int limit, string? lang, string? referenceKind, IReadOnlyList<string>? pathPatterns, IReadOnlyList<string>? excludePathPatterns, bool excludeTests, bool exact, int offset = 0, bool includeOrdering = true, bool excludeSelfReferences = false, bool includeQualifiedCommonCalls = false)
        => CreateSearchReferencesCommandCore(query, limit, lang, referenceKind, pathPatterns, excludePathPatterns, excludeTests, exact, offset, includeOrdering, excludeSelfReferences, includeQualifiedCommonCalls, targetSymbolId: null);

    private SqliteCommand CreateSearchReferencesCommandCore(string? query, int limit, string? lang, string? referenceKind, IReadOnlyList<string>? pathPatterns, IReadOnlyList<string>? excludePathPatterns, bool excludeTests, bool exact, int offset, bool includeOrdering, bool excludeSelfReferences, bool includeQualifiedCommonCalls, long? targetSymbolId, bool requireAuthoritativeIdentity = false)
    {
        var cmd = _conn.CreateCommand();
        var referenceLineJoin = ReferenceLineJoinSql("r");
        var contextSql = ReferenceContextSql("r");
        var selfReferenceSql = _referenceColumns.Contains("is_self_reference") ? "r.is_self_reference" : "0";
        var mutualRecursionSql = _referenceColumns.Contains("is_mutual_recursion") ? "r.is_mutual_recursion" : "0";
        var targetSymbolIdSql = _referenceIdentityContractCurrent ? "r.target_symbol_id" : "NULL";
        var targetSymbolKeySql = _referenceIdentityContractCurrent ? "r.target_symbol_key" : "NULL";
        var resolutionStateSql = _referenceIdentityContractCurrent ? "r.resolution_state" : "NULL";
        var resolutionCandidateCountSql = _referenceIdentityContractCurrent ? "r.resolution_candidate_count" : "0";
        var referenceSpanLengthSql = _referenceColumns.Contains("span_length") ? "r.span_length" : "NULL";
        var rootSymbolIdsSql = BuildReferenceRootSymbolIdsSql("r");
        var sql = referenceKind == null
            ? $@"
            WITH logical_references AS (
                SELECT f.path, f.lang, r.symbol_name,
                       {GetPreferredReferenceKindSql("r.reference_kind")} AS reference_kind,
                       r.line, r.column_number,
                       MIN({contextSql}) AS context,
                       CASE WHEN COUNT(DISTINCT COALESCE(r.container_kind, '')) = 1 THEN MIN(r.container_kind) ELSE NULL END AS container_kind,
                       CASE WHEN COUNT(DISTINCT COALESCE(r.container_name, '')) = 1 THEN MIN(r.container_name) ELSE NULL END AS container_name,
                       MAX({selfReferenceSql}) AS is_self_reference,
                       MAX({mutualRecursionSql}) AS is_mutual_recursion,
                       CASE WHEN COUNT(DISTINCT COALESCE({targetSymbolIdSql}, -1)) = 1 THEN MIN({targetSymbolIdSql}) ELSE NULL END AS target_symbol_id,
                       CASE WHEN COUNT(DISTINCT COALESCE({targetSymbolKeySql}, '')) = 1 THEN MIN({targetSymbolKeySql}) ELSE NULL END AS target_symbol_key,
                       CASE WHEN COUNT(DISTINCT COALESCE({resolutionStateSql}, '')) = 1 THEN MIN({resolutionStateSql}) ELSE 'ambiguous' END AS resolution_state,
                       MAX({resolutionCandidateCountSql}) AS resolution_candidate_count,
                       MIN(CASE WHEN {referenceSpanLengthSql} > 0 THEN {referenceSpanLengthSql} ELSE NULL END) AS span_length,
                       GROUP_CONCAT(DISTINCT {rootSymbolIdsSql}) AS root_symbol_ids
                FROM symbol_references r
                JOIN files f ON r.file_id = f.id
                {referenceLineJoin}
                WHERE 1=1
                  AND {BuildGraphSupportedLanguagePredicate(cmd, "f", "graphLang")}"
            : @"
            SELECT f.path, f.lang, r.symbol_name, r.reference_kind, r.line, r.column_number,
                   " + contextSql + @", r.container_kind, r.container_name,
                   " + selfReferenceSql + @" AS is_self_reference,
                   " + mutualRecursionSql + @" AS is_mutual_recursion,
                   " + targetSymbolIdSql + @" AS target_symbol_id,
                   " + targetSymbolKeySql + @" AS target_symbol_key,
                   " + resolutionStateSql + @" AS resolution_state,
                   " + resolutionCandidateCountSql + @" AS resolution_candidate_count,
                   " + referenceSpanLengthSql + @" AS span_length,
                   " + rootSymbolIdsSql + @" AS root_symbol_ids
            FROM symbol_references r
            JOIN files f ON r.file_id = f.id
            " + referenceLineJoin + @"
            WHERE 1=1";

        if (referenceKind != null)
            sql += $" AND {BuildGraphSupportedLanguagePredicate(cmd, "f", "graphLang")}";

        var referencesSuffixAlias = ComputeCSharpAttributeSuffixAlias(query, lang, referenceKind);
        var referencesAliasScope = referencesSuffixAlias != null
            ? " AND f.lang = 'csharp' AND r.reference_kind = 'attribute'"
            : string.Empty;
        var referencesCssScssVariableAlias = ComputeCssScssVariableAlias(query);
        var referencesCssScssVariableAliasScope = referencesCssScssVariableAlias != null
            ? " AND f.lang = 'css'"
            : string.Empty;
        var allowCSharpQualifiedContextMatch = query != null
            && SqlNameResolver.HasQualifier(query)
            && !HasQualifiedSymbolDefinition(query, lang, pathPatterns, excludePathPatterns, excludeTests);
        var allowQualifiedLeafFallback = query != null && HasSingleQualifiedSymbolDefinition(query, lang, pathPatterns, excludePathPatterns, excludeTests);
        const string sqlLeafReferenceScope = " AND f.lang = 'sql'";
        if (query != null)
        {
            var allowSqlLeafFallback = AllowSqlLeafFallbackForQuery(query);
            var useSqlQualifiedContextMatch = SqlNameResolver.HasQualifier(query);
            // --exact: Unicode-aware equality when FoldReady (#86), else ASCII COLLATE NOCASE.
            // Fold path: r.symbol_name_folded = @qFolded (indexed), query pre-folded in .NET.
            // Fallback equality uses the left prefix of idx_symbol_refs_name_nocase_file.
            // When the query ends with C# attribute suffix `Attribute`, also OR against the
            // suffix-stripped alias so `references FooAttribute --exact` reaches the idiomatic
            // `[Foo]` reference site stored with `symbol_name = "Foo"`. In substring mode we
            // still LIKE-match `%FooAttribute%` and add only the exact stripped alias to avoid
            // overmatching unrelated names (e.g. `FooAuditLog`) that share the stripped prefix.
            // The alias disjunct is scoped to C# attribute rows to avoid false positives.
            // --exact: FoldReady なら Unicode 折り畳み経路、未 ready なら ASCII NOCASE へ fallback。
            // C# の `Attribute` suffix が付いたクエリは、suffix を外した別名とも照合する。
            // 部分一致モードでは `%FooAttribute%` をそのまま使い、別名側は exact 照合だけを OR
            // することで `FooAuditLog` など無関係な名前を巻き込まないようにする。
            // 別名節は C# の attribute 行に限定し、誤一致を避ける。
            if (useSqlQualifiedContextMatch && exact && _foldReady)
            {
                var qualifiedContextSql = BuildQualifiedContextMatchSql(contextSql, "r.column_number", folded: true, like: false);
                var csharpQualifiedContextSql = BuildCSharpQualifiedContextFallbackSql(qualifiedContextSql);
                var qualifiedLeafFallbackSql = BuildQualifiedLeafFallbackSql("r.symbol_name", "r.symbol_name_folded", folded: true);
                sql += referencesSuffixAlias != null
                    ? $" AND (((f.lang = 'sql') AND {qualifiedContextSql}) OR ((f.lang != 'sql') AND ({BuildPersistedFoldedNameMatchSql("r.symbol_name_folded", "@query")} OR (r.symbol_name_folded = @queryAttributeAlias{referencesAliasScope}))) OR {csharpQualifiedContextSql} OR {qualifiedLeafFallbackSql})"
                    : $" AND (((f.lang = 'sql') AND {qualifiedContextSql}) OR ((f.lang != 'sql') AND {BuildPersistedFoldedNameMatchSql("r.symbol_name_folded", "@query")}) OR {csharpQualifiedContextSql} OR {qualifiedLeafFallbackSql})";
            }
            else if (useSqlQualifiedContextMatch && exact)
            {
                var qualifiedContextSql = BuildQualifiedContextMatchSql(contextSql, "r.column_number", folded: false, like: false);
                var csharpQualifiedContextSql = BuildCSharpQualifiedContextFallbackSql(qualifiedContextSql);
                var qualifiedLeafFallbackSql = BuildQualifiedLeafFallbackSql("r.symbol_name", "r.symbol_name_folded", folded: false);
                sql += referencesSuffixAlias != null
                    ? $" AND (((f.lang = 'sql') AND {qualifiedContextSql}) OR ((f.lang != 'sql') AND (r.symbol_name = @query COLLATE NOCASE OR (r.symbol_name = @queryAttributeAlias COLLATE NOCASE{referencesAliasScope}))) OR {csharpQualifiedContextSql} OR {qualifiedLeafFallbackSql})"
                    : $" AND (((f.lang = 'sql') AND {qualifiedContextSql}) OR ((f.lang != 'sql') AND r.symbol_name = @query COLLATE NOCASE) OR {csharpQualifiedContextSql} OR {qualifiedLeafFallbackSql})";
            }
            else if (useSqlQualifiedContextMatch && _foldReady)
            {
                var qualifiedContextSql = BuildQualifiedContextMatchSql(contextSql, "r.column_number", folded: true, like: true);
                var csharpQualifiedContextSql = BuildCSharpQualifiedContextFallbackSql(qualifiedContextSql);
                var qualifiedLeafFallbackSql = BuildQualifiedLeafFallbackSql("r.symbol_name", "r.symbol_name_folded", folded: true);
                sql += referencesSuffixAlias != null
                    ? $" AND (((f.lang = 'sql') AND {qualifiedContextSql}) OR ((f.lang != 'sql') AND (r.symbol_name LIKE @query ESCAPE '\\' OR (r.symbol_name = @queryAttributeAlias COLLATE NOCASE{referencesAliasScope}))) OR {csharpQualifiedContextSql} OR {qualifiedLeafFallbackSql})"
                    : $" AND (((f.lang = 'sql') AND {qualifiedContextSql}) OR ((f.lang != 'sql') AND r.symbol_name LIKE @query ESCAPE '\\') OR {csharpQualifiedContextSql} OR {qualifiedLeafFallbackSql})";
            }
            else if (useSqlQualifiedContextMatch)
            {
                var qualifiedContextSql = BuildQualifiedContextMatchSql(contextSql, "r.column_number", folded: false, like: true);
                var csharpQualifiedContextSql = BuildCSharpQualifiedContextFallbackSql(qualifiedContextSql);
                var qualifiedLeafFallbackSql = BuildQualifiedLeafFallbackSql("r.symbol_name", "r.symbol_name_folded", folded: false);
                sql += referencesSuffixAlias != null
                    ? $" AND (((f.lang = 'sql') AND {qualifiedContextSql}) OR ((f.lang != 'sql') AND (r.symbol_name LIKE @query ESCAPE '\\' OR (r.symbol_name = @queryAttributeAlias COLLATE NOCASE{referencesAliasScope}))) OR {csharpQualifiedContextSql} OR {qualifiedLeafFallbackSql})"
                    : $" AND (((f.lang = 'sql') AND {qualifiedContextSql}) OR ((f.lang != 'sql') AND r.symbol_name LIKE @query ESCAPE '\\') OR {csharpQualifiedContextSql} OR {qualifiedLeafFallbackSql})";
            }
            else if (exact && _foldReady)
                sql += referencesSuffixAlias != null
                    ? $" AND ({BuildPersistedFoldedNameMatchSql("r.symbol_name_folded", "@query")} OR (r.symbol_name_folded = @queryAttributeAlias{referencesAliasScope}){(allowSqlLeafFallback ? $" OR (r.symbol_name_folded = @aliasQueryLeafFolded{sqlLeafReferenceScope})" : string.Empty)})"
                    : referencesCssScssVariableAlias != null
                        ? $" AND ({BuildPersistedFoldedNameMatchSql("r.symbol_name_folded", "@query")} OR (r.symbol_name_folded = @queryCssScssVariableAlias{referencesCssScssVariableAliasScope}){(allowSqlLeafFallback ? $" OR (r.symbol_name_folded = @aliasQueryLeafFolded{sqlLeafReferenceScope})" : string.Empty)})"
                        : allowSqlLeafFallback
                        ? $" AND ({BuildPersistedFoldedNameMatchSql("r.symbol_name_folded", "@query")} OR (r.symbol_name_folded = @aliasQueryLeafFolded{sqlLeafReferenceScope}))"
                        : $" AND {BuildPersistedFoldedNameMatchSql("r.symbol_name_folded", "@query")}";
            else if (exact)
                sql += referencesSuffixAlias != null
                    ? $" AND (r.symbol_name = @query COLLATE NOCASE OR (r.symbol_name = @queryAttributeAlias COLLATE NOCASE{referencesAliasScope}){(allowSqlLeafFallback ? $" OR (r.symbol_name = sql_leaf_name(@aliasQuery) COLLATE NOCASE{sqlLeafReferenceScope})" : string.Empty)})"
                    : referencesCssScssVariableAlias != null
                        ? $" AND (r.symbol_name = @query COLLATE NOCASE OR (r.symbol_name = @queryCssScssVariableAlias COLLATE NOCASE{referencesCssScssVariableAliasScope}){(allowSqlLeafFallback ? $" OR (r.symbol_name = sql_leaf_name(@aliasQuery) COLLATE NOCASE{sqlLeafReferenceScope})" : string.Empty)})"
                        : allowSqlLeafFallback
                        ? $" AND (r.symbol_name = @query COLLATE NOCASE OR (r.symbol_name = sql_leaf_name(@aliasQuery) COLLATE NOCASE{sqlLeafReferenceScope}))"
                        : " AND r.symbol_name = @query COLLATE NOCASE";
            else
                sql += referencesSuffixAlias != null
                    ? $" AND (r.symbol_name LIKE @query ESCAPE '\\' OR (r.symbol_name = @queryAttributeAlias COLLATE NOCASE{referencesAliasScope}) OR (r.symbol_name = sql_leaf_name(@aliasQuery) COLLATE NOCASE{sqlLeafReferenceScope}))"
                    : referencesCssScssVariableAlias != null
                        ? $" AND (r.symbol_name LIKE @query ESCAPE '\\' OR (r.symbol_name = @queryCssScssVariableAlias COLLATE NOCASE{referencesCssScssVariableAliasScope}) OR (r.symbol_name = sql_leaf_name(@aliasQuery) COLLATE NOCASE{sqlLeafReferenceScope}))"
                        : $" AND (r.symbol_name LIKE @query ESCAPE '\\' OR (r.symbol_name = sql_leaf_name(@aliasQuery) COLLATE NOCASE{sqlLeafReferenceScope}))";
        }
        if (referenceKind != null)
            sql += " AND r.reference_kind = @referenceKind";
        if (excludeSelfReferences)
            sql += $" AND {selfReferenceSql} = 0";
        if (targetSymbolId != null)
        {
            sql += !_referenceIdentityContractCurrent
                ? " AND 1 = 0"
                : requireAuthoritativeIdentity
                    ? " AND r.resolution_state = 'resolved' AND r.target_symbol_id = @targetSymbolId"
                    : @"
                AND EXISTS (
                    SELECT 1
                    FROM symbol_reference_candidates AS identity_candidate
                    WHERE identity_candidate.reference_id = r.id
                      AND identity_candidate.symbol_id = @targetSymbolId
                )";
        }
        if (lang != null)
        {
            sql += IncludeAmbiguousMSourceForIdentityTarget(lang, targetSymbolId)
                ? " AND (f.lang = @lang OR f.lang = 'ambiguous_m')"
                : " AND f.lang = @lang";
        }
        sql += BuildCSharpBareMemberReferenceFilter(
            query ?? string.Empty,
            lang,
            "f",
            "r",
            includeQualifiedCommonCalls);
        AppendPathFilters(ref sql, pathPatterns, excludePathPatterns, excludeTests);
        if (referenceKind == null)
        {
            sql += @"
                GROUP BY f.path, f.lang, r.file_id, r.symbol_name, r.line, r.column_number, " + GetLogicalReferenceKindSql("r.reference_kind") + @"
            )
            SELECT path, lang, symbol_name, reference_kind, line, column_number,
                   context, container_kind, container_name, is_self_reference, is_mutual_recursion,
                   target_symbol_id, target_symbol_key, resolution_state, resolution_candidate_count, span_length,
                   root_symbol_ids
            FROM logical_references r";
        }
        if (includeOrdering)
            sql += $" ORDER BY CASE WHEN @preferExactCase = 1 AND r.symbol_name = @rawQuery THEN 0 ELSE 1 END, {(referenceKind == null ? GetPathBucketOrderSql("r.path") : PathBucketOrder)}, CASE WHEN r.symbol_name = @rankingQuery COLLATE NOCASE THEN 0 ELSE 1 END, CASE WHEN r.symbol_name COLLATE NOCASE LIKE @rankingQueryPrefix ESCAPE '\\' THEN 0 ELSE 1 END, {(referenceKind == null ? "r.path" : "f.path")}, r.line, r.column_number, r.reference_kind, r.symbol_name LIMIT @limit OFFSET @offset";

        cmd.CommandText = sql;
        if (query != null)
        {
            string queryParam;
            if (!exact)
                queryParam = $"%{EscapeLikeQuery(query)}%";
            else if (_foldReady)
                queryParam = FoldNameForLanguage(query, lang);
            else
                queryParam = query;
            if (exact && _foldReady)
                AddPersistedFoldedNameQueryParameters(cmd, "@query", query, lang);
            else
                SqliteCommandPolicy.Add(cmd, "@query", queryParam);
            SqliteCommandPolicy.Add(cmd, "@aliasQuery", query);
            AddQualifiedGraphQueryParameters(cmd, query, allowQualifiedLeafFallback, allowCSharpQualifiedContextMatch);
            SqliteCommandPolicy.Add(cmd, "@aliasQueryLeafFolded", NameFold.Fold(SqlNameResolver.GetLeafName(query)) ?? SqlNameResolver.GetLeafName(query));
            if (referencesSuffixAlias != null)
            {
                var aliasParam = exact && _foldReady
                    ? NameFold.Fold(referencesSuffixAlias) ?? referencesSuffixAlias
                    : referencesSuffixAlias;
                SqliteCommandPolicy.Add(cmd, "@queryAttributeAlias", aliasParam);
            }
            if (referencesCssScssVariableAlias != null)
            {
                var aliasParam = exact && _foldReady
                    ? NameFold.Fold(referencesCssScssVariableAlias) ?? referencesCssScssVariableAlias
                    : referencesCssScssVariableAlias;
                SqliteCommandPolicy.Add(cmd, "@queryCssScssVariableAlias", aliasParam);
            }
            SqliteCommandPolicy.Add(cmd, "@rankingQuery", query.Trim());
            SqliteCommandPolicy.Add(cmd, "@rankingQueryPrefix", $"{EscapeLikeQuery(query.Trim())}%");
        }
        else
        {
            SqliteCommandPolicy.Add(cmd, "@rankingQuery", "");
            SqliteCommandPolicy.Add(cmd, "@rankingQueryPrefix", "%");
        }
        SqliteCommandPolicy.Add(cmd, "@preferExactCase", exact && query != null ? 1 : 0);
        SqliteCommandPolicy.Add(cmd, "@rawQuery", exact && query != null ? query : string.Empty);
        if (referenceKind != null)
            SqliteCommandPolicy.Add(cmd, "@referenceKind", referenceKind);
        if (lang != null)
            SqliteCommandPolicy.Add(cmd, "@lang", NormalizeQueryLanguage(lang));
        if (targetSymbolId != null && _referenceIdentityContractCurrent)
            SqliteCommandPolicy.Add(cmd, "@targetSymbolId", targetSymbolId.Value);
        AddPathFilterParameters(cmd, pathPatterns, excludePathPatterns);
        if (includeOrdering)
        {
            SqliteCommandPolicy.Add(cmd, "@limit", limit);
            SqliteCommandPolicy.Add(cmd, "@offset", offset);
        }
        return cmd;
    }

    private static SearchReferenceRawRow ReadSearchReferenceRawRow(SqliteDataReader reader)
    {
        return new SearchReferenceRawRow(
            reader.GetString(0),
            GetNullableString(reader, 1),
            reader.GetString(2),
            reader.GetString(3),
            reader.GetInt32(4),
            reader.GetInt32(5),
            reader.GetString(6),
            GetNullableString(reader, 7),
            GetNullableString(reader, 8),
            reader.GetInt32(9) != 0,
            reader.GetInt32(10) != 0,
            reader.IsDBNull(11) ? null : reader.GetInt64(11),
            GetNullableString(reader, 12),
            GetNullableString(reader, 13),
            reader.GetInt32(14),
            GetNullableInt32(reader, 15));
    }

    private static bool ShouldApplyCSharpUsingStaticConstantPatternReferenceFilter(string? lang, string? referenceKind, bool exact) =>
        exact
        &&
        (lang == null || string.Equals(lang, "csharp", StringComparison.Ordinal))
        && (referenceKind == null
            || string.Equals(referenceKind, "type_reference", StringComparison.Ordinal)
            || string.Equals(referenceKind, "call", StringComparison.Ordinal));

    private bool ShouldSuppressCSharpUsingStaticConstantPatternReference(ReferenceResult result)
    {
        var contextForFilter = string.IsNullOrWhiteSpace(result.RawContext)
            ? result.Context
            : result.RawContext;
        return ShouldSuppressCSharpUsingStaticConstantPatternReference(
            result.Path,
            result.Lang,
            result.SymbolName,
            result.ReferenceKind,
            result.Line,
            result.Column,
            contextForFilter);
    }

    private bool ShouldSuppressCSharpUsingStaticConstantPatternReference(SearchReferenceRawRow row)
    {
        return ShouldSuppressCSharpUsingStaticConstantPatternReference(
            row.Path,
            row.Lang,
            row.SymbolName,
            row.ReferenceKind,
            row.Line,
            row.Column,
            row.Context);
    }

    private bool ShouldSuppressCSharpUsingStaticConstantPatternReference(string path, string? lang, string symbolName, string referenceKind, int lineNumber, int columnNumber, string contextForFilter)
    {
        if (!string.Equals(lang, "csharp", StringComparison.Ordinal)
            || string.IsNullOrWhiteSpace(symbolName)
            || string.IsNullOrWhiteSpace(contextForFilter)
            || symbolName.IndexOf('.') >= 0
            || symbolName.IndexOf(':') >= 0
            || symbolName.IndexOf('<') >= 0
            || symbolName.IndexOf('[') >= 0
            || symbolName.IndexOf(' ') >= 0)
        {
            return false;
        }

        if (HasActiveCSharpUsingTypeAlias(path, lineNumber, symbolName))
            return false;

        var patternContext = contextForFilter;
        var patternColumn = columnNumber;
        if (!TryBuildCSharpUsingStaticPatternContextWindow(
                path,
                lineNumber,
                contextForFilter,
                columnNumber,
                symbolName,
                out patternContext,
                out patternColumn))
        {
            return false;
        }

        if (ShouldSuppressCSharpQualifiedConstantPatternReference(path, lineNumber, symbolName, patternContext, patternColumn, referenceKind))
            return true;

        if (!string.Equals(referenceKind, "type_reference", StringComparison.Ordinal))
            return false;

        var activeTargets = GetActiveCSharpUsingStaticTargets(path, lineNumber);
        if (activeTargets.Count == 0)
            return false;

        var matchingContainers = GetCSharpConstantPatternContainersByMemberName(symbolName);
        if (matchingContainers.Count == 0)
            return false;

        if (HasScopedCSharpTypeCandidate(path, lineNumber, symbolName))
            return false;

        foreach (var target in activeTargets)
        {
            if (matchingContainers.Contains(target))
                return true;
        }

        return false;
    }

    private bool ShouldSuppressCSharpQualifiedConstantPatternReference(string path, int lineNumber, string symbolName, string patternContext, int patternColumn, string referenceKind)
    {
        if (!TryExtractQualifiedCSharpPatternQualifier(patternContext, symbolName, patternColumn, out var qualifier, out var anchorKind))
            return false;

        // Exact `call` suppression only applies to `case` constant patterns; `is` patterns
        // keep their preserved call row so qualified `is` expressions remain visible.
        // exact の `call` 抑制は `case` 定数パターンのみに限定する。`is` パターンは
        // preserved call row を維持し、qualified な `is` 式を可視のまま残す。
        if (string.Equals(referenceKind, "call", StringComparison.Ordinal)
            && !string.Equals(anchorKind, "case", StringComparison.Ordinal))
        {
            return false;
        }

        var matchingContainers = GetCSharpConstantPatternContainersByMemberName(symbolName);
        if (matchingContainers.Count == 0)
            return false;

        foreach (var candidate in GetScopedCSharpQualifiedPatternQualifierCandidates(path, lineNumber, qualifier))
        {
            if (matchingContainers.Contains(candidate))
                return true;
        }

        return false;
    }

    private static string? NormalizeCSharpVerbatimQuery(string? query, string? lang)
        => SymbolSearchQueryNormalizer.NormalizeCSharpVerbatim(query, lang);

    private static bool IsBareVerbatimQueryToken(string? value)
    {
        var trimmed = value?.Trim();
        return trimmed is { Length: > 0 } && trimmed.All(ch => ch == '@');
    }

    private static string? CombineDbQualifiedName(string? parentQualifiedName, string? name)
    {
        if (string.IsNullOrWhiteSpace(name))
            return parentQualifiedName;
        if (string.IsNullOrWhiteSpace(parentQualifiedName))
            return name;
        return $"{parentQualifiedName}.{name}";
    }

    private QueryCountResult CountSearchReferencesTotalWithUsingStaticFilter(string? query, string? lang, string? referenceKind, IReadOnlyList<string>? pathPatterns, IReadOnlyList<string>? excludePathPatterns, bool excludeTests, bool exact, bool includeQualifiedCommonCalls)
    {
        if (!_hasReferencesTable)
            return new QueryCountResult(0, 0);

        int count = 0;
        bool includesSql = false;
        var paths = new HashSet<string>(StringComparer.Ordinal);
        var rawLimit = CSharpUsingStaticReferenceFilterChunkSize;
        var rawOffset = 0;
        while (true)
        {
            using var cmd = CreateSearchReferencesCommand(
                query,
                rawLimit,
                lang,
                referenceKind,
                pathPatterns,
                excludePathPatterns,
                excludeTests,
                exact,
                rawOffset,
                includeQualifiedCommonCalls: includeQualifiedCommonCalls);
            using var reader = cmd.ExecuteTrackedReader();

            var rawRows = 0;
            while (reader.TrackedRead())
            {
                rawRows++;
                var row = ReadSearchReferenceRawRow(reader);
                if (ShouldSuppressCSharpUsingStaticConstantPatternReference(row))
                    continue;

                count++;
                includesSql |= IsSqlLanguage(row.Lang);
                paths.Add(row.Path);
            }

            if (rawRows < rawLimit)
                break;

            rawOffset += rawRows;
            rawLimit = Math.Min(rawLimit * 2, CSharpUsingStaticReferenceFilterMaxRawLimit);
        }

        return new QueryCountResult(count, paths.Count, includesSql);
    }

    public int CountSearchReferences(string? query = null, int limit = 20, string? lang = null, string? referenceKind = null, IReadOnlyList<string>? pathPatterns = null, IReadOnlyList<string>? excludePathPatterns = null, bool excludeTests = false, bool exact = false, bool includeQualifiedCommonCalls = false)
    {
        query = NormalizeSymbolSearchQuery(query, lang, exact) ?? query ?? string.Empty;
        if (ShouldApplyCSharpUsingStaticConstantPatternReferenceFilter(lang, referenceKind, exact))
            return SearchReferences(query, limit, lang, referenceKind, pathPatterns, excludePathPatterns, excludeTests, exact, includeQualifiedCommonCalls: includeQualifiedCommonCalls).Count;

        if (!_hasReferencesTable) return 0;
        using var cmd = _conn.CreateCommand();
        var referenceLineJoin = ReferenceLineJoinSql("r");
        var contextSql = ReferenceContextSql("r");

        var innerSql = @"
            SELECT 1
            FROM symbol_references r
            JOIN files f ON r.file_id = f.id" + referenceLineJoin + $@"
            WHERE 1=1";
        innerSql += $" AND {BuildGraphSupportedLanguagePredicate(cmd, "f", "graphLang")}";

        var countSuffixAlias = ComputeCSharpAttributeSuffixAlias(query, lang, referenceKind);
        var countAliasScope = countSuffixAlias != null
            ? " AND f.lang = 'csharp' AND r.reference_kind = 'attribute'"
            : string.Empty;
        var allowCSharpQualifiedContextMatch = query != null
            && SqlNameResolver.HasQualifier(query)
            && !HasQualifiedSymbolDefinition(query, lang, pathPatterns, excludePathPatterns, excludeTests);
        var allowQualifiedLeafFallback = query != null && HasSingleQualifiedSymbolDefinition(query, lang, pathPatterns, excludePathPatterns, excludeTests);
        const string sqlLeafCountScope = " AND f.lang = 'sql'";
        if (query != null)
        {
            var allowSqlLeafFallback = AllowSqlLeafFallbackForQuery(query);
            var useSqlQualifiedContextMatch = SqlNameResolver.HasQualifier(query);
            if (useSqlQualifiedContextMatch && exact && _foldReady)
            {
                var qualifiedContextSql = BuildQualifiedContextMatchSql(contextSql, "r.column_number", folded: true, like: false);
                var csharpQualifiedContextSql = BuildCSharpQualifiedContextFallbackSql(qualifiedContextSql);
                var qualifiedLeafFallbackSql = BuildQualifiedLeafFallbackSql("r.symbol_name", "r.symbol_name_folded", folded: true);
                innerSql += countSuffixAlias != null
                    ? $" AND (((f.lang = 'sql') AND {qualifiedContextSql}) OR ((f.lang != 'sql') AND ({BuildPersistedFoldedNameMatchSql("r.symbol_name_folded", "@query")} OR (r.symbol_name_folded = @queryAttributeAlias{countAliasScope}))) OR {csharpQualifiedContextSql} OR {qualifiedLeafFallbackSql})"
                    : $" AND (((f.lang = 'sql') AND {qualifiedContextSql}) OR ((f.lang != 'sql') AND {BuildPersistedFoldedNameMatchSql("r.symbol_name_folded", "@query")}) OR {csharpQualifiedContextSql} OR {qualifiedLeafFallbackSql})";
            }
            else if (useSqlQualifiedContextMatch && exact)
            {
                var qualifiedContextSql = BuildQualifiedContextMatchSql(contextSql, "r.column_number", folded: false, like: false);
                var csharpQualifiedContextSql = BuildCSharpQualifiedContextFallbackSql(qualifiedContextSql);
                var qualifiedLeafFallbackSql = BuildQualifiedLeafFallbackSql("r.symbol_name", "r.symbol_name_folded", folded: false);
                innerSql += countSuffixAlias != null
                    ? $" AND (((f.lang = 'sql') AND {qualifiedContextSql}) OR ((f.lang != 'sql') AND (r.symbol_name = @query COLLATE NOCASE OR (r.symbol_name = @queryAttributeAlias COLLATE NOCASE{countAliasScope}))) OR {csharpQualifiedContextSql} OR {qualifiedLeafFallbackSql})"
                    : $" AND (((f.lang = 'sql') AND {qualifiedContextSql}) OR ((f.lang != 'sql') AND r.symbol_name = @query COLLATE NOCASE) OR {csharpQualifiedContextSql} OR {qualifiedLeafFallbackSql})";
            }
            else if (useSqlQualifiedContextMatch && _foldReady)
            {
                var qualifiedContextSql = BuildQualifiedContextMatchSql(contextSql, "r.column_number", folded: true, like: true);
                var csharpQualifiedContextSql = BuildCSharpQualifiedContextFallbackSql(qualifiedContextSql);
                var qualifiedLeafFallbackSql = BuildQualifiedLeafFallbackSql("r.symbol_name", "r.symbol_name_folded", folded: true);
                innerSql += countSuffixAlias != null
                    ? $" AND (((f.lang = 'sql') AND {qualifiedContextSql}) OR ((f.lang != 'sql') AND (r.symbol_name LIKE @query ESCAPE '\\' OR (r.symbol_name = @queryAttributeAlias COLLATE NOCASE{countAliasScope}))) OR {csharpQualifiedContextSql} OR {qualifiedLeafFallbackSql})"
                    : $" AND (((f.lang = 'sql') AND {qualifiedContextSql}) OR ((f.lang != 'sql') AND r.symbol_name LIKE @query ESCAPE '\\') OR {csharpQualifiedContextSql} OR {qualifiedLeafFallbackSql})";
            }
            else if (useSqlQualifiedContextMatch)
            {
                var qualifiedContextSql = BuildQualifiedContextMatchSql(contextSql, "r.column_number", folded: false, like: true);
                var csharpQualifiedContextSql = BuildCSharpQualifiedContextFallbackSql(qualifiedContextSql);
                var qualifiedLeafFallbackSql = BuildQualifiedLeafFallbackSql("r.symbol_name", "r.symbol_name_folded", folded: false);
                innerSql += countSuffixAlias != null
                    ? $" AND (((f.lang = 'sql') AND {qualifiedContextSql}) OR ((f.lang != 'sql') AND (r.symbol_name LIKE @query ESCAPE '\\' OR (r.symbol_name = @queryAttributeAlias COLLATE NOCASE{countAliasScope}))) OR {csharpQualifiedContextSql} OR {qualifiedLeafFallbackSql})"
                    : $" AND (((f.lang = 'sql') AND {qualifiedContextSql}) OR ((f.lang != 'sql') AND r.symbol_name LIKE @query ESCAPE '\\') OR {csharpQualifiedContextSql} OR {qualifiedLeafFallbackSql})";
            }
            else if (exact && _foldReady)
                innerSql += countSuffixAlias != null
                    ? $" AND ({BuildPersistedFoldedNameMatchSql("r.symbol_name_folded", "@query")} OR (r.symbol_name_folded = @queryAttributeAlias{countAliasScope}){(allowSqlLeafFallback ? $" OR (r.symbol_name_folded = @aliasQueryLeafFolded{sqlLeafCountScope})" : string.Empty)})"
                    : allowSqlLeafFallback
                        ? $" AND ({BuildPersistedFoldedNameMatchSql("r.symbol_name_folded", "@query")} OR (r.symbol_name_folded = @aliasQueryLeafFolded{sqlLeafCountScope}))"
                        : $" AND {BuildPersistedFoldedNameMatchSql("r.symbol_name_folded", "@query")}";
            else if (exact)
                innerSql += countSuffixAlias != null
                    ? $" AND (r.symbol_name = @query COLLATE NOCASE OR (r.symbol_name = @queryAttributeAlias COLLATE NOCASE{countAliasScope}){(allowSqlLeafFallback ? $" OR (r.symbol_name = sql_leaf_name(@aliasQuery) COLLATE NOCASE{sqlLeafCountScope})" : string.Empty)})"
                    : allowSqlLeafFallback
                        ? $" AND (r.symbol_name = @query COLLATE NOCASE OR (r.symbol_name = sql_leaf_name(@aliasQuery) COLLATE NOCASE{sqlLeafCountScope}))"
                        : " AND r.symbol_name = @query COLLATE NOCASE";
            else
                innerSql += countSuffixAlias != null
                    ? $" AND (r.symbol_name LIKE @query ESCAPE '\\' OR (r.symbol_name = @queryAttributeAlias COLLATE NOCASE{countAliasScope}) OR (r.symbol_name = sql_leaf_name(@aliasQuery) COLLATE NOCASE{sqlLeafCountScope}))"
                    : $" AND (r.symbol_name LIKE @query ESCAPE '\\' OR (r.symbol_name = sql_leaf_name(@aliasQuery) COLLATE NOCASE{sqlLeafCountScope}))";
        }
        if (referenceKind != null)
            innerSql += " AND r.reference_kind = @referenceKind";
        if (lang != null)
            innerSql += " AND f.lang = @lang";
        innerSql += BuildCSharpBareMemberReferenceFilter(
            query ?? string.Empty,
            lang,
            "f",
            "r",
            includeQualifiedCommonCalls);
        AppendPathFilters(ref innerSql, pathPatterns, excludePathPatterns, excludeTests);
        if (referenceKind == null)
            innerSql += $" GROUP BY r.file_id, r.symbol_name, r.line, r.column_number, {GetLogicalReferenceKindSql("r.reference_kind")}";
        innerSql += " LIMIT @limit";

        cmd.CommandText = $"SELECT COUNT(*) FROM ({innerSql})";
        if (query != null)
        {
            var value = !exact
                ? $"%{EscapeLikeQuery(query)}%"
                : _foldReady
                    ? FoldNameForLanguage(query, lang)
                    : query;
            if (exact && _foldReady)
                AddPersistedFoldedNameQueryParameters(cmd, "@query", query, lang);
            else
                SqliteCommandPolicy.Add(cmd, "@query", value);
            SqliteCommandPolicy.Add(cmd, "@aliasQuery", query);
            AddQualifiedGraphQueryParameters(cmd, query, allowQualifiedLeafFallback, allowCSharpQualifiedContextMatch);
            SqliteCommandPolicy.Add(cmd, "@aliasQueryLeafFolded", NameFold.Fold(SqlNameResolver.GetLeafName(query)) ?? SqlNameResolver.GetLeafName(query));
            if (countSuffixAlias != null)
            {
                var aliasParam = exact && _foldReady
                    ? NameFold.Fold(countSuffixAlias) ?? countSuffixAlias
                    : countSuffixAlias;
                SqliteCommandPolicy.Add(cmd, "@queryAttributeAlias", aliasParam);
            }
        }
        if (referenceKind != null)
            SqliteCommandPolicy.Add(cmd, "@referenceKind", referenceKind);
        if (lang != null)
            SqliteCommandPolicy.Add(cmd, "@lang", NormalizeQueryLanguage(lang));
        AddPathFilterParameters(cmd, pathPatterns, excludePathPatterns);
        SqliteCommandPolicy.Add(cmd, "@limit", limit);

        var raw = cmd.ExecuteScalar();
        return raw is long l ? (int)l : Convert.ToInt32(raw);
    }

    public QueryCountResult CountSearchReferencesTotal(string? query = null, string? lang = null, string? referenceKind = null, IReadOnlyList<string>? pathPatterns = null, IReadOnlyList<string>? excludePathPatterns = null, bool excludeTests = false, bool exact = false, bool includeQualifiedCommonCalls = false)
    {
        lang = NormalizeQueryLanguage(lang);
        query = NormalizeSymbolSearchQuery(query, lang, exact) ?? query ?? string.Empty;
        if (ShouldApplyCSharpUsingStaticConstantPatternReferenceFilter(lang, referenceKind, exact))
            return CountSearchReferencesTotalWithUsingStaticFilter(query, lang, referenceKind, pathPatterns, excludePathPatterns, excludeTests, exact, includeQualifiedCommonCalls);

        if (!_hasReferencesTable)
            return new QueryCountResult(0, 0);

        using var cmd = _conn.CreateCommand();
        var referenceLineJoin = ReferenceLineJoinSql("r");
        var contextSql = ReferenceContextSql("r");

        var innerSql = @"
            SELECT path, lang
            FROM (
                SELECT f.path AS path, f.lang AS lang, r.file_id, r.symbol_name, r.line, r.column_number, " + GetLogicalReferenceKindSql("r.reference_kind") + @" AS logical_reference_kind
                FROM symbol_references r
                JOIN files f ON r.file_id = f.id" + referenceLineJoin + $@"
                WHERE 1=1";
        innerSql += $" AND {BuildGraphSupportedLanguagePredicate(cmd, "f", "graphLang")}";

        var totalSuffixAlias = ComputeCSharpAttributeSuffixAlias(query, lang, referenceKind);
        var totalAliasScope = totalSuffixAlias != null
            ? " AND f.lang = 'csharp' AND r.reference_kind = 'attribute'"
            : string.Empty;
        var totalCssScssVariableAlias = ComputeCssScssVariableAlias(query);
        var totalCssScssVariableAliasScope = totalCssScssVariableAlias != null
            ? " AND f.lang = 'css'"
            : string.Empty;
        var allowCSharpQualifiedContextMatch = query != null
            && SqlNameResolver.HasQualifier(query)
            && !HasQualifiedSymbolDefinition(query, lang, pathPatterns, excludePathPatterns, excludeTests);
        var allowQualifiedLeafFallback = query != null && HasSingleQualifiedSymbolDefinition(query, lang, pathPatterns, excludePathPatterns, excludeTests);
        const string sqlLeafTotalScope = " AND f.lang = 'sql'";
        if (query != null)
        {
            var allowSqlLeafFallback = AllowSqlLeafFallbackForQuery(query);
            var useSqlQualifiedContextMatch = SqlNameResolver.HasQualifier(query);
            if (useSqlQualifiedContextMatch && exact && _foldReady)
            {
                var qualifiedContextSql = BuildQualifiedContextMatchSql(contextSql, "r.column_number", folded: true, like: false);
                var csharpQualifiedContextSql = BuildCSharpQualifiedContextFallbackSql(qualifiedContextSql);
                var qualifiedLeafFallbackSql = BuildQualifiedLeafFallbackSql("r.symbol_name", "r.symbol_name_folded", folded: true);
                innerSql += totalSuffixAlias != null
                    ? $" AND (((f.lang = 'sql') AND {qualifiedContextSql}) OR ((f.lang != 'sql') AND ({BuildPersistedFoldedNameMatchSql("r.symbol_name_folded", "@query")} OR (r.symbol_name_folded = @queryAttributeAlias{totalAliasScope}))) OR {csharpQualifiedContextSql} OR {qualifiedLeafFallbackSql})"
                    : $" AND (((f.lang = 'sql') AND {qualifiedContextSql}) OR ((f.lang != 'sql') AND {BuildPersistedFoldedNameMatchSql("r.symbol_name_folded", "@query")}) OR {csharpQualifiedContextSql} OR {qualifiedLeafFallbackSql})";
            }
            else if (useSqlQualifiedContextMatch && exact)
            {
                var qualifiedContextSql = BuildQualifiedContextMatchSql(contextSql, "r.column_number", folded: false, like: false);
                var csharpQualifiedContextSql = BuildCSharpQualifiedContextFallbackSql(qualifiedContextSql);
                var qualifiedLeafFallbackSql = BuildQualifiedLeafFallbackSql("r.symbol_name", "r.symbol_name_folded", folded: false);
                innerSql += totalSuffixAlias != null
                    ? $" AND (((f.lang = 'sql') AND {qualifiedContextSql}) OR ((f.lang != 'sql') AND (r.symbol_name = @query COLLATE NOCASE OR (r.symbol_name = @queryAttributeAlias COLLATE NOCASE{totalAliasScope}))) OR {csharpQualifiedContextSql} OR {qualifiedLeafFallbackSql})"
                    : $" AND (((f.lang = 'sql') AND {qualifiedContextSql}) OR ((f.lang != 'sql') AND r.symbol_name = @query COLLATE NOCASE) OR {csharpQualifiedContextSql} OR {qualifiedLeafFallbackSql})";
            }
            else if (useSqlQualifiedContextMatch && _foldReady)
            {
                var qualifiedContextSql = BuildQualifiedContextMatchSql(contextSql, "r.column_number", folded: true, like: true);
                var csharpQualifiedContextSql = BuildCSharpQualifiedContextFallbackSql(qualifiedContextSql);
                var qualifiedLeafFallbackSql = BuildQualifiedLeafFallbackSql("r.symbol_name", "r.symbol_name_folded", folded: true);
                innerSql += totalSuffixAlias != null
                    ? $" AND (((f.lang = 'sql') AND {qualifiedContextSql}) OR ((f.lang != 'sql') AND (r.symbol_name LIKE @query ESCAPE '\\' OR (r.symbol_name = @queryAttributeAlias COLLATE NOCASE{totalAliasScope}))) OR {csharpQualifiedContextSql} OR {qualifiedLeafFallbackSql})"
                    : $" AND (((f.lang = 'sql') AND {qualifiedContextSql}) OR ((f.lang != 'sql') AND r.symbol_name LIKE @query ESCAPE '\\') OR {csharpQualifiedContextSql} OR {qualifiedLeafFallbackSql})";
            }
            else if (useSqlQualifiedContextMatch)
            {
                var qualifiedContextSql = BuildQualifiedContextMatchSql(contextSql, "r.column_number", folded: false, like: true);
                var csharpQualifiedContextSql = BuildCSharpQualifiedContextFallbackSql(qualifiedContextSql);
                var qualifiedLeafFallbackSql = BuildQualifiedLeafFallbackSql("r.symbol_name", "r.symbol_name_folded", folded: false);
                innerSql += totalSuffixAlias != null
                    ? $" AND (((f.lang = 'sql') AND {qualifiedContextSql}) OR ((f.lang != 'sql') AND (r.symbol_name LIKE @query ESCAPE '\\' OR (r.symbol_name = @queryAttributeAlias COLLATE NOCASE{totalAliasScope}))) OR {csharpQualifiedContextSql} OR {qualifiedLeafFallbackSql})"
                    : $" AND (((f.lang = 'sql') AND {qualifiedContextSql}) OR ((f.lang != 'sql') AND r.symbol_name LIKE @query ESCAPE '\\') OR {csharpQualifiedContextSql} OR {qualifiedLeafFallbackSql})";
            }
            else if (exact && _foldReady)
                innerSql += totalSuffixAlias != null
                    ? $" AND ({BuildPersistedFoldedNameMatchSql("r.symbol_name_folded", "@query")} OR (r.symbol_name_folded = @queryAttributeAlias{totalAliasScope}){(allowSqlLeafFallback ? $" OR (r.symbol_name_folded = @aliasQueryLeafFolded{sqlLeafTotalScope})" : string.Empty)})"
                    : totalCssScssVariableAlias != null
                        ? $" AND ({BuildPersistedFoldedNameMatchSql("r.symbol_name_folded", "@query")} OR (r.symbol_name_folded = @queryCssScssVariableAlias{totalCssScssVariableAliasScope}){(allowSqlLeafFallback ? $" OR (r.symbol_name_folded = @aliasQueryLeafFolded{sqlLeafTotalScope})" : string.Empty)})"
                        : allowSqlLeafFallback
                        ? $" AND ({BuildPersistedFoldedNameMatchSql("r.symbol_name_folded", "@query")} OR (r.symbol_name_folded = @aliasQueryLeafFolded{sqlLeafTotalScope}))"
                        : $" AND {BuildPersistedFoldedNameMatchSql("r.symbol_name_folded", "@query")}";
            else if (exact)
                innerSql += totalSuffixAlias != null
                    ? $" AND (r.symbol_name = @query COLLATE NOCASE OR (r.symbol_name = @queryAttributeAlias COLLATE NOCASE{totalAliasScope}){(allowSqlLeafFallback ? $" OR (r.symbol_name = sql_leaf_name(@aliasQuery) COLLATE NOCASE{sqlLeafTotalScope})" : string.Empty)})"
                    : totalCssScssVariableAlias != null
                        ? $" AND (r.symbol_name = @query COLLATE NOCASE OR (r.symbol_name = @queryCssScssVariableAlias COLLATE NOCASE{totalCssScssVariableAliasScope}){(allowSqlLeafFallback ? $" OR (r.symbol_name = sql_leaf_name(@aliasQuery) COLLATE NOCASE{sqlLeafTotalScope})" : string.Empty)})"
                        : allowSqlLeafFallback
                        ? $" AND (r.symbol_name = @query COLLATE NOCASE OR (r.symbol_name = sql_leaf_name(@aliasQuery) COLLATE NOCASE{sqlLeafTotalScope}))"
                        : " AND r.symbol_name = @query COLLATE NOCASE";
            else
                innerSql += totalSuffixAlias != null
                    ? $" AND (r.symbol_name LIKE @query ESCAPE '\\' OR (r.symbol_name = @queryAttributeAlias COLLATE NOCASE{totalAliasScope}) OR (r.symbol_name = sql_leaf_name(@aliasQuery) COLLATE NOCASE{sqlLeafTotalScope}))"
                    : totalCssScssVariableAlias != null
                        ? $" AND (r.symbol_name LIKE @query ESCAPE '\\' OR (r.symbol_name = @queryCssScssVariableAlias COLLATE NOCASE{totalCssScssVariableAliasScope}) OR (r.symbol_name = sql_leaf_name(@aliasQuery) COLLATE NOCASE{sqlLeafTotalScope}))"
                        : $" AND (r.symbol_name LIKE @query ESCAPE '\\' OR (r.symbol_name = sql_leaf_name(@aliasQuery) COLLATE NOCASE{sqlLeafTotalScope}))";
        }
        if (referenceKind != null)
            innerSql += " AND r.reference_kind = @referenceKind";
        if (lang != null)
            innerSql += " AND f.lang = @lang";
        innerSql += BuildCSharpBareMemberReferenceFilter(
            query ?? string.Empty,
            lang,
            "f",
            "r",
            includeQualifiedCommonCalls);
        AppendPathFilters(ref innerSql, pathPatterns, excludePathPatterns, excludeTests);
        if (referenceKind == null)
            innerSql += $" GROUP BY f.path, f.lang, r.file_id, r.symbol_name, r.line, r.column_number, {GetLogicalReferenceKindSql("r.reference_kind")}";
        innerSql += ")";

        cmd.CommandText = $"SELECT COUNT(*), COUNT(DISTINCT path), MAX(CASE WHEN lang = 'sql' THEN 1 ELSE 0 END) FROM ({innerSql})";
        if (query != null)
        {
            var value = !exact
                ? $"%{EscapeLikeQuery(query)}%"
                : _foldReady
                      ? FoldNameForLanguage(query, lang)
                    : query;
            if (exact && _foldReady)
                AddPersistedFoldedNameQueryParameters(cmd, "@query", query, lang);
            else
                SqliteCommandPolicy.Add(cmd, "@query", value);
            SqliteCommandPolicy.Add(cmd, "@aliasQuery", query);
            AddQualifiedGraphQueryParameters(cmd, query, allowQualifiedLeafFallback, allowCSharpQualifiedContextMatch);
            SqliteCommandPolicy.Add(cmd, "@aliasQueryLeafFolded", NameFold.Fold(SqlNameResolver.GetLeafName(query)) ?? SqlNameResolver.GetLeafName(query));
            if (totalSuffixAlias != null)
            {
                var aliasParam = exact && _foldReady
                    ? NameFold.Fold(totalSuffixAlias) ?? totalSuffixAlias
                    : totalSuffixAlias;
                SqliteCommandPolicy.Add(cmd, "@queryAttributeAlias", aliasParam);
            }
            if (totalCssScssVariableAlias != null)
            {
                var aliasParam = exact && _foldReady
                    ? NameFold.Fold(totalCssScssVariableAlias) ?? totalCssScssVariableAlias
                    : totalCssScssVariableAlias;
                SqliteCommandPolicy.Add(cmd, "@queryCssScssVariableAlias", aliasParam);
            }
        }
        if (referenceKind != null)
            SqliteCommandPolicy.Add(cmd, "@referenceKind", referenceKind);
        if (lang != null)
            SqliteCommandPolicy.Add(cmd, "@lang", NormalizeQueryLanguage(lang));
        AddPathFilterParameters(cmd, pathPatterns, excludePathPatterns);

        return ExecuteCountSummary(cmd);
    }
}

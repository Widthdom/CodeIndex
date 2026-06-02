using CodeIndex.Indexer;
using Microsoft.Data.Sqlite;

namespace CodeIndex.Database;

public partial class DbReader
{
    private const int CSharpUsingStaticReferenceFilterChunkSize = 64;
    private const int CSharpUsingStaticReferenceFilterMaxRawLimit = 65536;
    private sealed record SearchReferenceRawRow(string Path, string? Lang, string SymbolName, string ReferenceKind, int Line, int Column, string Context, string? ContainerKind, string? ContainerName, bool IsSelfReference, bool IsMutualRecursion);

    /// <summary>
    /// Search indexed references such as call sites.
    /// 呼び出し箇所などのインデックス済み参照を検索する。
    /// </summary>
    public List<ReferenceResult> SearchReferences(string? query = null, int limit = 20, string? lang = null, string? referenceKind = null, IReadOnlyList<string>? pathPatterns = null, IReadOnlyList<string>? excludePathPatterns = null, bool excludeTests = false, bool exact = false, int maxLineWidth = LineWidthFormatter.DefaultMaxLineWidth, bool excludeSelfReferences = false, int offset = 0)
    {
        maxLineWidth = LineWidthFormatter.ClampMaxLineWidth(maxLineWidth);
        lang = NormalizeQueryLanguage(lang);
        query = NormalizeSymbolSearchQuery(query, lang, exact) ?? query ?? string.Empty;
        if (!_hasReferencesTable)
            return new List<ReferenceResult>();

        if (!ShouldApplyCSharpUsingStaticConstantPatternReferenceFilter(lang, referenceKind, exact))
            return SearchReferencesCore(query, limit, lang, referenceKind, pathPatterns, excludePathPatterns, excludeTests, exact, offset, maxLineWidth, excludeSelfReferences);

        var rawLimit = Math.Max(limit, CSharpUsingStaticReferenceFilterChunkSize);
        var rawOffset = 0;
        var acceptedBeforePage = Math.Max(0, offset);
        var accepted = 0;
        var filtered = new List<ReferenceResult>();
        while (filtered.Count < limit)
        {
            var rawResults = SearchReferencesCore(query, rawLimit, lang, referenceKind, pathPatterns, excludePathPatterns, excludeTests, exact, rawOffset, maxLineWidth, excludeSelfReferences);
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

    private List<ReferenceResult> SearchReferencesCore(string? query, int limit, string? lang, string? referenceKind, IReadOnlyList<string>? pathPatterns, IReadOnlyList<string>? excludePathPatterns, bool excludeTests, bool exact, int offset, int maxLineWidth, bool excludeSelfReferences)
    {
        using var cmd = CreateSearchReferencesCommand(query, limit, lang, referenceKind, pathPatterns, excludePathPatterns, excludeTests, exact, offset, excludeSelfReferences: excludeSelfReferences);
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
                RawContext = row.Context,
                Context = clampedContext.Text,
                ContextTruncated = clampedContext.Truncated,
                ContainerKind = row.ContainerKind,
                ContainerName = row.ContainerName,
                IsSelfReference = row.IsSelfReference,
                IsMutualRecursion = row.IsMutualRecursion,
            });
        }
        return results;
    }

    private SqliteCommand CreateSearchReferencesCommand(string? query, int limit, string? lang, string? referenceKind, IReadOnlyList<string>? pathPatterns, IReadOnlyList<string>? excludePathPatterns, bool excludeTests, bool exact, int offset = 0, bool includeOrdering = true, bool excludeSelfReferences = false)
    {
        var cmd = _conn.CreateCommand();
        var referenceLineJoin = ReferenceLineJoinSql("r");
        var contextSql = ReferenceContextSql("r");
        var selfReferenceSql = _referenceColumns.Contains("is_self_reference") ? "r.is_self_reference" : "0";
        var mutualRecursionSql = _referenceColumns.Contains("is_mutual_recursion") ? "r.is_mutual_recursion" : "0";
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
                       MAX({mutualRecursionSql}) AS is_mutual_recursion
                FROM symbol_references r
                JOIN files f ON r.file_id = f.id
                {referenceLineJoin}
                WHERE 1=1
                  AND {BuildGraphSupportedLanguagePredicate(cmd, "f", "graphLang")}"
            : @"
            SELECT f.path, f.lang, r.symbol_name, r.reference_kind, r.line, r.column_number,
                   " + contextSql + @", r.container_kind, r.container_name,
                   " + selfReferenceSql + @" AS is_self_reference,
                   " + mutualRecursionSql + @" AS is_mutual_recursion
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
            // Fallback: r.symbol_name = @q COLLATE NOCASE (indexed by idx_symbol_refs_name_nocase).
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
                    ? $" AND (((f.lang = 'sql') AND {qualifiedContextSql}) OR ((f.lang != 'sql') AND (r.symbol_name_folded = @query OR (r.symbol_name_folded = @queryAttributeAlias{referencesAliasScope}))) OR {csharpQualifiedContextSql} OR {qualifiedLeafFallbackSql})"
                    : $" AND (((f.lang = 'sql') AND {qualifiedContextSql}) OR ((f.lang != 'sql') AND r.symbol_name_folded = @query) OR {csharpQualifiedContextSql} OR {qualifiedLeafFallbackSql})";
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
                    ? $" AND (r.symbol_name_folded = @query OR (r.symbol_name_folded = @queryAttributeAlias{referencesAliasScope}){(allowSqlLeafFallback ? $" OR (r.symbol_name_folded = @aliasQueryLeafFolded{sqlLeafReferenceScope})" : string.Empty)})"
                    : referencesCssScssVariableAlias != null
                        ? $" AND (r.symbol_name_folded = @query OR (r.symbol_name_folded = @queryCssScssVariableAlias{referencesCssScssVariableAliasScope}){(allowSqlLeafFallback ? $" OR (r.symbol_name_folded = @aliasQueryLeafFolded{sqlLeafReferenceScope})" : string.Empty)})"
                        : allowSqlLeafFallback
                        ? $" AND (r.symbol_name_folded = @query OR (r.symbol_name_folded = @aliasQueryLeafFolded{sqlLeafReferenceScope}))"
                        : " AND r.symbol_name_folded = @query";
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
        if (lang != null)
            sql += " AND f.lang = @lang";
        AppendPathFilters(ref sql, pathPatterns, excludePathPatterns, excludeTests);
        if (referenceKind == null)
        {
            sql += @"
                GROUP BY f.path, f.lang, r.file_id, r.symbol_name, r.line, r.column_number, " + GetLogicalReferenceKindSql("r.reference_kind") + @"
            )
            SELECT path, lang, symbol_name, reference_kind, line, column_number,
                   context, container_kind, container_name, is_self_reference, is_mutual_recursion
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
                queryParam = NameFold.Fold(query) ?? query;
            else
                queryParam = query;
            cmd.Parameters.AddWithValue("@query", queryParam);
            cmd.Parameters.AddWithValue("@aliasQuery", query);
            AddQualifiedGraphQueryParameters(cmd, query, allowQualifiedLeafFallback, allowCSharpQualifiedContextMatch);
            cmd.Parameters.AddWithValue("@aliasQueryLeafFolded", NameFold.Fold(SqlNameResolver.GetLeafName(query)) ?? SqlNameResolver.GetLeafName(query));
            if (referencesSuffixAlias != null)
            {
                var aliasParam = exact && _foldReady
                    ? NameFold.Fold(referencesSuffixAlias) ?? referencesSuffixAlias
                    : referencesSuffixAlias;
                cmd.Parameters.AddWithValue("@queryAttributeAlias", aliasParam);
            }
            if (referencesCssScssVariableAlias != null)
            {
                var aliasParam = exact && _foldReady
                    ? NameFold.Fold(referencesCssScssVariableAlias) ?? referencesCssScssVariableAlias
                    : referencesCssScssVariableAlias;
                cmd.Parameters.AddWithValue("@queryCssScssVariableAlias", aliasParam);
            }
            cmd.Parameters.AddWithValue("@rankingQuery", query.Trim());
            cmd.Parameters.AddWithValue("@rankingQueryPrefix", $"{EscapeLikeQuery(query.Trim())}%");
        }
        else
        {
            cmd.Parameters.AddWithValue("@rankingQuery", "");
            cmd.Parameters.AddWithValue("@rankingQueryPrefix", "%");
        }
        cmd.Parameters.AddWithValue("@preferExactCase", exact && query != null ? 1 : 0);
        cmd.Parameters.AddWithValue("@rawQuery", exact && query != null ? query : string.Empty);
        if (referenceKind != null)
            cmd.Parameters.AddWithValue("@referenceKind", referenceKind);
        if (lang != null)
            cmd.Parameters.AddWithValue("@lang", NormalizeQueryLanguage(lang));
        AddPathFilterParameters(cmd, pathPatterns, excludePathPatterns);
        if (includeOrdering)
        {
            cmd.Parameters.AddWithValue("@limit", limit);
            cmd.Parameters.AddWithValue("@offset", offset);
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
            reader.GetInt32(10) != 0);
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


    // Query-side mirror of the C# declaration canonicalizer. Users commonly type source
    // spellings such as `@class` or `Outer.@class`; the DB stores the canonical names
    // without the verbatim `@`, so query entrypoints normalize to the persisted form first.
    // Rust macro names are also accepted with a trailing `!` because the extractor stores
    // them without the punctuation, so `my_macro!` and `my_macro` resolve to the same row.
    // The normalization is applied when `--lang` is omitted or explicitly `csharp` because
    // name-based lookup still needs to treat C# verbatim spellings as canonical symbol names.
    // Other languages, including SQL, must preserve leading `@` characters.
    // C# 宣言側 canonicalizer の query 側ミラー。`@class` / `Outer.@class` のような source
    // spelling を受けても、DB 側の `@` なし canonical 名に合わせてから検索する。
    // Rust macro 名も extractor 側では末尾 `!` を落として保存するため、`my_macro!` と `my_macro`
    // を同じ行へ解決できるようにする。
    // `--lang` 未指定または `csharp` 指定では name-based lookup が verbatim spelling を canonical 名へ寄せる。
    // それ以外の言語、特に SQL では先頭 `@` を保持する。
    private static string? NormalizeCSharpVerbatimQuery(string? query, string? lang)
    {
        if (!string.IsNullOrWhiteSpace(lang) && string.Equals(lang, "rust", StringComparison.OrdinalIgnoreCase))
        {
            var rustNormalized = NormalizeRustSymbolSearchQuery(query);
            return string.IsNullOrWhiteSpace(rustNormalized) ? null : rustNormalized;
        }

        if (!string.IsNullOrWhiteSpace(lang) && !string.Equals(lang, "csharp", StringComparison.OrdinalIgnoreCase))
            return query;
        var normalized = query == null ? null : NormalizeDbCSharpQualifiedName(query);
        return string.IsNullOrWhiteSpace(normalized) ? null : normalized;
    }

    private static string? NormalizeRustMacroQuery(string? query)
    {
        if (query == null)
            return null;

        var trimmed = query.TrimEnd();
        if (!trimmed.EndsWith("!", StringComparison.Ordinal))
            return trimmed;

        var normalized = trimmed[..^1].TrimEnd();
        return string.IsNullOrWhiteSpace(normalized) ? null : normalized;
    }

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

    private QueryCountResult CountSearchReferencesTotalWithUsingStaticFilter(string? query, string? lang, string? referenceKind, IReadOnlyList<string>? pathPatterns, IReadOnlyList<string>? excludePathPatterns, bool excludeTests, bool exact)
    {
        if (!_hasReferencesTable)
            return new QueryCountResult(0, 0);

        using var cmd = CreateSearchReferencesCommand(
            query,
            int.MaxValue,
            lang,
            referenceKind,
            pathPatterns,
            excludePathPatterns,
            excludeTests,
            exact,
            includeOrdering: false);
        using var reader = cmd.ExecuteTrackedReader();

        int count = 0;
        bool includesSql = false;
        var paths = new HashSet<string>(StringComparer.Ordinal);
        while (reader.TrackedRead())
        {
            var row = ReadSearchReferenceRawRow(reader);
            if (ShouldSuppressCSharpUsingStaticConstantPatternReference(row))
                continue;

            count++;
            includesSql |= IsSqlLanguage(row.Lang);
            paths.Add(row.Path);
        }

        return new QueryCountResult(count, paths.Count, includesSql);
    }

    public int CountSearchReferences(string? query = null, int limit = 20, string? lang = null, string? referenceKind = null, IReadOnlyList<string>? pathPatterns = null, IReadOnlyList<string>? excludePathPatterns = null, bool excludeTests = false, bool exact = false)
    {
        query = NormalizeSymbolSearchQuery(query, lang, exact) ?? query ?? string.Empty;
        if (ShouldApplyCSharpUsingStaticConstantPatternReferenceFilter(lang, referenceKind, exact))
            return SearchReferences(query, limit, lang, referenceKind, pathPatterns, excludePathPatterns, excludeTests, exact).Count;

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
                    ? $" AND (((f.lang = 'sql') AND {qualifiedContextSql}) OR ((f.lang != 'sql') AND (r.symbol_name_folded = @query OR (r.symbol_name_folded = @queryAttributeAlias{countAliasScope}))) OR {csharpQualifiedContextSql} OR {qualifiedLeafFallbackSql})"
                    : $" AND (((f.lang = 'sql') AND {qualifiedContextSql}) OR ((f.lang != 'sql') AND r.symbol_name_folded = @query) OR {csharpQualifiedContextSql} OR {qualifiedLeafFallbackSql})";
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
                    ? $" AND (r.symbol_name_folded = @query OR (r.symbol_name_folded = @queryAttributeAlias{countAliasScope}){(allowSqlLeafFallback ? $" OR (r.symbol_name_folded = @aliasQueryLeafFolded{sqlLeafCountScope})" : string.Empty)})"
                    : allowSqlLeafFallback
                        ? $" AND (r.symbol_name_folded = @query OR (r.symbol_name_folded = @aliasQueryLeafFolded{sqlLeafCountScope}))"
                        : " AND r.symbol_name_folded = @query";
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
                    ? NameFold.Fold(query) ?? query
                    : query;
            cmd.Parameters.AddWithValue("@query", value);
            cmd.Parameters.AddWithValue("@aliasQuery", query);
            AddQualifiedGraphQueryParameters(cmd, query, allowQualifiedLeafFallback, allowCSharpQualifiedContextMatch);
            cmd.Parameters.AddWithValue("@aliasQueryLeafFolded", NameFold.Fold(SqlNameResolver.GetLeafName(query)) ?? SqlNameResolver.GetLeafName(query));
            if (countSuffixAlias != null)
            {
                var aliasParam = exact && _foldReady
                    ? NameFold.Fold(countSuffixAlias) ?? countSuffixAlias
                    : countSuffixAlias;
                cmd.Parameters.AddWithValue("@queryAttributeAlias", aliasParam);
            }
        }
        if (referenceKind != null)
            cmd.Parameters.AddWithValue("@referenceKind", referenceKind);
        if (lang != null)
            cmd.Parameters.AddWithValue("@lang", NormalizeQueryLanguage(lang));
        AddPathFilterParameters(cmd, pathPatterns, excludePathPatterns);
        cmd.Parameters.AddWithValue("@limit", limit);

        var raw = cmd.ExecuteScalar();
        return raw is long l ? (int)l : Convert.ToInt32(raw);
    }

    public QueryCountResult CountSearchReferencesTotal(string? query = null, string? lang = null, string? referenceKind = null, IReadOnlyList<string>? pathPatterns = null, IReadOnlyList<string>? excludePathPatterns = null, bool excludeTests = false, bool exact = false)
    {
        lang = NormalizeQueryLanguage(lang);
        query = NormalizeSymbolSearchQuery(query, lang, exact) ?? query ?? string.Empty;
        if (ShouldApplyCSharpUsingStaticConstantPatternReferenceFilter(lang, referenceKind, exact))
            return CountSearchReferencesTotalWithUsingStaticFilter(query, lang, referenceKind, pathPatterns, excludePathPatterns, excludeTests, exact);

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
                    ? $" AND (((f.lang = 'sql') AND {qualifiedContextSql}) OR ((f.lang != 'sql') AND (r.symbol_name_folded = @query OR (r.symbol_name_folded = @queryAttributeAlias{totalAliasScope}))) OR {csharpQualifiedContextSql} OR {qualifiedLeafFallbackSql})"
                    : $" AND (((f.lang = 'sql') AND {qualifiedContextSql}) OR ((f.lang != 'sql') AND r.symbol_name_folded = @query) OR {csharpQualifiedContextSql} OR {qualifiedLeafFallbackSql})";
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
                    ? $" AND (r.symbol_name_folded = @query OR (r.symbol_name_folded = @queryAttributeAlias{totalAliasScope}){(allowSqlLeafFallback ? $" OR (r.symbol_name_folded = @aliasQueryLeafFolded{sqlLeafTotalScope})" : string.Empty)})"
                    : totalCssScssVariableAlias != null
                        ? $" AND (r.symbol_name_folded = @query OR (r.symbol_name_folded = @queryCssScssVariableAlias{totalCssScssVariableAliasScope}){(allowSqlLeafFallback ? $" OR (r.symbol_name_folded = @aliasQueryLeafFolded{sqlLeafTotalScope})" : string.Empty)})"
                        : allowSqlLeafFallback
                        ? $" AND (r.symbol_name_folded = @query OR (r.symbol_name_folded = @aliasQueryLeafFolded{sqlLeafTotalScope}))"
                        : " AND r.symbol_name_folded = @query";
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
                    ? NameFold.Fold(query) ?? query
                    : query;
            cmd.Parameters.AddWithValue("@query", value);
            cmd.Parameters.AddWithValue("@aliasQuery", query);
            AddQualifiedGraphQueryParameters(cmd, query, allowQualifiedLeafFallback, allowCSharpQualifiedContextMatch);
            cmd.Parameters.AddWithValue("@aliasQueryLeafFolded", NameFold.Fold(SqlNameResolver.GetLeafName(query)) ?? SqlNameResolver.GetLeafName(query));
            if (totalSuffixAlias != null)
            {
                var aliasParam = exact && _foldReady
                    ? NameFold.Fold(totalSuffixAlias) ?? totalSuffixAlias
                    : totalSuffixAlias;
                cmd.Parameters.AddWithValue("@queryAttributeAlias", aliasParam);
            }
            if (totalCssScssVariableAlias != null)
            {
                var aliasParam = exact && _foldReady
                    ? NameFold.Fold(totalCssScssVariableAlias) ?? totalCssScssVariableAlias
                    : totalCssScssVariableAlias;
                cmd.Parameters.AddWithValue("@queryCssScssVariableAlias", aliasParam);
            }
        }
        if (referenceKind != null)
            cmd.Parameters.AddWithValue("@referenceKind", referenceKind);
        if (lang != null)
            cmd.Parameters.AddWithValue("@lang", NormalizeQueryLanguage(lang));
        AddPathFilterParameters(cmd, pathPatterns, excludePathPatterns);

        return ExecuteCountSummary(cmd);
    }
}

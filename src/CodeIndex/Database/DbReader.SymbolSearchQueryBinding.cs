using CodeIndex.Indexer;
using Microsoft.Data.Sqlite;

namespace CodeIndex.Database;

public partial class DbReader
{
    private static class SymbolSearchQueryPredicateBuilder
    {
        public static string BuildFull(DbReader reader, SymbolSearchQueryPlan plan)
        {
            if (plan.Queries is not { Count: > 0 } queries)
                return string.Empty;

            var clauses = plan.Exact
                ? queries.Select((query, index) => BuildFullExact(reader, plan, query, index))
                : queries.Select((query, index) => BuildFullLike(reader, query, index));
            return $" AND ({string.Join(" OR ", clauses)})";
        }

        public static string BuildBounded(DbReader reader, SymbolSearchQueryPlan plan)
        {
            if (plan.Queries is not { Count: 1 } queries)
                return string.Empty;

            var query = queries[0];
            var allowLeafFallback = !SqlNameResolver.HasQualifier(query);
            var qualifiedSymbolClause = SqlNameResolver.HasQualifier(query)
                ? reader.BuildQualifiedSymbolMatchSql("query0", reader._foldReady)
                : null;
            var csharpExplicitInterfaceClause = allowLeafFallback
                ? reader.BuildCSharpExplicitInterfaceShortAliasMatchSql("query0")
                : reader.BuildCSharpExplicitInterfaceIdentityMatchSql("query0");
            var rustQualifiedExact = ShouldPreserveRustQualifiedExactQuery(query, plan.Lang, plan.Exact);
            var rustQualifiedParts = rustQualifiedExact
                ? NormalizeRustQualifiedExactQueryParts(query)
                : default;
            if (!plan.Exact)
            {
                return $" AND (s.name LIKE @query0 ESCAPE '\\' OR (f.lang = 'sql' AND sql_normalize_name(s.name) LIKE @query0NormalizedLike ESCAPE '\\'){(qualifiedSymbolClause != null ? $" OR {qualifiedSymbolClause} OR {csharpExplicitInterfaceClause}" : string.Empty)})";
            }

            if (rustQualifiedParts.QualifiedPath != null)
            {
                return reader._foldReady
                    ? " AND ((s.container_qualified_name = @query0RustContainer COLLATE NOCASE OR s.container_name = @query0RustContainer COLLATE NOCASE) AND s.name_folded = @query0RustLeafFolded)"
                    : " AND ((s.container_qualified_name = @query0RustContainer COLLATE NOCASE OR s.container_name = @query0RustContainer COLLATE NOCASE) AND s.name = @query0RustLeaf COLLATE NOCASE)";
            }

            return " AND " + BuildExactCore(
                reader,
                query,
                plan.Lang,
                "query0",
                allowLeafFallback,
                qualifiedSymbolClause,
                csharpExplicitInterfaceClause,
                markdownAnchorClause: null,
                swiftBacktickClause: string.Empty);
        }

        public static void AppendFilters(
            DbReader reader,
            ref string sql,
            SymbolSearchQueryPlan plan,
            bool includeLineRange)
        {
            if (plan.Kind != null)
                sql += " AND s.kind = @kind";
            if (plan.Lang != null)
                sql += SymbolLanguageFileIdFilter;
            if (plan.Since != null && reader._fileColumns.Contains("modified"))
                sql += " AND f.modified >= @since";
            if (includeLineRange && plan.StartLine != null)
                sql += " AND s.line >= @startLine";
            if (includeLineRange && plan.EndLine != null)
                sql += " AND s.line <= @endLine";
            AppendPathFilters(
                ref sql,
                plan.PathPatterns,
                plan.ExcludePathPatterns,
                plan.ExcludeTests);
            reader.AppendVisibilityFilters(
                ref sql,
                plan.VisibilityFilters,
                plan.ExcludeVisibilityFilters);
        }

        private static string BuildFullExact(
            DbReader reader,
            SymbolSearchQueryPlan plan,
            string query,
            int index)
        {
            var parameterStem = $"query{index}";
            var rustQualifiedExact =
                ShouldPreserveRustQualifiedExactQuery(query, plan.Lang, plan.Exact);
            var rustQualifiedParts = rustQualifiedExact
                ? NormalizeRustQualifiedExactQueryParts(query)
                : default;
            if (rustQualifiedParts.QualifiedPath != null)
            {
                return reader._foldReady
                    ? $"((s.container_qualified_name = @{parameterStem}RustContainer COLLATE NOCASE OR s.container_name = @{parameterStem}RustContainer COLLATE NOCASE) AND s.name_folded = @{parameterStem}RustLeafFolded)"
                    : $"((s.container_qualified_name = @{parameterStem}RustContainer COLLATE NOCASE OR s.container_name = @{parameterStem}RustContainer COLLATE NOCASE) AND s.name = @{parameterStem}RustLeaf COLLATE NOCASE)";
            }

            var allowLeafFallback = !SqlNameResolver.HasQualifier(query);
            var markdownAnchorClause = reader._symbolColumns.Contains("name_folded")
                ? $"(f.lang = 'markdown' AND ((s.kind = 'heading' AND s.name_folded = @{parameterStem}MarkdownHeading) OR (s.kind = 'anchor' AND s.name_folded = @{parameterStem}MarkdownExplicitAnchor COLLATE BINARY)))"
                : "0";
            var qualifiedSymbolClause = SqlNameResolver.HasQualifier(query)
                ? reader.BuildQualifiedSymbolMatchSql(parameterStem, reader._foldReady)
                : null;
            var csharpExplicitInterfaceClause = allowLeafFallback
                ? reader.BuildCSharpExplicitInterfaceShortAliasMatchSql(parameterStem)
                : reader.BuildCSharpExplicitInterfaceIdentityMatchSql(parameterStem);
            var swiftBacktickAlias = ComputeSwiftBacktickAlias(query, plan.Lang);
            var swiftBacktickClause = swiftBacktickAlias != null
                ? reader._foldReady
                    ? $" OR s.name_folded = @{parameterStem}SwiftBacktickAlias"
                    : $" OR s.name = @{parameterStem}SwiftBacktickAlias COLLATE NOCASE"
                : string.Empty;
            return BuildExactCore(
                reader,
                query,
                plan.Lang,
                parameterStem,
                allowLeafFallback,
                qualifiedSymbolClause,
                csharpExplicitInterfaceClause,
                markdownAnchorClause,
                swiftBacktickClause);
        }

        private static string BuildExactCore(
            DbReader reader,
            string query,
            string? lang,
            string parameterStem,
            bool allowLeafFallback,
            string? qualifiedSymbolClause,
            string csharpExplicitInterfaceClause,
            string? markdownAnchorClause,
            string swiftBacktickClause)
        {
            var parameterSql = $"@{parameterStem}";
            var aliasClauses = $"{swiftBacktickClause} OR {csharpExplicitInterfaceClause}";
            if (markdownAnchorClause != null)
                aliasClauses += $" OR {markdownAnchorClause}";
            if (reader._foldReady)
            {
                return allowLeafFallback
                    ? $"({reader.BuildExactPrimarySymbolNameMatchSql(parameterSql, true, query, lang)}{aliasClauses} OR (f.lang = 'sql' AND ((sql_segment_count(s.name) = @{parameterStem}SegmentCount AND sql_normalize_name_folded(s.name) = @{parameterStem}NormalizedFolded) OR sql_leaf_name_folded(s.name) = @{parameterStem}LeafFolded)))"
                    : $"({reader.BuildExactPrimarySymbolNameMatchSql(parameterSql, true, query, lang)}{aliasClauses} OR (f.lang = 'sql' AND sql_segment_count(s.name) = @{parameterStem}SegmentCount AND sql_normalize_name_folded(s.name) = @{parameterStem}NormalizedFolded){(qualifiedSymbolClause != null ? $" OR {qualifiedSymbolClause}" : string.Empty)})";
            }

            return allowLeafFallback
                ? $"({reader.BuildExactPrimarySymbolNameMatchSql(parameterSql, false, query, lang)}{aliasClauses} OR (f.lang = 'sql' AND ((sql_segment_count(s.name) = @{parameterStem}SegmentCount AND sql_normalize_name(s.name) = @{parameterStem}Normalized COLLATE NOCASE) OR sql_leaf_name(s.name) = @{parameterStem}Leaf COLLATE NOCASE)))"
                : $"({reader.BuildExactPrimarySymbolNameMatchSql(parameterSql, false, query, lang)}{aliasClauses} OR (f.lang = 'sql' AND sql_segment_count(s.name) = @{parameterStem}SegmentCount AND sql_normalize_name(s.name) = @{parameterStem}Normalized COLLATE NOCASE){(qualifiedSymbolClause != null ? $" OR {qualifiedSymbolClause}" : string.Empty)})";
        }

        private static string BuildFullLike(DbReader reader, string query, int index)
        {
            var parameterStem = $"query{index}";
            var qualifiedSymbolClause = SqlNameResolver.HasQualifier(query)
                ? reader.BuildQualifiedSymbolMatchSql(parameterStem, reader._foldReady)
                : null;
            var csharpExplicitInterfaceClause = SqlNameResolver.HasQualifier(query)
                ? reader.BuildCSharpExplicitInterfaceIdentityMatchSql(parameterStem)
                : null;
            var markdownAnchorLikeClause = reader._symbolColumns.Contains("name_folded")
                ? $" OR (f.lang = 'markdown' AND ((s.kind = 'heading' AND s.name_folded LIKE @{parameterStem}MarkdownHeadingLike ESCAPE '\\') OR (s.kind = 'anchor' AND instr(s.name_folded, @{parameterStem}MarkdownExplicitAnchor) > 0)))"
                : string.Empty;
            return $"(s.name LIKE @{parameterStem} ESCAPE '\\'{markdownAnchorLikeClause} OR (f.lang = 'sql' AND sql_normalize_name(s.name) LIKE @{parameterStem}NormalizedLike ESCAPE '\\'){(qualifiedSymbolClause != null ? $" OR {qualifiedSymbolClause} OR {csharpExplicitInterfaceClause}" : string.Empty)})";
        }
    }

    private static class SymbolSearchQueryBinder
    {
        public static void BindFullQueries(
            DbReader reader,
            SqliteCommand command,
            SymbolSearchQueryPlan plan)
        {
            if (plan.Queries == null)
                return;

            for (var index = 0; index < plan.Queries.Count; index++)
                BindFullQuery(reader, command, plan, plan.Queries[index], index);
        }

        public static void BindBoundedQuery(
            DbReader reader,
            SqliteCommand command,
            SymbolSearchQueryPlan plan)
        {
            if (plan.Queries is not { Count: 1 } queries)
                return;

            BindBaseQuery(reader, command, plan, queries[0], 0);
            BindQualifiedAndRust(command, plan, queries[0], 0);
        }

        public static void BindFilters(
            DbReader reader,
            SqliteCommand command,
            SymbolSearchQueryPlan plan,
            bool includeLineRange)
        {
            if (plan.Kind != null)
                SqliteCommandPolicy.Add(command, "@kind", plan.Kind);
            if (plan.Lang != null)
                SqliteCommandPolicy.Add(command, "@lang", plan.Lang);
            if (plan.Since != null && reader._fileColumns.Contains("modified"))
                SqliteCommandPolicy.Add(command, "@since", plan.Since.Value);
            if (includeLineRange && plan.StartLine != null)
                SqliteCommandPolicy.Add(command, "@startLine", plan.StartLine.Value);
            if (includeLineRange && plan.EndLine != null)
                SqliteCommandPolicy.Add(command, "@endLine", plan.EndLine.Value);
            AddPathFilterParameters(
                command,
                plan.PathPatterns,
                plan.ExcludePathPatterns);
            AddVisibilityFilterParameters(
                command,
                plan.VisibilityFilters,
                plan.ExcludeVisibilityFilters);
        }

        public static void BindListOrdering(SqliteCommand command, SymbolSearchQueryPlan plan)
        {
            var hasSingleQuery = plan.Queries is { Count: 1 };
            var query = hasSingleQuery ? plan.Queries![0] : string.Empty;
            var preferSqlLeafMatch = hasSingleQuery && !SqlNameResolver.HasQualifier(query);
            SqliteCommandPolicy.Add(command, "@preferLiteralExactMatch", hasSingleQuery ? 1 : 0);
            SqliteCommandPolicy.Add(command, "@preferLiteralNormalizedSqlMatch", hasSingleQuery ? 1 : 0);
            SqliteCommandPolicy.Add(command, "@preferCaseInsensitiveExactMatch", hasSingleQuery ? 1 : 0);
            SqliteCommandPolicy.Add(command, "@preferCaseInsensitiveNormalizedSqlMatch", hasSingleQuery ? 1 : 0);
            SqliteCommandPolicy.Add(command, "@preferCaseInsensitiveSqlLeafMatch", preferSqlLeafMatch ? 1 : 0);
            SqliteCommandPolicy.Add(command, "@rawQuery", query);
            var normalized = hasSingleQuery ? SqlNameResolver.NormalizeQualifiedName(query) : string.Empty;
            SqliteCommandPolicy.Add(command, "@rawQueryNormalized", normalized);
            SqliteCommandPolicy.Add(command, "@rawQueryNormalizedFolded", hasSingleQuery ? NameFold.Fold(normalized) ?? normalized : string.Empty);
            var leaf = hasSingleQuery ? SqlNameResolver.GetLeafName(query) : string.Empty;
            SqliteCommandPolicy.Add(command, "@rawQueryLeaf", leaf);
            SqliteCommandPolicy.Add(command, "@rawQueryLeafFolded", hasSingleQuery ? NameFold.Fold(leaf) ?? leaf : string.Empty);
            SqliteCommandPolicy.Add(command, "@rawQuerySegmentCount", hasSingleQuery ? SqlNameResolver.GetSegmentCount(query) : 0);
        }

        private static void BindFullQuery(
            DbReader reader,
            SqliteCommand command,
            SymbolSearchQueryPlan plan,
            string query,
            int index)
        {
            BindBaseQuery(reader, command, plan, query, index);
            var parameterStem = $"query{index}";
            if (reader._symbolColumns.Contains("name_folded"))
            {
                var heading = MarkdownAnchorIdentity.NormalizeHeadingFragment(query);
                SqliteCommandPolicy.Add(command, $"@{parameterStem}MarkdownHeading", heading);
                SqliteCommandPolicy.Add(command, $"@{parameterStem}MarkdownHeadingLike", $"%{EscapeLikeQuery(heading)}%");
                SqliteCommandPolicy.Add(
                    command,
                    $"@{parameterStem}MarkdownExplicitAnchor",
                    MarkdownAnchorIdentity.NormalizeExplicitAnchorDefinition(query));
            }

            BindQualifiedAndRust(command, plan, query, index);
            var swiftBacktickAlias = ComputeSwiftBacktickAlias(query, plan.Lang);
            if (swiftBacktickAlias != null)
            {
                SqliteCommandPolicy.Add(
                    command,
                    $"@{parameterStem}SwiftBacktickAlias",
                    reader._foldReady
                        ? NameFold.Fold(swiftBacktickAlias) ?? swiftBacktickAlias
                        : swiftBacktickAlias);
            }
        }

        private static void BindBaseQuery(
            DbReader reader,
            SqliteCommand command,
            SymbolSearchQueryPlan plan,
            string query,
            int index)
        {
            var parameterStem = $"query{index}";
            var parameterName = $"@{parameterStem}";
            var parameterValue = !plan.Exact
                ? $"%{EscapeLikeQuery(query)}%"
                : reader._foldReady
                    ? FoldNameForLanguage(query, plan.Lang)
                    : query;
            if (plan.Exact && reader._foldReady)
                AddPersistedFoldedNameQueryParameters(command, parameterName, query, plan.Lang);
            else
                SqliteCommandPolicy.Add(command, parameterName, parameterValue);

            var normalized = SqlNameResolver.NormalizeQualifiedName(query);
            SqliteCommandPolicy.Add(command, $"@{parameterStem}Normalized", normalized);
            SqliteCommandPolicy.Add(command, $"@{parameterStem}NormalizedFolded", NameFold.Fold(normalized) ?? normalized);
            var leaf = GetQualifiedQueryLeaf(query, plan.Lang);
            SqliteCommandPolicy.Add(command, $"@{parameterStem}Leaf", leaf);
            SqliteCommandPolicy.Add(command, $"@{parameterStem}LeafFolded", NameFold.Fold(leaf) ?? leaf);
            SqliteCommandPolicy.Add(command, $"@{parameterStem}SegmentCount", SqlNameResolver.GetSegmentCount(query));
            SqliteCommandPolicy.Add(command, $"@{parameterStem}NormalizedLike", $"%{EscapeLikeQuery(normalized)}%");
            AddCSharpExplicitInterfaceIdentityQueryParameter(command, parameterStem, query);
        }

        private static void BindQualifiedAndRust(
            SqliteCommand command,
            SymbolSearchQueryPlan plan,
            string query,
            int index)
        {
            var parameterStem = $"query{index}";
            if (SqlNameResolver.HasQualifier(query))
                AddQualifiedSymbolQueryParameters(command, parameterStem, query);

            var rustQualifiedExact =
                ShouldPreserveRustQualifiedExactQuery(query, plan.Lang, plan.Exact);
            var rustQualifiedParts = rustQualifiedExact
                ? NormalizeRustQualifiedExactQueryParts(query)
                : default;
            if (rustQualifiedParts.QualifiedPath != null)
            {
                var rustLeaf = rustQualifiedParts.LeafName ?? string.Empty;
                SqliteCommandPolicy.Add(command, $"@{parameterStem}RustContainer", rustQualifiedParts.ContainerPath ?? string.Empty);
                SqliteCommandPolicy.Add(command, $"@{parameterStem}RustLeaf", rustLeaf);
                SqliteCommandPolicy.Add(command, $"@{parameterStem}RustLeafFolded", NameFold.Fold(rustLeaf) ?? rustLeaf);
            }
        }
    }
}

using Microsoft.Data.Sqlite;

namespace CodeIndex.Database;

public partial class DbReader
{
    private sealed record ImpactDefinitionColumnSql(
        string StartLine,
        string StartColumn,
        string EndLine,
        string BodyStartLine,
        string BodyEndLine,
        string Signature,
        string ContainerKind,
        string ContainerName,
        string ContainerQualifiedName,
        string FamilyKey,
        string Visibility,
        string ReturnType,
        string IdentifierStartColumn);

    private sealed record ImpactDefinitionQueryPlan(
        ImpactDefinitionRequest Request,
        string Sql,
        string LogicalPartialKeySql);

    private static class ImpactDefinitionQueryBuilder
    {
        public static ImpactDefinitionQueryPlan Build(
            DbReader reader,
            SqliteCommand cmd,
            ImpactDefinitionRequest request)
        {
            var supportedLangFilter = reader.BuildGraphSupportedLanguagePredicate(
                cmd,
                "f",
                "impactDefLang");
            var columns = BuildColumnSql(reader);
            var canonical = LogicalPartialQuerySql.Build(
                reader,
                columns.Signature,
                columns.ContainerName,
                columns.ContainerQualifiedName,
                columns.FamilyKey,
                columns.ReturnType,
                columns.BodyStartLine,
                columns.BodyEndLine);
            var matchingSql = BuildMatchingSql(
                reader,
                request,
                columns,
                canonical,
                supportedLangFilter);
            var pathCaseSensitive = ReferenceEquals(
                reader.GetIndexedPathComparer(),
                StringComparer.Ordinal);
            var sql = ImpactDefinitionQuerySql.Build(matchingSql, pathCaseSensitive);
            return new ImpactDefinitionQueryPlan(
                request,
                sql,
                canonical.LogicalPartialKey);
        }

        private static ImpactDefinitionColumnSql BuildColumnSql(DbReader reader)
        {
            return new ImpactDefinitionColumnSql(
                reader.GetSymbolColumnSql("start_line", "s.line"),
                reader.GetSymbolColumnSql("start_column"),
                reader.GetSymbolColumnSql("end_line", "s.line"),
                reader.GetSymbolColumnSql("body_start_line"),
                reader.GetSymbolColumnSql("body_end_line"),
                reader.GetSymbolColumnSql("signature"),
                reader.GetSymbolColumnSql("container_kind"),
                reader.GetSymbolColumnSql("container_name"),
                reader.GetSymbolColumnSql("container_qualified_name"),
                reader.GetSymbolColumnSql("family_key"),
                reader.GetSymbolColumnSql("visibility"),
                reader.GetSymbolColumnSql("return_type"),
                reader.GetSymbolColumnSql("identifier_start_column"));
        }

        private static string BuildMatchingSql(
            DbReader reader,
            ImpactDefinitionRequest request,
            ImpactDefinitionColumnSql columns,
            LogicalPartialCanonicalSql canonical,
            string supportedLangFilter)
        {
            var nameCondition = BuildNameCondition(reader, request);
            var matchOrderSql = BuildMatchOrderSql();
            var sql = $@"
            SELECT f.path, f.lang, s.kind, s.name, s.line,
                   {columns.StartLine} AS start_line,
                   {columns.StartColumn} AS start_column,
                   {columns.EndLine} AS end_line,
                   {columns.BodyStartLine} AS body_start_line,
                   {columns.BodyEndLine} AS body_end_line,
                   {columns.Signature} AS signature,
                   {columns.ContainerKind} AS container_kind,
                   {columns.ContainerName} AS container_name,
                   {columns.Visibility} AS visibility,
                   {columns.ReturnType} AS return_type,
                   {columns.ContainerQualifiedName} AS container_qualified_name,
                   {canonical.LogicalPartialKey} AS logical_partial_key,
                   s.id AS symbol_id,
                   {matchOrderSql} AS match_order,
                   {DbReader.PathBucketOrder} AS path_bucket,
                   {reader.VisibilityOrder} AS visibility_rank,
                   CASE WHEN s.kind IN ('class', 'struct', 'interface') THEN 1 ELSE 0 END AS is_precise,
                   CASE WHEN s.kind IN ('namespace', 'import') THEN 1 ELSE 0 END AS is_non_callable,
                   {canonical.PrimaryRank} AS canonical_primary_rank,
                   {canonical.Generated} AS canonical_generated_rank,
                   {canonical.SemanticScore} AS canonical_semantic_score,
                   {canonical.DeclarationIdentity} AS canonical_declaration_identity,
                   COALESCE({columns.StartColumn}, 2147483647) AS stable_start_column,
                   {columns.IdentifierStartColumn} AS identifier_start_column
            FROM symbols s
            JOIN files f ON s.file_id = f.id
            WHERE {nameCondition}
              AND {supportedLangFilter}";

            if (request.Lang != null)
                sql += " AND f.lang = @lang";
            DbReader.AppendPathFilters(
                ref sql,
                request.PathPatterns,
                request.ExcludePathPatterns,
                request.ExcludeTests);
            return sql;
        }

        private static string BuildNameCondition(
            DbReader reader,
            ImpactDefinitionRequest request)
        {
            var nameCondition = reader._foldReady
                ? BuildFoldedNameCondition(request.AllowLeafFallback)
                : BuildLegacyNameCondition(request.AllowLeafFallback);
            var explicitInterfaceClause = request.AllowLeafFallback
                ? reader.BuildCSharpExplicitInterfaceShortAliasMatchSql("resolvedName")
                : reader.BuildCSharpExplicitInterfaceIdentityMatchSql("resolvedName");
            nameCondition = $"({nameCondition} OR {explicitInterfaceClause})";
            if (request.AllowLeafFallback)
                return nameCondition;

            var csharpLeafCondition = reader._foldReady
                ? "s.name_folded = @resolvedNameLeafFolded"
                : "s.name = @resolvedNameLeaf COLLATE NOCASE";
            var containerName = reader.GetSymbolColumnSql("container_name", "''");
            var containerQualifiedName = reader.GetSymbolColumnSql(
                "container_qualified_name",
                containerName);
            return $"({nameCondition} OR (f.lang = 'csharp' AND {csharpLeafCondition} AND ({containerName} = @resolvedNameContainer COLLATE NOCASE OR {containerQualifiedName} = @resolvedNameContainer COLLATE NOCASE OR {containerQualifiedName} COLLATE NOCASE LIKE @resolvedNameContainerSuffix ESCAPE '\\')))";
        }

        private static string BuildFoldedNameCondition(bool allowLeafFallback)
        {
            return allowLeafFallback
                ? $"({DbReader.BuildPersistedFoldedNameMatchSql("s.name_folded", "@resolvedNameFolded")} OR (f.lang = 'sql' AND ((sql_segment_count(s.name) = @resolvedNameSegmentCount AND sql_normalize_name_folded(s.name) = @resolvedNameNormalizedFolded) OR sql_leaf_name_folded(s.name) = @resolvedNameLeafFolded)))"
                : $"({DbReader.BuildPersistedFoldedNameMatchSql("s.name_folded", "@resolvedNameFolded")} OR (f.lang = 'sql' AND sql_segment_count(s.name) = @resolvedNameSegmentCount AND sql_normalize_name_folded(s.name) = @resolvedNameNormalizedFolded))";
        }

        private static string BuildLegacyNameCondition(bool allowLeafFallback)
        {
            return allowLeafFallback
                ? "(s.name = @resolvedName COLLATE NOCASE OR (f.lang = 'sql' AND ((sql_segment_count(s.name) = @resolvedNameSegmentCount AND sql_normalize_name(s.name) = @resolvedNameNormalized COLLATE NOCASE) OR sql_leaf_name(s.name) = @resolvedNameLeaf COLLATE NOCASE)))"
                : "(s.name = @resolvedName COLLATE NOCASE OR (f.lang = 'sql' AND sql_segment_count(s.name) = @resolvedNameSegmentCount AND sql_normalize_name(s.name) = @resolvedNameNormalized COLLATE NOCASE))";
        }

        private static string BuildMatchOrderSql()
        {
            return @"CASE
                     WHEN s.name = @resolvedName THEN 0
                     WHEN f.lang = 'sql' AND sql_segment_count(s.name) = @resolvedNameSegmentCount AND sql_normalize_name(s.name) = @resolvedNameNormalized THEN 1
                     WHEN f.lang = 'sql' AND sql_segment_count(s.name) = @resolvedNameSegmentCount AND sql_normalize_name_folded(s.name) = @resolvedNameNormalizedFolded THEN 2
                     WHEN @allowLeafFallback = 1 AND f.lang = 'sql' AND sql_leaf_name(s.name) = @resolvedNameLeaf THEN 3
                     WHEN @allowLeafFallback = 1 AND f.lang = 'sql' AND sql_leaf_name_folded(s.name) = @resolvedNameLeafFolded THEN 4
                     ELSE 5
                   END";
        }

        public static void Bind(
            DbReader reader,
            SqliteCommand cmd,
            ImpactDefinitionQueryPlan plan)
        {
            var request = plan.Request;
            SqliteCommandPolicy.Add(cmd, "@resolvedName", request.ResolvedName);
            SqliteCommandPolicy.Add(cmd, "@resolvedNameNormalized", request.NormalizedName);
            SqliteCommandPolicy.Add(
                cmd,
                "@resolvedNameNormalizedFolded",
                DbReader.FoldNameForLanguage(request.NormalizedName, request.Lang));
            SqliteCommandPolicy.Add(cmd, "@resolvedNameLeaf", request.LeafName);
            SqliteCommandPolicy.Add(
                cmd,
                "@resolvedNameLeafFolded",
                DbReader.FoldNameForLanguage(request.LeafName, request.Lang));
            SqliteCommandPolicy.Add(cmd, "@resolvedNameSegmentCount", request.SegmentCount);
            SqliteCommandPolicy.Add(cmd, "@allowLeafFallback", request.AllowLeafFallback ? 1 : 0);
            DbReader.AddCSharpExplicitInterfaceIdentityQueryParameter(
                cmd,
                "resolvedName",
                request.ResolvedName);
            BindQualifiedContainer(cmd, request);
            if (reader._foldReady)
            {
                DbReader.AddPersistedFoldedNameQueryParameters(
                    cmd,
                    "@resolvedNameFolded",
                    request.ResolvedName,
                    request.Lang);
            }
            if (request.Lang != null)
                SqliteCommandPolicy.Add(cmd, "@lang", request.Lang);
            SqliteCommandPolicy.Add(cmd, "@definitionLimit", request.RepresentativeLimit);
            SqliteCommandPolicy.Add(cmd, "@definitionOffset", request.RepresentativeOffset);
            DbReader.AddPathFilterParameters(
                cmd,
                request.PathPatterns,
                request.ExcludePathPatterns);
        }

        private static void BindQualifiedContainer(
            SqliteCommand cmd,
            ImpactDefinitionRequest request)
        {
            if (request.AllowLeafFallback)
                return;

            var container = GetQualifiedQueryContainer(request.ResolvedName);
            SqliteCommandPolicy.Add(cmd, "@resolvedNameContainer", container);
            SqliteCommandPolicy.Add(
                cmd,
                "@resolvedNameContainerSuffix",
                $"%.{EscapeLikeQuery(container)}");
        }
    }
}

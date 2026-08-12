namespace CodeIndex.Database;

public partial class DbReader
{
    private sealed record LogicalPartialCanonicalSql(
        string LogicalPartialKey,
        string Generated,
        string PrimaryRank,
        string SemanticScore,
        string DeclarationIdentity);

    private static class LogicalPartialQuerySql
    {
        public static LogicalPartialCanonicalSql Build(
            DbReader reader,
            string signatureSql,
            string containerNameSql,
            string containerQualifiedNameSql,
            string familyKeySql,
            string returnTypeSql,
            string bodyStartLineSql,
            string bodyEndLineSql)
        {
            var logicalPartialKey = BuildKey(
                reader,
                signatureSql,
                containerNameSql,
                containerQualifiedNameSql,
                familyKeySql,
                returnTypeSql);
            var generated = reader._fileColumns.Contains("generated")
                ? "CASE WHEN COALESCE(f.generated, 0) <> 0 OR codeindex_generated_file_name(f.path) THEN 1 ELSE 0 END"
                : "CASE WHEN codeindex_generated_file_name(f.path) THEN 1 ELSE 0 END";
            var primaryRank = LogicalPartialSymbolGrouper.BuildSqlPrimaryRankExpression(
                "s.kind",
                bodyStartLineSql,
                bodyEndLineSql);
            var semanticScore = LogicalPartialSymbolGrouper.BuildSqlSemanticScoreExpression(
                signatureSql,
                "s.kind",
                reader.GetSymbolColumnSql("declaration_semantic_score"));
            var fallbackIdentity = BuildDeclarationIdentity(signatureSql);
            var declarationIdentity =
                $"CASE WHEN s.kind IN ('function', 'test.method') THEN COALESCE(csharp_partial_callable_identity({signatureSql}, s.name, {returnTypeSql}), {fallbackIdentity}) ELSE {fallbackIdentity} END";
            return new LogicalPartialCanonicalSql(
                logicalPartialKey,
                generated,
                primaryRank,
                semanticScore,
                declarationIdentity);
        }

        public static string BuildKey(
            DbReader reader,
            string signatureSql,
            string containerNameSql,
            string containerQualifiedNameSql,
            string familyKeySql,
            string returnTypeSql)
        {
            return LogicalPartialSymbolGrouper.BuildSqlKeyExpression(
                "f.lang",
                "s.kind",
                "s.name",
                "s.id",
                "f.path",
                signatureSql,
                containerNameSql,
                containerQualifiedNameSql,
                familyKeySql,
                returnTypeSql,
                reader.GetSymbolColumnSql("is_partial_declaration"),
                reader._hotspotFamilyReadyLanguages.Contains("csharp"));
        }

        private static string BuildDeclarationIdentity(string signatureSql)
            => $"csharp_partial_declaration_identity({signatureSql})";
    }
}

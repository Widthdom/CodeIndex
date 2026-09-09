namespace CodeIndex.Database;

public partial class DbReader
{
    private static string BuildSqlDependencyNameMatch(
        string targetName, string targetSegments, string referenceName,
        string referenceSegments, string rawReferenceName, string allowLeafFallback)
        => "(" + BuildSqlDependencyExactNameMatch(targetName, targetSegments, referenceName, referenceSegments)
           + " OR " + BuildSqlDependencyLeafNameMatch(targetSegments, referenceName, referenceSegments,
               rawReferenceName, allowLeafFallback, $"sql_leaf_name({targetName})", $"sql_segment_count({rawReferenceName})") + ")";

    private static string BuildSqlDependencyExactNameMatch(
        string targetName, string targetSegments, string referenceName, string referenceSegments)
        => $"({targetSegments} = {referenceSegments} AND {targetName} = {referenceName} COLLATE NOCASE)";

    private static string BuildSqlDependencyLeafNameMatch(
        string targetSegments, string referenceName, string referenceSegments,
        string rawReferenceName, string allowLeafFallback, string targetLeafName, string rawReferenceSegments)
        => $@"({rawReferenceSegments} = 1
                AND {allowLeafFallback} = 1
                AND {targetSegments} > 1
                AND {targetLeafName} = {rawReferenceName} COLLATE NOCASE
                AND NOT EXISTS (
                    SELECT 1 FROM target_files tf_exact
                    WHERE tf_exact.target_lang = 'sql'
                      AND tf_exact.symbol_segment_count = 1
                      AND tf_exact.symbol_name = {referenceName} COLLATE NOCASE)
                AND NOT EXISTS (
                    SELECT 1 FROM target_files tf_resolved
                    WHERE tf_resolved.target_lang = 'sql'
                      AND tf_resolved.symbol_segment_count = {referenceSegments}
                      AND tf_resolved.symbol_name = {referenceName} COLLATE NOCASE))";
}

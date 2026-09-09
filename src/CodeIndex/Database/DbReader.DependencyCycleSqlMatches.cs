namespace CodeIndex.Database;

public partial class DbReader
{
    private string BuildDependencyCycleSqlMatches()
    {
        var context = ReferenceContextSql("r");
        var exactMatch = BuildSqlDependencyExactNameMatch(
            "target.symbol_name", "target.symbol_segment_count", "reference.symbol_name", "reference.symbol_segment_count");
        var fallbackMatch = BuildSqlDependencyLeafNameMatch(
            "target.symbol_segment_count", "reference.symbol_name", "reference.symbol_segment_count",
            "reference.raw_symbol_name", "reference.allow_leaf_fallback", "target.symbol_leaf_name", "reference.raw_segment_count");
        // Materialize normalized keys once. Separate equality joins let SQLite index
        // the temporary keys instead of re-normalizing every target for every reference.
        return $@"
            cycle_sql_references AS MATERIALIZED (
                SELECT r.id AS reference_id,
                       r.symbol_name AS raw_symbol_name,
                       sql_segment_count(r.symbol_name) AS raw_segment_count,
                       sql_resolve_reference_name_at(r.symbol_name, {context}, r.container_name, r.column_number) AS symbol_name,
                       sql_resolve_reference_segment_count_at(r.symbol_name, {context}, r.container_name, r.column_number) AS symbol_segment_count,
                       sql_allow_leaf_fallback_at(r.symbol_name, {context}, r.container_name, r.column_number) AS allow_leaf_fallback
                FROM symbol_references r
                JOIN files src ON src.id = r.file_id
                {ReferenceLineJoinSql("r")}
                WHERE src.lang = 'sql'
            ),
            cycle_sql_matches AS MATERIALIZED (
                SELECT reference.reference_id, target.symbol_id
                FROM cycle_sql_references reference
                JOIN target_files target ON {exactMatch}
                UNION ALL
                SELECT reference.reference_id, target.symbol_id
                FROM cycle_sql_references reference
                JOIN target_files target ON {fallbackMatch}
            ),";
    }
}

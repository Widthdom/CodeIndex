namespace CodeIndex.Database;

public partial class DbReader
{
    // Derived-table joins that supply the per-file match and visibility buckets referenced by
    // search ranking. GROUP BY keeps the symbol predicates out of the outer per-hit scan while
    // preserving the best visibility rank among matching symbols in each file.
    // 検索順位で参照するファイル単位の一致・可視性 bucket を派生テーブルで供給する。GROUP BY により
    // symbol 述語を外側の hit ごとの scan から切り離しつつ、ファイル内の一致シンボルの最良 visibility を保持する。
    internal string SearchSymbolMatchJoinsSql
    {
        get
        {
            var exactVisibilityRankSql = GetVisibilityRankSql(GetSymbolColumnSql("visibility", symbolAlias: "s_exact"));
            var prefixVisibilityRankSql = GetVisibilityRankSql(GetSymbolColumnSql("visibility", symbolAlias: "s_prefix"));
            return $@"
        LEFT JOIN (
            SELECT
                file_id,
                MIN({exactVisibilityRankSql}) AS visibility_order,
                {GetVisibilityLabelSql($"MIN({exactVisibilityRankSql})")} AS visibility
            FROM symbols s_exact
            WHERE name = @rankingQuery COLLATE NOCASE
            GROUP BY file_id
        ) AS exact_symbol_match ON exact_symbol_match.file_id = f.id
        LEFT JOIN (
            SELECT
                file_id,
                MIN({prefixVisibilityRankSql}) AS visibility_order,
                {GetVisibilityLabelSql($"MIN({prefixVisibilityRankSql})")} AS visibility
            FROM symbols s_prefix
            WHERE name LIKE @rankingQueryPrefix ESCAPE '\' COLLATE NOCASE
            GROUP BY file_id
        ) AS prefix_symbol_match ON prefix_symbol_match.file_id = f.id
        LEFT JOIN (
            SELECT c_exact.id AS chunk_id
            FROM chunks c_exact
            JOIN symbols s_exact_chunk ON s_exact_chunk.file_id = c_exact.file_id
                AND COALESCE(s_exact_chunk.start_line, s_exact_chunk.line) <= c_exact.end_line
                AND COALESCE(s_exact_chunk.end_line, s_exact_chunk.line) >= c_exact.start_line
            WHERE s_exact_chunk.name = @rankingQuery COLLATE NOCASE
            GROUP BY c_exact.id
        ) AS exact_symbol_chunk_match ON exact_symbol_chunk_match.chunk_id = c.id
        LEFT JOIN (
            SELECT c_prefix.id AS chunk_id
            FROM chunks c_prefix
            JOIN symbols s_prefix_chunk ON s_prefix_chunk.file_id = c_prefix.file_id
                AND COALESCE(s_prefix_chunk.start_line, s_prefix_chunk.line) <= c_prefix.end_line
                AND COALESCE(s_prefix_chunk.end_line, s_prefix_chunk.line) >= c_prefix.start_line
            WHERE s_prefix_chunk.name LIKE @rankingQueryPrefix ESCAPE '\' COLLATE NOCASE
            GROUP BY c_prefix.id
        ) AS prefix_symbol_chunk_match ON prefix_symbol_chunk_match.chunk_id = c.id
        LEFT JOIN (
            SELECT
                c_rank.id AS chunk_id,
                MIN(CASE
                    WHEN instr(lower(COALESCE(s_rank.name, '')), lower(@rankingQuery)) > 0 OR
                         instr(lower(COALESCE(s_rank.signature, '')), lower(@rankingQuery)) > 0
                    THEN 0
                    ELSE 1
                END) AS structured_field_order,
                MIN(CASE lower(COALESCE(s_rank.kind, ''))
                    WHEN 'class' THEN 0
                    WHEN 'interface' THEN 0
                    WHEN 'struct' THEN 0
                    WHEN 'enum' THEN 0
                    WHEN 'function' THEN 1
                    WHEN 'method' THEN 1
                    WHEN 'test.method' THEN 1
                    WHEN 'property' THEN 2
                    WHEN 'field' THEN 2
                    WHEN 'import' THEN 4
                    WHEN 'reference' THEN 4
                    ELSE 3
                END) AS kind_order,
                MIN(sql_segment_count(COALESCE(NULLIF(s_rank.container_qualified_name, ''), NULLIF(s_rank.container_name, ''), s_rank.name))) AS depth_order
            FROM chunks c_rank
            JOIN symbols s_rank ON s_rank.file_id = c_rank.file_id
                AND COALESCE(s_rank.start_line, s_rank.line) <= c_rank.end_line
                AND COALESCE(s_rank.end_line, s_rank.line) >= c_rank.start_line
            GROUP BY c_rank.id
        ) AS chunk_symbol_rank ON chunk_symbol_rank.chunk_id = c.id";
        }
    }
}

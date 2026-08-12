namespace CodeIndex.Database;

public partial class DbReader
{
    private sealed class DependencySourceCandidateSqlBuilder
    {
        private readonly DbReader _reader;
        private readonly DependencyQueryRequest _request;
        private readonly DependencyQueryExpressions _expressions;

        internal DependencySourceCandidateSqlBuilder(
            DbReader reader,
            DependencyQueryRequest request,
            DependencyQueryExpressions expressions)
        {
            _reader = reader;
            _request = request;
            _expressions = expressions;
        }

        internal DependencySqlFragment Build()
            => _request.Lang == "csharp" ? BuildBoundedCSharpSource() : BuildUnboundedSource();

        private DependencySqlFragment BuildBoundedCSharpSource()
        {
            var sql = new DependencySqlFragmentBuilder();
            sql.Append(@",
            csharp_dependency_targets AS (
                SELECT dst.path AS target_path,
                       " + _expressions.TargetLogicalSymbolName + @" AS symbol_name,
                       MAX(CASE WHEN " + _reader.BuildMetadataTargetKindExpr("dst") + @" THEN 1 ELSE 0 END) AS has_metadata_target_kind
                FROM symbols s
                JOIN files dst ON s.file_id = dst.id
                WHERE dst.lang = 'csharp'");
            sql.Append(_reader.BuildDependencyGeneratedFilter("dst"));
            AppendReverseTargetScope(sql);
            sql.Append(BuildDependencySymbolFilter(
                _expressions.TargetLogicalSymbolName,
                _request.DependencySymbols,
                _request.DependencySymbolFamilies,
                _request.SuppressDependencyNoise,
                "boundedTargetDependency"));
            sql.Append(@"
                GROUP BY dst.path, " + _expressions.TargetLogicalSymbolName + @"
            ),
            bounded_source_name_counts AS (
                SELECT snc.*
                FROM source_name_counts snc
                WHERE EXISTS (
                    SELECT 1
                    FROM csharp_dependency_targets tf
                    WHERE tf.symbol_name = snc.symbol_name
                      AND tf.target_path != snc.source_path
                      AND (snc.is_metadata = 0 OR tf.has_metadata_target_kind = 1)
                )
                ORDER BY snc.ref_count DESC, snc.source_path, snc.symbol_name, snc.context, snc.column_number, snc.raw_reference_kind
                LIMIT @sourceCandidateLimit
            ),");
            return sql.Build();
        }

        private void AppendReverseTargetScope(DependencySqlFragmentBuilder sql)
        {
            if (_request.Reverse && _request.PathPatterns is { Count: > 0 })
            {
                var predicates = new List<string>(_request.PathPatterns.Count);
                for (var i = 0; i < _request.PathPatterns.Count; i++)
                    predicates.Add(BuildPathFilterPredicate("dst", "pathPattern", i, _request.PathPatterns[i]));
                sql.Append(" AND (" + string.Join(" OR ", predicates) + ")");
            }
            if (_request.Reverse && _request.ExcludePathPatterns is { Count: > 0 })
            {
                for (var i = 0; i < _request.ExcludePathPatterns.Count; i++)
                    sql.Append($" AND NOT {BuildPathFilterPredicate("dst", "excludePath", i, _request.ExcludePathPatterns[i])}");
            }
            if (_request.ExcludeTests)
                sql.Append($" AND NOT {DependencyTestPathCondition("dst.path")}");
        }

        private static DependencySqlFragment BuildUnboundedSource()
            => new(@",
            bounded_source_name_counts AS (
                SELECT * FROM source_name_counts
            ),", Array.Empty<DependencyQueryParameter>());
    }
}

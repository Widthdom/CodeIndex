namespace CodeIndex.Database;

public partial class DbReader
{
    private sealed class DependencyCycleCandidateSqlBuilder
    {
        private readonly DbReader _reader;
        private readonly DependencyQueryRequest _request;
        private readonly DependencyCycleQueryExpressions _expressions;
        private readonly DependencySqlFragmentBuilder _sql = new();

        internal DependencyCycleCandidateSqlBuilder(
            DbReader reader,
            DependencyQueryRequest request,
            DependencyCycleQueryExpressions expressions)
        {
            _reader = reader;
            _request = request;
            _expressions = expressions;
        }

        internal DependencySqlFragment Build()
        {
            AppendCandidateEdgeQuery();
            AppendCandidateScope();
            AppendCandidateFilters();
            return _sql.Build();
        }

        private void AppendCandidateEdgeQuery()
        {
            _sql.Append(@"
            WITH candidate_edges AS (
                SELECT src.path AS source_path,
                       dst.path AS target_path,
                       MAX(CASE WHEN " + _expressions.SuppressedEvidenceScope + @" THEN 0 ELSE 1 END) AS retained_evidence
            FROM symbol_references r
            JOIN files src ON r.file_id = src.id
            JOIN symbols s ON s.name = r.symbol_name
            JOIN files dst ON s.file_id = dst.id
            WHERE src.path != dst.path
              AND src.lang = dst.lang");
        }

        private void AppendCandidateScope()
        {
            AppendGraphLanguageScope("src", "depsCycleSourceLang");
            AppendGraphLanguageScope("dst", "depsCycleTargetLang");
            _sql.Append(_reader.BuildDependencyGeneratedFilter("src"));
            _sql.Append(_reader.BuildDependencyGeneratedFilter("dst"));
            if (_request.Lang != null)
                _sql.Append(" AND src.lang = @lang AND dst.lang = @lang");
            if (_request.PathPatterns is { Count: > 0 })
            {
                var predicates = new List<string>(_request.PathPatterns.Count);
                for (var i = 0; i < _request.PathPatterns.Count; i++)
                {
                    predicates.Add(BuildPathFilterPredicate(
                        _expressions.ConstrainedAlias,
                        "pathPattern",
                        i,
                        _request.PathPatterns[i]));
                }
                _sql.Append(" AND (" + string.Join(" OR ", predicates) + ")");
            }
            if (_request.ExcludePathPatterns is { Count: > 0 })
            {
                for (var i = 0; i < _request.ExcludePathPatterns.Count; i++)
                {
                    _sql.Append($" AND NOT {BuildPathFilterPredicate(_expressions.ConstrainedAlias, "excludePath", i, _request.ExcludePathPatterns[i])}");
                }
            }
            if (_request.ExcludeTests)
            {
                _sql.Append($" AND NOT {DependencyTestPathCondition("src.path")}");
                _sql.Append($" AND NOT {DependencyTestPathCondition("dst.path")}");
            }
        }

        private void AppendGraphLanguageScope(string fileAlias, string parameterPrefix)
        {
            var predicate = _reader.BuildDependencyGraphLanguagePredicate(fileAlias, parameterPrefix);
            _sql.Append(" AND " + predicate.Sql);
            _sql.AddParameters(predicate.Parameters);
        }

        private void AppendCandidateFilters()
        {
            _sql.Append(BuildDependencySymbolFilter(
                "r.symbol_name",
                _request.DependencySymbols,
                _request.DependencySymbolFamilies,
                suppressDependencyNoise: false,
                parameterPrefix: "cycleDependencyNames"));
            _sql.Append(BuildDependencySymbolFilter(
                "r.symbol_name",
                dependencySymbols: null,
                dependencySymbolFamilies: null,
                suppressDependencyNoise: _request.SuppressDependencyNoise,
                parameterPrefix: "cycleDependencyNoise",
                filterScopeSql: "NOT " + _expressions.NoiseEvidenceScope));
            _sql.Append(@"
                GROUP BY src.path, dst.path
                ORDER BY " + _expressions.CandidateOrder + @"
                LIMIT @limit
            ),");
        }
    }
}

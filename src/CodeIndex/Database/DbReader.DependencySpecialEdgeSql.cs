namespace CodeIndex.Database;

public partial class DbReader
{
    private sealed class DependencySpecialEdgeSqlBuilder
    {
        private readonly DependencyQueryRequest _request;
        private readonly DependencySqlFragmentBuilder _sql = new();

        internal DependencySpecialEdgeSqlBuilder(DependencyQueryRequest request)
        {
            _request = request;
        }

        internal DependencySqlFragment Build()
        {
            AppendMarkdownEdges();
            AppendDockerEdges();
            AppendProjectPathEdges();
            return _sql.Build();
        }

        private void AppendMarkdownEdges()
        {
            _sql.Append(@"
                UNION ALL
                -- Resolve explicit Markdown links once per target file. Joining these
                -- path references through target_files would multiply one link by every
                -- heading or symbol declared in the destination document.
                -- 明示的な Markdown link は target file ごとに一度だけ解決する。
                -- target_files 経由で結合すると、1 link が宛先 document 内の全見出し
                -- / symbol の件数だけ増幅されるため、file-level path として扱う。
                SELECT snc.source_path,
                       ptf.target_path,
                       snc.raw_symbol_name,
                       snc.ref_count,
                       snc.source_lang,
                       'markdown_explicit_link',
                       snc.evidence_resolution_state,
                       snc.raw_reference_kind,
                       'file'
                FROM bounded_source_name_counts snc
                JOIN path_target_files ptf
                  ON ptf.target_path = markdown_resolve_path(snc.source_path, snc.symbol_name)
                WHERE snc.source_lang = 'markdown'
                  AND snc.logical_reference_kind = 'import'
                  AND snc.source_path != ptf.target_path");
            _sql.Append(BuildDependencySymbolFilter(
                "snc.raw_symbol_name",
                _request.DependencySymbols,
                _request.DependencySymbolFamilies,
                suppressDependencyNoise: false,
                parameterPrefix: "markdownPathDependency"));
        }

        private void AppendDockerEdges()
        {
            _sql.Append(@"
                UNION ALL
                -- Dockerfile stages are symbols within one file, so their dependency edge is
                -- intentionally a self-file edge. Keep this exception stage-specific rather
                -- than weakening the cross-file contract for other symbols or languages.
                -- Dockerfile stage は同一ファイル内の symbol であるため、この依存 edge は
                -- 意図的に self-file edge とする。他の symbol / 言語の cross-file 契約を
                -- 緩めないよう stage に限定する。
                SELECT snc.source_path,
                       snc.source_path,
                       snc.symbol_name,
                       snc.ref_count,
                       snc.source_lang,
                       'docker_stage_reference',
                       snc.evidence_resolution_state,
                       snc.raw_reference_kind,
                       'stage'
                FROM source_name_counts snc
                JOIN files self_dst ON self_dst.id = snc.source_file_id
                WHERE snc.source_lang = 'dockerfile'
                  AND snc.raw_reference_kind = 'call'
                  AND EXISTS (
                        SELECT 1
                        FROM symbols stage
                        WHERE stage.file_id = snc.source_file_id
                          AND stage.kind = 'stage'
                          AND stage.name = snc.symbol_name COLLATE NOCASE
                  )");
            AppendReverseDockerScope();
            _sql.Append(BuildDependencySymbolFilter(
                "snc.symbol_name",
                _request.DependencySymbols,
                _request.DependencySymbolFamilies,
                _request.SuppressDependencyNoise,
                "dockerDependency"));
        }

        private void AppendReverseDockerScope()
        {
            if (_request.Reverse && _request.PathPatterns is { Count: > 0 })
            {
                var predicates = new List<string>(_request.PathPatterns.Count);
                for (var i = 0; i < _request.PathPatterns.Count; i++)
                    predicates.Add(BuildPathFilterPredicate("self_dst", "pathPattern", i, _request.PathPatterns[i]));
                _sql.Append(" AND (" + string.Join(" OR ", predicates) + ")");
            }
            if (_request.Reverse && _request.ExcludePathPatterns is { Count: > 0 })
            {
                for (var i = 0; i < _request.ExcludePathPatterns.Count; i++)
                    _sql.Append($" AND NOT {BuildPathFilterPredicate("self_dst", "excludePath", i, _request.ExcludePathPatterns[i])}");
            }
            if (_request.Reverse && _request.ExcludeTests)
                _sql.Append($" AND NOT {DependencyTestPathCondition("self_dst.path")}");
        }

        private void AppendProjectPathEdges()
        {
            _sql.Append(@"
                UNION ALL
                SELECT snc.source_path,
                       ptf.target_path,
                       snc.symbol_name,
                       snc.ref_count,
                       snc.source_lang,
                       'explicit_path_reference',
                       snc.evidence_resolution_state,
                       snc.raw_reference_kind,
                       'file'
                FROM bounded_source_name_counts snc
                JOIN path_target_files ptf
                  ON ptf.target_path = markdown_resolve_path(snc.source_path, snc.symbol_name)
                 WHERE snc.source_lang IN ('msbuild', 'solution')
                  AND snc.logical_reference_kind IN ('import', 'project_reference')
                  AND snc.source_path != ptf.target_path");
            _sql.Append(BuildDependencySymbolFilter(
                "snc.symbol_name",
                _request.DependencySymbols,
                _request.DependencySymbolFamilies,
                _request.SuppressDependencyNoise,
                "pathDependency"));
        }
    }
}

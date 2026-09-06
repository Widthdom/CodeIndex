namespace CodeIndex.Database;

public partial class DbReader
{
    private sealed class DependencySourceSqlBuilder
    {
        private const string SourceAlias = "src";

        private readonly DbReader _reader;
        private readonly DependencyQueryRequest _request;
        private readonly DependencyQueryExpressions _expressions;
        private readonly DependencySqlFragmentBuilder _sql = new();

        internal DependencySourceSqlBuilder(
            DbReader reader,
            DependencyQueryRequest request,
            DependencyQueryExpressions expressions)
        {
            _reader = reader;
            _request = request;
            _expressions = expressions;
        }

        internal DependencySqlFragment Build()
        {
            AppendPrimaryReferences();
            AppendSourceScope();
            _sql.Append(_reader.BuildDependencyEvidenceFilter(_request.EvidenceFilter, "dependencyEvidence"));
            AppendLogicalReferences();
            AppendSourceNameCounts();
            return _sql.Build();
        }

        private void AppendPrimaryReferences()
        {
            _sql.Append(@"
            WITH logical_references_primary AS (
                SELECT src.id AS source_file_id,
                       src.path AS source_path,
                       src.lang AS source_lang,
                       " + _expressions.ReferenceIdSql + @" AS reference_id,
                       " + _expressions.ScopedResolutionStateSql + @" AS resolution_state,
                       " + _expressions.IdentityScopedSql + @" AS identity_scoped,
                       " + _reader.DependencyResolutionStateSql() + @" AS evidence_resolution_state,
                       r.symbol_name,
                       " + _expressions.ContextSql + @" AS context,
                       r.container_name,
                       r.line,
                       r.column_number,
                       r.reference_kind AS raw_reference_kind,
                       " + GetLogicalReferenceKindSql("r.reference_kind") + @" AS logical_reference_kind
                FROM symbol_references r
                JOIN files src ON r.file_id = src.id" + _expressions.ReferenceLineJoin + @"
                WHERE 1 = 1");
            var languagePredicate = _reader.BuildDependencyGraphLanguagePredicate(SourceAlias, "depsLang");
            _sql.Append(" AND " + languagePredicate.Sql);
            _sql.AddParameters(languagePredicate.Parameters);
            _sql.Append(_reader.BuildDependencyGeneratedFilter(SourceAlias));
        }

        private void AppendSourceScope()
        {
            if (_request.Lang != null)
                _sql.Append(" AND src.lang = @lang");
            if (!_request.Reverse && _request.PathPatterns is { Count: > 0 })
            {
                var predicates = new List<string>(_request.PathPatterns.Count);
                for (var i = 0; i < _request.PathPatterns.Count; i++)
                    predicates.Add(BuildPathFilterPredicate(SourceAlias, "pathPattern", i, _request.PathPatterns[i]));
                _sql.Append(" AND (" + string.Join(" OR ", predicates) + ")");
            }
            if (!_request.Reverse && _request.ExcludePathPatterns is { Count: > 0 })
            {
                for (var i = 0; i < _request.ExcludePathPatterns.Count; i++)
                    _sql.Append($" AND NOT {BuildPathFilterPredicate(SourceAlias, "excludePath", i, _request.ExcludePathPatterns[i])}");
            }
            if (_request.ExcludeTests)
                _sql.Append($" AND NOT {DependencyTestPathCondition($"{SourceAlias}.path")}");
        }

        private void AppendLogicalReferences()
        {
            _sql.Append(@"
                GROUP BY evidence_resolution_state, src.id, src.path, src.lang, " + _expressions.ReferenceIdSql + @", " + _expressions.ScopedResolutionStateSql + @", " + _expressions.IdentityScopedSql + @", r.symbol_name, " + _expressions.ContextSql + @", r.container_name, r.line, r.column_number, r.reference_kind, logical_reference_kind
            ),
            logical_references AS (
                SELECT source_file_id, source_path, source_lang, reference_id, identity_scoped, evidence_resolution_state,
                       " + BuildLogicalReferenceNameExpr("source_lang", "symbol_name", "context", "container_name", "column_number") + @" AS symbol_name,
                       " + BuildLogicalReferenceSegmentCountExpr("source_lang", "symbol_name", "context", "container_name", "column_number") + @" AS symbol_segment_count,
                       " + BuildLogicalReferenceLeafFallbackAllowedExpr("source_lang", "symbol_name", "context", "container_name", "column_number") + @" AS allow_leaf_fallback,
                       symbol_name AS raw_symbol_name,
                       context, line, column_number, raw_reference_kind, logical_reference_kind,
                       0 AS is_attribute_alias,
                       CASE WHEN logical_reference_kind IN ('attribute', 'annotation') THEN 1 ELSE 0 END AS is_metadata
                FROM logical_references_primary
                UNION ALL
                -- C# attribute suffix alias: [Foo] in source is stored with symbol_name='Foo',
                -- but the defining class is named 'FooAttribute'. Emit the canonical 'Foo' + 'Attribute'
                -- form so deps can match the class file as a target. The alias rows are flagged
                -- so the edges CTE can restrict them to class-like targets and avoid spurious
                -- edges to unrelated functions / properties that happen to be named 'FooAttribute'.
                -- C# 属性のサフィックス別名: ソース上の [Foo] は symbol_name='Foo' で保存されるが、
                -- 定義クラスは 'FooAttribute' 命名になるため、正規形 'Foo' + 'Attribute' を補って
                -- deps がクラス側のファイルを target として join できるようにする。alias 行には
                -- フラグを付け、edges CTE 側で class-like target だけに限定する。これにより、
                -- 偶然 'FooAttribute' という名前を持つ関数やプロパティへの誤ったエッジを防ぐ。
                SELECT source_file_id, source_path, source_lang, reference_id, identity_scoped, evidence_resolution_state,
                       symbol_name || 'Attribute' AS symbol_name,
                       1 AS symbol_segment_count,
                       0 AS allow_leaf_fallback,
                       symbol_name || 'Attribute' AS raw_symbol_name,
                       context, line, column_number, raw_reference_kind, logical_reference_kind,
                       1 AS is_attribute_alias,
                       1 AS is_metadata
                FROM logical_references_primary
                WHERE source_lang = 'csharp'
                  AND logical_reference_kind = 'attribute'
                  AND symbol_name NOT LIKE '%Attribute'
            ),");
        }

        private void AppendSourceNameCounts()
        {
            _sql.Append(@"
            source_name_counts AS (
                -- Grouping includes is_metadata so metadata-only groups ([Foo] / @Foo)
                -- can be restricted to class-like targets independently from non-metadata
                -- call-graph groups that share the same symbol_name in the same file
                -- (e.g. `Foo()` call + `[Foo]` attribute both present in the same source).
                -- is_metadata を GROUP BY に含めることで、同じ source file / symbol_name を
                -- 共有する metadata 行と call-graph 行 (例: 同じファイル内の `Foo()` 呼び出し
                -- と `[Foo]` 属性) を別グループとして扱い、metadata 側だけに class-like
                -- target 制限を掛けられるようにする。
                SELECT source_file_id,
                       source_path,
                       source_lang,
                       identity_scoped,
                       evidence_resolution_state,
                       symbol_name,
                       symbol_segment_count,
                       allow_leaf_fallback,
                       raw_symbol_name,
                       context,
                       column_number,
                       raw_reference_kind,
                       logical_reference_kind,
                       is_attribute_alias,
                       is_metadata,
                       COUNT(*) AS ref_count
                FROM logical_references
                WHERE 1 = 1");
            _sql.Append(BuildDependencySymbolFilter(
                "symbol_name",
                _request.DependencySymbols,
                _request.DependencySymbolFamilies,
                _request.SuppressDependencyNoise,
                "sourceDependency",
                "source_lang = 'csharp'"));
            _sql.Append(@"
                GROUP BY source_file_id, source_path, source_lang, identity_scoped, evidence_resolution_state, symbol_name, symbol_segment_count, allow_leaf_fallback, raw_symbol_name, context, column_number, raw_reference_kind, logical_reference_kind, is_attribute_alias, is_metadata
            )");
        }
    }
}

namespace CodeIndex.Database;

public partial class DbReader
{
    private sealed class DependencyEdgeSqlBuilder
    {
        private readonly DependencyQueryRequest _request;
        private readonly DependencyQueryExpressions _expressions;
        private readonly DependencySqlFragment _resolvedIdentity;
        private readonly DependencySqlFragmentBuilder _sql = new();

        internal DependencyEdgeSqlBuilder(
            DependencyQueryRequest request,
            DependencyQueryExpressions expressions,
            DependencySqlFragment resolvedIdentity)
        {
            _request = request;
            _expressions = expressions;
            _resolvedIdentity = resolvedIdentity;
        }

        internal DependencySqlFragment Build()
        {
            AppendNameMatchedEdges();
            AppendNameFilters();
            return _sql.Build();
        }

        private void AppendNameMatchedEdges()
        {
            _sql.Append(@"
            edges AS (
                ");
            _sql.Append(_resolvedIdentity.Sql);
            _sql.Append(@"
                SELECT snc.source_path,
                       tf.target_path,
                       tf.symbol_name,
                       snc.ref_count,
                       snc.source_lang,
                       CASE
                           WHEN snc.source_lang = 'markdown' AND tf.has_heading_kind = 1
                               THEN 'markdown_heading_name_match'
                           ELSE 'symbol_name_match'
                       END AS origin,
                       snc.raw_reference_kind,
                       CASE WHEN tf.has_heading_kind = 1 THEN 'heading' ELSE 'symbol' END AS target_kind
                FROM bounded_source_name_counts snc
                JOIN target_files tf
                  ON " + _expressions.SqlDependencyTargetMatch + @"
                 AND tf.target_lang = snc.source_lang
                LEFT JOIN metadata_raw_suppression mrs
                  ON mrs.source_file_id = snc.source_file_id
                 AND mrs.symbol_name = snc.symbol_name
                LEFT JOIN target_ambiguity ta
                  ON ta.target_lang = snc.source_lang
                 AND ta.symbol_name = snc.symbol_name
                 AND ta.symbol_segment_count = snc.symbol_segment_count
                WHERE snc.source_path != tf.target_path
                  AND " + _expressions.IdentityNameEdgePredicate + @"
                  -- All metadata references ([Foo] / @Foo) and their synthetic C#
                  -- suffix aliases must only match class-like target kinds; otherwise
                  -- a metadata reference would spuriously depend on any file that
                  -- merely defines a function / property / variable sharing the name.
                  -- Non-metadata call-graph refs keep matching any kind so e.g. a
                  -- constructor call can still tie back to a class definition.
                  -- metadata 参照 ([Foo] / @Foo) と C# の合成 alias 行はいずれも
                  -- class 系の target 種別にのみ一致させる。これを許すと同名の
                  -- 関数/プロパティ/変数を持つだけのファイルまで誤って依存してしまう。
                  -- 非 metadata の call-graph 参照は任意の kind に一致させて構わない
                  -- (コンストラクタ呼び出しがクラス定義に結び付くケースなど)。
                  AND (snc.is_metadata = 0 OR tf.has_metadata_target_kind = 1)
                  -- Drop raw C# '[Foo]' rows when the suffix alias already resolves
                  -- to a class-like 'FooAttribute' target in the same source file.
                  -- 同じ source file で suffix alias が class 系 'FooAttribute' に
                  -- 解決できている C# の raw '[Foo]' 行は落とす。
                  AND NOT (
                        snc.is_metadata = 1
                    AND snc.is_attribute_alias = 0
                    AND snc.source_lang = 'csharp'
                    AND mrs.source_file_id IS NOT NULL
                  )
                  -- Metadata edges only survive when the target symbol resolves to
                  -- a single class-like definition within scope; ambiguous cases
                  -- (multiple same-name attribute / annotation classes) are dropped.
                  -- metadata エッジは同名 class 系 target が 1 つだけのときのみ残す。
                  AND (snc.is_metadata = 0 OR COALESCE(ta.class_like_target_count, 0) <= 1)
                  -- Python names are file-local bindings. A cross-file edge is valid only
                  -- when an import in the source file names the referenced symbol/module
                  -- and that module owns the target path. This prevents ubiquitous names
                  -- such as Path, main, json, and dataclass from joining every definition.
                  -- Python の名前はファイルローカルな binding である。cross-file edge は
                  -- source file の import が参照名/module を束縛し、その module が target
                  -- path を所有する場合だけ残し、Path/main/json/dataclass の誤結合を防ぐ。
                  AND (snc.source_lang != 'python' OR EXISTS (
                        SELECT 1
                        FROM symbols py_import
                        WHERE py_import.file_id = snc.source_file_id
                          AND py_import.kind = 'import'
                          AND python_import_resolves(snc.source_path, tf.target_path, snc.symbol_name, snc.raw_reference_kind, snc.context, snc.column_number, " + _expressions.PythonImportSignature + @")
                  ))");
        }

        private void AppendNameFilters()
        {
            _sql.Append(BuildDependencySymbolFilter(
                "tf.symbol_name",
                _request.DependencySymbols,
                _request.DependencySymbolFamilies,
                suppressDependencyNoise: false,
                parameterPrefix: "edgeDependencyNames"));
            _sql.Append(BuildDependencySymbolFilter(
                "tf.symbol_name",
                dependencySymbols: null,
                dependencySymbolFamilies: null,
                suppressDependencyNoise: _request.SuppressDependencyNoise,
                parameterPrefix: "edgeDependencyNoise",
                filterScopeSql: "NOT (snc.source_lang = 'markdown' AND tf.has_heading_kind = 1)"));
        }
    }
}

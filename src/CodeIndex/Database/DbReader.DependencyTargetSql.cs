namespace CodeIndex.Database;

public partial class DbReader
{
    private sealed class DependencyTargetSqlBuilder
    {
        private const string TargetAlias = "dst";

        private readonly DbReader _reader;
        private readonly DependencyQueryRequest _request;
        private readonly DependencyQueryExpressions _expressions;
        private readonly DependencySqlFragmentBuilder _sql = new();

        internal DependencyTargetSqlBuilder(
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
            AppendTargetFiles();
            AppendTargetScope();
            AppendTargetMetadataQueries();
            return _sql.Build();
        }

        private void AppendTargetFiles()
        {
            _sql.Append(@"
            target_files AS (
                -- Collapse per-symbol rows to one per (target_path, target_lang, symbol_name)
                -- and remember whether any of the same-name symbols is a class-like kind
                -- via MAX. Keeping kind in DISTINCT would split identical (path, lang, name)
                -- rows when one file defines both a class and a same-name function (e.g. a
                -- C# constructor), inflating the deps reference count.
                -- (target_path, target_lang, symbol_name) 単位に集約し、同名のシンボルの
                -- いずれかが class 系であるかを MAX で覚える。kind を DISTINCT に含めると、
                -- 同じ (path, lang, name) でも class と同名 function (C# のコンストラクタ等)
                -- が別行として残り、deps の参照カウントが膨らんでしまう。
                -- has_metadata_target_kind further narrows the class-like set to targets
                -- that can legitimately be referenced as [Attribute] metadata. For C#
                -- we cannot resolve base types transitively at SQL time, so the best
                -- portable approximation is an inheritance-clause check: any class
                -- declared with a base list is a potential attribute type (direct or
                -- indirect Attribute derivation). A plain class FooAttribute with no
                -- base clause is not a valid [Foo] target at compile time.
                -- Other languages keep the original class-like breadth. Legacy DBs
                -- without a signature column degrade to the broad class-like set.
                -- has_metadata_target_kind は [Attribute] metadata target として妥当な
                -- class-like のみに絞る。C# は SQL 時点で基底型を遡れないため、継承節を
                -- 持つクラスを候補とする近似を採る(直接・間接の Attribute 継承を
                -- 取りこぼさない)。他言語は class-like 全体を残す。signature 列が無い
                -- legacy DB では filter を無効化し class-like 全体に戻る。
                SELECT dst.path AS target_path,
                       dst.lang AS target_lang,
                       " + _expressions.TargetLogicalSymbolName + @" AS symbol_name,
                       " + _expressions.TargetLogicalSymbolSegmentCount + @" AS symbol_segment_count,
                       MAX(CASE WHEN s.kind IN ('class','struct','interface') THEN 1 ELSE 0 END) AS has_class_like_kind,
                       MAX(CASE WHEN s.kind = 'heading' THEN 1 ELSE 0 END) AS has_heading_kind,
                       MAX(CASE WHEN " + _reader.BuildMetadataTargetKindExpr(TargetAlias) + @"
                                THEN 1 ELSE 0 END) AS has_metadata_target_kind
                FROM symbols s
                JOIN files dst ON s.file_id = dst.id
                WHERE 1 = 1");
            var languagePredicate = _reader.BuildDependencyGraphLanguagePredicate(TargetAlias, "depsTargetLang");
            _sql.Append(" AND " + languagePredicate.Sql);
            _sql.AddParameters(languagePredicate.Parameters);
            _sql.Append(_reader.BuildDependencyGeneratedFilter(TargetAlias));
        }

        private void AppendTargetScope()
        {
            if (_request.Lang != null && !_request.Lang.Equals("solution", StringComparison.Ordinal))
                _sql.Append(" AND dst.lang = @lang");
            if (_request.Reverse && _request.PathPatterns is { Count: > 0 })
            {
                var predicates = new List<string>(_request.PathPatterns.Count);
                for (var i = 0; i < _request.PathPatterns.Count; i++)
                    predicates.Add(BuildPathFilterPredicate(TargetAlias, "pathPattern", i, _request.PathPatterns[i]));
                _sql.Append(" AND (" + string.Join(" OR ", predicates) + ")");
            }
            if (_request.Reverse && _request.ExcludePathPatterns is { Count: > 0 })
            {
                for (var i = 0; i < _request.ExcludePathPatterns.Count; i++)
                    _sql.Append($" AND NOT {BuildPathFilterPredicate(TargetAlias, "excludePath", i, _request.ExcludePathPatterns[i])}");
            }
            if (_request.ExcludeTests)
                _sql.Append($" AND NOT {DependencyTestPathCondition($"{TargetAlias}.path")}");
            _sql.Append(BuildDependencySymbolFilter(
                _expressions.TargetLogicalSymbolName,
                _request.DependencySymbols,
                _request.DependencySymbolFamilies,
                _request.SuppressDependencyNoise,
                "targetDependency",
                "dst.lang = 'csharp'"));
        }

        private void AppendTargetMetadataQueries()
        {
            _sql.Append(@"
                GROUP BY dst.path, dst.lang, " + _expressions.TargetLogicalSymbolName + @", " + _expressions.TargetLogicalSymbolSegmentCount + @"
            ),
            path_target_files AS (
                SELECT DISTINCT target_path, target_lang
                FROM target_files
            ),
            metadata_raw_suppression AS (
                -- When a raw C# attribute reference '[Foo]' (stored as symbol_name='Foo',
                -- logical_reference_kind='attribute') also has a synthetic suffix alias
                -- row that resolves to a class-like 'FooAttribute' target, drop the raw
                -- row to avoid creating a duplicate edge to any unrelated 'Foo' symbol
                -- (method, property, local class) that merely shares the bare name.
                -- 生の C# 属性参照 '[Foo]' (symbol_name='Foo', kind='attribute') に対して
                -- 同じ source_file 内で 'FooAttribute' の synthetic alias 行が
                -- class 系 target に解決できる場合、この行自体は落として
                -- 同名の関数/プロパティ/ローカルクラス 'Foo' への誤依存を防ぐ。
                SELECT DISTINCT lrp.source_file_id, lrp.symbol_name
                FROM logical_references_primary lrp
                JOIN target_files tf_alias
                  ON tf_alias.target_lang = lrp.source_lang
                 AND tf_alias.symbol_name = lrp.symbol_name || 'Attribute'
                 AND tf_alias.symbol_segment_count = 1
                 AND tf_alias.has_metadata_target_kind = 1
                WHERE lrp.source_lang = 'csharp'
                  AND lrp.logical_reference_kind = 'attribute'
                  AND lrp.symbol_name NOT LIKE '%Attribute'
            ),
            target_ambiguity AS (
                -- Count class-like definitions at symbol-identity level rather than
                -- file level. Two same-named class-like definitions in the same file
                -- (e.g. `namespace A { class FooAttribute { } } namespace B { class
                -- FooAttribute { } }` both inside one .cs file) collapse to a single
                -- target_files row because target_files is GROUPed by dst.path, so
                -- COUNT(DISTINCT target_path) alone would see count=1 and falsely
                -- treat the metadata target as unambiguous. Joining target_files back
                -- through files + symbols recovers the per-definition row count while
                -- still inheriting target_files' lang / path / graph-supported scope
                -- (since the join only keeps rows whose (path, lang, name) already
                -- appear in target_files).
                -- class-like 定義は path 単位ではなく symbol identity 単位で数える。
                -- 同じ .cs ファイル内に別名前空間で同名 class-like が 2 つあるケースは
                -- target_files (dst.path で GROUP BY) 上では 1 行に潰れており、
                -- COUNT(DISTINCT target_path) だけでは count=1 となり metadata target
                -- が一意と誤判定される。target_files から files + symbols に JOIN し直す
                -- ことで定義単位の件数を復元する。JOIN が target_files 既存行にしか
                -- 当たらないため、lang / path / graph-supported スコープはそのまま継承。
                SELECT tf.target_lang,
                       tf.symbol_name,
                       tf.symbol_segment_count,
                       COUNT(*) AS class_like_target_count
                FROM target_files tf
                JOIN files dst
                  ON dst.path = tf.target_path
                 AND dst.lang = tf.target_lang
                JOIN symbols s
                  ON s.file_id = dst.id
                 AND " + _expressions.TargetLogicalSymbolName + @" = tf.symbol_name
                 AND " + _expressions.TargetLogicalSymbolSegmentCount + @" = tf.symbol_segment_count
                 -- Same language-aware metadata-eligibility filter as
                 -- target_files: C# restricts to `class` with inheritance
                 -- clause (interface/struct cannot be attribute targets);
                 -- JS/TS additionally accepts `function` (decorator
                 -- factory); others keep the class-like candidate set.
                 -- target_files と同じ言語別 metadata 適格性フィルタ。
                 -- C# は class 限定 + 継承節 (interface/struct は除外)。
                 -- JS/TS は decorator factory 用に function も許容。
                 -- それ以外は class-like 全体を候補にする。
                 AND " + _reader.BuildMetadataTargetKindExpr(TargetAlias) + @"
                WHERE tf.has_metadata_target_kind = 1
                GROUP BY tf.target_lang, tf.symbol_name, tf.symbol_segment_count
            ),");
        }
    }
}

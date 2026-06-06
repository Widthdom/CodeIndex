using CodeIndex.Indexer;
using Microsoft.Data.Sqlite;
using System.Text;
using System.Text.RegularExpressions;

namespace CodeIndex.Database;

public partial class DbReader
{
    internal const int DependencySymbolSampleLimit = 32;

    private string ReferenceContextSql(string referenceAlias, string referenceLineAlias = "rl")
        => _canUseReferenceLines
            ? $"COALESCE({referenceAlias}.context, {referenceLineAlias}.context)"
            : $"{referenceAlias}.context";

    private string ReferenceLineJoinSql(string referenceAlias, string referenceLineAlias = "rl")
        => _canUseReferenceLines
            ? $" LEFT JOIN reference_lines {referenceLineAlias} ON {referenceLineAlias}.id = {referenceAlias}.reference_line_id"
            : string.Empty;

    private string GetSymbolColumnSql(string columnName, string? fallbackSql = null, string symbolAlias = "s")
    {
        if (_symbolColumns.Contains(columnName))
        {
            // Older binaries added the column but may have left existing rows with NULL.
            // Coalesce to the fallback so queries don't crash on legacy indexes.
            // 古いバイナリがカラムだけ追加して既存行を NULL のまま残しているケースに備え、
            // fallback と COALESCE してレガシーインデックスでクラッシュしないようにする。
            return fallbackSql != null
                ? $"COALESCE({symbolAlias}.{columnName}, {fallbackSql})"
                : $"{symbolAlias}.{columnName}";
        }

        return fallbackSql ?? "NULL";
    }

    internal string GetFileColumnSql(string columnName, string? fallbackSql = null)
    {
        if (_fileColumns.Contains(columnName))
            return $"f.{columnName}";

        return fallbackSql ?? "NULL";
    }

    // Build the language-aware metadata-target eligibility predicate used by
    // `deps` (target_files / target_ambiguity) and `impact`
    // (IsMetadataTargetUnambiguous). Returns a SQL fragment that evaluates to
    // TRUE when a `(symbols s, files <fileAlias>)` row should be counted as a
    // plausible metadata target (`[Attribute]` / `@Annotation` / `@decorator`).
    // Rules by language:
    //   - C# (`csharp`): only `kind = 'class'` with an inheritance clause
    //     (`signature LIKE '%: %'`). Transitive base-type resolution is not
    //     available at SQL time, so "has any inheritance clause" is the
    //     portable approximation for direct `: Attribute` plus indirect
    //     `: BaseAudit` where `BaseAudit` itself derives from Attribute.
    //     Extractor-driven authoritative `is_metadata_target` classification is
    //     tracked as a follow-up (issue #435) and would let `deps` / `impact`
    //     reject non-attribute classes like `class MyAuditAttribute : BaseService`
    //     that this heuristic cannot distinguish.
    //     For legacy-migration DBs whose `signature` column exists but stores
    //     NULL for individual C# class rows, fall back to the canonical C#
    //     attribute-naming convention (`name LIKE '%Attribute'`). This is
    //     strictly narrower than the previous unconditional NULL-signature
    //     pass-through and prevents every NULL-signature class from being
    //     treated as a plausible metadata target. DBs without any `signature`
    //     column at all degrade to the same naming heuristic.
    //   - JS / TS (`javascript` / `typescript`): decorators target runtime
    //     entities — classes and factory `function` definitions
    //     (e.g. `function sealed(target) {}` used as `@sealed class Foo {}`).
    //     TypeScript `interface` is a compile-time type-only construct and
    //     cannot be a decorator target at runtime; including it would let a
    //     same-name `interface` inject false ambiguity against the real
    //     `function` or `class` provider and silently drop the decorator edge.
    //   - Everything else (Java `@interface`, Kotlin `annotation class`,
    //     Scala annotation classes, etc.): the annotation target is a
    //     class-like declaration, so keep the original class-like candidate
    //     set (`class` / `struct` / `interface`).
    // `deps` と `impact` で共有する言語別 metadata-target 適格性判定。
    // C# は `kind = 'class'` かつ継承節を持つ行を対象とする（直接/間接の Attribute 継承を
    // ポータブルに近似するため）。signature 列は存在するが値が NULL の legacy-migration
    // DB では C# の命名規約 `name LIKE '%Attribute'` にフォールバック — 従来の
    // 無条件許容より厳密で、NULL-signature の全 class を metadata target 扱いしない。
    // signature 列自体が無い旧 DB も同じ命名規約ヒューリスティックを使う。
    // extractor 主導の authoritative な `is_metadata_target` 判定は follow-up（issue #435）
    // として追跡しており、schema 化すれば `class MyAuditAttribute : BaseService` のような
    // 非 attribute 継承も厳密に除外できるが、現状のヒューリスティックでは判別できない。
    // JS / TS は decorator が runtime entity (class / factory function) のみ対象。
    // TypeScript の `interface` は型定義で runtime decorator target にならないため除外し、
    // 同名 `interface` が本物の `function` / `class` provider を曖昧化するのを防ぐ。
    // それ以外は従来どおり class-like を候補にする。
    private string BuildMetadataTargetKindExpr(string fileAlias)
    {
        // C# clause — class only (interface/struct cannot be attribute targets).
        // Non-NULL signature: accept any inheritance clause (`: %`) as the portable
        // approximation of direct/indirect Attribute derivation (see issue #435).
        // NULL signature: require the C# attribute naming convention
        // (`name LIKE '%Attribute'`). This is strictly narrower than the previous
        // unconditional NULL pass-through and prevents arbitrary NULL-signature
        // classes on a legacy-migration DB from being treated as metadata targets.
        // DBs missing the `signature` column entirely degrade to the same naming
        // heuristic.
        // C# は class のみ（interface/struct は attribute target にできない）。
        // 非 NULL signature は従来どおり継承節 `: %` で判定（直接/間接 Attribute の近似）。
        // NULL signature は C# 命名規約 `name LIKE '%Attribute'` に縮退 — 従来の
        // 無条件許容より厳密で、legacy-migration DB で任意の NULL-signature class が
        // metadata target 扱いされるのを防ぐ。signature 列欠落 DB も同じ命名規約を使う。
        // Authoritative column takes precedence once the writer's resolver has stamped the
        // current `metadata_target_version_csharp` version. Drops the `: %` heuristic for C#
        // so non-attribute classes like `class MyAuditAttribute : BaseService` no longer fake
        // ambiguity against a sibling real `class MyAuditAttribute : Attribute`. Issue #435.
        // writer の resolver が current version を stamp 済みの DB では authoritative 列を優先し、
        // `class MyAuditAttribute : BaseService` のような非 Attribute 派生を ambiguity から除外する。
        // Three-way branch keyed off the `is_metadata_target` column presence, not
        // `signature`. Branch (2) (legacy heuristic) must only fire when both the new
        // column and the old signature column are present — a DB missing
        // `is_metadata_target` entirely is truly ancient and must degrade to branch (3).
        // Issue #435 codex review.
        // 3 way 分岐は `is_metadata_target` 列の有無で切り替え、`signature` の有無では判定しない。
        // `is_metadata_target` 列すらない DB は真に古い legacy なので命名規約 fallback (branch 3) に落とす。
        string csharpClause;
        if (_csharpMetadataTargetReady)
        {
            csharpClause = $"({fileAlias}.lang = 'csharp' AND s.kind = 'class' AND s.is_metadata_target = 1)";
        }
        else if (_symbolColumns.Contains("is_metadata_target") && _symbolColumns.Contains("signature"))
        {
            csharpClause = $"({fileAlias}.lang = 'csharp' AND s.kind = 'class' AND ((s.signature IS NOT NULL AND s.signature LIKE '%: %') OR (s.signature IS NULL AND s.name LIKE '%Attribute')))";
        }
        else
        {
            csharpClause = $"({fileAlias}.lang = 'csharp' AND s.kind = 'class' AND s.name LIKE '%Attribute')";
        }
        // JS / TS clause — decorators target runtime entities (classes and factory
        // functions). TS `interface` is a type-only construct that cannot be a
        // decorator target, so excluding it avoids false ambiguity against a
        // real function/class provider sharing the same name.
        // JS / TS: decorator は runtime entity (class / factory function) のみ対象。
        // TS の `interface` は型定義のため除外しないと同名 interface が偽の曖昧さを
        // 発生させる。
        var jsClause = $"({fileAlias}.lang IN ('javascript','typescript') AND s.kind IN ('class','function'))";
        // All other graph-supported languages keep the original class-like set.
        // その他の graph 対応言語は従来どおり class-like を対象にする。
        var otherClause = $"({fileAlias}.lang NOT IN ('csharp','javascript','typescript') AND s.kind IN ('class','struct','interface'))";
        return $"({csharpClause} OR {jsClause} OR {otherClause})";
    }

    // `deps` keeps persisted SQL symbol names qualified (`dbo.fn_X`) but must
    // still join bare SQL reference rows (`fn_X`) back to that definition.
    // Normalize dependency target keys to logical qualified names for SQL while leaving
    // other languages on the stored symbol name. SQL reference rows can still fall back to
    // leaf-only matching at join time when the source site itself is unqualified.
    // SQL の依存 target key は qualified 名 (`dbo.fn_X`) に正規化し、他言語は保存名のまま。
    // SQL の source 側が unqualified (`fn_X`) の場合だけ join 時に leaf fallback を許可する。
    private static string BuildLogicalDependencySymbolNameExpr(string fileAlias, string symbolNameExpr)
        => $"CASE WHEN {fileAlias}.lang = 'sql' THEN sql_normalize_name({symbolNameExpr}) ELSE {symbolNameExpr} END";

    private static string BuildLogicalDependencySymbolSegmentCountExpr(string fileAlias, string symbolNameExpr)
        => $"CASE WHEN {fileAlias}.lang = 'sql' THEN sql_segment_count({symbolNameExpr}) ELSE 1 END";

    private static string BuildLogicalReferenceNameExpr(string langExpr, string symbolNameExpr, string contextExpr, string containerNameExpr, string columnNumberExpr)
        => $@"CASE
                WHEN {langExpr} = 'sql' THEN sql_resolve_reference_name_at({symbolNameExpr}, {contextExpr}, {containerNameExpr}, {columnNumberExpr})
                ELSE {symbolNameExpr}
            END";

    private static string BuildLogicalReferenceSegmentCountExpr(string langExpr, string symbolNameExpr, string contextExpr, string containerNameExpr, string columnNumberExpr)
        => $@"CASE
                WHEN {langExpr} = 'sql' THEN sql_resolve_reference_segment_count_at({symbolNameExpr}, {contextExpr}, {containerNameExpr}, {columnNumberExpr})
                ELSE 1
            END";

    private static string BuildLogicalReferenceLeafFallbackAllowedExpr(string langExpr, string symbolNameExpr, string contextExpr, string containerNameExpr, string columnNumberExpr)
        => $@"CASE
                WHEN {langExpr} = 'sql' THEN sql_allow_leaf_fallback_at({symbolNameExpr}, {contextExpr}, {containerNameExpr}, {columnNumberExpr})
                ELSE 0
            END";

    /// <summary>
    /// Compute file-level dependency edges: which files reference symbols defined in which other files.
    /// ファイル間の依存関係エッジを算出: どのファイルがどのファイルで定義されたシンボルを参照しているか。
    /// </summary>
    // Issue #2121 audit: deps is a bounded aggregate query, not a depth-bounded
    // traversal, so there is no maxDepth contract to align here.
    // issue #2121 監査: deps は上限付きの集計クエリであり depth-bounded traversal ではないため、
    // maxDepth の inclusive/exclusive 契約は持たない。
    public List<FileDependencyResult> GetFileDependencies(int limit = 50, string? lang = null, IReadOnlyList<string>? pathPatterns = null, IReadOnlyList<string>? excludePathPatterns = null, bool excludeTests = false, bool reverse = false)
    {
        lang = NormalizeQueryLanguage(lang);
        if (!_hasReferencesTable) return new List<FileDependencyResult>();
        using var cmd = _conn.CreateCommand();
        var referenceLineJoin = ReferenceLineJoinSql("r");
        var contextSql = ReferenceContextSql("r");
        // Aggregate logical reference sites per source-file/name first, then join that bounded
        // set to distinct target files. This avoids the per-reference × per-symbol explosion that
        // could exhaust SQLite temp-store on large indexes with many same-named symbols.
        // まず source-file/name 単位に logical reference site 数を集約し、その後で distinct な
        // target file と結合することで、大規模 index で SQLite temp-store を枯渇させる
        // per-reference × per-symbol の膨張を防ぐ。
        var sourceFilterAlias = "src";
        var targetFilterAlias = "dst";
        var targetLogicalSymbolNameExpr = BuildLogicalDependencySymbolNameExpr("dst", "s.name");
        var targetLogicalSymbolSegmentCountExpr = BuildLogicalDependencySymbolSegmentCountExpr("dst", "s.name");
        var sqlDependencyTargetMatchExpr = @"(
                    (tf.target_lang != 'sql' AND tf.symbol_name = snc.symbol_name)
                 OR (tf.target_lang = 'sql' AND (
                        (tf.symbol_segment_count = snc.symbol_segment_count AND tf.symbol_name = snc.symbol_name COLLATE NOCASE)
                     OR (sql_segment_count(snc.raw_symbol_name) = 1
                         AND snc.allow_leaf_fallback = 1
                         AND tf.symbol_segment_count > 1
                         AND sql_leaf_name(tf.symbol_name) = snc.raw_symbol_name COLLATE NOCASE
                         AND NOT EXISTS (
                                SELECT 1
                                FROM target_files tf_exact
                                WHERE tf_exact.target_lang = tf.target_lang
                                  AND tf_exact.symbol_segment_count = 1
                                  AND tf_exact.symbol_name = snc.symbol_name COLLATE NOCASE
                            )
                         AND NOT EXISTS (
                                SELECT 1
                                FROM target_files tf_resolved
                                WHERE tf_resolved.target_lang = tf.target_lang
                                  AND tf_resolved.symbol_segment_count = snc.symbol_segment_count
                                  AND tf_resolved.symbol_name = snc.symbol_name COLLATE NOCASE
                            ))
                 ))
                )";
        var sql = @"
            WITH logical_references_primary AS (
                SELECT src.id AS source_file_id,
                       src.path AS source_path,
                       src.lang AS source_lang,
                       r.symbol_name,
                       " + contextSql + @" AS context,
                       r.container_name,
                       r.line,
                       r.column_number,
                       " + GetLogicalReferenceKindSql("r.reference_kind") + @" AS logical_reference_kind
                FROM symbol_references r
                JOIN files src ON r.file_id = src.id" + referenceLineJoin + @"
                WHERE 1 = 1";
        // `deps` intentionally includes metadata-only reference kinds
        // (`attribute` / `annotation`). Same rationale as
        // `GetFileDependencyHintsToResolvedType`: renaming or removing a type that
        // is only referenced via `[JsonConverter(typeof(User))]` or
        // `@Inject(User.class)` still breaks the annotated file at compile time, so
        // file-level dependency analysis must treat those sites as real edges.
        // Call-graph-specific commands (`callers` / `callees`) keep rejecting
        // metadata kinds at the CLI / MCP boundary — that is a separate contract.
        // `deps` は metadata-only 参照 (`attribute` / `annotation`) も意図的に
        // 含める。`GetFileDependencyHintsToResolvedType` と同じ理由で、
        // `[JsonConverter(typeof(User))]` や `@Inject(User.class)` 経由でしか参照
        // されない型でも、rename / 削除すれば annotated ファイルは compile-time
        // で壊れるため、ファイル単位の依存分析では本物の edge として扱う必要が
        // ある。call-graph 専用コマンド (`callers` / `callees`) 側では metadata
        // 種別の拒否を CLI / MCP boundary で引き続き行う — そちらは別契約。
        sql += $" AND {BuildGraphSupportedLanguagePredicate(cmd, "src", "depsLang")}";
        if (lang != null)
            sql += " AND src.lang = @lang";
        if (!reverse && pathPatterns is { Count: > 0 })
        {
            var ors = new List<string>(pathPatterns.Count);
            for (int i = 0; i < pathPatterns.Count; i++)
                ors.Add($"{sourceFilterAlias}.path LIKE @pathPattern{i} ESCAPE '\\'");
            sql += " AND (" + string.Join(" OR ", ors) + ")";
        }
        if (!reverse && excludePathPatterns is { Count: > 0 })
        {
            for (int i = 0; i < excludePathPatterns.Count; i++)
                sql += $" AND {sourceFilterAlias}.path NOT LIKE @excludePath{i} ESCAPE '\\'";
        }
        if (!reverse && excludeTests)
            sql += $" AND NOT {TestPathCondition.Replace("f.path", $"{sourceFilterAlias}.path")}";
        sql += @"
                GROUP BY src.id, src.path, src.lang, r.symbol_name, " + contextSql + @", r.container_name, r.line, r.column_number, logical_reference_kind
            ),
            logical_references AS (
                SELECT source_file_id, source_path, source_lang,
                       " + BuildLogicalReferenceNameExpr("source_lang", "symbol_name", "context", "container_name", "column_number") + @" AS symbol_name,
                       " + BuildLogicalReferenceSegmentCountExpr("source_lang", "symbol_name", "context", "container_name", "column_number") + @" AS symbol_segment_count,
                       " + BuildLogicalReferenceLeafFallbackAllowedExpr("source_lang", "symbol_name", "context", "container_name", "column_number") + @" AS allow_leaf_fallback,
                       symbol_name AS raw_symbol_name,
                       line, column_number, logical_reference_kind,
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
                SELECT source_file_id, source_path, source_lang,
                       symbol_name || 'Attribute' AS symbol_name,
                       1 AS symbol_segment_count,
                       0 AS allow_leaf_fallback,
                       symbol_name || 'Attribute' AS raw_symbol_name,
                       line, column_number, logical_reference_kind,
                       1 AS is_attribute_alias,
                       1 AS is_metadata
                FROM logical_references_primary
                WHERE source_lang = 'csharp'
                  AND logical_reference_kind = 'attribute'
                  AND symbol_name NOT LIKE '%Attribute'
            ),
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
                       symbol_name,
                       symbol_segment_count,
                       allow_leaf_fallback,
                       raw_symbol_name,
                       is_attribute_alias,
                       is_metadata,
                       COUNT(*) AS ref_count
                FROM logical_references
                GROUP BY source_file_id, source_path, source_lang, symbol_name, symbol_segment_count, allow_leaf_fallback, raw_symbol_name, is_attribute_alias, is_metadata
            ),
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
                       " + targetLogicalSymbolNameExpr + @" AS symbol_name,
                       " + targetLogicalSymbolSegmentCountExpr + @" AS symbol_segment_count,
                       MAX(CASE WHEN s.kind IN ('class','struct','interface') THEN 1 ELSE 0 END) AS has_class_like_kind,
                       MAX(CASE WHEN " + BuildMetadataTargetKindExpr("dst") + @"
                                THEN 1 ELSE 0 END) AS has_metadata_target_kind
                FROM symbols s
                JOIN files dst ON s.file_id = dst.id
                WHERE 1 = 1";
        sql += $" AND {BuildGraphSupportedLanguagePredicate(cmd, "dst", "depsTargetLang")}";
        if (lang != null)
            sql += " AND dst.lang = @lang";
        if (reverse && pathPatterns is { Count: > 0 })
        {
            var ors = new List<string>(pathPatterns.Count);
            for (int i = 0; i < pathPatterns.Count; i++)
                ors.Add($"{targetFilterAlias}.path LIKE @pathPattern{i} ESCAPE '\\'");
            sql += " AND (" + string.Join(" OR ", ors) + ")";
        }
        if (reverse && excludePathPatterns is { Count: > 0 })
        {
            for (int i = 0; i < excludePathPatterns.Count; i++)
                sql += $" AND {targetFilterAlias}.path NOT LIKE @excludePath{i} ESCAPE '\\'";
        }
        if (reverse && excludeTests)
            sql += $" AND NOT {TestPathCondition.Replace("f.path", $"{targetFilterAlias}.path")}";
        sql += @"
                GROUP BY dst.path, dst.lang, " + targetLogicalSymbolNameExpr + @", " + targetLogicalSymbolSegmentCountExpr + @"
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
                 AND " + targetLogicalSymbolNameExpr + @" = tf.symbol_name
                 AND " + targetLogicalSymbolSegmentCountExpr + @" = tf.symbol_segment_count
                 -- Same language-aware metadata-eligibility filter as
                 -- target_files: C# restricts to `class` with inheritance
                 -- clause (interface/struct cannot be attribute targets);
                 -- JS/TS additionally accepts `function` (decorator
                 -- factory); others keep the class-like candidate set.
                 -- target_files と同じ言語別 metadata 適格性フィルタ。
                 -- C# は class 限定 + 継承節 (interface/struct は除外)。
                 -- JS/TS は decorator factory 用に function も許容。
                 -- それ以外は class-like 全体を候補にする。
                 AND " + BuildMetadataTargetKindExpr("dst") + @"
                WHERE tf.has_metadata_target_kind = 1
                GROUP BY tf.target_lang, tf.symbol_name, tf.symbol_segment_count
            ),
            edges AS (
                SELECT snc.source_path,
                       tf.target_path,
                       tf.symbol_name,
                       snc.ref_count
                FROM source_name_counts snc
                JOIN target_files tf
                  ON " + sqlDependencyTargetMatchExpr + @"
                 AND tf.target_lang = snc.source_lang
                LEFT JOIN metadata_raw_suppression mrs
                  ON mrs.source_file_id = snc.source_file_id
                 AND mrs.symbol_name = snc.symbol_name
                LEFT JOIN target_ambiguity ta
                  ON ta.target_lang = snc.source_lang
                 AND ta.symbol_name = snc.symbol_name
                 AND ta.symbol_segment_count = snc.symbol_segment_count
                WHERE snc.source_path != tf.target_path
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
            ),
            edge_totals AS (
                SELECT source_path,
                       target_path,
                       SUM(ref_count) AS reference_count
                FROM edges
                GROUP BY source_path, target_path
            ),
            distinct_edge_symbols AS (
                SELECT DISTINCT source_path, target_path, symbol_name
                FROM edges
            ),
            ranked_edge_symbols AS (
                SELECT source_path,
                       target_path,
                       symbol_name,
                       ROW_NUMBER() OVER (PARTITION BY source_path, target_path ORDER BY symbol_name) AS symbol_rank
                FROM distinct_edge_symbols
            )
            SELECT edge_totals.source_path,
                   edge_totals.target_path,
                   edge_totals.reference_count,
                   COALESCE(GROUP_CONCAT(CASE WHEN ranked_edge_symbols.symbol_rank <= @symbolSampleLimit THEN ranked_edge_symbols.symbol_name END), '') AS symbols
            FROM edge_totals
            LEFT JOIN ranked_edge_symbols
              ON ranked_edge_symbols.source_path = edge_totals.source_path
             AND ranked_edge_symbols.target_path = edge_totals.target_path
            GROUP BY edge_totals.source_path, edge_totals.target_path, edge_totals.reference_count
            ORDER BY edge_totals.reference_count DESC, edge_totals.source_path, edge_totals.target_path
            LIMIT @limit";

        cmd.CommandText = sql;
        if (lang != null)
            cmd.Parameters.AddWithValue("@lang", lang);
        if (pathPatterns is { Count: > 0 })
        {
            for (int i = 0; i < pathPatterns.Count; i++)
                cmd.Parameters.AddWithValue($"@pathPattern{i}", BuildPathLikePattern(pathPatterns[i]));
        }
        if (excludePathPatterns is { Count: > 0 })
        {
            for (int i = 0; i < excludePathPatterns.Count; i++)
                cmd.Parameters.AddWithValue($"@excludePath{i}", BuildPathLikePattern(excludePathPatterns[i]));
        }
        cmd.Parameters.AddWithValue("@limit", limit);
        cmd.Parameters.AddWithValue("@symbolSampleLimit", DependencySymbolSampleLimit);

        var results = new List<FileDependencyResult>();
        using var reader = cmd.ExecuteTrackedReader();
        while (reader.TrackedRead())
        {
            results.Add(new FileDependencyResult
            {
                SourcePath = reader.GetString(0),
                TargetPath = reader.GetString(1),
                ReferenceCount = reader.GetInt32(2),
                Symbols = reader.GetString(3),
            });
        }
        return results;
    }
}

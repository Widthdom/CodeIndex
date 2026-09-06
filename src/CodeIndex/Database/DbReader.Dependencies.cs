using CodeIndex.Indexer;
using CodeIndex.Models;
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
    //   - C# (`csharp`): ready DBs use the authoritative `is_metadata_target`
    //     value stamped from extractor facts plus the writer resolver. Degraded
    //     DBs fall back to the legacy inheritance-clause heuristic
    //     (`signature LIKE '%: %'`) because transitive base-type resolution is not
    //     available at SQL time.
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
    // C# は ready DB では extractor/resolver が stamp した `is_metadata_target` を使う。
    // degraded DB では継承節と命名規約の legacy heuristic に縮退し、signature 列自体が無い旧 DB
    // では命名規約 `name LIKE '%Attribute'` のみに落とす。
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
        // Authoritative column takes precedence once the writer has stamped the current
        // `metadata_target_version_csharp` version. Drops the `: %` heuristic for C# so
        // non-attribute classes like `class MyAuditAttribute : BaseService` no longer fake
        // ambiguity against a sibling real `class MyAuditAttribute : Attribute`. Issue #3524.
        // writer が current version を stamp 済みの DB では authoritative 列を優先し、
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
    private string BuildLogicalDependencySymbolNameExpr(string fileAlias, string symbolNameExpr)
    {
        var containerQualifiedName = GetSymbolColumnSql("container_qualified_name");
        var visibility = GetSymbolColumnSql("visibility", "''");
        return $@"CASE
                WHEN {fileAlias}.lang = 'sql' THEN sql_normalize_name({symbolNameExpr})
                WHEN {fileAlias}.lang = 'csharp'
                 AND s.kind = 'property'
                 AND lower({visibility}) = 'private'
                 AND {containerQualifiedName} IS NOT NULL
                    THEN {containerQualifiedName} || '.' || {symbolNameExpr}
                ELSE {symbolNameExpr}
            END";
    }

    private static string BuildLogicalDependencySymbolSegmentCountExpr(string fileAlias, string symbolNameExpr)
        => $"CASE WHEN {fileAlias}.lang = 'sql' THEN sql_segment_count({symbolNameExpr}) ELSE 1 END";

    private static string BuildLogicalReferenceNameExpr(string langExpr, string symbolNameExpr, string contextExpr, string containerNameExpr, string columnNumberExpr)
        => $@"CASE
                WHEN {langExpr} = 'sql' THEN sql_resolve_reference_name_at({symbolNameExpr}, {contextExpr}, {containerNameExpr}, {columnNumberExpr})
                WHEN {langExpr} = 'markdown' AND instr({symbolNameExpr}, '#') > 0
                    THEN substr({symbolNameExpr}, 1, instr({symbolNameExpr}, '#') - 1)
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

    internal static string? ResolveMarkdownDependencyPath(string? sourcePath, string? targetPath)
    {
        if (string.IsNullOrWhiteSpace(sourcePath) || string.IsNullOrWhiteSpace(targetPath))
            return null;

        var target = targetPath.Replace('\\', '/').Trim();
        if (target.Length == 0 || target.Contains("://", StringComparison.Ordinal) || target.StartsWith("mailto:", StringComparison.OrdinalIgnoreCase))
            return null;

        var segments = new List<string>();
        if (!target.StartsWith("/", StringComparison.Ordinal))
        {
            var sourceSegments = sourcePath.Replace('\\', '/').Split('/', StringSplitOptions.RemoveEmptyEntries);
            segments.AddRange(sourceSegments.Take(Math.Max(0, sourceSegments.Length - 1)));
        }

        foreach (var segment in target.TrimStart('/').Split('/', StringSplitOptions.RemoveEmptyEntries))
        {
            if (segment == ".")
                continue;
            if (segment == "..")
            {
                if (segments.Count == 0)
                    return null;
                segments.RemoveAt(segments.Count - 1);
                continue;
            }
            segments.Add(segment);
        }

        return segments.Count == 0 ? null : string.Join('/', segments);
    }

    /// <summary>
    /// Compute file-level dependency edges: which files reference symbols defined in which other files.
    /// ファイル間の依存関係エッジを算出: どのファイルがどのファイルで定義されたシンボルを参照しているか。
    /// </summary>
    // Issue #2121 audit: deps is a bounded aggregate query, not a depth-bounded
    // traversal, so there is no maxDepth contract to align here.
    // issue #2121 監査: deps は上限付きの集計クエリであり depth-bounded traversal ではないため、
    // maxDepth の inclusive/exclusive 契約は持たない。
    public List<FileDependencyResult> GetFileDependencies(
        int limit = 50,
        string? lang = null,
        IReadOnlyList<string>? pathPatterns = null,
        IReadOnlyList<string>? excludePathPatterns = null,
        bool excludeTests = false,
        bool reverse = false,
        CancellationToken cancellationToken = default,
        IReadOnlyList<string>? dependencySymbols = null,
        IReadOnlyList<string>? dependencySymbolFamilies = null,
        bool suppressDependencyNoise = false,
        DependencyEvidenceFilter? evidenceFilter = null)
    {
        lang = NormalizeQueryLanguage(lang);
        if (!_hasReferencesTable) return new List<FileDependencyResult>();
        cancellationToken.ThrowIfCancellationRequested();

        var request = new DependencyQueryRequest(
            limit,
            lang,
            pathPatterns,
            excludePathPatterns,
            excludeTests,
            reverse,
            dependencySymbols,
            dependencySymbolFamilies,
            suppressDependencyNoise,
            evidenceFilter);
        return ExecuteDependencyQuery(BuildDependencyQueryPlan(request), cancellationToken);
    }



    private void AppendDependencyGeneratedFilter(ref string sql, string fileAlias)
        => sql += BuildDependencyGeneratedFilter(fileAlias);
}

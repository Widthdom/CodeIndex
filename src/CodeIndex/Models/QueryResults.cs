using System.Text.Json.Serialization;

namespace CodeIndex.Database;

// Result DTOs for query operations / クエリ操作用の結果DTO
// Extracted from DbReader.cs for file-size reduction.
// ファイルサイズ削減のため DbReader.cs から分離。

public class SearchResult
{
    public string Path { get; set; } = string.Empty;
    public string? Lang { get; set; }
    public int StartLine { get; set; }
    public int EndLine { get; set; }
    public string Content { get; set; } = string.Empty;
    public double Score { get; set; }
}

public readonly record struct QueryCountResult(int Count, int FileCount, bool IncludesSql = false);

public class SymbolResult
{
    public string Path { get; set; } = string.Empty;
    public string? Lang { get; set; }
    public string Kind { get; set; } = string.Empty;
    public string Name { get; set; } = string.Empty;
    public int Line { get; set; }
    public int StartLine { get; set; }
    public int EndLine { get; set; }
    public int? BodyStartLine { get; set; }
    public int? BodyEndLine { get; set; }
    public string? Signature { get; set; }
    public string? ContainerKind { get; set; }
    public string? ContainerName { get; set; }
    public string? Visibility { get; set; }
    public string? ReturnType { get; set; }
}

public class UnusedSymbolResult : SymbolResult
{
    public string UnusedBucket { get; set; } = string.Empty;
    public string UnusedConfidence { get; set; } = string.Empty;
    public string UnusedReason { get; set; } = string.Empty;
}

public class GroupedHotspotResult
{
    public SymbolResult Symbol { get; set; } = new();
    public int ReferenceCount { get; set; }
    public int DefinitionSites { get; set; }
    public List<string> Paths { get; set; } = [];
}

public class FileResult
{
    public string Path { get; set; } = string.Empty;
    public string? Lang { get; set; }
    public long Size { get; set; }
    public int Lines { get; set; }
    public int SymbolCount { get; set; }
    public int ReferenceCount { get; set; }
    public string? Checksum { get; set; }
    public DateTime? Modified { get; set; }
    public DateTime? IndexedAt { get; set; }
}

public class FileExcerptResult
{
    public string Path { get; set; } = string.Empty;
    public string? Lang { get; set; }
    public int StartLine { get; set; }
    public int EndLine { get; set; }
    public string Content { get; set; } = string.Empty;
    public bool ContentTruncated { get; set; }
}

public class FileFindResult
{
    public string Path { get; set; } = string.Empty;
    public string? Lang { get; set; }
    public int Line { get; set; }
    public int Column { get; set; }
    public int StartLine { get; set; }
    public int EndLine { get; set; }
    public string Snippet { get; set; } = string.Empty;
    public bool SnippetTruncated { get; set; }
}

public class IndexFreshnessCheckResult
{
    public bool Checked { get; set; }
    [JsonPropertyName("matches_workspace")]
    public bool MatchesWorkspace { get; set; }
    public string Reason { get; set; } = string.Empty;
    public int IndexedFileCount { get; set; }
    public int WorkspaceFileCount { get; set; }
    public int MatchedFileCount { get; set; }
    public int ChangedFileCount { get; set; }
    public int MissingFileCount { get; set; }
    public int OutsideSparseConeFileCount { get; set; }
    public int UnindexedFileCount { get; set; }
    public int UnverifiableFileCount { get; set; }
    public int ScanErrorCount { get; set; }
    public List<string> ChangedFiles { get; set; } = [];
    public List<string> MissingFiles { get; set; } = [];
    public List<string> OutsideSparseConeFiles { get; set; } = [];
    public List<string> UnindexedFiles { get; set; } = [];
    public List<string> UnverifiableFiles { get; set; } = [];
    public List<string> ScanErrors { get; set; } = [];
    [JsonIgnore(Condition = JsonIgnoreCondition.WhenWritingNull)]
    public string? IndexedHeadCommit { get; set; }
    [JsonIgnore(Condition = JsonIgnoreCondition.WhenWritingNull)]
    public string? WorkspaceHeadCommit { get; set; }
    public bool HeadChanged { get; set; }
}

public class DefinitionResult : SymbolResult
{
    public string Content { get; set; } = string.Empty;
    public string? BodyContent { get; set; }
    public int? Complexity { get; set; }
}

public class ExactZeroHintResult
{
    [JsonIgnore(Condition = JsonIgnoreCondition.WhenWritingNull)]
    public int? RelaxedCount { get; set; }
    public const string DefaultSuggestion = "drop --exact or use the exact indexed name";
    public List<string> SampleNames { get; set; } = [];
    public string Suggestion { get; set; } = DefaultSuggestion;

    public static ExactZeroHintResult? FromRelaxedMatches(int relaxedCount, IEnumerable<string?> names, int sampleLimit = 5)
    {
        if (relaxedCount <= 0)
            return null;

        var sampleNames = names
            .Where(name => !string.IsNullOrWhiteSpace(name))
            .Distinct(StringComparer.Ordinal)
            .Take(sampleLimit)
            .Select(name => name!)
            .ToList();

        return new ExactZeroHintResult
        {
            RelaxedCount = relaxedCount,
            SampleNames = sampleNames,
            Suggestion = DefaultSuggestion,
        };
    }
}

public class FreshnessHintResult
{
    public long FileCount { get; set; }
    public DateTime? IndexedAt { get; set; }
    public bool FreshnessAvailable { get; set; }
    public string? FreshnessDegradedReason { get; set; }
}

public class ReferenceResult
{
    public string Path { get; set; } = string.Empty;
    public string? Lang { get; set; }
    public string SymbolName { get; set; } = string.Empty;
    public string ReferenceKind { get; set; } = string.Empty;
    public int Line { get; set; }
    public int Column { get; set; }
    [JsonIgnore]
    public string RawContext { get; set; } = string.Empty;
    public string Context { get; set; } = string.Empty;
    public bool ContextTruncated { get; set; }
    public string? ContainerKind { get; set; }
    public string? ContainerName { get; set; }
}

public class CallerResult
{
    public string Path { get; set; } = string.Empty;
    public string? Lang { get; set; }
    public string? CallerKind { get; set; }
    public string? CallerName { get; set; }
    public string CalleeName { get; set; } = string.Empty;
    // Summary preferred reference_kind for the grouped row. Grouped caller rows can
    // collapse multiple underlying kinds into one label, so JSON/MCP consumers that
    // need the full picture should read ReferenceKinds + HasMixedReferenceKinds as
    // well (issue #501). The scalar is kept for back-compat with existing consumers.
    // グループ化された行は複数の reference_kind を 1 ラベルに畳むため、
    // JSON/MCP で全体を把握するには ReferenceKinds と HasMixedReferenceKinds を
    // 併読する（issue #501）。scalar は既存 consumer の後方互換のため残す。
    public string ReferenceKind { get; set; } = string.Empty;
    public IReadOnlyList<string> ReferenceKinds { get; set; } = Array.Empty<string>();
    public bool HasMixedReferenceKinds { get; set; }
    public int FirstLine { get; set; }
    public int ReferenceCount { get; set; }
}

public class CalleeResult
{
    public string Path { get; set; } = string.Empty;
    public string? Lang { get; set; }
    public string? CallerKind { get; set; }
    public string? CallerName { get; set; }
    public string CalleeName { get; set; } = string.Empty;
    public string ReferenceKind { get; set; } = string.Empty;
    public IReadOnlyList<string> ReferenceKinds { get; set; } = Array.Empty<string>();
    public bool HasMixedReferenceKinds { get; set; }
    public int FirstLine { get; set; }
    public int ReferenceCount { get; set; }
}

public class ImpactResult
{
    public string Path { get; set; } = string.Empty;
    public string? Lang { get; set; }
    public string? CallerKind { get; set; }
    public string? CallerName { get; set; }
    public string CalleeName { get; set; } = string.Empty;
    public int Depth { get; set; }
    public int FirstLine { get; set; }
    public int ReferenceCount { get; set; }
}

public class ImpactAnalysisResult
{
    public string Query { get; set; } = string.Empty;
    public string ResolvedName { get; set; } = string.Empty;
    public string ImpactMode { get; set; } = "callers";
    public bool Heuristic { get; set; }
    public int MaxDepth { get; set; }
    public int DefinitionCount { get; set; }
    public int DefinitionFileCount { get; set; }
    public int HintCount { get; set; }
    public bool HasClassLikeDefinitions { get; set; }
    public bool HasMultipleDefinitions { get; set; }
    public bool HasMultipleDefinitionFiles { get; set; }
    public List<SymbolResult> Definitions { get; set; } = [];
    public List<ImpactResult> Callers { get; set; } = [];
    public List<FileDependencyResult> FileImpacts { get; set; } = [];
    public bool Truncated { get; set; }
    public bool GraphTableAvailable { get; set; } = true;
    [JsonIgnore(Condition = JsonIgnoreCondition.WhenWritingNull)]
    public string? ZeroResultReason { get; set; }
    [JsonIgnore(Condition = JsonIgnoreCondition.WhenWritingNull)]
    public string? Suggestion { get; set; }
}

public class StatusResult
{
    public long Files { get; set; }
    public long Chunks { get; set; }
    public long Symbols { get; set; }
    public long References { get; set; }
    public DateTime? IndexedAt { get; set; }
    public DateTime? LatestModified { get; set; }
    public string? ProjectRoot { get; set; }
    public string? GitHead { get; set; }
    public bool? GitIsDirty { get; set; }
    /// <summary>
    /// Git HEAD commit captured at the end of the most recent successful full-scan
    /// index run (Issue #1508). Compared with the runtime `GitHead` to surface a
    /// worktree branch / HEAD switch that silently invalidates the on-disk index
    /// without requiring a `--check` workspace scan. Null when the DB has no
    /// `indexed_head_commit` meta (legacy DBs or projects indexed outside a git
    /// checkout). Issues #1508 / #1512.
    /// 直近 full-scan 成功時点で記録された git HEAD。runtime の `GitHead` と突き合わせ、
    /// `--check` を介さずに worktree 内の branch / HEAD 切替を検出する。
    /// </summary>
    [JsonPropertyName("indexed_head_commit")]
    [JsonIgnore(Condition = JsonIgnoreCondition.WhenWritingNull)]
    public string? IndexedHeadCommit { get; set; }
    /// <summary>
    /// True when the persisted `IndexedHeadCommit` differs from the runtime `GitHead`,
    /// indicating that the index was built against a different branch / commit and a
    /// re-index is needed to keep results trustworthy. Null when comparison is not
    /// possible (no persisted head, no runtime head). Issue #1512.
    /// 永続 HEAD と runtime HEAD が異なれば true。比較不能なら null。
    /// </summary>
    [JsonPropertyName("worktree_head_changed")]
    [JsonIgnore(Condition = JsonIgnoreCondition.WhenWritingNull)]
    public bool? WorktreeHeadChanged { get; set; }
    public Dictionary<string, long> Languages { get; set; } = new();
    public Dictionary<string, long>? SymbolKinds { get; set; }
    public List<string>? GraphSupportedLanguages { get; set; }
    public string? Version { get; set; }
    /// <summary>
    /// One-line human-readable summary for quick orientation.
    /// クイックオリエンテーション用の1行サマリー。
    /// </summary>
    public string? Summary { get; set; }
    [JsonPropertyName("index_matches_workspace")]
    [JsonIgnore(Condition = JsonIgnoreCondition.WhenWritingNull)]
    public bool? IndexMatchesWorkspace { get; set; }
    [JsonPropertyName("workspace_check")]
    [JsonIgnore(Condition = JsonIgnoreCondition.WhenWritingNull)]
    public IndexFreshnessCheckResult? WorkspaceCheck { get; set; }
    /// <summary>
    /// True when the index exposes the full reference / validation tables. False signals a
    /// degraded read (legacy or read-only DB where TryMigrateForRead could not create
    /// symbol_references / file_issues), so a zero reference or issue count must not be
    /// trusted as a real "no callers" or "clean" signal.
    /// インデックスに参照／検証テーブルが揃っているかの信頼シグナル。false の場合、references
    /// や issues の 0 件は「本当に 0 件」なのか「テーブルが無いから 0 件」なのか区別できない。
    /// </summary>
    public bool GraphTableAvailable { get; set; } = true;
    public bool IssuesTableAvailable { get; set; } = true;
    /// <summary>
    /// True when authoritative cross-file hotspot-family grouping metadata is current for every
    /// marker-capable language currently indexed in this DB. False means `hotspots` can still
    /// run, but duplicate-name families may be conservatively degraded until `cdidx index .`
    /// restamps the hotspot-family metadata.
    /// 現在 index 済みの marker-capable 言語すべてで authoritative な hotspot-family metadata
    /// が最新なら true。false の間も `hotspots` は動くが、duplicate-name family は保守的
    /// fallback に縮退しうるため、`cdidx index .` で metadata を restamp する必要がある。
    /// </summary>
    [JsonPropertyName("hotspot_family_ready")]
    public bool HotspotFamilyReady { get; set; } = true;
    [JsonPropertyName("hotspot_family_degraded_reason")]
    [JsonIgnore(Condition = JsonIgnoreCondition.WhenWritingNull)]
    public string? HotspotFamilyDegradedReason { get; set; }
    /// <summary>
    /// True when C# canonical symbol-name upgrades (for operators, conversion operators,
    /// indexers) have been applied to all indexed C# rows in this DB. False means exact-name
    /// lookup for those C# symbol families may still require an upgrade pass via `cdidx index .`.
    /// C# canonical symbol name 契約が DB 全体へ適用済みかどうか。
    /// </summary>
    [JsonPropertyName("csharp_symbol_name_ready")]
    public bool CSharpSymbolNameReady { get; set; } = true;
    /// <summary>
    /// True when every indexed C# class row carries an authoritative `is_metadata_target`
    /// value stamped under the current `metadata_target_version_csharp` contract. False
    /// means the `deps` / `impact` metadata-attribute edges fall back to the legacy
    /// `signature LIKE '%: %'` heuristic (or the `name LIKE '%Attribute'` suffix heuristic
    /// on truly-legacy DBs missing the `is_metadata_target` column), which silently drops
    /// impostor classes like `class FooAttribute : BaseService`. Run `cdidx index .` once
    /// to let the authoritative resolver rewrite the stamp (#435).
    /// true のとき deps / impact の metadata-attribute edge は persisted な
    /// `is_metadata_target` 列を使い、false のとき legacy heuristic 経路で縮退する。
    /// </summary>
    [JsonPropertyName("csharp_metadata_target_ready")]
    public bool CSharpMetadataTargetReady { get; set; } = true;
    /// <summary>
    /// True when every indexed SQL graph row was written under the current stored call-column /
    /// qualified-name contract. False means SQL graph/dependency readers may still return false
    /// negatives until `cdidx index .` rewrites unchanged SQL rows.
    /// SQL graph 行が current の call-column / qualified-name 契約で書かれていれば true。
    /// </summary>
    [JsonPropertyName("sql_graph_contract_ready")]
    public bool SqlGraphContractReady { get; set; } = true;
    [JsonPropertyName("sql_graph_contract_degraded_reason")]
    [JsonIgnore(Condition = JsonIgnoreCondition.WhenWritingNull)]
    public string? SqlGraphContractDegradedReason { get; set; }
    /// <summary>
    /// True when every row in symbols / symbol_references has its name_folded column
    /// populated AND the FoldReadyFlag bit is set on the DB. False means `--exact` still
    /// falls back to ASCII `COLLATE NOCASE` (non-ASCII casing pairs like Ä/ä won't match).
    /// AI clients should prefer `cdidx backfill-fold` to upgrade an older DB without
    /// reparsing files; `cdidx index . --rebuild` remains the full-rescan fallback.
    /// true のとき --exact は Unicode fold 経路、false のとき ASCII NOCASE fallback。
    /// </summary>
    public bool FoldReady { get; set; }
    [JsonPropertyName("fold_ready_reason")]
    [JsonIgnore(Condition = JsonIgnoreCondition.WhenWritingNull)]
    public string? FoldReadyReason { get; set; }
    [JsonPropertyName("degraded_reason")]
    [JsonIgnore(Condition = JsonIgnoreCondition.WhenWritingNull)]
    public string? DegradedReason { get; set; }
    [JsonPropertyName("recommended_action")]
    [JsonIgnore(Condition = JsonIgnoreCondition.WhenWritingNull)]
    public string? RecommendedAction { get; set; }
    [JsonPropertyName("alternative_action")]
    [JsonIgnore(Condition = JsonIgnoreCondition.WhenWritingNull)]
    public string? AlternativeAction { get; set; }
}

public class RepoMapResult
{
    public int FileCount { get; set; }
    public long TotalLines { get; set; }
    public long TotalSymbols { get; set; }
    public long TotalReferences { get; set; }
    public DateTime? IndexedAt { get; set; }
    public DateTime? LatestModified { get; set; }
    public DateTime? WorkspaceIndexedAt { get; set; }
    public DateTime? WorkspaceLatestModified { get; set; }
    public string? ProjectRoot { get; set; }
    public string? GitHead { get; set; }
    public bool? GitIsDirty { get; set; }
    [JsonPropertyName("indexed_head_commit")]
    [JsonIgnore(Condition = JsonIgnoreCondition.WhenWritingNull)]
    public string? IndexedHeadCommit { get; set; }
    [JsonPropertyName("worktree_head_changed")]
    [JsonIgnore(Condition = JsonIgnoreCondition.WhenWritingNull)]
    public bool? WorktreeHeadChanged { get; set; }
    public List<RepoLanguageResult> Languages { get; set; } = [];
    public List<RepoModuleResult> Modules { get; set; } = [];
    public List<RepoFileSummaryResult> TopFiles { get; set; } = [];
    public List<RepoFileSummaryResult> LargestFiles { get; set; } = [];
    public List<RepoFileSummaryResult> SymbolRichFiles { get; set; } = [];
    public List<RepoFileSummaryResult> ReferenceRichFiles { get; set; } = [];
    public List<RepoEntrypointResult> Entrypoints { get; set; } = [];
    /// <summary>
    /// False when reference-derived aggregates (TotalReferences, per-file / per-module /
    /// per-language reference counts, ReferenceRichFiles) were synthesized as 0 because the
    /// graph table was missing (legacy / read-only DB). Callers must not rank or prioritize
    /// based on reference counts when this is false — the repo may actually be reference-rich.
    /// 参照系集計が欠損テーブルによりゼロ合成されている場合 false。ランキングに使わないこと。
    /// </summary>
    public bool GraphTableAvailable { get; set; } = true;
}

public class RepoLanguageResult
{
    public string Lang { get; set; } = string.Empty;
    public int Files { get; set; }
    public long Lines { get; set; }
    public long Symbols { get; set; }
    public long References { get; set; }
}

public class RepoModuleResult
{
    public string Module { get; set; } = string.Empty;
    public int Files { get; set; }
    public long Lines { get; set; }
    public long Symbols { get; set; }
    public long References { get; set; }
}

public class RepoFileSummaryResult
{
    public string Path { get; set; } = string.Empty;
    public string? Lang { get; set; }
    public int Lines { get; set; }
    public long Size { get; set; }
    public int SymbolCount { get; set; }
    public int ReferenceCount { get; set; }
    public long? Score { get; set; }
}

public class RepoEntrypointResult
{
    public string Path { get; set; } = string.Empty;
    public string? Lang { get; set; }
    public string Kind { get; set; } = string.Empty;
    public string Name { get; set; } = string.Empty;
    public int Line { get; set; }
    public int Score { get; set; }
}

public class SymbolAnalysisResult
{
    public string Query { get; set; } = string.Empty;
    public FileResult? File { get; set; }
    public DateTime? WorkspaceIndexedAt { get; set; }
    public DateTime? WorkspaceLatestModified { get; set; }
    public string? ProjectRoot { get; set; }
    public string? GitHead { get; set; }
    public bool? GitIsDirty { get; set; }
    [JsonPropertyName("indexed_head_commit")]
    [JsonIgnore(Condition = JsonIgnoreCondition.WhenWritingNull)]
    public string? IndexedHeadCommit { get; set; }
    [JsonPropertyName("worktree_head_changed")]
    [JsonIgnore(Condition = JsonIgnoreCondition.WhenWritingNull)]
    public bool? WorktreeHeadChanged { get; set; }
    public string? GraphLanguage { get; set; }
    public bool? GraphSupported { get; set; }
    public string? GraphSupportReason { get; set; }
    [JsonIgnore(Condition = JsonIgnoreCondition.WhenWritingNull)]
    public bool? GraphDegraded { get; set; }
    [JsonIgnore(Condition = JsonIgnoreCondition.WhenWritingNull)]
    public string? UnsupportedSymbolKind { get; set; }
    public List<DefinitionResult> Definitions { get; set; } = [];
    public List<SymbolResult> NearbySymbols { get; set; } = [];
    public List<ReferenceResult> References { get; set; } = [];
    public List<CallerResult> Callers { get; set; } = [];
    public List<CalleeResult> Callees { get; set; } = [];
    /// <summary>
    /// False when the index does not contain the reference table (legacy / read-only DB),
    /// meaning empty References / Callers / Callees are degraded — not a true "no callers".
    /// インデックスに参照テーブルが無いと true / false で区別可能。空が本物かどうか見極める。
    /// </summary>
    public bool GraphTableAvailable { get; set; } = true;
    /// <summary>
    /// True when bundled SQL graph-backed reads in this analysis reflect the current
    /// call-column / qualified-name contract.
    /// bundle 内の SQL graph 読み取りが current 契約に揃っているかどうか。
    /// </summary>
    [JsonIgnore(Condition = JsonIgnoreCondition.WhenWritingNull)]
    public bool? SqlGraphContractReady { get; set; }
    [JsonIgnore(Condition = JsonIgnoreCondition.WhenWritingNull)]
    public string? SqlGraphContractDegradedReason { get; set; }
    [JsonIgnore(Condition = JsonIgnoreCondition.WhenWritingNull)]
    public ExactZeroHintResult? ExactZeroHint { get; set; }
    /// <summary>
    /// True when every active `--exact` sub-query in the bundle can use its supporting indexes.
    /// False means the bundled result still returns correct hits, but at least one exact
    /// sub-query degraded to a slower legacy fallback path.
    /// bundle 内の `--exact` sub-query がすべて対応 index を使えるか。false でも結果は正しい。
    /// </summary>
    [JsonIgnore(Condition = JsonIgnoreCondition.WhenWritingNull)]
    public bool? ExactIndexAvailable { get; set; }
    [JsonIgnore]
    public bool? ExactHasMissingIndex { get; set; }
    [JsonIgnore]
    public bool? ExactHasMissingTable { get; set; }
    [JsonIgnore(Condition = JsonIgnoreCondition.WhenWritingNull)]
    public string? DegradedReason { get; set; }
}

/// <summary>
/// Structured symbol outline for a single file.
/// 1ファイルの構造化シンボルアウトライン。
/// </summary>
public class OutlineResult
{
    public string Path { get; set; } = string.Empty;
    public string? Lang { get; set; }
    public int TotalLines { get; set; }
    public int SymbolCount { get; set; }
    public List<OutlineSymbol> Symbols { get; set; } = [];
}

public class OutlineSymbol
{
    public string Kind { get; set; } = string.Empty;
    public string Name { get; set; } = string.Empty;
    public int Line { get; set; }
    public int StartLine { get; set; }
    public int EndLine { get; set; }
    public int Depth { get; set; }
    public int? BodyStartLine { get; set; }
    public int? BodyEndLine { get; set; }
    public string? Signature { get; set; }
    public string? ContainerKind { get; set; }
    public string? ContainerName { get; set; }
    public string? Visibility { get; set; }
    public string? ReturnType { get; set; }
}

internal sealed class RepoFileStat
{
    public string Path { get; set; } = string.Empty;
    public string? Lang { get; set; }
    public string? ModuleName { get; set; }
    public long Size { get; set; }
    public int Lines { get; set; }
    public int SymbolCount { get; set; }
    public int ReferenceCount { get; set; }
    public string? Checksum { get; set; }
    public DateTime? Modified { get; set; }
    public DateTime? IndexedAt { get; set; }
}

public class FileDependencyResult
{
    public string SourcePath { get; set; } = string.Empty;
    public string TargetPath { get; set; } = string.Empty;
    public int ReferenceCount { get; set; }
    public string Symbols { get; set; } = string.Empty;
}

using CodeIndex.Cli;
using CodeIndex.Diagnostics;
using CodeIndex.Indexer;
using CodeIndex.Models;
using Microsoft.Data.Sqlite;
using System.Globalization;
using System.Runtime.ExceptionServices;

namespace CodeIndex.Database;

public partial class DbContext : IDisposable
{
    /// <summary>
    /// Initialize the database schema (tables, indexes, FTS).
    /// データベーススキーマ（テーブル、インデックス、FTS）を初期化する。
    /// </summary>
    // Readiness bitmap stamped into PRAGMA user_version at the end of a successful index.
    // Split so the CLI (graph + issues) and MCP (graph only, no validation pass) can mark
    // different subsets of trust independently.
    // index の成功末尾で user_version に打つビットマップ。CLI と MCP が独立に立てる。
    public const int GraphReadyFlag = 1;
    public const int IssuesReadyFlag = 2;
    // bit 2 (FoldReadyFlag, #86) — name_folded columns (Unicode NFKC + lowerInvariant) fully
    // backfilled on symbols and symbol_references. Set only after a full scan populates every
    // row's folded value so `--exact` queries can use the folded index path for Unicode
    // casing (Ä/ä). Legacy DBs without fold stay on the COLLATE NOCASE fallback until reindex.
    // bit 2 (FoldReadyFlag, #86): name_folded 列の完全バックフィル完了を示す。
    public const int FoldReadyFlag = 4;
    // bit 3 permanently protects the maintained hotspot aggregate from older writers that do not
    // update it. bit 4 is the transient trust signal: reference mutations clear it before changing
    // raw rows and restore it only after the aggregate is synchronized. ClearReadyFlags preserves
    // both aggregate bits because ordinary index-run readiness changes do not invalidate the counts.
    // bit 3 は旧 writer から maintained aggregate を永続的に保護し、bit 4 は同期状態を示す。
    public const int HotspotReferenceAggregateStorageContractFlag = 8;
    public const int HotspotReferenceAggregateReadyFlag = 16;
    public const int HotspotReferenceAggregateFlags =
        HotspotReferenceAggregateStorageContractFlag | HotspotReferenceAggregateReadyFlag;
    public const int CurrentSchemaVersion =
        GraphReadyFlag | IssuesReadyFlag | FoldReadyFlag | HotspotReferenceAggregateFlags; // 31
    public const int CodeIndexMetaSchemaVersion = 1;
    public const string CodeIndexMetaSchemaVersionMetaKey = "codeindex_meta_schema_version";
    // Query-semantic readiness for hotspot family grouping. Stored in codeindex_meta instead of
    // PRAGMA user_version because this guards a higher-level interpretation contract
    // (`family_key` / `container_qualified_name` are authoritative for the whole DB), not
    // low-level table availability.
    // hotspots family grouping 用 readiness。table の有無ではなく query 意味論の trust を表す。
    public const int HotspotFamilyVersion = 2;
    public const string HotspotFamilyVersionMetaKey = "hotspot_family_version";
    public const string HotspotFamilyMarkerFingerprintMetaKey = "hotspot_family_marker_fingerprint";
    public const string HotspotFamilyIncompleteMarkerFingerprintPrefix = "incomplete:";
    public static string GetHotspotFamilyVersionMetaKey(string lang) => $"hotspot_family_version_{lang}";
    public static string GetHotspotFamilyMarkerFingerprintMetaKey(string lang) => $"hotspot_family_marker_fingerprint_{lang}";
    public static bool IsIncompleteHotspotFamilyMarkerFingerprint(string? fingerprint)
        => !string.IsNullOrWhiteSpace(fingerprint)
           && fingerprint.StartsWith(HotspotFamilyIncompleteMarkerFingerprintPrefix, StringComparison.Ordinal);
    public static string BuildIncompleteHotspotFamilyMarkerFingerprint(string? fingerprint)
        => HotspotFamilyIncompleteMarkerFingerprintPrefix + (string.IsNullOrWhiteSpace(fingerprint) ? "unknown" : fingerprint);
    public const int CSharpSymbolNameContractVersion = 2;
    public const string CSharpSymbolNameContractVersionMetaKey = "csharp_symbol_name_contract_version";
    public const string CSharpStaticInterfaceSourceEvidenceMetaKey = "csharp_static_interface_source_evidence";
    public const int SqlGraphContractVersion = 1;
    public const string SqlGraphContractVersionMetaKey = "sql_graph_contract_version";
    public const int HdlGraphContractVersion = 1;
    public const string HdlGraphContractVersionMetaKey = "hdl_graph_contract_version";
    // Version 5 (#4846) invalidates name-only Markdown candidates written before
    // fragment resolution became document/path-scoped. Version 4 (#4845) previously
    // invalidated dependency-lock candidates written before resolution became file-local,
    // and version 3 (#4825) constrained C# type-reference candidates by kind and arity.
    // バージョン 5 (#4846) では、Markdown fragment の解決を document/path 内に限定する
    // 前に書かれた name-only candidate を無効化する。バージョン 4 (#4845) では、それ以前の
    // dependency-lock candidate を file 内に限定し、バージョン 3 (#4825) では C# 型参照を
    // kind と arity で制約した。
    public const int ReferenceIdentityContractVersion = 5;
    public const string ReferenceIdentityContractVersionMetaKey = "reference_identity_contract_version";
    public static string GetDynamicReferenceGraphContractVersionMetaKey(string lang) =>
        $"dynamic_reference_graph_contract_version_{lang}";
    public const string SymbolsOnlyGraphOmittedMetaKey = "symbols_only_graph_omitted";
    public const string IndexedProjectRootMetaKey = "indexed_project_root";
    public const string IndexedFollowSymlinksPolicyMetaKey = "indexed_follow_symlinks_policy";
    // Git HEAD commit captured at the end of the most recent full-scan index run (`--rebuild` or
    // the default incremental full scan). Reading this back lets the CLI detect that a user
    // ran `cdidx index <projectPath>` after switching branches / commits, where the DB still
    // mirrors the previously-indexed worktree even though the on-disk file set has diverged.
    // Partial update modes (`--commits` / `--files`) deliberately do NOT touch this key, so a
    // post-branch-switch partial refresh still surfaces as stale until a real full scan
    // republishes the captured HEAD. The same value is read at `status` time (without
    // `--check`) to surface a worktree branch / HEAD switch via `worktree_head_changed`.
    // Issues #1508 and #1512.
    // 直近の full-scan 成功時点で記録した git HEAD。`cdidx index` 後にブランチが切り替わると
    // DB は旧 worktree のスナップショットのまま残るため、ここを比較して「rebuild を勧める」
    // 警告を出す。partial update (`--commits` / `--files`) は本キーを更新せず、後続の
    // full scan が改めて記録する。同じ値を `status` (no `--check`) でも参照し、
    // `worktree_head_changed` として worktree の HEAD 切替を素早く通知する。Issues #1508 / #1512。
    public const string IndexedHeadCommitMetaKey = "indexed_head_commit";
    public const string IndexedHeadCommitBranchMetaKey = "indexed_head_commit_branch";
    // #1509: full Git HEAD commit and short branch name captured at the end of every
    // successful index run (full scan AND partial update), plus the UTC timestamp of that
    // stamp. Together they let `status` (and any future cross-session staleness check)
    // decide whether the index was built against the commit currently checked out, or
    // whether the working tree has advanced since indexing. This is DIFFERENT from
    // `IndexedHeadCommitMetaKey` above (#1508): that key only fires on full scans so it
    // can drive "rebuild after branch switch" warnings, while these keys fire on every
    // successful index so `commits_ahead_of_indexed_head` reflects the true last-touched
    // HEAD regardless of update mode. Stored as plain strings to keep DbReader's inline
    // codeindex_meta lookup degradation behavior intact on legacy / read-only DBs.
    // #1509: 成功 index (full scan / partial 問わず) の終端で HEAD commit / branch 名 /
    // stamp 時刻を保存する。これにより status などが「DB の HEAD が現在の HEAD と何コミット
    // ズレているか」を検出できる。`IndexedHeadCommitMetaKey` (#1508) とは異なり、こちらは
    // partial update でも更新するため commits_ahead_of_indexed_head が常に正確になる。
    // codeindex_meta が無い legacy DB では reader 側で null フォールバックする。
    public const string IndexedHeadShaMetaKey = "indexed_head_sha";
    public const string IndexedHeadBranchMetaKey = "indexed_head_branch";
    public const string IndexedHeadTimestampMetaKey = "indexed_head_timestamp";
    public const string CommitScopedFreshHeadShaMetaKey = "commit_scoped_fresh_head_sha";
    public const string LastFullScanElapsedMsMetaKey = "last_full_scan_elapsed_ms";
    public const string LastIndexRunModeMetaKey = "last_index_run_mode";
    public const string LastIndexRunStartedAtMetaKey = "last_index_run_started_at";
    public const string LastIndexRunDurationMsMetaKey = "last_index_run_duration_ms";
    public const string LastIndexRunFilesScannedMetaKey = "last_index_run_files_scanned";
    public const string LastIndexRunFilesSkippedMetaKey = "last_index_run_files_skipped";
    public const string LastIndexRunParseErrorsMetaKey = "last_index_run_parse_errors";
    public const string LastIndexRunBytesReadMetaKey = "last_index_run_bytes_read";
    public const string LastIndexRunBytesReadSkippedFileCountMetaKey = "last_index_run_bytes_read_skipped_file_count";
    public const string LastIndexRunBytesReadIncompleteMetaKey = "last_index_run_bytes_read_incomplete";
    public const string LastIndexRunRowsUpsertedMetaKey = "last_index_run_rows_upserted";
    public const string LastIndexRunRowsDeletedMetaKey = "last_index_run_rows_deleted";
    public const string LastIndexRunPeakMemoryMbMetaKey = "last_index_run_peak_memory_mb";
    public const string LastIndexRunDiagnosticsMetaKey = "last_index_run_diagnostics_json";
    public const string LastIndexRunDiagnosticCountMetaKey = "last_index_run_diagnostic_count";
    public const string LastIndexRunDiagnosticsTruncatedMetaKey = "last_index_run_diagnostics_truncated";
    public const string LastIndexRunReferenceExtractionCapHitsMetaKey = "last_index_run_reference_extraction_cap_hits_json";
    public const int LastIndexRunDiagnosticSampleLimit = 50;
    public const string LastFailedIndexRunStatusMetaKey = "last_failed_index_run_status";
    public const string LastFailedIndexRunModeMetaKey = "last_failed_index_run_mode";
    public const string LastFailedIndexRunStartedAtMetaKey = "last_failed_index_run_started_at";
    public const string LastFailedIndexRunDurationMsMetaKey = "last_failed_index_run_duration_ms";
    public const string LastFailedIndexRunFilesProcessedMetaKey = "last_failed_index_run_files_processed";
    public const string LastFailedIndexRunFilesTotalMetaKey = "last_failed_index_run_files_total";
    public const string LastFailedIndexRunErrorCodeMetaKey = "last_failed_index_run_error_code";
    public const string LastFailedIndexRunReasonMetaKey = "last_failed_index_run_reason";
    public const string LastFailedIndexRunProgressPersistedMetaKey = "last_failed_index_run_progress_persisted";
    public const string LastFailedIndexRunRecoveryHintMetaKey = "last_failed_index_run_recovery_hint";
    public const string LastFailedIndexRunFileErrorsMetaKey = "last_failed_index_run_file_errors_json";
    public const string IndexCompletenessMetaKey = "index_completeness";
    public const string IndexIncompleteReasonsMetaKey = "index_incomplete_reasons_json";
    // Issue #1585: count of files seen by the most recent successful full-repository scan
    // whose non-empty extension did not map to a known language. This is a scan coverage
    // signal, not an indexed-file count, and is omitted by readers until a current index pass
    // has stamped it.
    // Issue #1585: 直近成功した全体 scan で、非空の拡張子が既知言語に対応しなかった
    // ファイル数。index 済み件数ではなく scan coverage の信号であり、現行 index が stamp
    // するまでは reader 側で省略する。
    public const string UnknownExtensionFileCountMetaKey = "unknown_extension_file_count";
    public const string UnknownExtensionFilePathsMetaKey = "unknown_extension_file_paths_json";
    public const string UnknownExtensionFilesTruncatedMetaKey = "unknown_extension_files_truncated";
    public const string UnknownExtensionFilePathLimitMetaKey = "unknown_extension_file_path_limit";
    public const string UnknownExtensionExtensionCountsMetaKey = "unknown_extension_extension_counts_json";
    public const string UnknownExtensionCategoryCountsMetaKey = "unknown_extension_category_counts_json";
    public const string UnknownExtensionGroupsMetaKey = "unknown_extension_groups_json";
    public const int UnknownExtensionFilePathSampleLimit = 50;
    public const string BatchInProgressMetaKey = "batch_in_progress";
    // Issue #1546: case-sensitivity of the workspace filesystem the most recent successful
    // index ran on, persisted as the string "true" / "false". Resolved via the probe in
    // `PathCasing` (which honors `core.ignorecase` when the project is a git workspace and
    // falls back to a per-volume probe otherwise) so case-sensitive APFS volumes on macOS,
    // case-sensitive NTFS via WSL, and case-sensitive ReFS no longer collapse onto the OS
    // family heuristic. Exposed back through `cdidx status` (`path_case_sensitive`) so
    // operators can diagnose phantom path collapses / missing-file reports.
    // #1546: 直近 index 時のワークスペース FS の大小区別を "true"/"false" で保存する。
    // OS 系列だけに依存していた既存ヒューリスティックでは case-sensitive APFS 等で
    // ファイルが誤って同一視されるため、`PathCasing` の実 FS プローブで判定し、
    // `cdidx status` の `path_case_sensitive` で診断できるようにする。
    public const string WorkspacePathCaseSensitiveMetaKey = "workspace_path_case_sensitive";
    // Authoritative `symbols.is_metadata_target` flag readiness, per language. Stamped at the
    // end of a successful index pass once extractor facts and the writer resolver have
    // classified every class-like row for that language. Readers fall back to the legacy
    // heuristic when the per-language stamp is absent or its version does not match. Issue #3524.
    // 言語別 metadata-target 列の正式 readiness。index 終端で extractor fact と writer resolver が
    // 当該言語の class-like 行を全部分類した後にだけ stamp する。stamp が無い・version 不一致の
    // 言語については reader が legacy ヒューリスティックにフォールバックする。Issue #3524。
    // Version 2 (#435 iter 5) made the writer-side resolver import-aware: unqualified base
    // identifiers now resolve through the deriving file's `using Namespace;` / `using Alias =
    // FQN;` directives (plus `global using` aggregated across the repo) before falling back
    // to the BCL `Attribute`-suffix convention. Iter 4 DBs that only resolved through the
    // deriving class's own scope chain would miss `using A; class FooAttribute : BaseAttr`
    // where `A.BaseAttr : Attribute` is indexed in a sibling file. Bumping the contract
    // forces those DBs to degrade to the legacy `signature LIKE '%: %'` reader path until a
    // reindex republishes `is_metadata_target`.
    // Version 3 (#435 iter 6) normalizes C# verbatim-identifier `@` prefixes on the writer
    // side so `using @Foo.@Bar;`, `using @AliasAttr = @Foo.@BaseAttr;`, and `class Foo :
    // @BaseAttr` resolve identically to their non-verbatim counterparts. Iter-5 DBs stored
    // the raw `@Foo.@Bar` token in the import map and never matched the qualified index,
    // leaving `VerbatimImportAttribute : BaseAttr` as `is_metadata_target=0` and dropping
    // the attribute-consumer edge from `deps` / `impact`. Bumping the contract degrades
    // iter-5 DBs to the legacy reader path until reindexed.
    // Version 4 (#435 iter 7) widens the C# namespace / class / struct / interface / enum
    // declaration regexes to accept verbatim identifiers (`public class @BaseAttr : Attribute`,
    // `namespace @Foo.@Bar`) and canonicalizes the persisted symbol name so the qualified
    // index keys off `BaseAttr` / `Foo.Bar` regardless of source syntax. Iter-6 DBs never
    // indexed verbatim class declarations at all (the extractor regex rejected them), so
    // every derived `class X : @BaseAttr` stayed `is_metadata_target=0` and dropped the
    // attribute edge even with iter-6's base-name stripping in place. Iter 7 also teaches
    // `StripCSharpVerbatimPrefixes` about the `::` boundary so `global::@Foo.@Bar.BaseAttr`
    // canonicalizes all the way to `global::Foo.Bar.BaseAttr` instead of leaving the first
    // `@` after `::` intact. Bumping the contract forces iter-6 DBs to degrade to the
    // legacy reader path until a reindex republishes `is_metadata_target`.
    // バージョン 2 (#435 iter 5)で resolver が import を考慮するようになった。非修飾な基底は
    // deriving ファイルの `using Namespace;` / `using Alias = FQN;`（および全ファイル集約の
    // `global using`）を通して解決してから BCL の `Attribute` サフィックス規約にフォールバック
    // する。iter 4 の DB は `using A; class FooAttribute : BaseAttr` のような一般的な C# パターンで
    // 正しく解決できないため、契約バージョンを上げて reader を legacy ヒューリスティックに縮退
    // させ、再 index で republish されるまで metadata edge を誤って主張させない。
    // バージョン 3 (#435 iter 6) で書き込み側が C# verbatim 識別子の `@` 先頭を正規化するよう
    // になった。`using @Foo.@Bar;` / `using @AliasAttr = @Foo.@BaseAttr;` / `class Foo :
    // @BaseAttr` が非 verbatim 形と同じキーで解決される。iter-5 DB は import map に生の
    // `@Foo.@Bar` を残していたため qualified 索引に当たらず、`VerbatimImportAttribute :
    // BaseAttr` が `is_metadata_target=0` となり attribute consumer 側の edge が落ちていた。
    // 契約バージョンを上げて、再 index 前の iter-5 DB を reader の legacy パスに縮退させる。
    // バージョン 4 (#435 iter 7) で C# の namespace / class / struct / interface / enum 宣言
    // 正規表現が verbatim 識別子（`public class @BaseAttr : Attribute` / `namespace
    // @Foo.@Bar`）を受理するようになり、永続化されるシンボル名も canonical 化される。qualified
    // 索引は `BaseAttr` / `Foo.Bar` としてキー付けされ、ソース表記に依らない。iter-6 DB は
    // verbatim class 宣言自体がインデックスされず（extractor の regex が弾いていた）、
    // `class X : @BaseAttr` のような派生は iter 6 の base 側 `@` 剥がしでも resolve できず
    // `is_metadata_target=0` のまま attribute edge が落ちていた。iter 7 では
    // `StripCSharpVerbatimPrefixes` も `::` 境界を処理するよう拡張し、`global::@Foo.@Bar.BaseAttr`
    // を `global::Foo.Bar.BaseAttr` まで完全に canonical 化する（iter 6 は `::` 直後の `@` を
    // 残していた）。契約バージョンを上げて iter-6 DB を reader の legacy パスに縮退させ、
    // 再 index で republish されるまで metadata edge を黙って誤るのを防ぐ。
    // Version 5 (#435 iter 8) teaches the resolver to expand alias-qualified bases
    // such as `using Alias = A; class FooAttribute : Alias.MetaBase` into
    // `A.MetaBase` before the qualified index lookup. Iter-5 only handled
    // alias-unqualified bases (`class Foo : Alias` where the whole base name is the
    // alias), and the qualified branch fell straight through to the BCL
    // `Attribute`-suffix heuristic — which misses any `MetaBase` real attribute in
    // the alias target namespace unless the derived class happens to be named
    // `...Attribute`. Iter-7 DBs that indexed without this expansion therefore
    // dropped every `[FooAttribute]` edge whose declaration used an alias-qualified
    // base, so the contract is bumped to force a re-index.
    // バージョン 5 (#435 iter 8) で resolver が alias 修飾された基底を展開するようになった。
    // `using Alias = A; class FooAttribute : Alias.MetaBase` の場合、qualified 索引を
    // `A.MetaBase` で引けるようになり、従来は alias 展開が無いまま BCL の `Attribute`
    // サフィックス規約までフォールバックしていたため、alias target 名前空間に居る本物の
    // `MetaBase : Attribute` が同 repo にあっても、派生クラス名が `...Attribute` で終わる
    // 偶然でしか metadata edge を張れなかった。iter-7 DB はこの展開なしで index された
    // ため alias-qualified 基底の edge が黙って落ちていた。契約バージョンを上げて再 index
    // を強制する。
    // Version 6 (#435 iter 9) extends alias-qualified expansion to the `::`
    // separator. C# accepts both `Alias.X` (member access) and `Alias::X`
    // (qualified-alias-member, §7.8) for using aliases that name a namespace,
    // and production code uses the `::` form to disambiguate namespaces from
    // type names. Iter-8 only split on `.` in the expansion helper, so
    // `class FooAttribute : Alias::MetaBase` still fell through to the BCL
    // suffix heuristic and dropped the `[FooAttribute]` edge. Iter-8 DBs that
    // indexed without this expansion must degrade to the legacy reader path
    // until a reindex republishes `is_metadata_target` with `::`-aware
    // resolution.
    // バージョン 6 (#435 iter 9) で alias 修飾展開が `::` 区切りにも対応した。C# では
    // using alias が名前空間を指す場合、`Alias.X`（メンバ アクセス）と `Alias::X`
    // （qualified-alias-member、§7.8）のどちらも許容され、現場コードは名前空間と型
    // 名を衝突させないために `::` を使うことがある。iter-8 の展開 helper は `.` のみで
    // 区切っていたため `class FooAttribute : Alias::MetaBase` は BCL サフィックス規約
    // まで抜け落ち、`[FooAttribute]` の edge が落ちていた。iter-8 DB はこの展開なしで
    // index されたため、再 index で `::` 対応の resolver が `is_metadata_target` を
    // republish するまで reader を legacy 経路へ縮退させる。
    // Version 7 (#3524) persists metadata-target provenance in
    // `symbols.metadata_target_source` so readers and diagnostics can tell direct extractor
    // facts from writer-resolved transitive targets. Iter-6 DBs only stored the flattened
    // `is_metadata_target` bit, so they must degrade until reindexed with source-aware
    // storage.
    // バージョン 7 (#3524) で `symbols.metadata_target_source` に provenance を保存する。
    // extractor が直接検出した fact と writer が推移的に解決した target を reader / diagnostics
    // が区別できるようにするため、平坦な `is_metadata_target` だけを持つ iter-6 DB は
    // source-aware storage で再 index されるまで縮退させる。
    public const int MetadataTargetVersion = 7;
    public static string GetMetadataTargetVersionMetaKey(string lang) => $"metadata_target_version_{lang}";
    public const int TypeScriptAugmentationVersion = 1;
    public const string TypeScriptAugmentationVersionMetaKey = "typescript_augmentation_version";
    // Audit trail: cdidx version string (e.g. "1.22.0") that produced the most recent
    // successful end-of-index pass on this DB. Readers use it to surface "DB written by
    // a newer cdidx" warnings when any persisted contract version exceeds this binary's
    // compiled max so silent rollback / mixed-version-team degradation becomes visible.
    // Issue #1515.
    // 監査用: 成功 index の末尾に書き込んだ cdidx の version 文字列。reader はここと
    // 各種 contract version の比較で「より新しい cdidx が書いた DB」を検知し、
    // 黙って縮退するのではなく status で警告するために利用する。Issue #1515。
    public const string CdidxWriterVersionMetaKey = "cdidx_writer_version";

    public int GetUserVersion()
    {
        using var cmd = SqliteConnectionPolicy.CreateCommand(_connection);
        cmd.CommandText = "PRAGMA user_version";
        var result = cmd.ExecuteScalar();
        return result is long l ? (int)l : (result is int i ? i : 0);
    }

    private void MarkHotspotReferenceAggregateReady()
    {
        var next = GetUserVersion() | HotspotReferenceAggregateFlags;
        Execute($"PRAGMA user_version = {next}");
    }

    // Reset readiness bits. Called at the START of every index run so an interrupted run
    // on an already-stamped DB demotes the trust signal to degraded until the end-of-run
    // stamp is written on fully successful completion.
    // index 開始時にビットをクリア。途中で落ちた場合は縮退状態のまま残す。
    public void ClearReadyFlags()
    {
        var aggregateContractBits = GetUserVersion() & HotspotReferenceAggregateFlags;
        Execute($"PRAGMA user_version = {aggregateContractBits}");
    }

    /// <summary>
    /// Read a string value from `codeindex_meta`. Returns null when absent or the table
    /// hasn't been created (legacy DBs, read-only sandboxes where migration was skipped).
    /// codeindex_meta からの読み取り。テーブル未作成や未登録キーは null を返す。
    /// </summary>
    public string? GetMetaString(string key)
    {
        if (!TableExists("codeindex_meta")) return null;
        using var cmd = SqliteConnectionPolicy.CreateCommand(_connection);
        cmd.CommandText = "SELECT value FROM codeindex_meta WHERE key = @key";
        SqliteCommandPolicy.Add(cmd, "@key", key);
        var raw = cmd.ExecuteScalar();
        return raw is string s ? s : null;
    }

    public IReadOnlyDictionary<string, string?> GetMetaStrings(IReadOnlyList<string> keys)
    {
        var values = new Dictionary<string, string?>(keys.Count, StringComparer.Ordinal);
        foreach (var key in keys)
            values[key] = null;

        if (keys.Count == 0 || !TableExists("codeindex_meta"))
            return values;

        var parameterNames = new string[keys.Count];
        for (var i = 0; i < keys.Count; i++)
            parameterNames[i] = "@key" + i.ToString(CultureInfo.InvariantCulture);

        using var cmd = SqliteConnectionPolicy.CreateCommand(_connection);
        cmd.CommandText = "SELECT key, value FROM codeindex_meta WHERE key IN (" + string.Join(", ", parameterNames) + ")";
        for (var i = 0; i < keys.Count; i++)
            SqliteCommandPolicy.Add(cmd, parameterNames[i], keys[i]);

        using var reader = cmd.ExecuteReader();
        while (reader.Read())
        {
            var key = reader.GetString(0);
            values[key] = reader.IsDBNull(1) ? null : reader.GetString(1);
        }

        return values;
    }

    public bool TryValidateIsCodeIndexDb(out string? reason)
    {
        var requiredTables = new[] { "files", "symbols" };
        foreach (var table in requiredTables)
        {
            if (!TableExists(table))
            {
                reason = $"missing required table `{table}`";
                return false;
            }
        }

        reason = null;
        return true;
    }

    private bool TableExists(string name)
    {
        using var cmd = SqliteConnectionPolicy.CreateCommand(_connection);
        cmd.CommandText = "SELECT 1 FROM sqlite_master WHERE type = 'table' AND name = @name";
        SqliteCommandPolicy.Add(cmd, "@name", name);
        return cmd.ExecuteScalar() != null;
    }

}

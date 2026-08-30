# CodeIndex DB Compatibility

> **[日本語版はこちら / Japanese version](#codeindex-db-compatibility日本語)**

This document defines the compatibility contract between `cdidx` binaries and
the local SQLite database under `.cdidx/codeindex.db`.

## Supported Readers

The public compatibility boundary is the `cdidx` CLI and MCP server reading a
database created by a released `cdidx` binary. The SQLite schema is an internal
storage format, not a public API.

Within a supported release line, newer binaries must read older databases and
degrade optional features explicitly when stored readiness metadata is missing
or stale. Older binaries may read newer databases only when the newer database
does not advertise unknown readiness or contract stamps. If an older binary sees
unknown persisted contract stamps, it must degrade loudly in `status` output and
must refuse writes that could silently discard newer data.

## Schema and Readiness Stamps

`PRAGMA user_version` is a readiness bitmap, not a linear migration number:

| Bit | Field | Meaning |
|---|---|---|
| `1` | `graph_table_available` / graph presence | `symbol_references` contains a queryable committed generation. Use `graph_data_current` / `index_complete` / `reference_graph_complete` to decide whether current-workspace coverage is complete. |
| `2` | `issues_table_available` / issue readiness | `file_issues` has been populated for validation results. |
| `4` | `fold_ready` | Folded-name columns are current for Unicode-aware exact-name matching. |
| `8` | hotspot reference aggregate storage contract | The database uses `hotspot_reference_counts`; this permanent downgrade guard is preserved while other readiness bits are cleared. |
| `16` | hotspot reference aggregate readiness | `hotspot_reference_counts` is synchronized with raw reference rows. Writers clear this bit before reference mutations and restore it only after updating the aggregate. |
| `32` | symbol-kind filter audit storage contract | The per-file `symbols_dropped_by_kind_filter` audit belongs to the persisted filter policy. This permanent downgrade guard blocks older writers that cannot maintain the column or audit generation. |

Storage-contract bits are downgrade guards: binaries that predate the
maintained aggregate or per-file symbol-filter audit see an unknown bit and
refuse write-capable opens instead of leaving evidence stale. They remain set
while transient readiness bits are cleared. Current query readers fall back to
raw reference rows when aggregate readiness is absent; older query-only readers
may still use their normal forward-compatibility degradation behavior.

Additional per-feature contract versions live in `codeindex_meta`, including
folded-key metadata, C# symbol-name and metadata-target versions, SQL graph
contract stamps, hotspot-family readiness, index writer version, indexed HEAD
metadata, unknown-extension counts, filesystem case-sensitivity, MAC profile,
and DB/WAL/status diagnostics. These stamps let readers distinguish a feature
that is absent, stale, or newer than the running binary.

The canonical C# partial-declaration change in #4914 raises
`hotspot_family_version_csharp` from `2` to `14` and the reference-identity
contract from `6` to `8`. The minimum compatible implementation is therefore a
binary that understands hotspot-family contract `14` and reference-identity
contract `8`. Existing databases remain readable, but their C# family and
reference-identity data is reported as stale until a rebuild with
`cdidx index <projectPath> --rebuild` refreshes the persisted partial metadata
and reference candidates.
Older binaries treat those newer stamps as forward-version data and must retain
their normal query degradation and write-refusal behavior.

Reference-extraction cap hits use existing per-file `file_issues` rows rather
than a new schema bit. Current readers aggregate those rows into
`reference_extraction_cap_hits` and set `reference_graph_complete=false`; a
legacy database without inspectable issue state degrades rather than claiming
complete graph coverage.

Symbol-kind indexing policy uses normalized `index_symbol_kind_filter` metadata,
the successful-generation `index_symbol_kind_filter_audit_version` marker, and
the additive per-file `files.symbols_dropped_by_kind_filter` audit column.
Current writable opens add the column in place. An active legacy generation
without the audit marker omits the count until a full incremental scan
re-extracts every file; scoped updates are rejected instead of mixing audit
generations. A successful current index also stamps user-version bit `32`, so
pre-change binaries refuse later writes that would update the policy without
maintaining its per-file evidence. Read-only legacy DBs without the column remain readable and report
`symbol_kind_filter_provenance_unavailable` when the policy stamp is also
missing. Any active persisted policy reports `symbol_kind_filter_coverage_limited`
and keeps negative symbol/graph results non-authoritative. Rebuild unfiltered to
restore full coverage; the additive column itself does not require a rebuild.

## Version Skew Behavior

Use `cdidx status --json` or `cdidx status --check --json` before relying on a
database across binary upgrades or downgrades.

| Scenario | Expected behavior | Operator action |
|---|---|---|
| New binary reads an older DB | Queries continue where possible. Missing readiness fields report degraded status and include remediation strings. | Run the recommended maintenance command from `status`, usually `cdidx backfill-fold` or `cdidx index <projectPath> --rebuild`. |
| Same binary reads its own DB | `status --check --json` reports `index_matches_workspace: true` when file content and HEAD metadata match. | No rebuild required. |
| Older binary reads a newer DB | `index_newer_than_reader` becomes `true` when unknown readiness bits or contract stamps exceed the binary's maximum. Mutating commands refuse to write unsafe newer DBs. | Use the newer `cdidx` binary that wrote the DB, or rebuild the index with the older binary only after accepting loss of newer feature data. |
| Read-only CI artifact | Query commands may use `--read-only` / `--immutable`. Mutating commands reject read-only DBs. | Pin the `cdidx` binary version with the DB artifact when possible. |

## Rebuild Requirements

Additive schema changes should be readable by newer binaries without requiring a
full rebuild. Prefer in-place maintenance for derived data, such as
`cdidx backfill-fold`, when a feature can be refreshed from existing rows.

A rebuild is required when:

- `status` recommends `cdidx index <projectPath> --rebuild`;
- the workspace and DB are intentionally being reset to an older binary version;
- the database is corrupt or fails `cdidx db --integrity-check`;
- a release note explicitly calls out a breaking storage change.

Breaking DB changes must be rare and must document the minimum binary version,
the downgrade behavior, and the rebuild path in release notes.

## CodeIndex DB Compatibility（日本語）

この文書は、`cdidx` binary と `.cdidx/codeindex.db` のローカル SQLite database
の互換性契約を定義します。

## 対応する reader

公開される互換性境界は、release 済み `cdidx` binary が作成した database を
`cdidx` CLI / MCP server が読むことです。SQLite schema は内部 storage format
であり、公開 API ではありません。

対応 release line 内では、新しい binary は古い database を読み、保存済みの
readiness metadata が不足または stale の場合は optional feature を明示的に
degrade しなければなりません。古い binary が新しい database を読めるのは、
その database が未知の readiness / contract stamp を示していない場合だけです。
未知の永続 contract stamp を見た古い binary は `status` で明示的に degrade を
報告し、新しい data を黙って破棄しうる write を拒否します。

## Schema と readiness stamp

`PRAGMA user_version` は線形 migration number ではなく readiness bitmap です。

| Bit | Field | 意味 |
|---|---|---|
| `1` | `graph_table_available` / graph presence | graph query 可能な commit 済み `symbol_references` generation が存在する。current workspace の coverage 完全性は `graph_data_current` / `index_complete` / `reference_graph_complete` で判定する。 |
| `2` | `issues_table_available` / issue readiness | validation result 用の `file_issues` が作成済み。 |
| `4` | `fold_ready` | Unicode-aware exact-name matching 用の folded-name column が最新。 |
| `8` | hotspot reference aggregate storage contract | database が `hotspot_reference_counts` を使用することを示す永続 downgrade guard。他の readiness bit のクリア時にも保持される。 |
| `16` | hotspot reference aggregate readiness | `hotspot_reference_counts` と raw reference row が同期済み。writer は reference の変更前にこの bit をクリアし、aggregate 更新後だけ復元する。 |
| `32` | symbol-kind filter audit storage contract | file ごとの `symbols_dropped_by_kind_filter` audit が永続 filter policy に属することを示す。column / audit 世代を維持できない旧 writer を拒否する永続 downgrade guard。 |

storage-contract bit は downgrade guard です。maintained aggregate または file ごとの
symbol-filter audit 導入前の binary は未知 bit を検知し、証拠を stale にする
write-capable open を拒否します。一時的な readiness bit のクリア中も保持されます。
現行の query reader は readiness が無い場合に raw reference row へフォールバックし、
旧 query-only reader は通常の forward-compatibility degradation を継続できます。

追加の feature contract version は `codeindex_meta` に保存されます。これには
folded-key metadata、C# symbol-name / metadata-target version、SQL graph
contract stamp、hotspot-family readiness、index writer version、indexed HEAD
metadata、unknown-extension count、filesystem case-sensitivity、MAC profile、
DB/WAL/status diagnostics が含まれます。reader はこれらの stamp により、feature
が存在しないのか、stale なのか、実行中 binary より新しいのかを判別できます。

#4914 の canonical C# partial declaration 対応では、
`hotspot_family_version_csharp` を `2` から `14` へ、reference identity contract
を `6` から `8` へ更新します。したがって最低互換実装は hotspot-family contract
`14` と reference-identity contract `8` を理解する binary です。既存 database は
引き続き読み取り可能ですが、`cdidx index <projectPath> --rebuild` で永続 partial
metadata と reference candidate を更新するまでは、C# family / reference identity
data が stale として報告されます。古い binary はこれらの新しい stamp を
forward-version data として扱い、通常の query degradation と write refusal を
維持しなければなりません。

reference-extraction cap hit は新しい schema bit ではなく、既存の file ごとの
`file_issues` row を使います。current reader はそれを `reference_extraction_cap_hits`
へ集約して `reference_graph_complete=false` にします。issue state を確認できない
legacy database は complete graph coverage を主張せず degraded になります。

symbol-kind indexing policy は正規化済み `index_symbol_kind_filter` metadata、成功世代の
`index_symbol_kind_filter_audit_version` marker、additive な file ごとの
`files.symbols_dropped_by_kind_filter` audit column を使います。現行の writable open は列を
in-place で追加します。audit marker の無い active な legacy generation は、全 file を再抽出する
full incremental scan まで count を省略し、scoped update は audit 世代を混在させず拒否します。
現行 binary の index 成功時には user-version bit `32` も stamp されるため、policy だけを
更新して file ごとの証拠を維持できない旧 binary は以後の write を拒否します。
列を持たない read-only legacy DB も読み取り可能で、policy stamp も無い場合は
`symbol_kind_filter_provenance_unavailable` を報告します。active な永続 policy は
`symbol_kind_filter_coverage_limited` を報告し、symbol/graph の否定結果を
non-authoritative に保ちます。full coverage の復元には filter なし rebuild を使いますが、
additive column 自体のために rebuild する必要はありません。

## Version skew 時の動作

binary upgrade / downgrade をまたいで database を使う前に、
`cdidx status --json` または `cdidx status --check --json` を確認してください。

| 状況 | 期待される動作 | 操作者の対応 |
|---|---|---|
| 新しい binary が古い DB を読む | 可能な query は継続します。不足した readiness field は degraded status と remediation を返します。 | `status` の推奨に従い、通常は `cdidx backfill-fold` または `cdidx index <projectPath> --rebuild` を実行します。 |
| 同じ binary が自身の DB を読む | file content と HEAD metadata が一致すると `status --check --json` は `index_matches_workspace: true` を返します。 | rebuild は不要です。 |
| 古い binary が新しい DB を読む | 未知の readiness bit または contract stamp が binary の最大値を超えると `index_newer_than_reader` が `true` になります。mutating command は unsafe な write を拒否します。 | その DB を書いた新しい `cdidx` binary を使うか、新しい feature data が失われることを受け入れて古い binary で index を作り直します。 |
| read-only CI artifact | query command は `--read-only` / `--immutable` を利用できます。mutating command は read-only DB を拒否します。 | 可能なら DB artifact と `cdidx` binary version を一緒に pin します。 |

## Rebuild が必要な場合

Additive schema change は、full rebuild を要求せずに新しい binary で読めるべきです。
既存 row から再生成できる derived data は、`cdidx backfill-fold` のような
in-place maintenance を優先します。

rebuild が必要なのは次の場合です。

- `status` が `cdidx index <projectPath> --rebuild` を推奨している;
- workspace と DB を意図的に古い binary version へ戻す;
- database が壊れている、または `cdidx db --integrity-check` に失敗する;
- release note が breaking storage change を明示している。

Breaking DB change は稀であるべきで、minimum binary version、downgrade behavior、
rebuild path を release note に記載しなければなりません。

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
| `1` | `graph_table_available` / graph readiness | `symbol_references` has been fully populated for graph queries. |
| `2` | `issues_table_available` / issue readiness | `file_issues` has been populated for validation results. |
| `4` | `fold_ready` | Folded-name columns are current for Unicode-aware exact-name matching. |
| `8` | hotspot reference aggregate storage contract | The database uses `hotspot_reference_counts`; this permanent downgrade guard is preserved while other readiness bits are cleared. |
| `16` | hotspot reference aggregate readiness | `hotspot_reference_counts` is synchronized with raw reference rows. Writers clear this bit before reference mutations and restore it only after updating the aggregate. |

The storage-contract bit is a downgrade guard: binaries that predate the
maintained aggregate see an unknown bit and refuse write-capable opens instead
of leaving hotspot counts stale. It remains set while the separate readiness
bit is transiently cleared during reference mutations. Current query readers
fall back to raw reference rows when readiness is absent; older query-only
readers may still use their normal forward-compatibility degradation behavior.

Additional per-feature contract versions live in `codeindex_meta`, including
folded-key metadata, C# symbol-name and metadata-target versions, SQL graph
contract stamps, hotspot-family readiness, index writer version, indexed HEAD
metadata, unknown-extension counts, filesystem case-sensitivity, MAC profile,
and DB/WAL/status diagnostics. These stamps let readers distinguish a feature
that is absent, stale, or newer than the running binary.

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
| `1` | `graph_table_available` / graph readiness | graph query 用の `symbol_references` が完全に作成済み。 |
| `2` | `issues_table_available` / issue readiness | validation result 用の `file_issues` が作成済み。 |
| `4` | `fold_ready` | Unicode-aware exact-name matching 用の folded-name column が最新。 |
| `8` | hotspot reference aggregate storage contract | database が `hotspot_reference_counts` を使用することを示す永続 downgrade guard。他の readiness bit のクリア時にも保持される。 |
| `16` | hotspot reference aggregate readiness | `hotspot_reference_counts` と raw reference row が同期済み。writer は reference の変更前にこの bit をクリアし、aggregate 更新後だけ復元する。 |

storage-contract bit は downgrade guard です。maintained aggregate 導入前の binary は
この未知 bit を検知し、hotspot count を stale にする write-capable open を拒否します。
reference 更新中に別の readiness bit が一時的にクリアされても、この bit は保持されます。
現行の query reader は readiness が無い場合に raw reference row へフォールバックし、
旧 query-only reader は通常の forward-compatibility degradation を継続できます。

追加の feature contract version は `codeindex_meta` に保存されます。これには
folded-key metadata、C# symbol-name / metadata-target version、SQL graph
contract stamp、hotspot-family readiness、index writer version、indexed HEAD
metadata、unknown-extension count、filesystem case-sensitivity、MAC profile、
DB/WAL/status diagnostics が含まれます。reader はこれらの stamp により、feature
が存在しないのか、stale なのか、実行中 binary より新しいのかを判別できます。

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

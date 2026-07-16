# cdidx

> **[日本語版はこちら / Japanese version](#cdidx日本語)**

[![Build and Test](https://github.com/Widthdom/CodeIndex/actions/workflows/dotnet.yml/badge.svg)](https://github.com/Widthdom/CodeIndex/actions/workflows/dotnet.yml)
[![CodeQL](https://github.com/Widthdom/CodeIndex/actions/workflows/codeql.yml/badge.svg)](https://github.com/Widthdom/CodeIndex/actions/workflows/codeql.yml)
[![Release](https://github.com/Widthdom/CodeIndex/actions/workflows/release.yml/badge.svg)](https://github.com/Widthdom/CodeIndex/actions/workflows/release.yml)

![.NET 8.x / 9.x tests](https://img.shields.io/badge/.NET-8.x%20%2F%209.x%20tests-512BD4?logo=dotnet&logoColor=white)
![C#](https://img.shields.io/badge/C%23-12-239120?logo=csharp&logoColor=white)
![Platform](https://img.shields.io/badge/Platform-Windows%20%7C%20macOS%20%7C%20Linux-lightgrey)
![License](https://img.shields.io/badge/License-FSL--1.1--ALv2-orange)
![SQLite](https://img.shields.io/badge/SQLite-FTS5-003B57?logo=sqlite&logoColor=white)

**CLI code indexing, MCP search, and LSP editor lookup for local repositories.**

`cdidx` builds a local SQLite index of a repository so humans, scripts, AI
agents, MCP clients, and LSP-native editors can run fast full-text, symbol,
dependency, and inspection queries without repeatedly rescanning the same tree.

## Why cdidx

> **Index once. Ask many times.** `cdidx` turns a repository into a local
> retrieval runtime for repeated code investigation.

| If your workflow is... | Best fit | Why |
|---|---|---|
| One-off string hunting | `rg` | zero setup, direct file scan |
| Repeated repository investigation | `cdidx` | local SQLite FTS5 index, structured results, incremental refresh |
| VS Code-only chat context | VS Code workspace index | editor-managed context inside the Copilot / VS Code UX |
| Terminal, CI, scripts, or MCP clients | `cdidx` | explicit CLI and MCP surfaces outside an IDE |

Details: [why cdidx](USER_GUIDE.md#why-cdidx), [cdidx vs rg](USER_GUIDE.md#cdidx-vs-rg),
and [cdidx vs VS Code workspace index](USER_GUIDE.md#cdidx-vs-vs-code-workspace-index).

## Design boundaries

CodeIndex is a local-first code index and retrieval backend. It is not an AI
editor, coding agent, chat application, compiler, or exact semantic-analysis
engine. Conversation, editing, commits, pull requests, and autonomous change
decisions belong to the external tool that calls `cdidx`.

Symbol and reference extraction are lightweight indexing hints optimized for
speed, locality, explainability, and retrieval usefulness. Embeddings, vector
search, and LLM-based semantic ranking are not assumptions of CodeIndex core.

NuGet.config XML receives security-policy symbols for package sources and source
mappings, signature validation mode, trusted signers, certificate fingerprints,
and `allowUntrustedRoot`, so these controls can be queried by their configured values.

## Contribution Policy

Issue reports, feature requests, and improvement suggestions are welcome.

This repository currently does not accept external pull requests. Pull request
creation is restricted to collaborators only, and implementation changes are
handled by the maintainer or trusted collaborators.

## Quick Start

Install with one of these:

```bash
brew install widthdom/tap/codeindex
dotnet tool install -g cdidx
curl -fsSL https://raw.githubusercontent.com/Widthdom/CodeIndex/main/install.sh | bash
```

Index and query:

```bash
cdidx .
cdidx status --check --json
cdidx search "handleRequest"
cdidx search "TODO" --first-per-file --sample 25 --json=ndjson --max-json-bytes 65536
cdidx definition UserService
cdidx inspect QueryCommandRunner --outline-only
cdidx outline src/CodeIndex/Cli/QueryCommandRunner.cs --compact --kind function --sort size --limit 10
cdidx unused --compact --by-bucket
cdidx map --compact --max-json-bytes 65536
cdidx map --format issue-drafts --limit 10
cdidx search --recipe risky-code --format compact --max-json-bytes 65536
cdidx search --recipe risky-code --format compact --summary-only --json
cdidx search --named-query todo=TODO --named-query fixme=FIXME --format count --summary-only --json
cdidx suggestions add --category output_format --description "Record a local dogfood finding" --evidence-path src/CodeIndex/Cli/SuggestionsCommandRunner.cs --json
cdidx suggestions update <id> --description "Corrected finding" --context "Corrected context" --json
cdidx suggestions delete <id> --json
cdidx validate
```

The default NDJSON output of `search`, `symbols`, and `files` always ends with a bounded `terminal_record` unless `--results-only` explicitly suppresses it; recipe/audit search row streams use the same contract. The record reports returned and observed total counts, whether that total is authoritative or a lower bound, the truncation reason, applied limits, omitted rows, and recovery guidance. `--max-json-bytes` is a hard cap over all stdout bytes, including row newlines and the terminal record. If that record cannot fit by itself, the command fails with a usage error before writing stdout. Capped output rejects `--profile`, `--verbose`, and `--json-envelope`, whose additional serialization would otherwise escape the cap.

When the byte cap omits rows, these commands return partial-result exit code `11`; pass `--allow-partial` to opt into exit code `0` while retaining the same terminal metadata. Ordinary `--limit` truncation remains a successful, explicitly described stream. Array and compact outputs keep their documented whole-response behavior; check `cdidx <command> --help` before relying on partial output.

`find --all --json` also makes bounded scans explicit. Default streaming JSON rows end with a terminal record containing `scan_complete`, `authoritative_rows`, scanned file/line counts, active caps, truncation reason, and recovery guidance; count JSON carries the same scan state in its single result object through `authoritative_count`. Row formats that cannot carry this metadata, including JSON array and location-only formats, are rejected with `--all`; use text, NDJSON, or count output. A candidate-file or line-scan cap returns partial-result exit code `11` unless `--allow-partial` is set. Ordinary result-limit early stops remain exit `0` but report `scan_complete=false` and `result_limit_reached=true`.

Use it with AI tools or editors:

```bash
cdidx mcp
cdidx lsp --db .cdidx/codeindex.db
```

The first index does the expensive scan once. After edits, branch switches, or
CI checkouts, refresh with `cdidx .`, `--files`, `--commits`, or
`--changed-between` as appropriate. See the [User Guide quick start](USER_GUIDE.md#quick-start)
and [incremental update reliability](USER_GUIDE.md#incremental-update-reliability)
for the full workflow. Repository-wide refreshes load unchanged-file metadata
in one batch, then validate each candidate against the live filesystem instead
of issuing a SQLite lookup for every file. Successful no-op finalization also
validates folded lookup rows once inside the readiness-stamp transaction.
During reference extraction, repeated C# property and Python import/class
membership checks reuse file-local lookup sets instead of rescanning every
extracted symbol for each call site.
C# declaration-container resolution and GitHub Actions job ownership likewise
use name-indexed candidates instead of a full container scan per reference.
Dense Python import, GitHub Actions dependency, JSON path, and Fortran procedure
lists are scanned in place instead of allocating temporary split arrays.
Across all reference languages, duplicate detection stores structured identity
keys instead of allocating a concatenated key string for every candidate.

For a faster first pass when you only need text search, `definition`, `symbols`,
or `map`, run `cdidx . --symbols-only`. Reference graph commands remain degraded
until you rerun `cdidx .` without that flag.

For generated or dense source that emits excessive reference rows, use
`cdidx . --max-references-per-file <n>` to keep text search and symbols indexed
while skipping references for only the over-limit file.

## Highlights

| Area | What to use |
|---|---|
| Search and navigation | `search`, `find`, `excerpt`, `symbols`, `definition`, `references`, `callers`, `callees`, `inspect`, `map`, `deps`, `impact`, `unused`, and `hotspots`. See the [command reference](USER_GUIDE.md#command-reference). |
| AI integration | `cdidx mcp` exposes indexed search tools for Claude Code, Cursor, Windsurf, Copilot, Codex, and other MCP clients. See [AI Integration](USER_GUIDE.md#ai-integration). |
| Editor lookup | `cdidx lsp --db .cdidx/codeindex.db` starts a read-only LSP shim for editors that can launch an LSP command. C# semantic tokens distinguish keywords, modifiers, namespace components, types, fields, methods, and declarations. |
| Freshness | `status --check`, `--files`, `--commits`, `--changed-between`, and `--watch` keep the DB aligned with the workspace. |
| Validation | `cdidx validate` reports encoding and line-ending issues in indexed files. See [Validate indexed files](USER_GUIDE.md#validate-indexed-files). |
| Language coverage | `cdidx languages --json` is the live capability probe; add `--format count`, `--summary-only`, `--capability <filter>`, `--language`, `--extension`, or `--alias` to narrow output. See [Supported languages](USER_GUIDE.md#supported-languages). |
| Custom extraction | Extension aliases and regex-backed symbol patterns are documented in [Custom Language Extraction](DEVELOPER_GUIDE.md#custom-language-extraction). |
| Operations | Install channels, proxy diagnostics, release verification, upgrade, uninstall, troubleshooting, and output controls live in the [User Guide](USER_GUIDE.md). |
| Internals | Architecture, database schema, status trust fields, release workflow, and extractor contracts live in the [Developer Guide](DEVELOPER_GUIDE.md). |

## Documentation

| Document | Contents |
|---|---|
| [User Guide](USER_GUIDE.md) | Detailed installation, command examples, options, output formats, supported languages, MCP setup, and troubleshooting. |
| [Distribution Channels](DISTRIBUTION.md) | Install channel comparison, update paths, platform support, and package maintainer policy. |
| [Cloud Bootstrap](CLOUD_BOOTSTRAP_PROMPT.md) | Install guidance for restricted cloud agent sessions. |
| [Platform Support](docs/platform-support.md) | Official release asset RIDs, unsupported platforms, and source-build alternatives. |
| [Developer Guide](DEVELOPER_GUIDE.md) | Architecture, database schema, implementation notes, status contracts, custom extraction, and release workflow. |
| [Testing Guide](TESTING_GUIDE.md) | Test suite layout, helper utilities, cross-platform rules, and validation commands. |
| [Agent Guide](AGENT_GUIDE.md) | Shared agent entry point, workflow index, search policy, and status contract maintenance rules. |
| [Integration Policy](INTEGRATION_POLICY.md) | Supported CLI, JSON, MCP, and integration use. |
| [Security Policy](SECURITY.md) | Private vulnerability reporting and coordinated disclosure policy. |

## Supported Surfaces

`cdidx` is a **CLI, MCP server, and read-only LSP shim**. The supported,
versioned surfaces are the `cdidx` CLI, CLI JSON output, and `cdidx mcp`
JSON-RPC interface. There is no public library / SDK API. See
[INTEGRATION_POLICY.md](INTEGRATION_POLICY.md#api-surface-and-library-use).

## Status JSON Contract

`cdidx status --json` exposes trust, freshness, compatibility, and remediation
fields for scripts, MCP clients, and release checks. Detailed semantics live in
the [Developer Guide](DEVELOPER_GUIDE.md#ai-integration); README keeps the field
names visible so documentation and tests stay synchronized.
Use `cdidx status --explain <field>` for concise explanations of visible status
fields, including readiness fields and runtime diagnostics such as
`path_case_sensitive`.

| Field group | Fields |
|---|---|
| Readiness and graph trust | `fold_ready`, `fold_ready_reason`, `graph_table_available`, `issues_table_available`, `file_issues_data_current`, `migration_in_progress`, `sql_graph_contract_ready`, `sql_graph_contract_degraded_reason`, `hotspot_family_ready`, `hotspot_family_degraded_reason`, `language_readiness`, `csharp_symbol_name_ready`, `csharp_metadata_target_ready`, `csharp_metadata_target_degraded_reason`. |
| Workspace and HEAD freshness | `indexed_head_commit`, `worktree_head_changed`, `indexed_head_sha`, `indexed_head_branch`, `indexed_head_timestamp`, `commits_ahead_of_indexed_head`, `head_freshness`. |
| Version and forward compatibility | `index_writer_version`, `index_newer_than_reader`, `index_newer_than_reader_reason`. |
| Unknown-extension and runtime diagnostics | `unknown_extension_file_count`, `unknown_extension_files`, `unknown_extension_files_truncated`, `unknown_extension_file_path_limit`, `unknown_extension_extension_counts`, `unknown_extension_category_counts`, `unknown_extension_groups`, `extractors`, `hooks`, `hook_diagnostics`, `trust_overrides`, `path_case_sensitive`, `data_dir_mode`, `db_file_mode`, `database_permission_policy`, `database_permission_diagnostics`, `mac_profile`, `mac_profile_diagnostics`, `stale_after_seconds`, `index_age_seconds`, `process`, `last_index_run`, `last_workspace_freshened_at`, `last_index_run.bytes_read_skipped_file_count`, `last_index_run.bytes_read_incomplete`, `last_index_run.diagnostics`, `last_index_run.diagnostic_count`, `last_index_run.diagnostics_truncated`, `last_failed_or_partial_index_run`, `last_failed_or_partial_index_run.progress_persisted`, `last_failed_or_partial_index_run.recovery_hint`. |
| Database maintenance | `db_size_bytes`, `wal_size_bytes`, `db_pragma_settings` (`journal_mode`, `synchronous`, `wal_autocheckpoint`, `busy_timeout_ms`, `page_count`, `freelist_count`, `page_size`, `auto_vacuum`), `prepared_command_cache` (`count`, `capacity`, `hit_count`, `miss_count`, `eviction_count`), `maintenance_guidance`. |
| WAL checkpoint diagnostics | `read_only_fallback`, `wal_checkpoint_attempted`, `wal_checkpoint_succeeded`, `wal_checkpoint_skipped_reason`, `wal_checkpoint_failure_reason`, `wal_checkpoint_busy`, `wal_checkpoint_log_page_count`, `wal_checkpoint_checkpointed_page_count`, `wal_checkpoint_remaining_page_count`, `read_only_immutable_fallback`, `wal_stale_snapshot_risk`, `wal_stale_snapshot_reason`. |
| Remediation fields | `degraded_root_cause`, `degraded_reason`, `recommended_action`, `alternative_action`, `readiness_degradations`, `repair_commands`. |
| MCP-only session diagnostics | `mcp_session`, `mcp_session.metrics`, `mcp_session.audit_log`, `mcp.rate_limit.bucket_limit`, `mcp.rate_limit.bucket_limit_rejection_count`. |

Database Unix-mode hardening defaults to `database_permission_policy=best_effort`.
If a filesystem permits SQLite I/O but rejects mode reads or changes, cdidx continues,
emits a stable `database_permission_hardening_failed` warning, and adds support-safe
entries to `database_permission_diagnostics`. Set
`CDIDX_DB_PERMISSION_POLICY=strict` when every applicable database/WAL/SHM mode
operation must succeed; strict failures return the same stable error code with remediation.

Explicit `PRAGMA wal_checkpoint(TRUNCATE)` calls read SQLite's `(busy, log,
checkpointed)` result row. A non-zero `busy` value or positive remaining page
count makes `wal_checkpoint_succeeded=false`, with the bounded reasons
`checkpoint_busy` or `checkpoint_pages_remaining`; the count fields preserve
the relevant evidence. SQLite's `(0, -1, -1)` response for a non-WAL database
is a successful no-op with zero remaining pages. SQLite errors are reduced to
stable machine reasons such as `sqlite_read_only` and never expose raw
exception text or paths.

Full MCP status always includes `mcp_session.metrics`; an unconfigured sink is
`{"enabled":false}`. An enabled object reports `enabled`, `path`, `max_bytes`,
`bytes_written`, `disposed`, `degraded`, `queue_capacity`, `queue_depth`,
`queued_event_count`, `written_event_count`, `dropped_event_count`,
`queue_full_drop_count`, `serialization_failure_count`, `write_failure_count`,
`rotation_failure_count`, `batch_flush_count`, `consecutive_failure_count`, and
`recovery_count`, plus optional `next_retry_at`, `last_recovery_at`, and
`last_failure`. MCP ping mirrors this object as `metrics`. Metrics are optional
telemetry, so a degraded or recovering sink does not change the top-level MCP
liveness result.

When MCP audit logging is enabled, full status exposes `mcp_session.audit_log`
and ping mirrors it as `audit_log`. The object includes `enabled`, `path`,
`include_values`, `max_bytes`, `bytes_written`, `disposed`, `queue_capacity`,
`queue_depth`, `queued_record_count`, `written_record_count`,
`dropped_record_count`, `queue_full_drop_count`, `serialization_failure_count`,
`write_failure_count`, `rotation_failure_count`,
`rotation_cleanup_failure_count`, and `rotation_degraded`, plus optional
`last_drop_reason` and `last_rotation_failure`. Dropped records or degraded
rotation degrade MCP ping/health. Shutdown-only abandonment and deadline state
are returned by the sink shutdown result and emitted in the bounded stderr
diagnostic; they are not advertised as live MCP status after the server stops.

When MCP rate limiting is enabled, every direct `tools/call` first consumes one
caller-wide coarse bucket before detailed tool-name, enablement, and argument
validation. Canonical known tool names additionally retain secondary per-tool
buckets; missing, malformed, empty, oversized, case-variant, and unknown names
create no name-derived buckets. `batch_query` maps unknown inner-slot names to
one fixed bounded bucket. At the process-local bucket cap, cdidx first prunes
expired buckets. If a charged coarse token and secondary bucket-cap denial overlap,
`retry_after_ms` reports the earliest point when every required token and capacity
constraint can admit the retry, so legitimate calls recover at the advertised time
(#4547).

`worktree_head_changed` compares the runtime HEAD with the latest successful
index stamp from `indexed_head_sha` when available, and falls back to the older
full-scan-only `indexed_head_commit` only for legacy DBs.
`status --explain indexed_head_sha` describes the last-successful-index stamp,
while `status --explain indexed_head_commit` calls out the legacy
full-scan-only stamp so consumers can prefer `indexed_head_sha` after
incremental indexing.
`head_freshness` summarizes those fields for machine consumers: `state=fresh`
requires a successful `status --check` workspace comparison, while
`state=head_current` only means the runtime HEAD matched the `indexed_head`
selected by `indexed_head_source` without a workspace scan.

Runtime diagnostics under `extractors` include `retained_load_context_count` and
`load_context_lifecycle` so long-running processes can see how many plugin
assembly load contexts are still held and why. Plugin contexts are collectible
but retained while registered extractor instances remain active; rejected or
unretained contexts are unloaded. `hooks[]` entries include `callback_budget_ms`
and `load_context_lifecycle` to show that hook contexts are collectible and
unloaded when the hook runner is disposed. `extractors.diagnostics[]` and
`hook_diagnostics[]` include sanitized `category` machine codes alongside
bounded paths and messages.
Accepted extension trust overrides such as `CDIDX_TRUST_WORKSPACE_PLUGINS` and
`CDIDX_HOOKS_DIR` are also reported in sanitized `trust_overrides[]` entries.

Successful CLI and MCP index runs can also persist bounded
`last_index_run.diagnostics` when best-effort metadata writes fail after the
index data itself has been written successfully.
`last_index_run.bytes_read_skipped_file_count` reports files omitted from the
`bytes_read` total because their size could not be probed.

## Verifying Releases

GitHub releases ship checksums, a detached checksum signature, SBOM assets, and
platform archives. The installer verifies downloaded archives against the
release manifest. For manual verification and provenance checks, see
[Release artifact verification](USER_GUIDE.md#release-artifact-verification)
and [Platform Support](docs/platform-support.md).

## License and Fair Source Use

CodeIndex and official `cdidx` binaries are Fair Source-style software,
source-available under [FSL-1.1-ALv2](LICENSE), unless a specific file or
directory says otherwise. Integration materials may be
[Apache-2.0](LICENSES/Apache-2.0.txt) where marked.

See [COMMERCIAL_LICENSE.md](COMMERCIAL_LICENSE.md),
[INTEGRATION_POLICY.md](INTEGRATION_POLICY.md), and [TRADEMARKS.md](TRADEMARKS.md)
for commercial, integration, and naming details.

---

<a id="cdidx日本語"></a>
# cdidx（日本語）

[![Build and Test](https://github.com/Widthdom/CodeIndex/actions/workflows/dotnet.yml/badge.svg)](https://github.com/Widthdom/CodeIndex/actions/workflows/dotnet.yml)
[![CodeQL](https://github.com/Widthdom/CodeIndex/actions/workflows/codeql.yml/badge.svg)](https://github.com/Widthdom/CodeIndex/actions/workflows/codeql.yml)
[![Release](https://github.com/Widthdom/CodeIndex/actions/workflows/release.yml/badge.svg)](https://github.com/Widthdom/CodeIndex/actions/workflows/release.yml)

![.NET 8.x / 9.x tests](https://img.shields.io/badge/.NET-8.x%20%2F%209.x%20tests-512BD4?logo=dotnet&logoColor=white)
![C#](https://img.shields.io/badge/C%23-12-239120?logo=csharp&logoColor=white)
![Platform](https://img.shields.io/badge/Platform-Windows%20%7C%20macOS%20%7C%20Linux-lightgrey)
![License](https://img.shields.io/badge/License-FSL--1.1--ALv2-orange)
![SQLite](https://img.shields.io/badge/SQLite-FTS5-003B57?logo=sqlite&logoColor=white)

**ローカルリポジトリ向けの CLI コードインデックス、MCP 検索、LSP editor lookup です。**

`cdidx` はリポジトリのローカル SQLite index を作成します。人間、script、
AI エージェント、MCP client、LSP-native editor は、同じツリーを何度も
読み直さずに、高速な全文検索、シンボル、依存関係、inspect query を実行できます。

## なぜ cdidx なのか

> **一度インデックスして、何度も聞く。** `cdidx` はリポジトリを、反復的な
> code investigation のためのローカル retrieval runtime に変えます。

| ワークフロー | 向いているもの | 理由 |
|---|---|---|
| 1回限りの文字列探し | `rg` | セットアップ不要で直接ファイルを読む |
| 同じリポジトリの反復調査 | `cdidx` | SQLite FTS5 のローカル index、構造化結果、差分更新 |
| VS Code 内だけの chat 文脈 | VS Code workspace index | Copilot / VS Code UX 内で editor が管理 |
| ターミナル、CI、スクリプト、MCP client | `cdidx` | IDE 外でも使える明示的な CLI と MCP surface |

詳細: [なぜ cdidx なのか](USER_GUIDE.md#なぜ-cdidx-なのか)、[rg との違い](USER_GUIDE.md#rg-との違い)、
[VS Code workspace index との違い](USER_GUIDE.md#vs-code-workspace-index-との違い)。

## 設計上の境界

CodeIndex は local-first な code index and retrieval backend です。AI editor、
coding agent、chat application、compiler、exact semantic-analysis engine ではありません。
会話、編集、コミット、pull request、自律的な変更判断は、`cdidx` を呼び出す外部 tool の責務です。

Symbol / reference extraction は、速度、ローカル完結、説明可能性、retrieval の有用性に
寄せた lightweight indexing hint です。Embedding、vector search、LLM-based semantic
ranking は CodeIndex core の前提機能ではありません。

NuGet.config XML では package source / source mapping、署名検証モード、trusted
signer、証明書 fingerprint、`allowUntrustedRoot` をセキュリティポリシーの
シンボルとして抽出するため、設定値からこれらの制御を検索できます。

## コントリビューション方針

不具合報告、機能要望、改善提案の Issue は歓迎します。

このリポジトリでは、現在外部からの Pull Request は受け付けていません。
Pull Request の作成は collaborator のみに制限しており、実装変更は maintainer
または信頼済み collaborator が行います。

## すぐに試す

いずれかの方法でインストールします。

```bash
brew install widthdom/tap/codeindex
dotnet tool install -g cdidx
curl -fsSL https://raw.githubusercontent.com/Widthdom/CodeIndex/main/install.sh | bash
```

index を作って検索します。

```bash
cdidx .
cdidx status --check --json
cdidx search "handleRequest"
cdidx search "TODO" --first-per-file --sample 25 --json=ndjson --max-json-bytes 65536
cdidx definition UserService
cdidx inspect QueryCommandRunner --outline-only
cdidx outline src/CodeIndex/Cli/QueryCommandRunner.cs --compact --kind function --sort size --limit 10
cdidx unused --compact --by-bucket
cdidx map --compact --max-json-bytes 65536
cdidx map --format issue-drafts --limit 10
cdidx search --recipe risky-code --format compact --max-json-bytes 65536
cdidx search --recipe risky-code --format compact --summary-only --json
cdidx search --named-query todo=TODO --named-query fixme=FIXME --format count --summary-only --json
cdidx suggestions add --category output_format --description "ローカル dogfood finding を記録する" --evidence-path src/CodeIndex/Cli/SuggestionsCommandRunner.cs --json
cdidx suggestions update <id> --description "修正した finding" --context "修正した context" --json
cdidx suggestions delete <id> --json
cdidx validate
```

`search`、`symbols`、`files` の既定 NDJSON 出力は、`--results-only` で明示的に抑止しない限り、常に上限付きの `terminal_record` で終了し、recipe / audit search の row stream も同じ契約を使います。このレコードは返却件数と観測済み総件数、その総件数が authoritative か lower bound か、切り詰め理由、適用上限、省略行数、復旧案内を返します。`--max-json-bytes` は各行の改行と終端レコードを含む stdout 全体の hard cap です。終端レコード自体が収まらない場合は、stdout を書く前に usage error で失敗します。追加 serialization が cap 外へ出ることを防ぐため、上限付き出力では `--profile`、`--verbose`、`--json-envelope` を拒否します。

byte cap により行を省略した場合、これらのコマンドは partial-result 終了コード `11` を返します。同じ終端 metadata を維持したまま終了コード `0` を明示的に許容するには `--allow-partial` を指定します。通常の `--limit` による切り詰めは、理由が明示された成功 stream のままです。array / compact 出力は文書化済みの whole-response 挙動を維持します。部分出力へ依存する前に `cdidx <command> --help` を確認してください。

`find --all --json` も上限付き scan を明示します。既定の streaming JSON row は `scan_complete`、`authoritative_rows`、走査 file / line 数、有効な cap、切り詰め理由、復旧案内を含む終端レコードで終了します。count JSON は単一 result object の `authoritative_count` と同じ scan 状態を返します。この metadata を表現できない JSON array や location-only 形式は `--all` との組み合わせを拒否するため、text、NDJSON、count 出力を使ってください。candidate-file cap または line-scan cap に達した場合は、`--allow-partial` を指定しない限り partial-result 終了コード `11` を返します。通常の result limit による早期停止は終了コード `0` のままですが、`scan_complete=false` と `result_limit_reached=true` を報告します。

AI tool や editor から使います。

```bash
cdidx mcp
cdidx lsp --db .cdidx/codeindex.db
```

初回 index が重い scan を一度だけ行います。編集後、ブランチ切り替え後、CI checkout 後は、
状況に応じて `cdidx .`、`--files`、`--commits`、`--changed-between` で更新します。
全体の流れは [ユーザーガイドのクイックスタート](USER_GUIDE.md#クイックスタート) と
[インクリメンタル更新の信頼性](USER_GUIDE.md#インクリメンタル更新の信頼性) を参照してください。
リポジトリ全体の更新では unchanged-file metadata を一括で読み、file ごとの SQLite lookup を
繰り返さずに各候補を実 filesystem と照合します。成功する no-op の finalization でも、folded
lookup row の検証を readiness-stamp transaction 内の 1 回にまとめます。
reference extraction でも、C# property と Python import / class の反復 membership check は
call site ごとに全 extracted symbol を再走査せず、file-local な lookup set を再利用します。
C# declaration-container 解決と GitHub Actions の job ownership も同様に、reference ごとの
全 container 走査ではなく name-indexed candidate を使います。
密な Python import、GitHub Actions dependency、JSON path、Fortran procedure list は、
一時的な split array を作らず入力上で直接走査します。
全 reference language の重複検出でも、candidate ごとの連結 key string を作らず、
structured identity key を保持します。

まず text search、`definition`、`symbols`、`map` だけを速く使いたい場合は
`cdidx . --symbols-only` を使えます。reference graph 系コマンドは、このフラグなしで
`cdidx .` を再実行するまで degraded のままです。

生成コードや高密度なソースが過剰な reference 行を生成する場合は
`cdidx . --max-references-per-file <n>` を使うと、text search と symbols は保持しつつ
上限を超えたファイルだけ references をスキップできます。

## 特長

| 分野 | 使うもの |
|---|---|
| 検索とナビゲーション | `search`、`find`、`excerpt`、`symbols`、`definition`、`references`、`callers`、`callees`、`inspect`、`map`、`deps`、`impact`、`unused`、`hotspots`。詳細は [コマンドリファレンス](USER_GUIDE.md#コマンドリファレンス)。 |
| AI 連携 | `cdidx mcp` は Claude Code、Cursor、Windsurf、Copilot、Codex などの MCP client に indexed search tool を提供します。詳細は [AIとの連携](USER_GUIDE.md#aiとの連携)。 |
| editor lookup | `cdidx lsp --db .cdidx/codeindex.db` は、LSP command を起動できる editor 向けの read-only LSP shim です。C# semantic token は keyword、modifier、namespace component、type、field、method、declaration を区別します。 |
| 鮮度管理 | `status --check`、`--files`、`--commits`、`--changed-between`、`--watch` で DB と workspace を揃えます。 |
| validation | `cdidx validate` は indexed file の encoding / line-ending 問題を報告します。詳細は [Indexed files を validate する](USER_GUIDE.md#indexed-files-を-validate-する)。 |
| 対応言語 | `cdidx languages --json` が live capability probe です。`--language`、`--extension`、`--alias` で 1 行を lookup できます。詳細は [対応言語](USER_GUIDE.md#対応言語)。 |
| custom extraction | 拡張子 alias と regex-backed symbol pattern は [Custom Language Extraction](DEVELOPER_GUIDE.md#custom-language-extraction) にあります。 |
| 運用 | install channel、proxy 診断、release 検証、upgrade、uninstall、troubleshooting、output controls は [ユーザーガイド](USER_GUIDE.md#cdidx日本語) にあります。 |
| 内部仕様 | architecture、database schema、status trust field、release workflow、extractor contract は [開発者ガイド](DEVELOPER_GUIDE.md#開発者ガイド) にあります。 |

## ドキュメント

| ドキュメント | 内容 |
|---|---|
| [ユーザーガイド](USER_GUIDE.md#cdidx日本語) | 詳細なインストール、コマンド例、オプション、出力形式、対応言語、MCP 設定、トラブルシュート。 |
| [配布チャネル](DISTRIBUTION.md) | install channel の比較、update path、platform support、package maintainer policy。 |
| [クラウドブートストラップ](CLOUD_BOOTSTRAP_PROMPT.md#日本語) | 制限されたクラウドエージェント環境でのインストール手順。 |
| [プラットフォームサポート](docs/platform-support.md#プラットフォームサポート) | 公式リリースアセットの RID、未対応 platform、source build の代替手段。 |
| [開発者ガイド](DEVELOPER_GUIDE.md#開発者ガイド) | アーキテクチャ、DB schema、実装メモ、status contract、custom extraction、リリース手順。 |
| [テストガイド](TESTING_GUIDE.md#テストガイド) | テストスイート構成、共有ヘルパー、クロスプラットフォーム注意点、検証コマンド。 |
| [エージェントガイド](AGENT_GUIDE.md) | 共有エージェント入口、workflow index、検索ポリシー、status contract の保守ルール。 |
| [統合ポリシー](INTEGRATION_POLICY.md) | CLI、JSON、MCP、各種統合で許可される利用。 |
| [セキュリティポリシー](SECURITY.md) | 非公開の脆弱性報告と協調的開示の方針。 |

## サポート対象の利用面

`cdidx` は **CLI、MCP server、read-only LSP shim** として提供します。
バージョニング契約の対象は、`cdidx` CLI、CLI JSON 出力、`cdidx mcp` の
JSON-RPC interface です。公開 library / SDK API は提供していません。詳細は
[INTEGRATION_POLICY.md](INTEGRATION_POLICY.md#api-surface-and-library-use) を参照してください。

## Status JSON 契約

`cdidx status --json` は script、MCP client、release check 向けに trust、
freshness、compatibility、remediation field を返します。詳細な意味は
[開発者ガイド](DEVELOPER_GUIDE.md#ai連携) にあり、README では docs と test を
同期するため field 名を明示します。
visible な status field の簡潔な説明は `cdidx status --explain <field>` で確認できます。
readiness field に加えて、`path_case_sensitive` などの runtime diagnostic field も対象です。

| field group | fields |
|---|---|
| readiness / graph trust | `fold_ready`, `fold_ready_reason`, `graph_table_available`, `issues_table_available`, `file_issues_data_current`, `migration_in_progress`, `sql_graph_contract_ready`, `sql_graph_contract_degraded_reason`, `hotspot_family_ready`, `hotspot_family_degraded_reason`, `language_readiness`, `csharp_symbol_name_ready`, `csharp_metadata_target_ready`, `csharp_metadata_target_degraded_reason`。 |
| workspace / HEAD freshness | `indexed_head_commit`, `worktree_head_changed`, `indexed_head_sha`, `indexed_head_branch`, `indexed_head_timestamp`, `commits_ahead_of_indexed_head`, `head_freshness`。 |
| version / forward compatibility | `index_writer_version`, `index_newer_than_reader`, `index_newer_than_reader_reason`。 |
| unknown-extension / runtime diagnostics | `unknown_extension_file_count`, `unknown_extension_files`, `unknown_extension_files_truncated`, `unknown_extension_file_path_limit`, `unknown_extension_extension_counts`, `unknown_extension_category_counts`, `unknown_extension_groups`, `extractors`, `hooks`, `hook_diagnostics`, `trust_overrides`, `path_case_sensitive`, `data_dir_mode`, `db_file_mode`, `database_permission_policy`, `database_permission_diagnostics`, `mac_profile`, `mac_profile_diagnostics`, `stale_after_seconds`, `index_age_seconds`, `process`, `last_index_run`, `last_workspace_freshened_at`, `last_index_run.bytes_read_skipped_file_count`, `last_index_run.bytes_read_incomplete`, `last_index_run.diagnostics`, `last_index_run.diagnostic_count`, `last_index_run.diagnostics_truncated`, `last_failed_or_partial_index_run`, `last_failed_or_partial_index_run.progress_persisted`, `last_failed_or_partial_index_run.recovery_hint`。 |
| database maintenance | `db_size_bytes`, `wal_size_bytes`, `db_pragma_settings` (`journal_mode`, `synchronous`, `wal_autocheckpoint`, `busy_timeout_ms`, `page_count`, `freelist_count`, `page_size`, `auto_vacuum`), `prepared_command_cache` (`count`, `capacity`, `hit_count`, `miss_count`, `eviction_count`), `maintenance_guidance`。 |
| WAL checkpoint diagnostics | `read_only_fallback`、`wal_checkpoint_attempted`、`wal_checkpoint_succeeded`、`wal_checkpoint_skipped_reason`、`wal_checkpoint_failure_reason`、`wal_checkpoint_busy`、`wal_checkpoint_log_page_count`、`wal_checkpoint_checkpointed_page_count`、`wal_checkpoint_remaining_page_count`、`read_only_immutable_fallback`、`wal_stale_snapshot_risk`、`wal_stale_snapshot_reason`。 |
| remediation fields | `degraded_root_cause`, `degraded_reason`, `recommended_action`, `alternative_action`, `readiness_degradations`, `repair_commands`。 |
| MCP-only session diagnostics | `mcp_session`, `mcp_session.metrics`, `mcp_session.audit_log`, `mcp.rate_limit.bucket_limit`, `mcp.rate_limit.bucket_limit_rejection_count`。 |

database の Unix mode hardening は既定で
`database_permission_policy=best_effort` です。SQLite I/O は可能でも mode の
読み取りまたは変更を拒否する filesystem では、cdidx は処理を継続し、安定した
`database_permission_hardening_failed` warning と support-safe な
`database_permission_diagnostics` を返します。database / WAL / SHM の該当 mode
操作をすべて必須にする場合は `CDIDX_DB_PERMISSION_POLICY=strict` を設定します。
strict failure は同じ安定 error code と remediation を返します。

明示的な `PRAGMA wal_checkpoint(TRUNCATE)` は SQLite の `(busy, log,
checkpointed)` 結果行を読み取ります。`busy` が 0 以外、または未 checkpoint
page 数が正の場合は `wal_checkpoint_succeeded=false` とし、上限付きの
`checkpoint_busy` または `checkpoint_pages_remaining` を理由として返します。
関連する件数は count field に保持されます。非 WAL database に対する SQLite の
`(0, -1, -1)` は remaining page が 0 の成功した no-op として扱います。SQLite
error は `sqlite_read_only` など安定した machine reason に変換し、raw exception
text や path は公開しません。

MCP の full status は常に `mcp_session.metrics` を含み、sink が未設定なら
`{"enabled":false}` になります。有効な object は `enabled`、`path`、`max_bytes`、
`bytes_written`、`disposed`、`degraded`、`queue_capacity`、`queue_depth`、
`queued_event_count`、`written_event_count`、`dropped_event_count`、
`queue_full_drop_count`、`serialization_failure_count`、`write_failure_count`、
`rotation_failure_count`、`batch_flush_count`、`consecutive_failure_count`、
`recovery_count` に加え、任意の `next_retry_at`、`last_recovery_at`、
`last_failure` を報告します。MCP ping は同じ object を `metrics` として返します。
metrics は任意の telemetry であるため、sink が degraded または recovery 中でも
top-level MCP liveness result は変わりません。

MCP audit log が有効な場合、full status は `mcp_session.audit_log` を公開し、
ping は同じ object を `audit_log` として返します。この object は `enabled`、
`path`、`include_values`、`max_bytes`、`bytes_written`、`disposed`、
`queue_capacity`、`queue_depth`、`queued_record_count`、`written_record_count`、
`dropped_record_count`、`queue_full_drop_count`、`serialization_failure_count`、
`write_failure_count`、`rotation_failure_count`、
`rotation_cleanup_failure_count`、`rotation_degraded` に加え、任意の
`last_drop_reason` と `last_rotation_failure` を含みます。record の drop または
rotation degradation は MCP ping / health を degraded にします。shutdown 専用の
abandoned count と deadline 状態は sink の shutdown result と上限付き stderr
diagnostic で報告し、server 停止後に live MCP status として公開しません。

MCP rate limiting が有効な場合、direct な `tools/call` request はすべて tool 名、
enablement、argument の詳細検証前に caller-wide の coarse bucket を 1 つ消費します。
canonical な既知 tool 名は secondary per-tool bucket も維持し、missing、malformed、
empty、oversized、case-variant、unknown な名前は名前由来 bucket を作成しません。
`batch_query` の unknown inner-slot 名は 1 つの固定 bounded bucket へ集約します。
process-local bucket 上限到達時は期限切れ bucket を先に prune します。消費済み coarse
token と secondary bucket-cap 拒否が重なる場合、`retry_after_ms` は必要なすべての token
と capacity 制約が再試行を許可できる最短時刻を返すため、正規 call は通知時刻に回復できます
（#4547）。

`worktree_head_changed` は、利用可能な場合は最新の成功 index stamp である
`indexed_head_sha` と runtime HEAD を比較し、legacy DB だけで従来の
full-scan 限定 `indexed_head_commit` に fallback します。
`status --explain indexed_head_sha` は最後に成功した index stamp を説明し、
`status --explain indexed_head_commit` は legacy 向けの full-scan 限定 stamp であることを
明示するため、incremental indexing 後の consumer は `indexed_head_sha` を優先できます。
`head_freshness` はこれらの field を機械向けに要約します。`state=fresh` は
`status --check` による workspace 比較が成功した場合だけで、`state=head_current` は
workspace scan なしで runtime HEAD と `indexed_head_source` が選んだ `indexed_head` が一致したことだけを示します。

`extractors` の runtime diagnostics は `retained_load_context_count` と
`load_context_lifecycle` を含むため、長時間実行プロセスは保持中の plugin assembly load
context 数とその理由を確認できます。plugin context は collectible ですが、
登録済み extractor instance が active な間は保持され、reject された context や保持されない
context は unload されます。`hooks[]` entry は `callback_budget_ms` と
`load_context_lifecycle` を含み、hook context が collectible で hook runner の dispose 時に
unload されることを示します。`extractors.diagnostics[]` と `hook_diagnostics[]` は、
bounded な path と message に加えて sanitization 済みの `category` machine code を含みます。
受理された `CDIDX_TRUST_WORKSPACE_PLUGINS` や `CDIDX_HOOKS_DIR` などの
拡張信頼境界 override は、sanitization 済みの `trust_overrides[]` entry としても報告されます。

成功した CLI / MCP index run は、index data 自体の書き込みが成功した後に
best-effort metadata write が失敗した場合、上限付きの
`last_index_run.diagnostics` も保存できます。
`last_index_run.bytes_read_skipped_file_count` は、size probe に失敗して
`bytes_read` 合計から除外された file 数を報告します。

## リリース成果物の検証

GitHub release には checksum、detached checksum signature、SBOM asset、
platform archive が含まれます。installer は download した archive を release
manifest と照合します。手動検証と provenance check は
[リリースアセットの検証](USER_GUIDE.md#リリースアセットの検証) と
[プラットフォームサポート](docs/platform-support.md#プラットフォームサポート) を参照してください。

## ライセンスと Fair Source の扱い

CodeIndex と公式 `cdidx` バイナリは、ファイルやディレクトリで別途明記されない限り
[FSL-1.1-ALv2](LICENSE) の source-available / Fair Source-style software です。
統合用の素材は、明記されている場合 [Apache-2.0](LICENSES/Apache-2.0.txt) で利用できます。

商用利用、統合、名称の扱いについては [COMMERCIAL_LICENSE.md](COMMERCIAL_LICENSE.md)、
[INTEGRATION_POLICY.md](INTEGRATION_POLICY.md)、[TRADEMARKS.md](TRADEMARKS.md) を参照してください。

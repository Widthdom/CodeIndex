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
dependency, and inspection queries without rescanning the same tree for every query.

## Why cdidx

> **Index once. Ask many times.** `cdidx` turns a repository into a local
> retrieval runtime for repeated code investigation.

| If your workflow is... | Best fit | Why |
|---|---|---|
| One-off string hunting | `rg` | Zero setup and a direct file scan. |
| Repeated repository investigation | `cdidx` | Local SQLite FTS5 index, structured results, and incremental refresh. |
| VS Code-only chat context | VS Code workspace index | Editor-managed context inside the Copilot / VS Code UX. |
| Terminal, CI, scripts, or MCP clients | `cdidx` | Explicit CLI and MCP surfaces outside an IDE. |

Details: [why cdidx](USER_GUIDE.md#why-cdidx), [cdidx vs rg](USER_GUIDE.md#cdidx-vs-rg),
and [cdidx vs VS Code workspace index](USER_GUIDE.md#cdidx-vs-vs-code-workspace-index).

## Design boundaries

| Boundary | What it means |
|---|---|
| Local-first retrieval | CodeIndex indexes and queries local repositories; it does not provide a hosted code-search service. |
| Lightweight extraction | Symbols and references are retrieval hints, not compiler-grade semantic analysis. |
| External agent owns changes | Conversation, editing, commits, pull requests, and autonomous decisions belong to the tool calling `cdidx`. |
| No AI ranking dependency | Embeddings, vector search, and LLM-based ranking are not assumptions of CodeIndex core. |

## Contribution Policy

Issue reports, feature requests, and improvement suggestions are welcome.

This repository currently does not accept external pull requests. Pull request
creation is restricted to collaborators, and implementation changes are handled
by the maintainer or trusted collaborators.

## Quick Start

Install with one of these:

```bash
brew install widthdom/tap/codeindex
dotnet tool install -g cdidx
curl -fsSL https://raw.githubusercontent.com/Widthdom/CodeIndex/main/install.sh | bash
```

Index once, then run focused queries:

```bash
cdidx .
cdidx status --check --json
cdidx search "handleRequest"
cdidx definition UserService
cdidx references UserService --limit 20
cdidx inspect QueryCommandRunner --outline-only
cdidx map --compact --max-json-bytes 65536
cdidx audit risky-code --format sarif --limit 20
cdidx doctor --json
cdidx doctor --integrations --json
cdidx validate
```

Use the indexed repository with AI tools or editors:

```bash
cdidx mcp
cdidx lsp --db .cdidx/codeindex.db
```

| Next step | Documentation |
|---|---|
| Learn the query workflow | [First Query Quick Start](USER_GUIDE.md#first-query-quick-start) |
| Browse every command | [Command reference](USER_GUIDE.md#command-reference) |
| Keep the index current | [Keeping the index fresh](USER_GUIDE.md#keeping-the-index-fresh) and [incremental update reliability](USER_GUIDE.md#incremental-update-reliability) |
| Control JSON size and pagination | [JSON output format](USER_GUIDE.md#json-output-format) |
| Configure MCP, Codex, or an editor | [AI Integration](USER_GUIDE.md#ai-integration) |
| Tune large repositories | [Performance tuning](USER_GUIDE.md#performance-tuning-for-large-repositories) |

For text- and symbol-only workflows, `cdidx . --symbols-only` provides a faster
first pass; graph commands remain degraded until a normal `cdidx .` refresh.

## Highlights

| Area | What to use |
|---|---|
| Search and navigation | `search`, `find`, `excerpt`, `symbols`, `definition`, `references`, `callers`, `callees`, `inspect`, `map`, `deps`, `impact`, `unused`, and `hotspots`. See the [command reference](USER_GUIDE.md#command-reference). |
| AI integration | `cdidx mcp` exposes indexed tools to MCP clients. See [AI Integration](USER_GUIDE.md#ai-integration). |
| Editor lookup | `cdidx lsp --db .cdidx/codeindex.db` starts the read-only LSP shim. Setup and behavior are documented in [AI Integration](USER_GUIDE.md#ai-integration). |
| Freshness | `status --check`, `--files`, `--commits`, `--changed-between`, and `--watch` keep the DB aligned with the workspace. |
| Validation | `cdidx validate` reports encoding and line-ending issues. See [Validate indexed files](USER_GUIDE.md#validate-indexed-files). |
| Language coverage | `cdidx languages --json` is the live capability probe. See [Supported languages](USER_GUIDE.md#supported-languages). |
| Custom extraction | Extension aliases and regex-backed patterns are documented in [Custom Language Extraction](DEVELOPER_GUIDE.md#custom-language-extraction). |
| Operations | Installation, upgrades, release verification, troubleshooting, and output controls live in the [User Guide](USER_GUIDE.md). |

Git-scoped refreshes reconcile from the last whole-workspace verification baseline, so a
branch switch still converges when the supplied old ref is newer than or divergent from the
indexed baseline. `status` and `map` expose that proof as `workspace_verified_head_sha`,
separately from the latest scoped-update HEAD. Scoped writes retain their affected paths for
the next verified Git refresh, including when a later commit range has no net file diff.

## Documentation

| Document | Contents |
|---|---|
| [User Guide](USER_GUIDE.md) | Installation, command examples, options, output formats, languages, MCP setup, and troubleshooting. |
| [Distribution Channels](DISTRIBUTION.md) | Install channel comparison, update paths, platform support, and package policy. |
| [Cloud Bootstrap](CLOUD_BOOTSTRAP_PROMPT.md) | Install guidance for restricted cloud agent sessions. |
| [Platform Support](docs/platform-support.md) | Official release RIDs, unsupported platforms, and source-build alternatives. |
| [Developer Guide](DEVELOPER_GUIDE.md) | Architecture, database schema, status contracts, custom extraction, and release workflow. |
| [Testing Guide](TESTING_GUIDE.md) | Test layout, helpers, cross-platform rules, and validation commands. |
| [Agent Guide](AGENT_GUIDE.md) | Agent workflow index, repository search policy, and contract-maintenance rules. |
| [Integration Policy](INTEGRATION_POLICY.md) | Supported CLI, JSON, MCP, and integration use. |
| [Security Policy](SECURITY.md) | Private vulnerability reporting and coordinated disclosure. |

## Supported Surfaces

| Surface | Entry point | Contract |
|---|---|---|
| CLI | `cdidx <command>` | Supported, versioned command-line interface. |
| JSON | `cdidx <command> --json` | Supported structured output for automation. |
| MCP | `cdidx mcp` | Supported JSON-RPC tools for MCP clients. |
| LSP | `cdidx lsp --db .cdidx/codeindex.db` | Read-only editor lookup shim. |
| Library / SDK | -- | No public library or SDK API. |

See [Integration Policy](INTEGRATION_POLICY.md#api-surface-and-library-use) for
the compatibility boundary and [AI Integration](USER_GUIDE.md#ai-integration)
for MCP and LSP setup.

## CLI JSON Error Contract

Recoverable command failures use a versioned, sanitized JSON envelope in JSON
mode and corresponding `Error`, `Hint`, and `Usage` lines in human mode. See
the [Developer Guide](DEVELOPER_GUIDE.md#cli-recoverable-error-format) for the
field definitions and stable code/category mapping.

## Extensionless zsh Completion Functions

For extensionless files and files with an unknown extension, the bounded
first-line probe treats a token-delimited `#compdef` directive as zsh `shell`
source. Known extensions, exact or prefix filename matches, explicit
language-map overrides, extractor plugins, and ambiguous extensions retain
their existing precedence. `#compdef` is not a generic remedy for source in
other languages.

## Index Dry-Run Mutation Estimates

`cdidx index <project> --dry-run --json` previews file actions and bounded
table-mutation estimates without changing the source tree or index. See the
[User Guide indexing workflow](USER_GUIDE.md#index-a-project) for usage and the
[Developer Guide](DEVELOPER_GUIDE.md#build--test) for implementation limits.

Scoped `--files`, `--commits`, and `--changed-between` previews include the
same read-only C# workspace expansion that execution requires for static
interface contracts and qualified member-read refreshes. JSON reports
`projection_authoritative`, `projection_unavailable_reasons`, and
`csharp_workspace_expansion_status` / `csharp_workspace_expansion_reason`.
Expanded paths count toward candidate and sample limits. If a cap, scan error,
or unavailable preflight prevents an exact projection, `totals_lower_bound` is
true and the stable reason explains why the preview is non-authoritative.

## Status JSON Contract

`cdidx status --json` exposes trust, freshness, compatibility, and remediation
data for scripts, MCP clients, and release checks. The field groups remain
visible here as a compact compatibility index.

| Field group | Fields |
|---|---|
| Readiness and graph trust | `fold_ready`, `fold_ready_reason`, `graph_table_available`, `graph_data_current`, `reference_extraction_limits`, `reference_graph_complete`, `reference_graph_incomplete_reasons`, `reference_extraction_cap_hits`, `index_complete`, `index_incomplete_reasons`, `symbol_kind_filter_provenance_available`, `symbol_kind_filter`, `symbols_dropped_by_kind_filter`, `issues_table_available`, `file_issues_data_current`, `migration_in_progress`, `sql_graph_contract_ready`, `sql_graph_contract_degraded_reason`. |
| Language readiness | `hotspot_family_ready`, `hotspot_family_degraded_reason`, `language_readiness`, `csharp_symbol_name_ready`, `csharp_metadata_target_ready`, `csharp_metadata_target_degraded_reason`. |
| Workspace and HEAD freshness | `indexed_head_commit`, `worktree_head_changed`, `indexed_head_sha`, `indexed_head_branch`, `indexed_head_timestamp`, `commits_ahead_of_indexed_head`, `head_freshness`. |
| Workspace-check path samples | `workspace_check.changed_files`, `workspace_check.missing_files`, `workspace_check.outside_sparse_cone_files`, `workspace_check.unindexed_files`, `workspace_check.unverifiable_files`, and `workspace_check.scan_errors`, each paired with authoritative `*_count`, `*_truncated`, `*_path_limit`, and `*_omitted_count` fields. |
| Version compatibility | `index_writer_version`, `index_newer_than_reader`, `index_newer_than_reader_reason`. |
| Extension and extractor diagnostics | `unknown_extension_file_count`, `unknown_extension_files`, `unknown_extension_files_truncated`, `unknown_extension_file_path_limit`, `unknown_extension_extension_counts`, `unknown_extension_category_counts`, `unknown_extension_groups`, `unknown_extension_group_count`, `unknown_extension_groups_truncated`, `unknown_extension_group_limit`, `unknown_extension_group_omitted_count`, `unknown_extension_guidance`, `extractors`, `hooks`, `hook_diagnostics`. |
| Runtime trust and permissions | `trust_overrides`, `git_executable`, `github_cli_executable`, `path_case_sensitive`, `data_dir_mode`, `db_file_mode`, `database_permission_policy`, `database_permission_diagnostics`, `mac_profile`, `mac_profile_diagnostics`. |
| Check context and run diagnostics | `stale_after_seconds`, `index_age_seconds`, `query_context.check_mode`, `query_context.stale_after_seconds`, `process`, `last_index_run`, `last_workspace_freshened_at`, `last_failed_or_partial_index_run`, `status_metadata_diagnostics`. |
| Last-run detail | `last_index_run.bytes_read_skipped_file_count`, `last_index_run.bytes_read_incomplete`, `last_index_run.diagnostics`, `last_index_run.diagnostic_count`, `last_index_run.diagnostics_truncated`, `last_index_run.reference_extraction_cap_hits`, `last_index_run.rebuild_reclaim`, `last_failed_or_partial_index_run.progress_persisted`, `last_failed_or_partial_index_run.recovery_hint`, `last_failed_or_partial_index_run.file_errors`. |
| SQLite and maintenance | `sqlite_connection_policy`, `db_size_bytes`, `wal_size_bytes`, `db_pragma_settings`, `prepared_command_cache`, `maintenance_guidance`, `maintenance_guidance.fts_optimization`, `threshold_writes`, `observed_writes`. |
| WAL checkpoint diagnostics | `read_only_fallback`, `wal_checkpoint_attempted`, `wal_checkpoint_succeeded`, `wal_checkpoint_skipped_reason`, `wal_checkpoint_failure_reason`, `wal_checkpoint_busy`, `wal_checkpoint_log_page_count`, `wal_checkpoint_checkpointed_page_count`, `wal_checkpoint_remaining_page_count`, `read_only_immutable_fallback`, `wal_stale_snapshot_risk`, `wal_stale_snapshot_reason`. |
| Database size attribution | `database_size_attribution`. |
| Remediation | `degraded_root_cause`, `degraded_reason`, `recommended_action`, `alternative_action`, `readiness_degradations`, `repair_commands`. |
| MCP-only session diagnostics | `mcp_session`, `mcp_session.metrics`, `queue_capacity`, `queue_depth`, `queued_event_count`, `written_event_count`, `dropped_event_count`, `queue_full_drop_count`, `serialization_failure_count`, `write_failure_count`, `rotation_failure_count`, `batch_flush_count`, `consecutive_failure_count`, `recovery_count`, `next_retry_at`, `last_recovery_at`, `last_failure`, `mcp_session.audit_log`, `queued_record_count`, `written_record_count`, `mcp.rate_limit.bucket_limit`, `mcp.rate_limit.bucket_limit_rejection_count`. |

Persisted JSON subdocuments for `last_index_run.reference_extraction_cap_hits`,
`last_index_run.rebuild_reclaim`, and
`last_failed_or_partial_index_run.file_errors` have a 512 KiB UTF-8 input limit
and a maximum nesting depth of 16. File-error and cap-hit file lists accept at
most 50 entries; nested reason lists accept at most 16. Paths accept 32,768
characters, category/phase/reason codes accept 128, detail/rebuild-reason text
accepts 4,096, and each subdocument accepts at most 262,144 decoded string
characters in aggregate. An unavailable subdocument does not fail the rest of
`status`; `status_metadata_diagnostics` identifies its field with the stable
reason `raw_size_exceeded`, `invalid_json`, or `semantic_validation_failed`, the
configured `max_utf8_bytes`, and the observed byte size when available. Human
status output flattens persisted control characters and uses the stable bounded
value truncation marker, while accepted JSON values remain unchanged.

Full index completion, the watch initial scan, and full-scan dry runs also report
the unknown-language file count, the top 10 extension groups, explicit omission
metadata, and remediation guidance. `status --compact` includes the persisted
equivalent from the most recent successful full scan.

Use `cdidx status --explain <field>` for bounded field guidance. Detailed
semantics, repair-action structure, readiness degradation, SQLite/WAL handling,
and MCP diagnostics live in the [Developer Guide](DEVELOPER_GUIDE.md#ai-integration);
the everyday status workflow is in [Check status](USER_GUIDE.md#check-status).
Regular JSON keeps the full explanation. `--compact`, `--format compact`, and a
byte-bounded explanation without explicit `--fields` use the minimum compact
schema `api_version`, `field`, `meaning`, `interpretation`, and `remediation`.
Envelope metadata reports that schema plus the names and count of omitted
optional fields; a budget too small for the envelope and one compact row returns
an explicit response-budget error with minimum-size and retry guidance.
Selecting one of these lists through `--fields` also retains its count and
truncation signals. Compact output keeps the signals without the paths, and a
JSON byte budget may shorten a path sample while increasing its omitted count.

## Verifying Releases

GitHub releases include checksums, detached checksum signatures, SBOM assets,
and platform archives. See [release artifact verification](USER_GUIDE.md#release-artifact-verification)
and [platform support](docs/platform-support.md).

## License and Fair Source Use

CodeIndex and the official `cdidx` binaries are source-available /
Fair Source-style software under [FSL-1.1-ALv2](LICENSE), unless a file or
directory states otherwise. Marked integration materials may use
[Apache-2.0](LICENSES/Apache-2.0.txt).

For commercial use, integration, and naming guidance, see
[COMMERCIAL_LICENSE.md](COMMERCIAL_LICENSE.md),
[INTEGRATION_POLICY.md](INTEGRATION_POLICY.md), and
[TRADEMARKS.md](TRADEMARKS.md).

# cdidx（日本語）

> **[English version](#cdidx)**

[![Build and Test](https://github.com/Widthdom/CodeIndex/actions/workflows/dotnet.yml/badge.svg)](https://github.com/Widthdom/CodeIndex/actions/workflows/dotnet.yml)
[![CodeQL](https://github.com/Widthdom/CodeIndex/actions/workflows/codeql.yml/badge.svg)](https://github.com/Widthdom/CodeIndex/actions/workflows/codeql.yml)
[![Release](https://github.com/Widthdom/CodeIndex/actions/workflows/release.yml/badge.svg)](https://github.com/Widthdom/CodeIndex/actions/workflows/release.yml)

![.NET 8.x / 9.x tests](https://img.shields.io/badge/.NET-8.x%20%2F%209.x%20tests-512BD4?logo=dotnet&logoColor=white)
![C#](https://img.shields.io/badge/C%23-12-239120?logo=csharp&logoColor=white)
![Platform](https://img.shields.io/badge/Platform-Windows%20%7C%20macOS%20%7C%20Linux-lightgrey)
![License](https://img.shields.io/badge/License-FSL--1.1--ALv2-orange)
![SQLite](https://img.shields.io/badge/SQLite-FTS5-003B57?logo=sqlite&logoColor=white)

**ローカルリポジトリ向けの CLI コード索引、MCP 検索、LSP editor lookup。**

`cdidx` はリポジトリをローカル SQLite DB に索引化し、人、script、AI agent、
MCP client、LSP 対応 editor が同じ tree を繰り返し走査せずに全文・symbol・
dependency・inspection query を実行できるようにします。

## なぜ cdidx なのか

> **一度索引し、何度も問い合わせる。** `cdidx` は繰り返すコード調査のための
> ローカル retrieval runtime です。

| ワークフロー | 最適な選択 | 理由 |
|---|---|---|
| 単発の文字列検索 | `rg` | setup 不要で file を直接走査します。 |
| 繰り返すリポジトリ調査 | `cdidx` | SQLite FTS5、構造化結果、incremental refresh を利用できます。 |
| VS Code 内だけの chat context | VS Code workspace index | Copilot / VS Code UX 内で editor が context を管理します。 |
| terminal、CI、script、MCP client | `cdidx` | IDE 外から明示的な CLI / MCP interface を利用できます。 |

詳しくは [なぜ cdidx なのか](USER_GUIDE.md#なぜ-cdidx-なのか)、
[rg との違い](USER_GUIDE.md#rg-との違い)、
[VS Code workspace index との違い](USER_GUIDE.md#vs-code-workspace-index-との違い)を参照してください。

## 設計上の境界

| 境界 | 意味 |
|---|---|
| local-first retrieval | CodeIndex はローカルリポジトリを索引・検索し、hosted code-search service は提供しません。 |
| lightweight extraction | symbol と reference は retrieval hint であり、compiler-grade semantic analysis ではありません。 |
| 変更は外部 agent が所有 | conversation、編集、commit、PR、自律的な判断は `cdidx` を呼び出す tool が担当します。 |
| AI ranking に非依存 | embedding、vector search、LLM ranking を CodeIndex core の前提にしません。 |

## コントリビューション方針

Issue report、feature request、改善提案を歓迎します。

このリポジトリでは現在、外部からの pull request を受け付けていません。
PR の作成は collaborator に限定し、実装変更は maintainer または信頼済み collaborator が担当します。

## すぐに試す

次のいずれかでインストールします。

```bash
brew install widthdom/tap/codeindex
dotnet tool install -g cdidx
curl -fsSL https://raw.githubusercontent.com/Widthdom/CodeIndex/main/install.sh | bash
```

一度索引してから、対象を絞った query を実行します。

```bash
cdidx .
cdidx status --check --json
cdidx search "handleRequest"
cdidx definition UserService
cdidx references UserService --limit 20
cdidx inspect QueryCommandRunner --outline-only
cdidx map --compact --max-json-bytes 65536
cdidx audit risky-code --format sarif --limit 20
cdidx doctor --json
cdidx doctor --integrations --json
cdidx validate
```

AI tool や editor から使う場合:

```bash
cdidx mcp
cdidx lsp --db .cdidx/codeindex.db
```

| 次に行うこと | ドキュメント |
|---|---|
| query workflow を学ぶ | [最初の検索を試す](USER_GUIDE.md#最初の検索を試す) |
| 全 command を確認する | [コマンドリファレンス](USER_GUIDE.md#コマンドリファレンス) |
| index を最新に保つ | [インデックスを最新に保つ](USER_GUIDE.md#インデックスを最新に保つ) と [インクリメンタル更新の信頼性](USER_GUIDE.md#インクリメンタル更新の信頼性) |
| JSON size と pagination を制御する | [出力形式](USER_GUIDE.md#出力形式) |
| MCP、Codex、editor を設定する | [AI との連携](USER_GUIDE.md#aiとの連携) |
| 大規模リポジトリを調整する | [大規模リポジトリの performance tuning](USER_GUIDE.md#大規模リポジトリの-performance-tuning) |

text / symbol だけを先に検索する場合、`cdidx . --symbols-only` で初回処理を
短縮できます。graph command は通常の `cdidx .` を実行するまで degraded のままです。

## 特長

| 分野 | 使うもの |
|---|---|
| 検索とナビゲーション | `search`、`find`、`excerpt`、`symbols`、`definition`、`references`、`callers`、`callees`、`inspect`、`map`、`deps`、`impact`、`unused`、`hotspots`。詳細は [コマンドリファレンス](USER_GUIDE.md#コマンドリファレンス)。 |
| AI 連携 | `cdidx mcp` が MCP client に indexed tool を提供します。詳細は [AI との連携](USER_GUIDE.md#aiとの連携)。 |
| editor lookup | `cdidx lsp --db .cdidx/codeindex.db` で read-only LSP shim を起動します。setup と動作は [AI との連携](USER_GUIDE.md#aiとの連携) を参照してください。 |
| 鮮度管理 | `status --check`、`--files`、`--commits`、`--changed-between`、`--watch` で DB と workspace を揃えます。 |
| validation | `cdidx validate` が encoding / line-ending 問題を報告します。詳細は [Indexed files を validate する](USER_GUIDE.md#indexed-files-を-validate-する)。 |
| 対応言語 | `cdidx languages --json` が live capability probe です。詳細は [対応言語](USER_GUIDE.md#対応言語)。 |
| custom extraction | 拡張子 alias と regex-backed pattern は [カスタム言語抽出](DEVELOPER_GUIDE.md#カスタム言語抽出) を参照してください。 |
| 運用 | install、upgrade、release 検証、troubleshooting、output control は [ユーザーガイド](USER_GUIDE.md#cdidx日本語) にあります。 |

Git 差分 refresh は最後に workspace 全体を検証した基準から差分を補完するため、指定した
old ref が indexed 基準より新しい場合や分岐している場合でも branch switch 後に収束します。
`status` と `map` は、この検証済み HEAD を `workspace_verified_head_sha` として、最新
scoped-update HEAD とは別に返します。scoped write の対象 path は次回の検証済み Git
refresh まで保持されるため、後続 commit range の file 差分が相殺された場合も再照合されます。

## ドキュメント

| ドキュメント | 内容 |
|---|---|
| [ユーザーガイド](USER_GUIDE.md#cdidx日本語) | install、command 例、option、出力形式、対応言語、MCP setup、troubleshooting。 |
| [配布チャネル](DISTRIBUTION.md) | install channel、update path、platform support、package policy。 |
| [クラウドブートストラップ](CLOUD_BOOTSTRAP_PROMPT.md#日本語) | 制限された cloud agent session での install guidance。 |
| [プラットフォームサポート](docs/platform-support.md#プラットフォームサポート) | 公式 release RID、未対応 platform、source-build の代替手段。 |
| [開発者ガイド](DEVELOPER_GUIDE.md#開発者ガイド) | architecture、database schema、status contract、custom extraction、release workflow。 |
| [テストガイド](TESTING_GUIDE.md#テストガイド) | test layout、helper、cross-platform rule、validation command。 |
| [エージェントガイド](AGENT_GUIDE.md) | agent workflow index、リポジトリ検索 policy、contract maintenance rule。 |
| [統合ポリシー](INTEGRATION_POLICY.md) | CLI、JSON、MCP、integration の利用境界。 |
| [セキュリティポリシー](SECURITY.md) | 非公開の脆弱性報告と協調的開示。 |

## サポート対象の利用面

| 利用面 | entry point | 契約 |
|---|---|---|
| CLI | `cdidx <command>` | versioned command-line interface。 |
| JSON | `cdidx <command> --json` | automation 向けの structured output。 |
| MCP | `cdidx mcp` | MCP client 向け JSON-RPC tool。 |
| LSP | `cdidx lsp --db .cdidx/codeindex.db` | read-only editor lookup shim。 |
| library / SDK | -- | public library / SDK API はありません。 |

互換性の境界は [統合ポリシー](INTEGRATION_POLICY.md#api-surface-and-library-use)、
MCP / LSP setup は [AI との連携](USER_GUIDE.md#aiとの連携)を参照してください。

## CLI JSON エラー契約

回復可能な command failure は、JSON mode では versioned / sanitized envelope、
human mode では対応する `Error`、`Hint`、`Usage` 行を返します。field 定義と
安定した code/category 対応は
[開発者ガイド](DEVELOPER_GUIDE.md#cli-の回復可能エラー形式)を参照してください。

## 拡張子なしの zsh 補完関数

拡張子なし、または未知拡張子のファイルでは、上限付き先頭行 probe が token 境界を持つ
`#compdef` directive を zsh の `shell` source として認識します。既知拡張子、exact / prefix
filename match、明示的な language-map override、extractor plugin、曖昧拡張子は既存の
precedence を維持します。`#compdef` は他言語の source に対する一般的な解決策ではありません。

## index dry-run の mutation 推定

`cdidx index <project> --dry-run --json` は source tree や index を変更せず、
file action と上限付き table mutation estimate を preview します。使い方は
[プロジェクトをインデックス](USER_GUIDE.md#プロジェクトをインデックス)、実装上の制限は
[開発者ガイド](DEVELOPER_GUIDE.md#ビルドテスト)を参照してください。

`--files`、`--commits`、`--changed-between` の scoped preview では、static
interface contract と修飾 member-read refresh のために実行時に必要となるものと同じ
read-only C# workspace 展開を反映します。JSON は `projection_authoritative`、
`projection_unavailable_reasons`、`csharp_workspace_expansion_status` /
`csharp_workspace_expansion_reason` を返します。展開された path も candidate / sample
上限に含まれます。cap、scan error、利用不能な preflight によって正確な予測を作れない場合は
`totals_lower_bound` が true となり、安定した reason が非 authoritative である理由を示します。

## Status JSON 契約

`cdidx status --json` は script、MCP client、release check 向けに trust、
freshness、compatibility、remediation data を返します。compatibility index として
field group を表に残します。

| field group | field |
|---|---|
| readiness / graph trust | `fold_ready`、`fold_ready_reason`、`graph_table_available`、`graph_data_current`、`reference_extraction_limits`、`reference_graph_complete`、`reference_graph_incomplete_reasons`、`reference_extraction_cap_hits`、`index_complete`、`index_incomplete_reasons`、`symbol_kind_filter_provenance_available`、`symbol_kind_filter`、`symbols_dropped_by_kind_filter`、`issues_table_available`、`file_issues_data_current`、`migration_in_progress`、`sql_graph_contract_ready`、`sql_graph_contract_degraded_reason`。 |
| language readiness | `hotspot_family_ready`、`hotspot_family_degraded_reason`、`language_readiness`、`csharp_symbol_name_ready`、`csharp_metadata_target_ready`、`csharp_metadata_target_degraded_reason`。 |
| workspace / HEAD freshness | `indexed_head_commit`、`worktree_head_changed`、`indexed_head_sha`、`indexed_head_branch`、`indexed_head_timestamp`、`commits_ahead_of_indexed_head`、`head_freshness`。 |
| workspace-check の path sample | `workspace_check.changed_files`、`workspace_check.missing_files`、`workspace_check.outside_sparse_cone_files`、`workspace_check.unindexed_files`、`workspace_check.unverifiable_files`、`workspace_check.scan_errors`。各一覧には authoritative な `*_count`、`*_truncated`、`*_path_limit`、`*_omitted_count` が対応します。 |
| version compatibility | `index_writer_version`、`index_newer_than_reader`、`index_newer_than_reader_reason`。 |
| extension / extractor diagnostics | `unknown_extension_file_count`、`unknown_extension_files`、`unknown_extension_files_truncated`、`unknown_extension_file_path_limit`、`unknown_extension_extension_counts`、`unknown_extension_category_counts`、`unknown_extension_groups`、`unknown_extension_group_count`、`unknown_extension_groups_truncated`、`unknown_extension_group_limit`、`unknown_extension_group_omitted_count`、`unknown_extension_guidance`、`extractors`、`hooks`、`hook_diagnostics`。 |
| runtime trust / permissions | `trust_overrides`、`git_executable`、`github_cli_executable`、`path_case_sensitive`、`data_dir_mode`、`db_file_mode`、`database_permission_policy`、`database_permission_diagnostics`、`mac_profile`、`mac_profile_diagnostics`。 |
| check context / run diagnostics | `stale_after_seconds`、`index_age_seconds`、`query_context.check_mode`、`query_context.stale_after_seconds`、`process`、`last_index_run`、`last_workspace_freshened_at`、`last_failed_or_partial_index_run`、`status_metadata_diagnostics`。 |
| last-run detail | `last_index_run.bytes_read_skipped_file_count`、`last_index_run.bytes_read_incomplete`、`last_index_run.diagnostics`、`last_index_run.diagnostic_count`、`last_index_run.diagnostics_truncated`、`last_index_run.reference_extraction_cap_hits`、`last_index_run.rebuild_reclaim`、`last_failed_or_partial_index_run.progress_persisted`、`last_failed_or_partial_index_run.recovery_hint`、`last_failed_or_partial_index_run.file_errors`。 |
| SQLite / maintenance | `sqlite_connection_policy`、`db_size_bytes`、`wal_size_bytes`、`db_pragma_settings`、`prepared_command_cache`、`maintenance_guidance`、`maintenance_guidance.fts_optimization`、`threshold_writes`、`observed_writes`。 |
| WAL checkpoint diagnostics | `read_only_fallback`、`wal_checkpoint_attempted`、`wal_checkpoint_succeeded`、`wal_checkpoint_skipped_reason`、`wal_checkpoint_failure_reason`、`wal_checkpoint_busy`、`wal_checkpoint_log_page_count`、`wal_checkpoint_checkpointed_page_count`、`wal_checkpoint_remaining_page_count`、`read_only_immutable_fallback`、`wal_stale_snapshot_risk`、`wal_stale_snapshot_reason`。 |
| database size attribution | `database_size_attribution`。 |
| remediation | `degraded_root_cause`、`degraded_reason`、`recommended_action`、`alternative_action`、`readiness_degradations`、`repair_commands`。 |
| MCP-only session diagnostics | `mcp_session`、`mcp_session.metrics`、`queue_capacity`、`queue_depth`、`queued_event_count`、`written_event_count`、`dropped_event_count`、`queue_full_drop_count`、`serialization_failure_count`、`write_failure_count`、`rotation_failure_count`、`batch_flush_count`、`consecutive_failure_count`、`recovery_count`、`next_retry_at`、`last_recovery_at`、`last_failure`、`mcp_session.audit_log`、`queued_record_count`、`written_record_count`、`mcp.rate_limit.bucket_limit`、`mcp.rate_limit.bucket_limit_rejection_count`。 |

`last_index_run.reference_extraction_cap_hits`、`last_index_run.rebuild_reclaim`、
`last_failed_or_partial_index_run.file_errors` の永続化 JSON subdocument には、
UTF-8 で 512 KiB の入力上限と最大 depth 16 を適用します。file error と cap-hit
file の一覧は最大 50 件、入れ子の reason 一覧は最大 16 件です。path は 32,768
文字、category / phase / reason code は 128 文字、detail / rebuild reason は
4,096 文字、subdocument 内の decoded string 合計は 262,144 文字まで受け付けます。
利用不能な subdocument があっても `status` の残りは失敗せず、
`status_metadata_diagnostics` が対象 field、安定した reason
（`raw_size_exceeded`、`invalid_json`、`semantic_validation_failed`）、設定済みの
`max_utf8_bytes`、取得できる場合は実測 byte size を返します。human status 出力は
永続化された control character を平坦化し、安定した bounded-value truncation marker
を使いますが、契約内で受理した JSON 値は変更しません。

全体 index の完了時、watch の初回 scan、全体 scan の dry-run でも、言語未対応
ファイル数、上位 10 個の拡張子 group、明示的な省略 metadata、対処 guidance を
返します。`status --compact` には、直近に成功した全体 scan で永続化された同等の
診断が含まれます。

上限付きの field guidance は `cdidx status --explain <field>` で確認できます。
repair action、readiness degradation、SQLite/WAL、MCP diagnostic の詳細は
[開発者ガイド](DEVELOPER_GUIDE.md#ai連携)、日常的な使い方は
[クイックスタート](USER_GUIDE.md#クイックスタート)を参照してください。
通常の JSON は完全な説明を維持します。`--compact`、`--format compact`、および
明示的な `--fields` を伴わない byte 上限付き説明は、`api_version`、`field`、
`meaning`、`interpretation`、`remediation` の最小 compact schema を使います。
envelope metadata は schema と省略した任意 field の名前・件数を返し、envelope と
compact row 1件を収められない上限では、必要最小 size と retry guidance を含む
明示的な response-budget error を返します。
これらの一覧を `--fields` で選択した場合も count と truncation signal は残ります。
compact 出力は path を省いて signal を維持し、JSON byte budget で path sample が
さらに短縮された場合は omitted count が増加します。

## リリース成果物の検証

GitHub release には checksum、detached checksum signature、SBOM asset、
platform archive が含まれます。手動検証は
[リリースアセットの検証](USER_GUIDE.md#リリースアセットの検証)と
[プラットフォームサポート](docs/platform-support.md#プラットフォームサポート)を参照してください。

## ライセンスと Fair Source の扱い

CodeIndex と公式 `cdidx` binary は、別途明記されない限り
[FSL-1.1-ALv2](LICENSE) の source-available / Fair Source-style software です。
明記された integration material には
[Apache-2.0](LICENSES/Apache-2.0.txt) を適用できます。

商用利用、統合、名称の扱いについては
[COMMERCIAL_LICENSE.md](COMMERCIAL_LICENSE.md)、
[INTEGRATION_POLICY.md](INTEGRATION_POLICY.md)、
[TRADEMARKS.md](TRADEMARKS.md)を参照してください。

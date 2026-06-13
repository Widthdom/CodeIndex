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
cdidx definition UserService
cdidx validate
```

Use it with AI tools or editors:

```bash
cdidx mcp
cdidx lsp --db .cdidx/codeindex.db
```

The first index does the expensive scan once. After edits, branch switches, or
CI checkouts, refresh with `cdidx .`, `--files`, `--commits`, or
`--changed-between` as appropriate. See the [User Guide quick start](USER_GUIDE.md#quick-start)
and [incremental update reliability](USER_GUIDE.md#incremental-update-reliability)
for the full workflow.

## Highlights

| Area | What to use |
|---|---|
| Search and navigation | `search`, `find`, `excerpt`, `symbols`, `definition`, `references`, `callers`, `callees`, `inspect`, `map`, `deps`, `impact`, `unused`, and `hotspots`. See the [command reference](USER_GUIDE.md#command-reference). |
| AI integration | `cdidx mcp` exposes indexed search tools for Claude Code, Cursor, Windsurf, Copilot, Codex, and other MCP clients. See [AI Integration](USER_GUIDE.md#ai-integration). |
| Editor lookup | `cdidx lsp --db .cdidx/codeindex.db` starts a read-only LSP shim for editors that can launch an LSP command. |
| Freshness | `status --check`, `--files`, `--commits`, `--changed-between`, and `--watch` keep the DB aligned with the workspace. |
| Validation | `cdidx validate` reports encoding and line-ending issues in indexed files. See [Validate indexed files](USER_GUIDE.md#validate-indexed-files). |
| Language coverage | `cdidx languages --json` is the live capability probe. See [Supported languages](USER_GUIDE.md#supported-languages). |
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

| Field group | Fields |
|---|---|
| Readiness and graph trust | `fold_ready`, `fold_ready_reason`, `graph_table_available`, `issues_table_available`, `file_issues_data_current`, `migration_in_progress`, `sql_graph_contract_ready`, `sql_graph_contract_degraded_reason`, `hotspot_family_ready`, `hotspot_family_degraded_reason`, `language_readiness`, `csharp_symbol_name_ready`, `csharp_metadata_target_ready`, `csharp_metadata_target_degraded_reason`. |
| Workspace and HEAD freshness | `indexed_head_commit`, `worktree_head_changed`, `indexed_head_sha`, `indexed_head_branch`, `indexed_head_timestamp`, `commits_ahead_of_indexed_head`. |
| Version and forward compatibility | `index_writer_version`, `index_newer_than_reader`, `index_newer_than_reader_reason`. |
| Unknown-extension and runtime diagnostics | `unknown_extension_file_count`, `unknown_extension_files`, `unknown_extension_files_truncated`, `unknown_extension_file_path_limit`, `unknown_extension_extension_counts`, `unknown_extension_category_counts`, `unknown_extension_groups`, `extractors`, `hooks`, `hook_diagnostics`, `path_case_sensitive`, `data_dir_mode`, `mac_profile`, `stale_after_seconds`, `index_age_seconds`, `last_failed_or_partial_index_run`. |
| Database maintenance | `db_size_bytes`, `wal_size_bytes`, `db_pragma_settings` (`journal_mode`, `synchronous`, `wal_autocheckpoint`, `page_count`, `freelist_count`, `page_size`, `auto_vacuum`), `maintenance_guidance`. |
| Remediation fields | `degraded_root_cause`, `degraded_reason`, `recommended_action`, `alternative_action`, `readiness_degradations`, `repair_commands`. |
| MCP-only session diagnostics | `mcp_session`. |

`worktree_head_changed` compares the runtime HEAD with the latest successful
index stamp from `indexed_head_sha` when available, and falls back to the older
full-scan-only `indexed_head_commit` only for legacy DBs.

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
cdidx definition UserService
cdidx validate
```

AI tool や editor から使います。

```bash
cdidx mcp
cdidx lsp --db .cdidx/codeindex.db
```

初回 index が重い scan を一度だけ行います。編集後、ブランチ切り替え後、CI checkout 後は、
状況に応じて `cdidx .`、`--files`、`--commits`、`--changed-between` で更新します。
全体の流れは [ユーザーガイドのクイックスタート](USER_GUIDE.md#クイックスタート) と
[インクリメンタル更新の信頼性](USER_GUIDE.md#インクリメンタル更新の信頼性) を参照してください。

## 特長

| 分野 | 使うもの |
|---|---|
| 検索とナビゲーション | `search`、`find`、`excerpt`、`symbols`、`definition`、`references`、`callers`、`callees`、`inspect`、`map`、`deps`、`impact`、`unused`、`hotspots`。詳細は [コマンドリファレンス](USER_GUIDE.md#コマンドリファレンス)。 |
| AI 連携 | `cdidx mcp` は Claude Code、Cursor、Windsurf、Copilot、Codex などの MCP client に indexed search tool を提供します。詳細は [AIとの連携](USER_GUIDE.md#aiとの連携)。 |
| editor lookup | `cdidx lsp --db .cdidx/codeindex.db` は、LSP command を起動できる editor 向けの read-only LSP shim です。 |
| 鮮度管理 | `status --check`、`--files`、`--commits`、`--changed-between`、`--watch` で DB と workspace を揃えます。 |
| validation | `cdidx validate` は indexed file の encoding / line-ending 問題を報告します。詳細は [Indexed files を validate する](USER_GUIDE.md#indexed-files-を-validate-する)。 |
| 対応言語 | `cdidx languages --json` が live capability probe です。詳細は [対応言語](USER_GUIDE.md#対応言語)。 |
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

| field group | fields |
|---|---|
| readiness / graph trust | `fold_ready`, `fold_ready_reason`, `graph_table_available`, `issues_table_available`, `file_issues_data_current`, `migration_in_progress`, `sql_graph_contract_ready`, `sql_graph_contract_degraded_reason`, `hotspot_family_ready`, `hotspot_family_degraded_reason`, `language_readiness`, `csharp_symbol_name_ready`, `csharp_metadata_target_ready`, `csharp_metadata_target_degraded_reason`。 |
| workspace / HEAD freshness | `indexed_head_commit`, `worktree_head_changed`, `indexed_head_sha`, `indexed_head_branch`, `indexed_head_timestamp`, `commits_ahead_of_indexed_head`。 |
| version / forward compatibility | `index_writer_version`, `index_newer_than_reader`, `index_newer_than_reader_reason`。 |
| unknown-extension / runtime diagnostics | `unknown_extension_file_count`, `unknown_extension_files`, `unknown_extension_files_truncated`, `unknown_extension_file_path_limit`, `unknown_extension_extension_counts`, `unknown_extension_category_counts`, `unknown_extension_groups`, `extractors`, `hooks`, `hook_diagnostics`, `path_case_sensitive`, `data_dir_mode`, `mac_profile`, `stale_after_seconds`, `index_age_seconds`, `last_failed_or_partial_index_run`。 |
| database maintenance | `db_size_bytes`, `wal_size_bytes`, `db_pragma_settings` (`journal_mode`, `synchronous`, `wal_autocheckpoint`, `page_count`, `freelist_count`, `page_size`, `auto_vacuum`), `maintenance_guidance`。 |
| remediation fields | `degraded_root_cause`, `degraded_reason`, `recommended_action`, `alternative_action`, `readiness_degradations`, `repair_commands`。 |
| MCP-only session diagnostics | `mcp_session`。 |

`worktree_head_changed` は、利用可能な場合は最新の成功 index stamp である
`indexed_head_sha` と runtime HEAD を比較し、legacy DB だけで従来の
full-scan 限定 `indexed_head_commit` に fallback します。

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

# cdidx

> **[日本語版はこちら / Japanese version](#cdidx日本語)**

[![Build and Test](https://github.com/Widthdom/CodeIndex/actions/workflows/dotnet.yml/badge.svg)](https://github.com/Widthdom/CodeIndex/actions/workflows/dotnet.yml)
[![CodeQL](https://github.com/Widthdom/CodeIndex/actions/workflows/codeql.yml/badge.svg)](https://github.com/Widthdom/CodeIndex/actions/workflows/codeql.yml)
[![Release](https://github.com/Widthdom/CodeIndex/actions/workflows/release.yml/badge.svg)](https://github.com/Widthdom/CodeIndex/actions/workflows/release.yml)

![.NET 8.x](https://img.shields.io/badge/.NET-8.x-512BD4?logo=dotnet&logoColor=white)
![C#](https://img.shields.io/badge/C%23-12-239120?logo=csharp&logoColor=white)
![Platform](https://img.shields.io/badge/Platform-Windows%20%7C%20macOS%20%7C%20Linux-lightgrey)
![License](https://img.shields.io/badge/License-FSL--1.1--ALv2-orange)
![SQLite](https://img.shields.io/badge/SQLite-FTS5-003B57?logo=sqlite&logoColor=white)

**CLI code indexing and MCP search for local repositories.**

`cdidx` is a command-line code indexer and MCP server that builds a local
SQLite index of your repository so humans and AI agents can run fast
full-text, symbol, dependency, and inspection queries without repeatedly
rescanning the same tree.

## Why cdidx

> **Index once. Ask many times.** `cdidx` turns a repository into a local
> retrieval runtime, so humans and AI agents can pull scoped code context
> without rescanning or resending broad files every turn.

| If your workflow is... | Best fit | Why |
|---|---|---|
| One-off string hunting | `rg` | zero setup, direct file scan |
| Repeated repository investigation | `cdidx` | local SQLite FTS5 index, structured results, incremental refresh |
| VS Code-only chat context | VS Code workspace index | editor-managed context inside the Copilot/VS Code UX |
| Terminal, CI, scripts, or MCP clients | `cdidx` | explicit CLI + MCP boundary that works outside an IDE |

Details: [why cdidx](USER_GUIDE.md#why-cdidx), [cdidx vs rg](USER_GUIDE.md#cdidx-vs-rg),
and [cdidx vs VS Code workspace index](USER_GUIDE.md#cdidx-vs-vs-code-workspace-index).

## Quick Start

```bash
# Install is usually seconds.
curl -fsSL https://raw.githubusercontent.com/Widthdom/CodeIndex/main/install.sh | bash

# First index: ~30-60s on small repos; minutes or longer on 100k-file trees.
# Add --verbose to see each file status while it runs.
cdidx .
cdidx status --check --json
cdidx search "handleRequest"
cdidx definition UserService
cdidx search "Handle" --project MyApp
cdidx mcp
```

During indexing, the terminal shows `Scanning...`, `Indexing...`, and a
`67.0% [28/42]`-style progress line, then a unit-labelled elapsed time such as
`2.4s` or `5m 42s`. For later edits or branch switches, refresh incrementally
with `--files`, `--commits`, or `--changed-between <old-ref> <new-ref>` instead
of rebuilding; see
[Quick Start](USER_GUIDE.md#quick-start) and
[Incremental update reliability](USER_GUIDE.md#incremental-update-reliability).
If you do need `--rebuild`, interactive terminals ask for confirmation before
deleting the existing DB, and scripts/CI must pass `--yes` (or `--force`).
If a directory cannot be scanned because of permissions or an I/O error,
`cdidx` records the scan error, keeps scanning other directories, and writes a
temporary `.cdidx/scan-checkpoint.json` so a same-HEAD retry can skip directories
that already completed successfully.
Terminals that request ASCII-only output with `--ascii`, `CDIDX_ASCII=1`,
`NO_UNICODE`, `TERM=dumb`, accessibility env hints, or a non-UTF-8 locale render
spinner frames as `|` / `/` / `-` / `\` and progress bars with `#` / `-` instead
of Unicode glyphs. Very narrow Unicode-capable terminals show a percentage-only
progress line so the display does not wrap.

Use `cdidx` when a repository will be searched repeatedly from terminals,
scripts, CI, or AI tools. Use `rg` when you only need a one-off text scan.

## Highlights

- CLI-first search with human-readable and machine-oriented output.
- Search ranking prefers public/exported symbol matches ahead of protected,
  internal, and private matches; `--no-visibility-rank` keeps the legacy order.
- `symbols`, `definition`, `unused`, and `hotspots` can include or exclude
  `public`, `protected`, `internal`, and `private` symbols with `--visibility`
  and `--exclude-visibility`.
- Full-text, symbol, reference, caller/callee, dependency, map, inspect, and
  excerpt commands.
- `.sln` / `.csproj`-aware `--project <name|path>` filters for indexing and
  query commands, with `--solution <path>` when a workspace has multiple
  solution files.
- MCP server for AI clients such as Claude Code, Cursor, and Windsurf,
  including tools, indexed-file resources, and canned analysis prompts.
- Local suggestion history can be listed, inspected, and exported with
  `cdidx suggestions`.
- Parallel full-scan extraction with configurable `--parallelism`, incremental refreshes
  with `--files` and `--commits`, plus continuous `--watch` mode.
- Post-extraction hooks from `~/.config/cdidx/hooks/*.dll` (or `CDIDX_HOOKS_DIR`)
  can enrich symbols and references before persistence.
- Exact DB/worktree freshness checks with `status --check`, including an
  overridable age threshold via `--stale-after` / `CDIDX_STALE_AFTER`.
- Human `status` output translates readiness flags and `status --explain <field>`
  describes one readiness field/remediation; `status --json` keeps raw fields for
  automation, including the last full-scan unknown-extension count.
- Read commands accept `--profile` to append SQL timing, row-count, and
  `EXPLAIN QUERY PLAN` JSON after the normal result; `--slow-query-ms <n>` logs
  profiled SQL statements that meet the threshold.
- `cdidx diff <db1> <db2>` compares two index databases for CI or drift
  debugging, reporting schema, file, symbol, and reference deltas with stable
  exit codes: `0` identical, `1` drift, `2` schema mismatch, `3` unreadable DB.
- The documented `status --json` trust contract covers `fold_ready`,
  `fold_ready_reason`, `graph_table_available`, `issues_table_available`,
  `sql_graph_contract_ready`, `sql_graph_contract_degraded_reason`,
  `hotspot_family_ready`, `hotspot_family_degraded_reason`,
  `csharp_symbol_name_ready`, `csharp_metadata_target_ready`,
  `csharp_metadata_target_degraded_reason`,
  `indexed_head_commit`, `worktree_head_changed`, `indexed_head_sha`,
  `indexed_head_branch`, `indexed_head_timestamp`, `commits_ahead_of_indexed_head`,
  `index_writer_version`, `index_newer_than_reader`,
  `index_newer_than_reader_reason`, `unknown_extension_file_count`,
  `path_case_sensitive`, `mac_profile`, `db_pragma_settings`, `hooks`, `stale_after_seconds`,
  `index_age_seconds`, `degraded_reason`, `recommended_action`, and `alternative_action`; keep this
  list synchronized with `DEVELOPER_GUIDE.md` and `AGENT_GUIDE.md`.
  `hotspot_family_degraded_reason` distinguishes legacy DBs without hotspot-family
  support (`hotspot_family_support_not_indexed`), stale metadata
  (`hotspot_family_metadata_stale`), and indexes written while marker fingerprints
  were unavailable (`hotspot_family_disabled_at_index_time`); hotspot-family
  readiness is tracked by per-language `hotspot_family_version_<lang>` metadata
  introduced with hotspot-family contract version 2.
- Local-first storage in `.cdidx/codeindex.db`.
- 78 detected languages, with symbol and graph support where available.
- MCP `tools/list` descriptions include a `Language support:` clause sourced
  from the same language registries as `cdidx languages`.
- MCP `resources/list` exposes indexed files as `cdidx://file/<path>` resources,
  `resources/read` returns indexed file text, and `prompts/list` advertises
  starter prompts such as `summarize_file`, `find_unused`, and
  `impact_of_changing`.

## Documentation

| Document | Contents |
|---|---|
| [User Guide](USER_GUIDE.md) | Detailed installation, command examples, options, supported languages, MCP setup, and troubleshooting. |
| [Cloud Bootstrap](CLOUD_BOOTSTRAP_PROMPT.md) | Install guidance for restricted cloud agent sessions. |
| [Platform Support](docs/platform-support.md) | Official release asset RIDs, unsupported platforms, and source-build alternatives. |
| [Developer Guide](DEVELOPER_GUIDE.md) | Architecture, implementation notes, release workflow, and the [`reference_kind` filtering matrix](DEVELOPER_GUIDE.md#reference-kind-filtering-matrix) for `callers` / `impact` / `deps` count reconciliation. |
| [Testing Guide](TESTING_GUIDE.md) | Test conventions and validation commands. |
| [Self-Improvement Contract](SELF_IMPROVEMENT.md) | Rules for agents improving CodeIndex itself. |
| [Integration Policy](INTEGRATION_POLICY.md) | Permitted CLI, JSON, MCP, and integration use. |
| [Security Policy](SECURITY.md) | Private vulnerability reporting and coordinated disclosure policy. |
| [Code of Conduct](CODE_OF_CONDUCT.md) | Community standards and reporting expectations. |

## Supported Surfaces

`cdidx` is a **CLI and MCP server** only. The supported, versioned surfaces are
the `cdidx` CLI (including its `--json` output) and the `cdidx mcp` JSON-RPC
interface. There is no public library / SDK API: the `cdidx` NuGet package is
published as a .NET global tool (`PackAsTool=true`), and `public` types on the
assembly are implementation details that may change without notice. See
[INTEGRATION_POLICY.md — API Surface and Library Use](INTEGRATION_POLICY.md#api-surface-and-library-use).

## Verifying releases

Every GitHub release ships the following supply-chain artifacts next to
the per-platform `CodeIndex-<rid>.tar.gz` / `.zip` binaries:

| Asset | Purpose |
|---|---|
| `sha256sums.txt` | SHA-256 of every release asset (including the SBOM). `install.sh` uses it to verify the downloaded tarball before placing anything under `$HOME/.local/bin/`. |
| `cdidx.sbom.cdx.json` | CycloneDX 1.x JSON Software Bill of Materials covering every NuGet dependency (including the bundled `SQLitePCLRaw` native asset) so compliance reviewers (SOC2, FedRAMP-style) and scanners (Snyk, Trivy, Grype) can audit transitive dependencies without re-deriving them from `.deps.json`. |

Quick check after downloading both files from the release page:

```bash
sha256sum --check sha256sums.txt --ignore-missing
jq '.metadata.component.name, .components | length' cdidx.sbom.cdx.json
```

## License and Fair Source Use

CodeIndex and official `cdidx` binaries are source-available / Fair Source-style software
under [FSL-1.1-ALv2](LICENSE), unless a specific file or directory says
otherwise. Integration materials may be [Apache-2.0](LICENSES/Apache-2.0.txt)
where marked.

See [COMMERCIAL_LICENSE.md](COMMERCIAL_LICENSE.md), [INTEGRATION_POLICY.md](INTEGRATION_POLICY.md),
and [TRADEMARKS.md](TRADEMARKS.md) for commercial, integration, and naming
details.

# cdidx（日本語）

[![Build and Test](https://github.com/Widthdom/CodeIndex/actions/workflows/dotnet.yml/badge.svg)](https://github.com/Widthdom/CodeIndex/actions/workflows/dotnet.yml)
[![CodeQL](https://github.com/Widthdom/CodeIndex/actions/workflows/codeql.yml/badge.svg)](https://github.com/Widthdom/CodeIndex/actions/workflows/codeql.yml)
[![Release](https://github.com/Widthdom/CodeIndex/actions/workflows/release.yml/badge.svg)](https://github.com/Widthdom/CodeIndex/actions/workflows/release.yml)

![.NET 8.x](https://img.shields.io/badge/.NET-8.x-512BD4?logo=dotnet&logoColor=white)
![C#](https://img.shields.io/badge/C%23-12-239120?logo=csharp&logoColor=white)
![Platform](https://img.shields.io/badge/Platform-Windows%20%7C%20macOS%20%7C%20Linux-lightgrey)
![License](https://img.shields.io/badge/License-FSL--1.1--ALv2-orange)
![SQLite](https://img.shields.io/badge/SQLite-FTS5-003B57?logo=sqlite&logoColor=white)

**ローカルリポジトリ向けの CLI コードインデックスと MCP 検索です。**

`cdidx` はコマンドラインのコードインデクサー兼 MCP サーバーで、リポジトリの
ローカル SQLite index を作成します。人間と AI エージェントは、同じツリーを
何度も読み直さずに、高速な全文検索、シンボル、依存関係、inspect クエリを
実行できます。

## なぜ cdidx なのか

> **一度インデックスして、何度も聞く。** `cdidx` はリポジトリをローカルな
> retrieval runtime に変え、人間と AI エージェントが毎回広いファイルを
> 読み直さずに、必要なコード文脈だけを取り出せるようにします。

| ワークフロー | 向いているもの | 理由 |
|---|---|---|
| 1回限りの文字列探し | `rg` | セットアップ不要で直接ファイルを読む |
| 同じリポジトリの反復調査 | `cdidx` | SQLite FTS5 のローカル index、構造化結果、差分更新 |
| VS Code 内だけの chat 文脈 | VS Code workspace index | Copilot / VS Code UX 内で editor が管理 |
| ターミナル、CI、スクリプト、MCP client | `cdidx` | IDE 外でも使える明示的な CLI + MCP 境界 |

詳細: [なぜ cdidx なのか](USER_GUIDE.md#なぜ-cdidx-なのか)、[rg との違い](USER_GUIDE.md#rg-との違い)、
[VS Code workspace index との違い](USER_GUIDE.md#vs-code-workspace-index-との違い)。

## すぐに試す

```bash
# インストールは通常数秒で終わります。
curl -fsSL https://raw.githubusercontent.com/Widthdom/CodeIndex/main/install.sh | bash

# 初回 index は小規模 repo で約30-60秒、100kファイル級では数分以上かかることがあります。
# 実行中のファイル別ステータスを見たい場合は --verbose を付けてください。
cdidx .
cdidx status --check --json
cdidx search "handleRequest"
cdidx definition UserService
cdidx search "Handle" --project MyApp
cdidx mcp
```

インデックス中は `Scanning...`、`Indexing...`、`67.0% [28/42]` のような
進捗行が表示され、最後に `2.4s` や `5m 42s` のような単位付き経過時間が出ます。
編集後やブランチ切り替え後は再構築ではなく `--files`、`--commits`、または
`--changed-between <old-ref> <new-ref>` で差分更新できます。詳細は
[クイックスタート](USER_GUIDE.md#クイックスタート) と
[インクリメンタル更新の信頼性](USER_GUIDE.md#インクリメンタル更新の信頼性)
を参照してください。
`--rebuild` が必要な場合、interactive terminal では既存 DB 削除前に確認を求め、
script / CI では `--yes`（または `--force`）が必要です。
権限や I/O エラーでディレクトリを走査できない場合でも、`cdidx` は scan error を
記録して他のディレクトリの走査を続け、同じ HEAD の再実行で成功済みディレクトリを
読み飛ばせるように一時的な `.cdidx/scan-checkpoint.json` を書き込みます。
`--ascii`、`CDIDX_ASCII=1`、`NO_UNICODE`、`TERM=dumb`、accessibility 系の環境変数、
または非 UTF-8 locale により ASCII-only 出力が要求されている端末では、スピナーは
`|` / `/` / `-` / `\`、進捗バーは `#` / `-` で描画されます。Unicode を利用できる端末でも
幅が非常に狭い場合は、折り返しを避けるため percentage-only の進捗行を表示します。

ターミナル、スクリプト、CI、AI ツールから同じリポジトリを繰り返し検索する
場合は `cdidx` が向いています。1回限りのテキスト検索には `rg` が向いています。

## 特長

- CLI-first の検索。人間向け出力と機械処理向け出力に対応。
- 検索順位は public/exported なシンボル一致を protected、internal、private より優先します。
  従来順が必要な場合は `--no-visibility-rank` を使えます。
- `symbols`、`definition`、`unused`、`hotspots` は `--visibility` と
  `--exclude-visibility` で `public`、`protected`、`internal`、`private`
  シンボルを include / exclude できます。
- 全文検索、シンボル、参照、caller/callee、依存関係、map、inspect、excerpt
  コマンドを提供。
- `.sln` / `.csproj` を使った `--project <name|path>` filter により、
  index と query コマンドを特定の .NET project 配下へ絞り込めます。
  workspace に solution が複数ある場合は `--solution <path>` を指定します。
- Claude Code、Cursor、Windsurf などの AI クライアント向け MCP サーバー。
  tools、インデックス済みファイル resources、定型分析 prompts を提供します。
- `cdidx suggestions` でローカルの提案履歴を一覧表示、詳細表示、エクスポート可能。
- `--files` と `--commits` による差分更新、および `--watch` による継続更新モード。
- `~/.config/cdidx/hooks/*.dll`（または `CDIDX_HOOKS_DIR`）の post-extraction hook で、
  永続化前のシンボルと参照を拡張できます。
- `status --check` による DB と作業ツリーの完全一致確認。`--stale-after` /
  `CDIDX_STALE_AFTER` で age threshold を上書き可能。
- 人間向け `status` は readiness flag を翻訳し、`status --explain <field>` は
  個別 field の意味と対処を説明します。自動化向けの `status --json` は raw field
  と直近 full scan の未知拡張子数を維持します。
- read 系コマンドは `--profile` で通常結果の後に SQL の時間、行数、
  `EXPLAIN QUERY PLAN` の JSON を追加できます。`--slow-query-ms <n>` は
  閾値以上の profiled SQL をログに記録します。
- `cdidx diff <db1> <db2>` は CI や drift 調査向けに 2 つの index DB を比較し、
  schema、file、symbol、reference の差分を報告します。exit code は `0` identical、
  `1` drift、`2` schema mismatch、`3` unreadable DB です。
- 文書化された `status --json` trust contract は `fold_ready`、
  `fold_ready_reason`、`graph_table_available`、`issues_table_available`、
  `sql_graph_contract_ready`、`sql_graph_contract_degraded_reason`、
  `hotspot_family_ready`、`hotspot_family_degraded_reason`、
  `csharp_symbol_name_ready`、`csharp_metadata_target_ready`、
  `csharp_metadata_target_degraded_reason`、
  `indexed_head_commit`、`worktree_head_changed`、`indexed_head_sha`、
  `indexed_head_branch`、`indexed_head_timestamp`、`commits_ahead_of_indexed_head`、
  `index_writer_version`、`index_newer_than_reader`、
  `index_newer_than_reader_reason`、`unknown_extension_file_count`、
  `path_case_sensitive`、`mac_profile`、`db_pragma_settings`、`hooks`、`stale_after_seconds`、
  `index_age_seconds`、`degraded_reason`、`recommended_action`、`alternative_action` を対象にします。
  この一覧は `DEVELOPER_GUIDE.md` と `AGENT_GUIDE.md` に同期してください。
  `hotspot_family_degraded_reason` は、hotspot-family 未対応の legacy DB
  (`hotspot_family_support_not_indexed`)、古い metadata
  (`hotspot_family_metadata_stale`)、marker fingerprint が利用できない状態で書かれた index
  (`hotspot_family_disabled_at_index_time`) を区別します。hotspot-family readiness は
  hotspot-family contract version 2 で導入された言語別
  `hotspot_family_version_<lang>` metadata で追跡されます。
- `.cdidx/codeindex.db` に保存するローカルファースト設計。
- 78 言語を検出し、対応言語ではシンボルとグラフも利用可能。
- MCP の `tools/list` 説明には、`cdidx languages` と同じ言語レジストリから
  生成した `Language support:` 句を含めます。
- MCP の `resources/list` はインデックス済みファイルを `cdidx://file/<path>`
  resources として公開し、`resources/read` はファイル本文を返します。
  `prompts/list` は `summarize_file`、`find_unused`、`impact_of_changing`
  などの starter prompt を公開します。

## ドキュメント

| ドキュメント | 内容 |
|---|---|
| [ユーザーガイド](USER_GUIDE.md#cdidx日本語) | 詳細なインストール、コマンド例、オプション、対応言語、MCP 設定、トラブルシュート。 |
| [クラウドブートストラップ](CLOUD_BOOTSTRAP_PROMPT.md#日本語) | 制限されたクラウドエージェント環境でのインストール手順。 |
| [プラットフォームサポート](docs/platform-support.md#プラットフォームサポート) | 公式リリースアセットの RID、未対応 platform、source build の代替手段。 |
| [開発者ガイド](DEVELOPER_GUIDE.md#開発者ガイド) | アーキテクチャ、実装メモ、リリース手順、`callers` / `impact` / `deps` の件数差を照合する [`reference_kind` フィルタの対応表](DEVELOPER_GUIDE.md#reference-kind-filtering-matrix)。 |
| [テストガイド](TESTING_GUIDE.md#テストガイド) | テスト規約と検証コマンド。 |
| [自己改善コントラクト](SELF_IMPROVEMENT.md#自己改善ループ) | CodeIndex 自身を改善するエージェント向けルール。 |
| [統合ポリシー](INTEGRATION_POLICY.md) | CLI、JSON、MCP、各種統合で許可される利用。 |
| [セキュリティポリシー](SECURITY.md) | 非公開の脆弱性報告と協調的開示の方針。 |
| [行動規範](CODE_OF_CONDUCT.md) | コミュニティ標準と報告時の期待事項。 |

## サポート対象の利用面

`cdidx` は **CLI と MCP サーバーとしてのみ** 提供します。バージョニング契約の
対象となるのは、`cdidx` CLI（`--json` 出力を含む）と `cdidx mcp` の JSON-RPC
インターフェースだけです。公開ライブラリ/SDK API は提供していません。`cdidx`
NuGet パッケージは .NET グローバルツールとして公開されており
（`PackAsTool=true`）、アセンブリ上の `public` 型は CLI/MCP の実装上の事情で
公開されているだけの実装詳細で、予告なく変更される可能性があります。詳細は
[INTEGRATION_POLICY.md — API Surface and Library Use](INTEGRATION_POLICY.md#api-surface-and-library-use)
を参照してください。

## リリース成果物の検証

各 GitHub release では、プラットフォーム別の
`CodeIndex-<rid>.tar.gz` / `.zip` バイナリと並んで、サプライチェーン関連の
成果物を同梱しています。

| アセット | 用途 |
|---|---|
| `sha256sums.txt` | 各リリースアセット（SBOM を含む）の SHA-256。`install.sh` は `$HOME/.local/bin/` に何も書き込む前に tarball をこのファイルで検証します。 |
| `cdidx.sbom.cdx.json` | CycloneDX 1.x JSON 形式の Software Bill of Materials。同梱の `SQLitePCLRaw` ネイティブアセットを含む全 NuGet 依存を列挙するため、SOC2 / FedRAMP 系のコンプライアンスレビューや Snyk / Trivy / Grype などのスキャナーが `.deps.json` から再構築せずに推移的依存を監査できます。 |

リリースページから両ファイルをダウンロードしたあとの簡易チェック例:

```bash
sha256sum --check sha256sums.txt --ignore-missing
jq '.metadata.component.name, .components | length' cdidx.sbom.cdx.json
```

## ライセンスと Fair Source の扱い

CodeIndex と公式 `cdidx` バイナリは、ファイルやディレクトリで別途明記されない
限り [FSL-1.1-ALv2](LICENSE) の source-available / Fair Source-style software
です。統合用の素材は、明記されている場合 [Apache-2.0](LICENSES/Apache-2.0.txt)
で利用できます。

商用利用、統合、名称の扱いについては [COMMERCIAL_LICENSE.md](COMMERCIAL_LICENSE.md)、
[INTEGRATION_POLICY.md](INTEGRATION_POLICY.md)、[TRADEMARKS.md](TRADEMARKS.md) を
参照してください。

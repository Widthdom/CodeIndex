# cdidx

> **[日本語版はこちら / Japanese version](#cdidx日本語)**

[![Build and Test](https://github.com/Widthdom/CodeIndex/actions/workflows/dotnet.yml/badge.svg)](https://github.com/Widthdom/CodeIndex/actions/workflows/dotnet.yml)
[![CodeQL](https://github.com/Widthdom/CodeIndex/actions/workflows/codeql.yml/badge.svg)](https://github.com/Widthdom/CodeIndex/actions/workflows/codeql.yml)
[![Release](https://github.com/Widthdom/CodeIndex/actions/workflows/release.yml/badge.svg)](https://github.com/Widthdom/CodeIndex/actions/workflows/release.yml)

![.NET 8](https://img.shields.io/badge/.NET-8.0-512BD4?logo=dotnet&logoColor=white)
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
cdidx mcp
```

During indexing, the terminal shows `Scanning...`, `Indexing...`, and a
`67.0% [28/42]`-style progress line, then a unit-labelled elapsed time such as
`2.4s` or `5m 42s`. For later edits, refresh incrementally with `--files` or
`--commits` instead of rebuilding; see
[Quick Start](USER_GUIDE.md#quick-start).

Use `cdidx` when a repository will be searched repeatedly from terminals,
scripts, CI, or AI tools. Use `rg` when you only need a one-off text scan.

## Highlights

- CLI-first search with human-readable and machine-oriented output.
- Full-text, symbol, reference, caller/callee, dependency, map, inspect, and
  excerpt commands.
- MCP server for AI clients such as Claude Code, Cursor, and Windsurf.
- Incremental refreshes with `--files` and `--commits`, plus continuous `--watch` mode.
- Exact DB/worktree freshness checks with `status --check`.
- `status --json` reports the last full-scan unknown-extension count so
  extension-table gaps are visible.
- Local-first storage in `.cdidx/codeindex.db`.
- 78 detected languages, with symbol and graph support where available.

## Documentation

| Document | Contents |
|---|---|
| [User Guide](USER_GUIDE.md) | Detailed installation, command examples, options, supported languages, MCP setup, and troubleshooting. |
| [Cloud Bootstrap](CLOUD_BOOTSTRAP_PROMPT.md) | Install guidance for restricted cloud agent sessions. |
| [Developer Guide](DEVELOPER_GUIDE.md) | Architecture, implementation notes, and release workflow. |
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

![.NET 8](https://img.shields.io/badge/.NET-8.0-512BD4?logo=dotnet&logoColor=white)
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
cdidx mcp
```

インデックス中は `Scanning...`、`Indexing...`、`67.0% [28/42]` のような
進捗行が表示され、最後に `2.4s` や `5m 42s` のような単位付き経過時間が出ます。
編集後は再構築ではなく `--files` や `--commits` で差分更新できます。詳細は
[クイックスタート](USER_GUIDE.md#クイックスタート)
を参照してください。

ターミナル、スクリプト、CI、AI ツールから同じリポジトリを繰り返し検索する
場合は `cdidx` が向いています。1回限りのテキスト検索には `rg` が向いています。

## 特長

- CLI-first の検索。人間向け出力と機械処理向け出力に対応。
- 全文検索、シンボル、参照、caller/callee、依存関係、map、inspect、excerpt
  コマンドを提供。
- Claude Code、Cursor、Windsurf などの AI クライアント向け MCP サーバー。
- `--files` と `--commits` による差分更新、および `--watch` による継続更新モード。
- `status --check` による DB と作業ツリーの完全一致確認。
- `status --json` で直近 full scan の未知拡張子数を返し、拡張子テーブルの
  抜けを確認可能。
- `.cdidx/codeindex.db` に保存するローカルファースト設計。
- 78 言語を検出し、対応言語ではシンボルとグラフも利用可能。

## ドキュメント

| ドキュメント | 内容 |
|---|---|
| [ユーザーガイド](USER_GUIDE.md#cdidx日本語) | 詳細なインストール、コマンド例、オプション、対応言語、MCP 設定、トラブルシュート。 |
| [クラウドブートストラップ](CLOUD_BOOTSTRAP_PROMPT.md#日本語) | 制限されたクラウドエージェント環境でのインストール手順。 |
| [開発者ガイド](DEVELOPER_GUIDE.md#開発者ガイド) | アーキテクチャ、実装メモ、リリース手順。 |
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

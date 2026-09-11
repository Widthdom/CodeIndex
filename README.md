# cdidx

[![Build and Test](https://github.com/Widthdom/CodeIndex/actions/workflows/dotnet.yml/badge.svg)](https://github.com/Widthdom/CodeIndex/actions/workflows/dotnet.yml)
[![CodeQL](https://github.com/Widthdom/CodeIndex/actions/workflows/codeql.yml/badge.svg)](https://github.com/Widthdom/CodeIndex/actions/workflows/codeql.yml)
[![Release](https://github.com/Widthdom/CodeIndex/actions/workflows/release.yml/badge.svg)](https://github.com/Widthdom/CodeIndex/actions/workflows/release.yml)

![.NET 8.x / 9.x tests](https://img.shields.io/badge/.NET-8.x%20%2F%209.x%20tests-512BD4?logo=dotnet&logoColor=white)
![C#](https://img.shields.io/badge/C%23-12-239120?logo=csharp&logoColor=white)
![Platform](https://img.shields.io/badge/Platform-Windows%20%7C%20macOS%20%7C%20Linux-lightgrey)
![License](https://img.shields.io/badge/License-FSL--1.1--ALv2-orange)
![SQLite](https://img.shields.io/badge/SQLite-FTS5-003B57?logo=sqlite&logoColor=white)

> **[日本語版はこちら / Japanese version](#cdidx日本語)**

**CLI code indexing, MCP search, and LSP editor lookup for local repositories.**

`cdidx` builds a local SQLite index for fast full-text, symbol, dependency, and
inspection queries. Index once, then reuse it from your terminal, scripts,
AI tools, or editor. Supports Windows, macOS, and Linux.

## Quick Start

Install with one of these:

```bash
brew install widthdom/tap/codeindex
dotnet tool install -g cdidx
curl -fsSL https://raw.githubusercontent.com/Widthdom/CodeIndex/main/install.sh | bash
```

Index a repository and run your first query:

```bash
cdidx .
cdidx status --check --json
cdidx search "handleRequest"
cdidx definition UserService
```

For AI tools and editors, run `cdidx mcp` or
`cdidx lsp --db .cdidx/codeindex.db`. See [AI Integration](USER_GUIDE.md#ai-integration)
for client setup and [installation](USER_GUIDE.md#installation) for other options.

## What it does

- Search text and symbols, inspect definitions, and trace references and dependencies.
- Run audit recipes and inspect repository structure and potential hotspots.
- Refresh the index incrementally and expose results through CLI, JSON, MCP, or LSP.

Extraction provides retrieval hints rather than compiler-grade analysis.
Conversation and code changes belong to the external tool using `cdidx`.
See [why cdidx](USER_GUIDE.md#why-cdidx), [comparison with rg](USER_GUIDE.md#cdidx-vs-rg),
and [supported languages](USER_GUIDE.md#supported-languages).

## Documentation

| Topic | Reference |
|---|---|
| Commands and examples | [User Guide](USER_GUIDE.md#command-reference) |
| Indexing and freshness | [Index a project](USER_GUIDE.md#index-a-project), [check status](USER_GUIDE.md#check-status) |
| Search and audits | [Search code](USER_GUIDE.md#search-code), [regex find controls](docs/find-scan-controls.md) |
| JSON fields and limits | [Output format](USER_GUIDE.md#json-output-format), [Status JSON contract](DEVELOPER_GUIDE.md#status-json-contract) |
| MCP, LSP, and compatibility | [AI Integration](USER_GUIDE.md#ai-integration), [Integration Policy](INTEGRATION_POLICY.md) |
| Installation and releases | [Distribution](DISTRIBUTION.md), [platforms](docs/platform-support.md), [release verification](USER_GUIDE.md#release-artifact-verification), [cloud bootstrap](CLOUD_BOOTSTRAP_PROMPT.md) |
| Development | [Developer Guide](DEVELOPER_GUIDE.md), [Testing Guide](TESTING_GUIDE.md), [Agent Guide](AGENT_GUIDE.md) |
| Changes and security | [Changelog](CHANGELOG.md), [Security Policy](SECURITY.md) |

## Contribution Policy

Issue reports, feature requests, and improvement suggestions are welcome.
External pull requests are currently not accepted; implementation and PR creation
are handled by the maintainer or trusted collaborators.

## License and Fair Source Use

CodeIndex and official `cdidx` binaries are source-available / Fair Source-style software
under [FSL-1.1-ALv2](LICENSE), unless stated otherwise. Marked integration materials
may use [Apache-2.0](LICENSES/Apache-2.0.txt).
See [commercial licensing](COMMERCIAL_LICENSE.md), [integration policy](INTEGRATION_POLICY.md),
and [trademarks](TRADEMARKS.md) for use and naming terms.

# cdidx（日本語）

> **[English version](#cdidx)**

**ローカルリポジトリ向けのコード索引・検索ツール。CLI、MCP、LSPから利用できます。**

`cdidx` はローカルの SQLite 索引を使い、全文・シンボル・依存関係の検索や
コードの調査を高速に行います。一度索引を作れば、ターミナル、スクリプト、
AIツール、エディターから繰り返し利用できます。Windows・macOS・Linuxに対応しています。

## すぐに試す

次のいずれかでインストールします。

```bash
brew install widthdom/tap/codeindex
dotnet tool install -g cdidx
curl -fsSL https://raw.githubusercontent.com/Widthdom/CodeIndex/main/install.sh | bash
```

リポジトリを索引化して検索します。

```bash
cdidx .
cdidx status --check --json
cdidx search "handleRequest"
cdidx definition UserService
```

AIツールやエディターから利用する場合は `cdidx mcp` または
`cdidx lsp --db .cdidx/codeindex.db` を起動します。設定は[AIとの連携](USER_GUIDE.md#aiとの連携)、
その他の導入方法は[インストール](USER_GUIDE.md#インストール)を参照してください。

## 主な機能

- 全文・シンボル検索、定義の確認、参照・依存関係の追跡。
- 監査レシピの実行、リポジトリ構造や変更が集中する箇所の調査。
- 索引の差分更新と、CLI・JSON・MCP・LSP経由での結果取得。

抽出結果はコード調査の手がかりであり、コンパイラー相当の解析ではありません。
対話やコード変更は `cdidx` を利用する外部ツールが担当します。
[なぜ cdidx なのか](USER_GUIDE.md#なぜ-cdidx-なのか)、[rgとの違い](USER_GUIDE.md#rg-との違い)、
[対応言語](USER_GUIDE.md#対応言語)も参照してください。

## ドキュメント

| 目的 | 参照先 |
|---|---|
| コマンドと使用例 | [ユーザーガイド](USER_GUIDE.md#コマンドリファレンス) |
| 索引と鮮度の管理 | [プロジェクトをインデックス](USER_GUIDE.md#プロジェクトをインデックス)、[状態確認](USER_GUIDE.md#状態確認) |
| 検索・監査 | [コード検索](USER_GUIDE.md#コード検索)、[正規表現 find の制御](docs/find-scan-controls.md#日本語) |
| JSONフィールドと上限 | [出力形式](USER_GUIDE.md#json-出力形式)、[Status JSON 契約](DEVELOPER_GUIDE.md#status-json-契約) |
| MCP・LSPと互換性 | [AIとの連携](USER_GUIDE.md#aiとの連携)、[統合ポリシー](INTEGRATION_POLICY.md) |
| 導入とリリース | [配布チャネル](DISTRIBUTION.md)、[対応環境](docs/platform-support.md)、[成果物の検証](USER_GUIDE.md#リリースアセットの検証)、[クラウドでの導入](CLOUD_BOOTSTRAP_PROMPT.md#日本語) |
| 開発 | [開発者ガイド](DEVELOPER_GUIDE.md#開発者ガイド)、[テストガイド](TESTING_GUIDE.md#テストガイド)、[エージェントガイド](AGENT_GUIDE.md) |
| 変更履歴とセキュリティ | [変更履歴](CHANGELOG.md)、[セキュリティポリシー](SECURITY.md) |

## コントリビューション方針

Issue報告、機能要望、改善提案を歓迎します。
現在、外部からのpull requestは受け付けていません。
実装とPR作成はメンテナーまたは信頼された共同開発者が担当します。

## ライセンスと Fair Source の扱い

CodeIndexと公式 `cdidx` バイナリは、別途明記されない限り
[FSL-1.1-ALv2](LICENSE) に基づく source-available / Fair Source-style software です。
明記された連携用の素材には [Apache-2.0](LICENSES/Apache-2.0.txt) を適用できます。
利用条件と名称の扱いは[商用ライセンス](COMMERCIAL_LICENSE.md)、
[統合ポリシー](INTEGRATION_POLICY.md)、[商標](TRADEMARKS.md)を参照してください。

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

**AI-native local code search for terminal and MCP workflows.**

`cdidx` indexes a repository into a local SQLite FTS5 database, then answers
full-text, symbol, dependency, and inspection queries without repeatedly
rescanning the same tree. It is built for humans and AI agents that need small,
structured code context from local repositories.

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

Details: [why cdidx](docs/USER_GUIDE.md#why-cdidx), [cdidx vs rg](docs/USER_GUIDE.md#cdidx-vs-rg),
and [cdidx vs VS Code workspace index](docs/USER_GUIDE.md#cdidx-vs-vs-code-workspace-index).

## Quick Start

```bash
curl -fsSL https://raw.githubusercontent.com/Widthdom/CodeIndex/main/install.sh | bash
cdidx .
cdidx status --check --json
cdidx search "handleRequest"
cdidx definition UserService
cdidx mcp
```

Use `cdidx` when a repository will be searched repeatedly from terminals,
scripts, CI, or AI tools. Use `rg` when you only need a one-off text scan.

## Highlights

- CLI-first search with human-readable and machine-oriented output.
- Full-text, symbol, reference, caller/callee, dependency, map, inspect, and
  excerpt commands.
- MCP server for AI clients such as Claude Code, Cursor, and Windsurf.
- Incremental refreshes with `--files` and `--commits`.
- Exact DB/worktree freshness checks with `status --check`.
- Local-first storage in `.cdidx/codeindex.db`.
- 78 detected languages, with symbol and graph support where available.

## Documentation

- [User Guide](docs/USER_GUIDE.md): detailed installation, command examples,
  options, supported languages, MCP setup, and troubleshooting.
- [Cloud Bootstrap](docs/CLOUD_BOOTSTRAP_PROMPT.md): install guidance for restricted
  cloud agent sessions.
- [Developer Guide](docs/DEVELOPER_GUIDE.md): architecture, implementation notes, and
  release workflow.
- [Testing Guide](docs/TESTING_GUIDE.md): test conventions and validation commands.
- [Self-Improvement Contract](docs/SELF_IMPROVEMENT.md): rules for agents improving
  CodeIndex itself.
- [Integration Policy](INTEGRATION_POLICY.md): permitted CLI, JSON, MCP, and
  integration use.

## License and Fair Source Use

CodeIndex and official `cdidx` binaries are source-available / Fair Source-style software
under [FSL-1.1-ALv2](LICENSE), unless a specific file or directory says
otherwise. Integration materials may be [Apache-2.0](LICENSES/Apache-2.0.txt)
where marked.

See [COMMERCIAL_LICENSE.md](COMMERCIAL_LICENSE.md), [INTEGRATION_POLICY.md](INTEGRATION_POLICY.md),
and [TRADEMARKS.md](TRADEMARKS.md) for commercial, integration, and naming
details.

# cdidx（日本語）

![License](https://img.shields.io/badge/License-FSL--1.1--ALv2-orange)

**ターミナルと MCP ワークフロー向けの、AI ネイティブなローカルコード検索です。**

`cdidx` はリポジトリをローカル SQLite FTS5 データベースにインデックスし、
同じツリーを何度も読み直さずに全文検索、シンボル、依存関係、inspect
クエリへ応答します。人間と AI エージェントの両方が、ローカルリポジトリから
小さく構造化されたコード文脈を取り出すためのツールです。

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

詳細: [なぜ cdidx なのか](docs/USER_GUIDE.md#なぜ-cdidx-なのか)、[rg との違い](docs/USER_GUIDE.md#rg-との違い)、
[VS Code workspace index との違い](docs/USER_GUIDE.md#vs-code-workspace-index-との違い)。

## すぐに試す

```bash
curl -fsSL https://raw.githubusercontent.com/Widthdom/CodeIndex/main/install.sh | bash
cdidx .
cdidx status --check --json
cdidx search "handleRequest"
cdidx definition UserService
cdidx mcp
```

ターミナル、スクリプト、CI、AI ツールから同じリポジトリを繰り返し検索する
場合は `cdidx` が向いています。1回限りのテキスト検索には `rg` が向いています。

## 特長

- CLI-first の検索。人間向け出力と機械処理向け出力に対応。
- 全文検索、シンボル、参照、caller/callee、依存関係、map、inspect、excerpt
  コマンドを提供。
- Claude Code、Cursor、Windsurf などの AI クライアント向け MCP サーバー。
- `--files` と `--commits` による差分更新。
- `status --check` による DB と作業ツリーの完全一致確認。
- `.cdidx/codeindex.db` に保存するローカルファースト設計。
- 78 言語を検出し、対応言語ではシンボルとグラフも利用可能。

## ドキュメント

- [ユーザーガイド](docs/USER_GUIDE.md): 詳細なインストール、コマンド例、オプション、
  対応言語、MCP 設定、トラブルシュート。
- [クラウドブートストラップ](docs/CLOUD_BOOTSTRAP_PROMPT.md): 制限されたクラウド
  エージェント環境でのインストール手順。
- [開発者ガイド](docs/DEVELOPER_GUIDE.md): アーキテクチャ、実装メモ、リリース手順。
- [テストガイド](docs/TESTING_GUIDE.md): テスト規約と検証コマンド。
- [自己改善コントラクト](docs/SELF_IMPROVEMENT.md): CodeIndex 自身を改善する
  エージェント向けルール。
- [統合ポリシー](INTEGRATION_POLICY.md): CLI、JSON、MCP、各種統合で許可される利用。

## ライセンスと Fair Source の扱い

CodeIndex と公式 `cdidx` バイナリは、ファイルやディレクトリで別途明記されない
限り [FSL-1.1-ALv2](LICENSE) の source-available / Fair Source-style software
です。統合用の素材は、明記されている場合 [Apache-2.0](LICENSES/Apache-2.0.txt)
で利用できます。

商用利用、統合、名称の扱いについては [COMMERCIAL_LICENSE.md](COMMERCIAL_LICENSE.md)、
[INTEGRATION_POLICY.md](INTEGRATION_POLICY.md)、[TRADEMARKS.md](TRADEMARKS.md) を
参照してください。

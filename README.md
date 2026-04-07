# CodeIndex

> **[日本語版はこちら / Japanese version](#codeindex日本語)**

[![Build and Test](https://github.com/Widthdom/CodeIndex/actions/workflows/dotnet.yml/badge.svg)](https://github.com/Widthdom/CodeIndex/actions/workflows/dotnet.yml)
[![CodeQL](https://github.com/Widthdom/CodeIndex/actions/workflows/codeql.yml/badge.svg)](https://github.com/Widthdom/CodeIndex/actions/workflows/codeql.yml)
[![Release](https://github.com/Widthdom/CodeIndex/actions/workflows/release.yml/badge.svg)](https://github.com/Widthdom/CodeIndex/actions/workflows/release.yml)

![.NET 8](https://img.shields.io/badge/.NET-8.0-512BD4?logo=dotnet&logoColor=white)
![C#](https://img.shields.io/badge/C%23-12-239120?logo=csharp&logoColor=white)
![Platform](https://img.shields.io/badge/Platform-Windows%20%7C%20macOS%20%7C%20Linux-lightgrey)
![License](https://img.shields.io/badge/License-MIT-blue)
![SQLite](https://img.shields.io/badge/SQLite-FTS5-003B57?logo=sqlite&logoColor=white)

A CLI tool that indexes large codebases into a SQLite database for fast search. Works for both humans and AI agents.

## Installation

```bash
# Build
dotnet build src/CodeIndex/CodeIndex.csproj -c Release

# Publish as a single binary
dotnet publish src/CodeIndex/CodeIndex.csproj -c Release -o ./publish

# Optional: add to PATH
# Linux / macOS
cp ./publish/cdidx /usr/local/bin/cdidx

# Windows (PowerShell — run as Administrator)
# Copy-Item .\publish\cdidx.exe C:\Tools\cdidx.exe
# Then add C:\Tools to your system PATH if not already there
```

## Quick Start

### Index a project

```bash
cdidx ./myproject
cdidx ./myproject --rebuild     # full rebuild from scratch
cdidx ./myproject --verbose     # show per-file details
```

### Search code

```bash
cdidx search "authenticate"              # full-text search
cdidx search "handleRequest" --lang go   # filter by language
cdidx search "TODO" --limit 50           # more results
```

### Search symbols (functions, classes, etc.)

```bash
cdidx symbols UserService              # find by name
cdidx symbols --kind class             # all classes
cdidx symbols --kind function --lang python
```

### List files

```bash
cdidx files                            # all indexed files
cdidx files --lang csharp              # only C# files
```

### Check status

```bash
cdidx status
```

## Options

| Option | Applies to | Description |
|---|---|---|
| `--db <path>` | All commands | Database file path (default: `codeindex.db`) |
| `--json` | All commands | JSON output (default for search/symbols/files) |
| `--no-json` | Query commands | Force human-readable output |
| `--limit <n>` | Query commands | Max results (default: 20) |
| `--lang <lang>` | Query commands | Filter by language |
| `--kind <kind>` | `symbols` | Filter by symbol kind (function/class/import) |
| `--rebuild` | `index` | Delete existing DB and rebuild |
| `--verbose` | `index` | Show detailed per-file output |
| `--commits <id...>` | `index` | Update only files changed in specified commits |
| `--files <path...>` | `index` | Update only the specified files |

## How it works

cdidx scans your project directory, splits each source file into overlapping chunks, and stores everything in a SQLite database with FTS5 full-text search. Incremental mode (default) skips files that haven't changed, so re-indexing after a branch switch is fast.

## Supported languages

| Language | Extensions | Symbols |
|---|---|:---:|
| Python | `.py` | yes |
| JavaScript | `.js`, `.jsx` | yes |
| TypeScript | `.ts`, `.tsx` | yes |
| C# | `.cs` | yes |
| Go | `.go` | yes |
| Rust | `.rs` | yes |
| Java | `.java` | yes |
| Kotlin | `.kt` | yes |
| Ruby | `.rb` | yes |
| C | `.c`, `.h` | yes |
| C++ | `.cpp` | yes |
| PHP | `.php` | yes |
| Swift | `.swift` | yes |
| Shell | `.sh` | -- |
| SQL | `.sql` | -- |
| Markdown | `.md` | -- |
| YAML | `.yaml`, `.yml` | -- |
| JSON | `.json` | -- |
| TOML | `.toml` | -- |
| HTML | `.html` | -- |
| CSS | `.css`, `.scss` | -- |
| Vue | `.vue` | -- |
| Svelte | `.svelte` | -- |
| Terraform | `.tf` | -- |

All languages are fully searchable via FTS5. Languages with **Symbols = yes** also support structured queries by function/class/import name.

## Prerequisites: sqlite3

AI agents that query the database directly via SQL need the `sqlite3` CLI.

| OS | Status |
|---|---|
| **macOS** | Pre-installed |
| **Linux** | Usually pre-installed. If not: `sudo apt install sqlite3` |
| **Windows** | `winget install SQLite.SQLite` or `scoop install sqlite` |

## More

- [Developer Guide](DEVELOPER_GUIDE.md) — Architecture, database schema, FTS5 internals, AI integration, design decisions

---

<a id="codeindex日本語"></a>
# CodeIndex（日本語）

![.NET 8](https://img.shields.io/badge/.NET-8.0-512BD4?logo=dotnet&logoColor=white)
![C#](https://img.shields.io/badge/C%23-12-239120?logo=csharp&logoColor=white)
![Platform](https://img.shields.io/badge/Platform-Windows%20%7C%20macOS%20%7C%20Linux-lightgrey)
![License](https://img.shields.io/badge/License-MIT-blue)
![SQLite](https://img.shields.io/badge/SQLite-FTS5-003B57?logo=sqlite&logoColor=white)

大規模コードベースをSQLiteデータベースにインデックスし、高速検索を実現するCLIツールです。人間にもAIエージェントにも対応しています。

## インストール

```bash
# ビルド
dotnet build src/CodeIndex/CodeIndex.csproj -c Release

# 単一バイナリとしてパブリッシュ
dotnet publish src/CodeIndex/CodeIndex.csproj -c Release -o ./publish

# 任意: PATHに追加
# Linux / macOS
cp ./publish/cdidx /usr/local/bin/cdidx

# Windows（PowerShell — 管理者として実行）
# Copy-Item .\publish\cdidx.exe C:\Tools\cdidx.exe
# C:\Tools がPATHに含まれていない場合は追加してください
```

## クイックスタート

### プロジェクトをインデックス

```bash
cdidx ./myproject
cdidx ./myproject --rebuild     # 完全再構築
cdidx ./myproject --verbose     # ファイルごとの詳細表示
```

### コード検索

```bash
cdidx search "authenticate"              # 全文検索
cdidx search "handleRequest" --lang go   # 言語でフィルタ
cdidx search "TODO" --limit 50           # 結果数を増やす
```

### シンボル検索（関数、クラスなど）

```bash
cdidx symbols UserService              # 名前で検索
cdidx symbols --kind class             # すべてのクラス
cdidx symbols --kind function --lang python
```

### ファイル一覧

```bash
cdidx files                            # 全インデックス済みファイル
cdidx files --lang csharp              # C#ファイルのみ
```

### 状態確認

```bash
cdidx status
```

## オプション一覧

| オプション | 対象 | 説明 |
|---|---|---|
| `--db <path>` | 全コマンド | DBファイルパス（デフォルト: `codeindex.db`） |
| `--json` | 全コマンド | JSON出力（search/symbols/filesはデフォルト） |
| `--no-json` | クエリ系 | 人間向け出力を強制 |
| `--limit <n>` | クエリ系 | 最大結果数（デフォルト: 20） |
| `--lang <lang>` | クエリ系 | 言語でフィルタ |
| `--kind <kind>` | `symbols` | シンボル種別でフィルタ（function/class/import） |
| `--rebuild` | `index` | 既存DBを削除して再構築 |
| `--verbose` | `index` | ファイルごとの詳細出力 |
| `--commits <id...>` | `index` | 指定コミットの変更ファイルのみ更新 |
| `--files <path...>` | `index` | 指定ファイルのみ更新 |

## 動作の仕組み

cdidxはプロジェクトディレクトリを走査し、各ソースファイルを重複を持つチャンクに分割し、FTS5全文検索付きのSQLiteデータベースに格納します。インクリメンタルモード（デフォルト）では変更のないファイルをスキップするため、ブランチ切り替え後の再インデックスも高速です。

## 対応言語

| 言語 | 拡張子 | シンボル |
|---|---|:---:|
| Python | `.py` | yes |
| JavaScript | `.js`, `.jsx` | yes |
| TypeScript | `.ts`, `.tsx` | yes |
| C# | `.cs` | yes |
| Go | `.go` | yes |
| Rust | `.rs` | yes |
| Java | `.java` | yes |
| Kotlin | `.kt` | yes |
| Ruby | `.rb` | yes |
| C | `.c`, `.h` | yes |
| C++ | `.cpp` | yes |
| PHP | `.php` | yes |
| Swift | `.swift` | yes |
| Shell | `.sh` | -- |
| SQL | `.sql` | -- |
| Markdown | `.md` | -- |
| YAML | `.yaml`, `.yml` | -- |
| JSON | `.json` | -- |
| TOML | `.toml` | -- |
| HTML | `.html` | -- |
| CSS | `.css`, `.scss` | -- |
| Vue | `.vue` | -- |
| Svelte | `.svelte` | -- |
| Terraform | `.tf` | -- |

全言語がFTS5による全文検索に対応。**シンボル = yes** の言語は関数・クラス・インポート名での構造化検索にも対応しています。

## 前提条件: sqlite3

AIエージェントがDBを直接SQL検索する場合、`sqlite3` CLIが必要です。

| OS | 状況 |
|---|---|
| **macOS** | プリインストール済み |
| **Linux** | 通常プリインストール済み。未導入時: `sudo apt install sqlite3` |
| **Windows** | `winget install SQLite.SQLite` または `scoop install sqlite` |

## もっと詳しく

- [開発者ガイド](DEVELOPER_GUIDE.md) — アーキテクチャ、DBスキーマ、FTS5の内部構造、AI連携、設計判断

# Developer Guide

> **[日本語版はこちら / Japanese version](#開発者ガイド)**

## Build & Test

```bash
dotnet build
dotnet test
dotnet run --project src/CodeIndex -- <command> [options]
```

## Architecture

```
src/CodeIndex/
  Program.cs                  — CLI entry point, subcommand routing
  Database/
    DbContext.cs              — SQLite connection, WAL mode, schema init
    DbWriter.cs               — UPSERT, batch insert, stale file purge
    DbReader.cs               — FTS search, symbol lookup, file listing, status
  Indexer/
    FileIndexer.cs            — Directory scan, language detection, FileRecord building
    ChunkSplitter.cs          — 80-line chunks with 10-line overlap
    SymbolExtractor.cs        — Regex-based symbol extraction (13 languages)
  Models/
    FileRecord.cs             — File metadata DTO
    ChunkRecord.cs            — Chunk DTO
    SymbolRecord.cs           — Symbol DTO
tests/CodeIndex.Tests/
  UnitTest1.cs                — xUnit tests (chunker, symbols, indexer, DB integration)
```

### Indexing pipeline

```
Directory scan → Language detection → File read (UTF-8)
  → UPSERT file record
  → Split into chunks (80 lines, 10-line overlap)
  → Extract symbols via regex
  → Batch insert chunks + symbols (500 per transaction)
  → Populate FTS5 index
```

## Database schema

### Tables

```sql
-- File metadata
files (
    id          INTEGER PRIMARY KEY AUTOINCREMENT,
    path        TEXT NOT NULL UNIQUE,       -- relative path from project root
    lang        TEXT,                        -- detected language (e.g. "python")
    size        INTEGER,                    -- file size in bytes
    lines       INTEGER,                    -- line count
    snippet     TEXT,                        -- first 2000 characters
    checksum    TEXT,                        -- SHA256 of content
    modified    DATETIME,                   -- file modification time (UTC)
    indexed_at  DATETIME DEFAULT CURRENT_TIMESTAMP
)

-- Content chunks for full-text search
chunks (
    id          INTEGER PRIMARY KEY AUTOINCREMENT,
    file_id     INTEGER NOT NULL REFERENCES files(id) ON DELETE CASCADE,
    chunk_index INTEGER NOT NULL,           -- 0-based chunk position
    start_line  INTEGER,                    -- 1-based start line
    end_line    INTEGER,                    -- 1-based end line (inclusive)
    content     TEXT,
    UNIQUE(file_id, chunk_index)
)

-- Extracted symbols (functions, classes, imports)
symbols (
    id      INTEGER PRIMARY KEY AUTOINCREMENT,
    file_id INTEGER NOT NULL REFERENCES files(id) ON DELETE CASCADE,
    kind    TEXT,                            -- "function", "class", or "import"
    name    TEXT,
    line    INTEGER                         -- 1-based line number
)

-- FTS5 virtual table mirroring chunks.content
fts_chunks USING fts5(content, content='chunks', content_rowid='id')
```

### Indexes

```sql
idx_files_lang      ON files(lang)
idx_files_modified  ON files(modified)
idx_files_path      ON files(path)
idx_chunks_file     ON chunks(file_id)
idx_symbols_name    ON symbols(name)
```

### Entity-Relationship

```
files 1──N chunks 1──1 fts_chunks (content mirror)
files 1──N symbols
```

## Why a database instead of grep?

On small projects, `grep` works fine. But as a codebase grows to tens of thousands of files, `grep` becomes a bottleneck — especially when an AI agent calls it repeatedly. cdidx solves this by **reading every file once at index time** and building a search structure so that queries never need to touch the original files again.

`grep -r "keyword" .` performs a brute-force linear scan: it opens every file, reads every line, and checks for a match. The tenth search costs the same as the first. cdidx shifts the expensive work to a one-time indexing step, and subsequent searches are cheap lookups into the pre-built database.

| Factor | `grep -r` | cdidx (SQLite FTS5) |
|---|---|---|
| **Search algorithm** | Linear scan of every file, every time | Token lookup in inverted index |
| **Repeated searches** | Same full cost each time | Near-instant after initial index |
| **Startup cost** | None | One-time indexing (incremental updates after) |
| **What is stored** | Nothing — reads files on the fly | Source text in chunks + inverted index of tokens |
| **Structured queries** | Text matching only | Filter by language, path, symbol kind, line range |
| **Symbol awareness** | None — just raw text | Knows function/class/import names and locations |
| **AI token cost** | Returns raw lines — noisy, high token usage | Returns precise chunks with file path and line numbers |

### When to use which

| Scenario | Recommended |
|---|---|
| Quick one-off search in a small project | `grep` |
| Repeated searches across a large codebase | **cdidx** |
| AI agent performing multiple code lookups | **cdidx** |
| Finding all usages of a function by name | **cdidx** (`symbols` table) |
| Searching binary files or non-code content | `grep` |

## FTS5 full-text search

[FTS5](https://www.sqlite.org/fts5.html) (Full-Text Search 5) is a SQLite extension that provides an **inverted index** for full-text search: it maps each token (word) to a list of documents containing it, enabling O(1) lookups by keyword rather than scanning every row.

FTS5 works through a **virtual table** — a table that looks and behaves like a normal SQLite table but stores its data in a specialized format optimized for text search.

### What is an inverted index?

An inverted index maps each word (token) to the list of documents (or rows) that contain it — like the index at the back of a textbook.

For example, suppose three chunks contain the following code:

| Chunk ID | Content (simplified) |
|---|---|
| 1 | `handleRequest(ctx)` |
| 2 | `sendResponse(ctx)` |
| 3 | `handleRequest(req); sendResponse(res)` |

The inverted index built by FTS5 would look like:

| Token | Chunk IDs |
|---|---|
| `handleRequest` | 1, 3 |
| `sendResponse` | 2, 3 |
| `ctx` | 1, 2 |
| `req` | 3 |
| `res` | 3 |

When you search for `handleRequest`, FTS5 reads the entry for that token and immediately returns chunk IDs `{1, 3}` — no scanning required.

### How it differs from B-tree indexes

A B-tree (balanced tree) is the default index structure in SQLite. It organizes values in a sorted, tree-shaped hierarchy — similar to how a phone book is sorted alphabetically:

```
            [go | python]
           /      |       \
    [csharp]  [java, kotlin]  [rust, typescript]
```

B-tree indexes are good for exact matches (`WHERE lang = 'csharp'`), range queries (`WHERE modified > '2025-01-01'`), and sorting. However, they cannot efficiently answer "which rows contain the word `handleRequest` somewhere in a text column?" — that requires FTS5.

| | B-tree index | FTS5 inverted index |
|---|---|---|
| **Use case** | Exact match, range, prefix on a single column | Natural language keyword search across text |
| **Lookup** | `WHERE path = 'foo.py'` | `WHERE fts_chunks MATCH 'authenticate'` |
| **Structure** | Sorted tree of column values | Token → document ID posting lists |
| **Ranking** | N/A (returns exact matches) | BM25 relevance scoring |
| **Used on** | `path`, `lang`, `modified`, `file_id`, `name` | `chunks.content` (code text) |

These two index types complement each other. A typical query might use FTS5 to find matching chunks and then use B-tree indexes to filter by language or file path.

### The `fts_chunks` virtual table

```sql
CREATE VIRTUAL TABLE IF NOT EXISTS fts_chunks USING fts5(
    content,
    content='chunks',
    content_rowid='id'
);
```

| Parameter | Meaning |
|---|---|
| `USING fts5(...)` | Use the FTS5 engine to manage this virtual table |
| `content` | The column to index — corresponds to `chunks.content` (the actual code text) |
| `content='chunks'` | **External-content table** — `fts_chunks` does not store a copy of the text. It references `chunks`. |
| `content_rowid='id'` | The `rowid` of each FTS5 entry matches `chunks.id` for direct row lookup |

### Content sync

`fts_chunks` is a **content-external** FTS5 table (`content='chunks'`). It does not store the original text; instead, it points to `chunks.id` via `content_rowid`. This avoids doubling storage. cdidx manually keeps the FTS index in sync during insert and delete operations.

### Query syntax

FTS5 supports advanced query syntax:

```sql
-- Single term
WHERE fts_chunks MATCH 'authenticate'

-- Phrase (exact sequence)
WHERE fts_chunks MATCH '"handle request"'

-- Boolean operators
WHERE fts_chunks MATCH 'auth AND token'
WHERE fts_chunks MATCH 'auth OR login'
WHERE fts_chunks MATCH 'auth NOT oauth'

-- Prefix search
WHERE fts_chunks MATCH 'auth*'

-- Column filter (only one column here, but useful in multi-column FTS)
WHERE fts_chunks MATCH 'content:authenticate'
```

### How the search works

When you run:
```sql
SELECT f.path, c.start_line, c.content
FROM fts_chunks fc
JOIN chunks c ON c.id = fc.rowid
JOIN files f ON f.id = c.file_id
WHERE fts_chunks MATCH 'handleRequest'
LIMIT 20;
```

1. FTS5 looks up `handleRequest` in its inverted index → gets a list of matching chunk `rowid`s directly
2. Joins back to `chunks` to get the 80-line code block with start/end line numbers
3. Joins to `files` to get the file path and language

No files are opened. No directories are scanned. The entire search runs inside SQLite.

## Chunking strategy

Files are split into **80-line chunks with 10-line overlap**. The overlap ensures that a symbol definition or code block spanning a chunk boundary will appear in full in at least one chunk.

```
Lines   1-80   → Chunk 0
Lines  71-150  → Chunk 1  (10-line overlap with chunk 0)
Lines 141-220  → Chunk 2  (10-line overlap with chunk 1)
...
```

The step size is `80 - 10 = 70` lines. A file with N lines produces `ceil((N - 80) / 70) + 1` chunks (minimum 1).

## Symbol extraction

Symbols are extracted with **compiled regex patterns**, matching one line at a time. Each language has patterns for functions, classes, and sometimes imports. Named capture group `(?<name>\w+)` extracts the identifier.

Supported symbol kinds by language:

| Language | function | class | import |
|---|:---:|:---:|:---:|
| Python | def, async def | class | -- |
| JavaScript | function, const/let/var arrow | class | import...from |
| TypeScript | function, arrow | class, interface, type, enum | import...from |
| C# | methods, constructors | class, interface, enum, record, struct | -- |
| Go | func, methods | type struct/interface | -- |
| Rust | fn | struct, enum, trait, impl | -- |
| Java | methods | class, interface, enum | -- |
| Kotlin | fun | class, interface, enum class, object | -- |
| Ruby | def | class, module | -- |
| C | functions | struct, enum | -- |
| C++ | functions | class, struct, namespace, enum | -- |
| PHP | function | class, interface, trait, enum | -- |
| Swift | func | class, struct, enum, protocol | -- |

Regex-based extraction is intentionally simple. Speed and portability are prioritized over AST-level accuracy.

## Incremental indexing

By default, cdidx compares each file's `modified` timestamp (UTC) against the stored value in the database. If unchanged, the file is skipped entirely.

When a file is re-indexed:
1. The file record is upserted (`INSERT OR REPLACE`)
2. Old chunks and FTS entries for that file are deleted
3. Old symbols for that file are deleted
4. New chunks, FTS entries, and symbols are inserted

### Stale file purge

Before indexing begins, cdidx queries all file paths from the database and checks each against the filesystem. Files that no longer exist on disk (e.g., after a branch switch or deletion) are removed along with their chunks and symbols.

| Situation | What happens |
|---|---|
| File unchanged across branches | Skipped (instant) |
| File content changed | Re-indexed |
| File deleted after checkout | Purged from DB |
| File added after checkout | Indexed as new |

```mermaid
flowchart LR
    A[git checkout branch-B] --> B[cdidx .]
    B --> C{Per-file check}
    C -->|Unchanged| D[Skip]
    C -->|Changed| E[Re-index]
    C -->|Deleted| F[Purge from DB]
    C -->|New| G[Index as new]
    D & E & F & G --> H[DB = branch-B in sync]
```

### Partial update mode

Use `--commits` or `--files` to update only specific files instead of scanning the entire project:

```bash
cdidx ./myproject --commits abc123 def456   # files changed in these commits
cdidx ./myproject --files src/app.cs        # specific files only
```

`--commits` uses `git diff-tree --no-commit-id -r --name-only` to resolve changed file paths.

## AI integration

### Setting up CLAUDE.md

To let AI agents use the generated `codeindex.db`, place a `CLAUDE.md` in your project root:

````markdown
# Code Search Rules

This project has a `codeindex.db` file.
When searching code, **query this SQLite database** instead of using `find`, `grep`, or `ls -R`.

## Queries

### Full-text search
```sql
SELECT f.path, c.start_line, c.content
FROM fts_chunks fc
JOIN chunks c ON c.id = fc.rowid
JOIN files f ON f.id = c.file_id
WHERE fts_chunks MATCH 'keyword'
LIMIT 20;
```

### Search by function/class name
```sql
SELECT f.path, s.name, s.line
FROM symbols s
JOIN files f ON f.id = s.file_id
WHERE s.kind = 'function' AND s.name LIKE '%keyword%';
```
````

### CLI-based integration

If `cdidx` is on PATH, AI agents can use the CLI directly:

```bash
cdidx search "keyword"           # JSON lines output, ready for parsing
cdidx symbols "ClassName"        # structured symbol search
```

### Output format

Query commands (`search`, `symbols`, `files`) default to **JSON lines** (one JSON object per line), designed for easy parsing by AI agents. Use `--no-json` for human-readable output.

```json
{"path":"src/auth.py","lang":"python","start_line":1,"end_line":80,"content":"def authenticate(user):\n ...","score":-1.5}
```

## Exit codes

| Code | Meaning |
|---|---|
| `0` | Success |
| `1` | Usage error (invalid arguments) |
| `2` | Not found (no search results, missing directory) |
| `3` | Database error |

## Design decisions

- **No ORM** — Raw `Microsoft.Data.Sqlite` with parameterized queries. Keeps dependencies minimal and control explicit.
- **Batch commits** — 500 records per transaction for write performance. Reduces fsync overhead.
- **WAL mode** — Write-Ahead Logging for concurrent read/write access and crash safety.
- **Content-external FTS5** — Avoids doubling storage by pointing to `chunks` table instead of storing a copy.
- **Regex symbol extraction** — No AST parsers, no language-specific dependencies. Trades accuracy for speed and portability.
- **JSON-first output** — Query commands default to JSON lines for AI consumption. `--no-json` for humans.
- **Manual arg parsing** — `System.CommandLine` was removed to reduce dependencies. Simple switch-based parsing.
- **SHA256 checksums** — Stored per file for integrity verification (not currently used for change detection, but available).
- **UTF-8 with fallback** — Invalid UTF-8 bytes are replaced with U+FFFD rather than failing the entire file.

## Coding conventions

- Comments are bilingual (English / Japanese), e.g. `// Enable WAL mode / WALモードを有効化`
- Documentation (README, CHANGELOG) is structured: English first, then Japanese.
- No unnecessary packages.

---

<a id="開発者ガイド"></a>
# 開発者ガイド

## ビルド・テスト

```bash
dotnet build
dotnet test
dotnet run --project src/CodeIndex -- <command> [options]
```

## アーキテクチャ

```
src/CodeIndex/
  Program.cs                  — CLIエントリポイント、サブコマンドルーティング
  Database/
    DbContext.cs              — SQLite接続、WALモード、スキーマ初期化
    DbWriter.cs               — UPSERT、バッチ挿入、古いファイルのパージ
    DbReader.cs               — FTS検索、シンボル検索、ファイル一覧、ステータス
  Indexer/
    FileIndexer.cs            — ディレクトリ走査、言語検出、FileRecord構築
    ChunkSplitter.cs          — 80行チャンク（10行重複）
    SymbolExtractor.cs        — 正規表現によるシンボル抽出（13言語対応）
  Models/
    FileRecord.cs             — ファイルメタデータDTO
    ChunkRecord.cs            — チャンクDTO
    SymbolRecord.cs           — シンボルDTO
tests/CodeIndex.Tests/
  UnitTest1.cs                — xUnitテスト（チャンク、シンボル、インデクサー、DB統合）
```

### インデックスパイプライン

```
ディレクトリ走査 → 言語検出 → ファイル読み込み（UTF-8）
  → ファイルレコードUPSERT
  → チャンク分割（80行、10行重複）
  → 正規表現でシンボル抽出
  → チャンク＋シンボルをバッチ挿入（1トランザクション500件）
  → FTS5インデックス反映
```

## データベーススキーマ

### テーブル

```sql
-- ファイルメタデータ
files (
    id          INTEGER PRIMARY KEY AUTOINCREMENT,
    path        TEXT NOT NULL UNIQUE,       -- プロジェクトルートからの相対パス
    lang        TEXT,                        -- 検出された言語（例: "python"）
    size        INTEGER,                    -- ファイルサイズ（バイト）
    lines       INTEGER,                    -- 行数
    snippet     TEXT,                        -- 先頭2000文字
    checksum    TEXT,                        -- コンテンツのSHA256
    modified    DATETIME,                   -- ファイル更新日時（UTC）
    indexed_at  DATETIME DEFAULT CURRENT_TIMESTAMP
)

-- 全文検索用コンテンツチャンク
chunks (
    id          INTEGER PRIMARY KEY AUTOINCREMENT,
    file_id     INTEGER NOT NULL REFERENCES files(id) ON DELETE CASCADE,
    chunk_index INTEGER NOT NULL,           -- 0始まりのチャンク位置
    start_line  INTEGER,                    -- 1始まりの開始行
    end_line    INTEGER,                    -- 1始まりの終了行（含む）
    content     TEXT,
    UNIQUE(file_id, chunk_index)
)

-- 抽出されたシンボル（関数、クラス、インポート）
symbols (
    id      INTEGER PRIMARY KEY AUTOINCREMENT,
    file_id INTEGER NOT NULL REFERENCES files(id) ON DELETE CASCADE,
    kind    TEXT,                            -- "function"、"class"、または"import"
    name    TEXT,
    line    INTEGER                         -- 1始まりの行番号
)

-- chunks.contentをミラーするFTS5仮想テーブル
fts_chunks USING fts5(content, content='chunks', content_rowid='id')
```

### インデックス

```sql
idx_files_lang      ON files(lang)
idx_files_modified  ON files(modified)
idx_files_path      ON files(path)
idx_chunks_file     ON chunks(file_id)
idx_symbols_name    ON symbols(name)
```

### エンティティ関連図

```
files 1──N chunks 1──1 fts_chunks（コンテンツミラー）
files 1──N symbols
```

## なぜgrepではなくデータベースなのか？

小規模プロジェクトなら `grep` で十分です。しかしファイルが数万規模になると `grep` はボトルネックになります。特にAIエージェントが繰り返し検索を実行するケースで顕著です。cdidxは**すべてのファイルを一度だけ読み込んで検索用の構造を構築する**ことで、以降の検索で元のファイルを一切開かずに済むようにします。

`grep -r "keyword" .` は力任せの線形スキャンです。毎回すべてのファイルを開き、すべての行を読み、マッチを確認します。10回目の検索でも1回目と同じコストです。cdidxは重い処理を一度きりのインデックス作成ステップに集約し、以降の検索は事前構築されたデータベースへの軽い参照で済みます。

| 比較項目 | `grep -r` | cdidx（SQLite FTS5） |
|---|---|---|
| **検索アルゴリズム** | 毎回全ファイルを線形スキャン | 転置インデックスでのトークン参照 |
| **繰り返し検索** | 毎回同じフルコスト | 初回インデックス後はほぼ即時 |
| **初期コスト** | なし | 一度きりのインデックス作成（以降はインクリメンタル更新） |
| **保存内容** | なし — 毎回ファイルを読み込み | チャンク化されたソーステキスト＋トークンの転置インデックス |
| **構造化クエリ** | テキストマッチのみ | 言語、パス、シンボル種別、行範囲でフィルタ可能 |
| **シンボル認識** | なし — 生テキストのみ | 関数・クラス・インポート名と位置を認識 |
| **AIトークンコスト** | 生の行を返す — ノイズが多くトークン消費大 | ファイルパスと行番号付きの正確なチャンクを返す |

### 使い分け

| シナリオ | 推奨 |
|---|---|
| 小規模プロジェクトでの一回きりの検索 | `grep` |
| 大規模コードベースでの繰り返し検索 | **cdidx** |
| AIエージェントによる複数回のコード検索 | **cdidx** |
| 関数名で全使用箇所を検索 | **cdidx**（`symbols`テーブル） |
| バイナリファイルや非コードコンテンツの検索 | `grep` |

## FTS5 全文検索

[FTS5](https://www.sqlite.org/fts5.html)（Full-Text Search 5）はSQLiteの拡張で、全文検索用の**転置インデックス**を提供します。各トークン（単語）からそれを含むドキュメントのリストへのマッピングを構築し、全行スキャンではなくO(1)のキーワード検索を実現します。

FTS5は**仮想テーブル**で動作します。通常のSQLiteテーブルと同じように見え、`SELECT`やJOINが可能ですが、テキスト検索に最適化された特殊な形式でデータを格納します。

### 転置インデックスとは？

転置インデックスは、各単語（トークン）からそれを含むドキュメント（行）のリストへのマッピングです。教科書の巻末索引のようなものです。

例えば、3つのチャンクに以下のコードが含まれるとします:

| チャンクID | 内容（簡略化） |
|---|---|
| 1 | `handleRequest(ctx)` |
| 2 | `sendResponse(ctx)` |
| 3 | `handleRequest(req); sendResponse(res)` |

FTS5が構築する転置インデックス:

| トークン | チャンクID |
|---|---|
| `handleRequest` | 1, 3 |
| `sendResponse` | 2, 3 |
| `ctx` | 1, 2 |
| `req` | 3 |
| `res` | 3 |

`handleRequest` で検索すると、FTS5はそのトークンのエントリを読み、チャンクID `{1, 3}` を即座に返します。スキャンは不要です。

### B-treeインデックスとの違い

B-tree（平衡木）はSQLiteのデフォルトのインデックス構造です。値をソート済みのツリー型階層に整理します:

```
            [go | python]
           /      |       \
    [csharp]  [java, kotlin]  [rust, typescript]
```

B-treeインデックスは完全一致（`WHERE lang = 'csharp'`）、範囲クエリ（`WHERE modified > '2025-01-01'`）、ソートに適しています。しかし「テキストカラムのどこかに `handleRequest` という単語を含む行はどれか？」には効率的に答えられません。FTS5が必要です。

| | B-treeインデックス | FTS5転置インデックス |
|---|---|---|
| **用途** | 単一カラムの完全一致・範囲・前方一致 | テキスト全体に対する自然言語キーワード検索 |
| **検索例** | `WHERE path = 'foo.py'` | `WHERE fts_chunks MATCH 'authenticate'` |
| **構造** | カラム値のソート済みツリー | トークン → ドキュメントIDのポスティングリスト |
| **ランキング** | なし（完全一致を返す） | BM25関連度スコアリング |
| **使用対象** | `path`, `lang`, `modified`, `file_id`, `name` | `chunks.content`（コードテキスト） |

この2種類のインデックスは相互補完的です。典型的なクエリではFTS5でマッチするチャンクを見つけ、B-treeインデックスで言語やファイルパスでフィルタします。

### `fts_chunks` 仮想テーブル

```sql
CREATE VIRTUAL TABLE IF NOT EXISTS fts_chunks USING fts5(
    content,
    content='chunks',
    content_rowid='id'
);
```

| パラメータ | 意味 |
|---|---|
| `USING fts5(...)` | FTS5エンジンでこの仮想テーブルを管理 |
| `content` | インデックス対象のカラム — `chunks.content`（コード本文）に対応 |
| `content='chunks'` | **外部コンテンツテーブル** — `fts_chunks`はテキストのコピーを保存せず`chunks`を参照 |
| `content_rowid='id'` | 各FTS5エントリの`rowid`が`chunks.id`に一致し、直接行参照が可能 |

### コンテンツ同期

`fts_chunks`は**コンテンツ外部参照型**のFTS5テーブル（`content='chunks'`）です。元のテキストを保存せず、`content_rowid`で`chunks.id`を参照します。これによりストレージの倍増を回避しています。cdidxは挿入・削除操作時にFTSインデックスを手動で同期します。

### クエリ構文

FTS5は高度なクエリ構文をサポートしています:

```sql
-- 単一語句
WHERE fts_chunks MATCH 'authenticate'

-- フレーズ（完全一致の語順）
WHERE fts_chunks MATCH '"handle request"'

-- ブール演算子
WHERE fts_chunks MATCH 'auth AND token'
WHERE fts_chunks MATCH 'auth OR login'
WHERE fts_chunks MATCH 'auth NOT oauth'

-- 前方一致検索
WHERE fts_chunks MATCH 'auth*'

-- カラムフィルタ（ここでは1カラムだが、複数カラムFTSで有用）
WHERE fts_chunks MATCH 'content:authenticate'
```

### 検索の仕組み

以下のクエリを実行すると:
```sql
SELECT f.path, c.start_line, c.content
FROM fts_chunks fc
JOIN chunks c ON c.id = fc.rowid
JOIN files f ON f.id = c.file_id
WHERE fts_chunks MATCH 'handleRequest'
LIMIT 20;
```

1. FTS5が転置インデックスで `handleRequest` を参照 → マッチするチャンクの`rowid`リストを直接取得
2. `chunks`テーブルにJOINして80行のコードブロックと行番号を取得
3. `files`テーブルにJOINしてファイルパスと言語を取得

ファイルを開くことも、ディレクトリを走査することもありません。検索全体がSQLite内部で完結します。

## チャンク分割戦略

ファイルは**80行のチャンクに10行の重複**で分割されます。重複により、チャンク境界をまたぐシンボル定義やコードブロックが少なくとも1つのチャンクに完全に含まれます。

```
1-80行    → チャンク0
71-150行  → チャンク1（チャンク0と10行重複）
141-220行 → チャンク2（チャンク1と10行重複）
...
```

ステップサイズは `80 - 10 = 70` 行です。N行のファイルは `ceil((N - 80) / 70) + 1` 個のチャンクを生成します（最小1個）。

## シンボル抽出

シンボルは**コンパイル済み正規表現パターン**で1行ずつマッチングして抽出されます。各言語に関数、クラス、場合によってはインポート用のパターンがあります。名前付きキャプチャグループ `(?<name>\w+)` で識別子を取得します。

言語別対応シンボル種別:

| 言語 | function | class | import |
|---|:---:|:---:|:---:|
| Python | def, async def | class | -- |
| JavaScript | function, const/let/var アロー | class | import...from |
| TypeScript | function, アロー | class, interface, type, enum | import...from |
| C# | メソッド, コンストラクタ | class, interface, enum, record, struct | -- |
| Go | func, メソッド | type struct/interface | -- |
| Rust | fn | struct, enum, trait, impl | -- |
| Java | メソッド | class, interface, enum | -- |
| Kotlin | fun | class, interface, enum class, object | -- |
| Ruby | def | class, module | -- |
| C | 関数 | struct, enum | -- |
| C++ | 関数 | class, struct, namespace, enum | -- |
| PHP | function | class, interface, trait, enum | -- |
| Swift | func | class, struct, enum, protocol | -- |

正規表現ベースの抽出は意図的にシンプルです。AST精度よりも速度とポータビリティを優先しています。

## インクリメンタルインデックス

デフォルトでは、cdidxは各ファイルの`modified`タイムスタンプ（UTC）をデータベースの値と比較します。変更がなければファイルは完全にスキップされます。

ファイルが再インデックスされる場合:
1. ファイルレコードをUPSERT（`INSERT OR REPLACE`）
2. そのファイルの古いチャンクとFTSエントリを削除
3. そのファイルの古いシンボルを削除
4. 新しいチャンク、FTSエントリ、シンボルを挿入

### 古いファイルのパージ

インデックス開始前に、cdidxはデータベースの全ファイルパスをクエリし、ファイルシステムと照合します。ディスク上に存在しなくなったファイル（ブランチ切り替えや削除後など）はチャンクやシンボルとともに削除されます。

| 状況 | 動作 |
|---|---|
| ブランチ間でファイル未変更 | スキップ（即時） |
| ファイル内容が変更 | 再インデックス |
| checkout後にファイル削除 | DBからパージ |
| checkout後にファイル追加 | 新規インデックス |

```mermaid
flowchart LR
    A[git checkout branch-B] --> B[cdidx .]
    B --> C{ファイルごとの判定}
    C -->|未変更| D[スキップ]
    C -->|変更あり| E[再インデックス]
    C -->|削除済み| F[DBからパージ]
    C -->|新規| G[新規インデックス]
    D & E & F & G --> H[DB = branch-Bと同期完了]
```

### 部分更新モード

`--commits` や `--files` で、プロジェクト全体をスキャンせずに特定ファイルのみ更新できます:

```bash
cdidx ./myproject --commits abc123 def456   # これらのコミットの変更ファイル
cdidx ./myproject --files src/app.cs        # 特定ファイルのみ
```

`--commits` は `git diff-tree --no-commit-id -r --name-only` で変更ファイルパスを解決します。

## AI連携

### CLAUDE.mdの設定

AIエージェントに `codeindex.db` を活用させるには、プロジェクトルートに `CLAUDE.md` を配置してください:

````markdown
# コードベース検索ルール

このプロジェクトには `codeindex.db` があります。
コードを検索する際は `find`, `grep`, `ls -R` ではなく**このSQLiteデータベースを検索**してください。

## クエリ

### 全文検索
```sql
SELECT f.path, c.start_line, c.content
FROM fts_chunks fc
JOIN chunks c ON c.id = fc.rowid
JOIN files f ON f.id = c.file_id
WHERE fts_chunks MATCH 'キーワード'
LIMIT 20;
```

### 関数・クラス名で検索
```sql
SELECT f.path, s.name, s.line
FROM symbols s
JOIN files f ON f.id = s.file_id
WHERE s.kind = 'function' AND s.name LIKE '%キーワード%';
```
````

### CLI経由の連携

`cdidx` がPATH上にある場合、AIエージェントはCLIを直接使うこともできます:

```bash
cdidx search "keyword"           # JSONライン出力、パース可能
cdidx symbols "ClassName"        # 構造化シンボル検索
```

### 出力形式

クエリコマンド（`search`、`symbols`、`files`）はデフォルトで**JSONライン**（1行1 JSONオブジェクト）を出力し、AIエージェントが容易にパースできるよう設計されています。`--no-json`で人間向け出力に切り替えられます。

```json
{"path":"src/auth.py","lang":"python","start_line":1,"end_line":80,"content":"def authenticate(user):\n ...","score":-1.5}
```

## 終了コード

| コード | 意味 |
|---|---|
| `0` | 成功 |
| `1` | 引数エラー |
| `2` | 未検出（検索結果なし、ディレクトリ不在） |
| `3` | データベースエラー |

## 設計判断

- **ORMなし** — `Microsoft.Data.Sqlite`でパラメータ化クエリを直接使用。依存関係を最小限に、制御を明確に。
- **バッチコミット** — 書き込み性能のため1トランザクション500レコード。fsyncオーバーヘッドを削減。
- **WALモード** — Write-Ahead Loggingで読み書き同時アクセスとクラッシュ安全性を確保。
- **コンテンツ外部参照FTS5** — `chunks`テーブルを参照しコピーを保存しないことでストレージ倍増を回避。
- **正規表現シンボル抽出** — ASTパーサーも言語固有の依存関係も不要。精度より速度とポータビリティを優先。
- **JSONファースト出力** — クエリコマンドはAI向けにデフォルトJSONライン。`--no-json`で人間向け。
- **手動引数解析** — `System.CommandLine`は依存削減のため削除。シンプルなswitch文での解析。
- **SHA256チェックサム** — 完全性検証用にファイルごとに保存（現在は変更検出には未使用だが利用可能）。
- **UTF-8フォールバック** — 不正なUTF-8バイトはファイル全体を失敗させずU+FFFDに置換。

## コーディング規約

- コメントは英日併記（例: `// Enable WAL mode / WALモードを有効化`）
- ドキュメント（README, CHANGELOG）は前半英語、後半日本語の構成。
- 不要なパッケージは入れない。

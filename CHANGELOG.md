# Changelog

All notable changes to this project will be documented in this file.

The English section comes first, followed by a Japanese translation.
The format is based on [Keep a Changelog](https://keepachangelog.com/en/1.1.0/), and this project adheres to [Semantic Versioning](https://semver.org/spec/v2.0.0.html).

## English

### [Unreleased]

### [1.1.0] - 2026-04-10

#### Changed

- **Clarified safe indexing after history rewrites** — README and `SELF_IMPROVEMENT.md` now explicitly recommend `cdidx .` after `git reset`, `git rebase`, `git commit --amend`, `git switch`, or `git merge`, and `--commits` now prints the same guidance in human-readable mode. Added a regression test for the new CLI note. Affected: `README.md`, `SELF_IMPROVEMENT.md`, `src/CodeIndex/Cli/IndexCommandRunner.cs`, `tests/CodeIndex.Tests/IndexCommandRunnerTests.cs`.

- **Hardened test cleanup for Windows SQLite file locks** — `IndexCommandRunnerTests` now clears SQLite pools before retrying temporary directory deletion, avoiding CI failures where Windows still held an external DB file open during cleanup. Also documented cross-platform expectations for filesystem, process, and SQLite-lifetime changes in the AI workflow docs. Affected: `tests/CodeIndex.Tests/IndexCommandRunnerTests.cs`, `CLAUDE.md`, `SELF_IMPROVEMENT.md`.

- **Synchronized README tables with the expanded CLI/MCP surface** — Updated the README English and Japanese tables for MCP tools and query options so they match the current command set, including `definition`, `references`, `callers`, `callees`, `excerpt`, `map`, `inspect`, and MCP `analyze_symbol`. Affected: `README.md`.

- **Added a dedicated self-improvement playbook for AI agents** — Added `SELF_IMPROVEMENT.md`, a bilingual operating contract for iterative cdidx self-improvement loops, including branch/commit discipline, rebuild-and-refresh requirements, approval gates for breaking changes, and language-aware search guidance. Updated README discoverability and CLAUDE.md checklist/sync rules to keep the playbook current. Affected: `SELF_IMPROVEMENT.md`, `README.md`, `CLAUDE.md`.

- **Bundled symbol analysis in one request** — Added `inspect` CLI and MCP `analyze_symbol` workflows that return the primary definition, nearby symbols, references, callers, callees, and file metadata together, so AI clients can answer common symbol questions without chaining several separate queries. Affected: `src/CodeIndex/Database/DbReader.cs`, `src/CodeIndex/Cli/QueryCommandRunner.cs`, `src/CodeIndex/Cli/ConsoleUi.cs`, `src/CodeIndex/Program.cs`, `src/CodeIndex/Mcp/McpServer.cs`, `tests/CodeIndex.Tests/DbReaderTests.cs`, `tests/CodeIndex.Tests/McpServerTests.cs`, `tests/CodeIndex.Tests/ConsoleUiTests.cs`, `README.md`, `DEVELOPER_GUIDE.md`, `CLAUDE.md`.

- **Added freshness metadata for status, map, and files** — `status` and `map` now report `indexed_at`, `latest_modified`, `git_head`, and `git_is_dirty`, while `files` exposes per-file checksum plus modified/indexed timestamps. Older databases opportunistically add missing file columns on open, and tests now avoid flaky global SQLite pool resets during cleanup. Affected: `src/CodeIndex/Cli/DbPathResolver.cs`, `src/CodeIndex/Cli/GitHelper.cs`, `src/CodeIndex/Cli/WorkspaceMetadataEnricher.cs`, `src/CodeIndex/Cli/QueryCommandRunner.cs`, `src/CodeIndex/Database/DbContext.cs`, `src/CodeIndex/Database/DbReader.cs`, `src/CodeIndex/Mcp/McpServer.cs`, `tests/CodeIndex.Tests/DbReaderTests.cs`, `tests/CodeIndex.Tests/McpServerTests.cs`, `tests/CodeIndex.Tests/GitHelperTests.cs`, `tests/CodeIndex.Tests/DatabaseTests.cs`, `README.md`, `DEVELOPER_GUIDE.md`, `CLAUDE.md`.

- **Added repo map overview for AI orientation** — Added `map` CLI/MCP workflows that summarize languages, modules, top files, large files, symbol/reference hot spots, and likely entrypoints from indexed data so AI clients can orient before deep queries. Affected: `src/CodeIndex/Database/DbReader.cs`, `src/CodeIndex/Cli/QueryCommandRunner.cs`, `src/CodeIndex/Cli/ConsoleUi.cs`, `src/CodeIndex/Program.cs`, `src/CodeIndex/Mcp/McpServer.cs`, `tests/CodeIndex.Tests/DbReaderTests.cs`, `tests/CodeIndex.Tests/McpServerTests.cs`, `tests/CodeIndex.Tests/ConsoleUiTests.cs`, `README.md`, `DEVELOPER_GUIDE.md`, `CLAUDE.md`.

- **Compact search snippets for AI clients** — `search --json` and MCP `search` now return match-centered snippets with snippet ranges, match lines, highlights, and context counts instead of whole chunks. Added `--snippet-lines` so callers can cap snippet size up front, while keeping human-readable search output centered on the same window. Affected: `src/CodeIndex/Cli/SearchSnippetFormatter.cs`, `src/CodeIndex/Cli/QueryCommandRunner.cs`, `src/CodeIndex/Cli/ConsoleUi.cs`, `src/CodeIndex/Mcp/McpServer.cs`, `tests/CodeIndex.Tests/SearchSnippetFormatterTests.cs`, `tests/CodeIndex.Tests/McpServerTests.cs`, `tests/CodeIndex.Tests/ConsoleUiTests.cs`, `README.md`, `DEVELOPER_GUIDE.md`, `CLAUDE.md`.

- **Indexed references, callers, and callees** — Added a `symbol_references` table plus regex-based reference extraction for supported languages, new CLI/MCP workflows for `references`, `callers`, and `callees`, and status/index summaries that report reference counts. Older databases are upgraded by creating the new reference table on open, so new binaries do not crash on pre-reference layouts. Affected: `src/CodeIndex/Indexer/ReferenceExtractor.cs`, `src/CodeIndex/Models/ReferenceRecord.cs`, `src/CodeIndex/Database/DbContext.cs`, `src/CodeIndex/Database/DbWriter.cs`, `src/CodeIndex/Database/DbReader.cs`, `src/CodeIndex/Cli/IndexCommandRunner.cs`, `src/CodeIndex/Cli/QueryCommandRunner.cs`, `src/CodeIndex/Cli/ConsoleUi.cs`, `src/CodeIndex/Program.cs`, `src/CodeIndex/Mcp/McpServer.cs`, `tests/CodeIndex.Tests/ReferenceExtractorTests.cs`, `tests/CodeIndex.Tests/DatabaseTests.cs`, `tests/CodeIndex.Tests/DbReaderTests.cs`, `tests/CodeIndex.Tests/McpServerTests.cs`, `tests/CodeIndex.Tests/ConsoleUiTests.cs`, `README.md`, `DEVELOPER_GUIDE.md`, `CLAUDE.md`.

- **Path-aware query filters and source-first ranking** — Added shared `--path`, repeatable `--exclude-path`, and `--exclude-tests` filters to `search`, `definition`, `symbols`, and `files`, exposed the same controls through MCP, and adjusted full-text ranking to prefer likely implementation files over tests/docs by boosting exact symbol-name and path matches. Affected: `src/CodeIndex/Database/DbReader.cs`, `src/CodeIndex/Cli/QueryCommandRunner.cs`, `src/CodeIndex/Cli/ConsoleUi.cs`, `src/CodeIndex/Mcp/McpServer.cs`, `tests/CodeIndex.Tests/DbReaderTests.cs`, `tests/CodeIndex.Tests/McpServerTests.cs`, `README.md`, `DEVELOPER_GUIDE.md`, `CLAUDE.md`.

- **Rich symbol metadata and backward-compatible symbol reads** — Symbol indexing now stores definition ranges, optional body ranges, signatures, enclosing symbols, visibility, and return types when the language extractor can infer them. Query paths auto-initialize missing columns for older databases when possible, and symbol reads fall back to the legacy schema instead of crashing if in-place migration is unavailable. Affected: `src/CodeIndex/Indexer/SymbolExtractor.cs`, `src/CodeIndex/Models/SymbolRecord.cs`, `src/CodeIndex/Database/DbContext.cs`, `src/CodeIndex/Database/DbReader.cs`, `src/CodeIndex/Database/DbWriter.cs`, `src/CodeIndex/Cli/QueryCommandRunner.cs`, `src/CodeIndex/Mcp/McpServer.cs`, `tests/CodeIndex.Tests/SymbolExtractorTests.cs`, `tests/CodeIndex.Tests/DbReaderTests.cs`, `tests/CodeIndex.Tests/McpServerTests.cs`, `README.md`, `DEVELOPER_GUIDE.md`, `CLAUDE.md`.

- **Added definition and excerpt retrieval workflows** — Added `definition` and `excerpt` CLI commands plus matching MCP tools so AI clients can fetch reconstructed declarations, optional symbol bodies, and arbitrary file line ranges from the index without opening source files directly. Affected: `src/CodeIndex/Program.cs`, `src/CodeIndex/Cli/ConsoleUi.cs`, `src/CodeIndex/Cli/QueryCommandRunner.cs`, `src/CodeIndex/Database/DbReader.cs`, `src/CodeIndex/Mcp/McpServer.cs`, `tests/CodeIndex.Tests/ConsoleUiTests.cs`, `tests/CodeIndex.Tests/DbReaderTests.cs`, `tests/CodeIndex.Tests/McpServerTests.cs`, `README.md`, `DEVELOPER_GUIDE.md`, `CLAUDE.md`.

### [1.0.5] - 2026-04-10

#### Changed

- **Sharpened cdidx positioning in docs and package metadata** — Repositioned README and NuGet package description around `cdidx` as an AI-native local code index for CLI and MCP workflows, added an upfront `cdidx` vs `rg` framing, and moved a copy-paste quick start into the README opening so the intended usage is clear within seconds. Affected: `README.md`, `src/CodeIndex/CodeIndex.csproj`.

#### Fixed

- **`.git/info/exclude` now always receives repository-relative patterns for DB paths** — Indexing no longer writes filesystem absolute paths when `--db` is absolute. DB directories outside the project root are skipped for auto-exclude, and worktree scenarios continue to resolve/write via the shared git common directory. The auto-generated marker line is now English-only, and regression tests cover inside-project absolute paths, outside-project absolute paths, and worktree layouts. Affected: `Cli/IndexCommandRunner.cs`, `tests/CodeIndex.Tests/IndexCommandRunnerTests.cs`.

### [1.0.4] - 2026-04-09

#### Changed

- **Help banner only on successful help commands** — Explicit help commands such as `cdidx --help` and `cdidx index --help` still show the banner, but usage text shown for invocation errors now omits it. Help output also no longer lists themed spinner easter eggs, and now shows explicit `index --commits` and `index --files` workflows so update commands are easier to discover. Affected: `Program.cs`, `Cli/ConsoleUi.cs`, `Cli/IndexCommandRunner.cs`, `tests/CodeIndex.Tests/ConsoleUiTests.cs`, `tests/CodeIndex.Tests/IndexCommandRunnerTests.cs`.

#### Fixed

- **`--commits` update mode no longer crashes on `git diff-tree` invocation** — Fixed the git argument order used to resolve changed files from commit IDs, added `--root` so initial commits return their changed files, and converted commit-resolution failures into normal CLI errors instead of unhandled exceptions. Affected: `Cli/GitHelper.cs`, `Cli/IndexCommandRunner.cs`, `tests/CodeIndex.Tests/GitHelperTests.cs`.

- **`--commits` handles merge commits** — Commit-based updates now ask `git diff-tree` to expand merge commits so their changed files are included instead of silently producing an empty update set. Affected: `Cli/GitHelper.cs`, `tests/CodeIndex.Tests/GitHelperTests.cs`.

- **`--files` no longer misclassifies project-local `..*` paths as outside the project** — Update mode now only rejects paths that actually resolve outside the project root (such as `../file.cs`), while allowing valid project-relative paths like `..hidden/file.cs`. Affected: `Cli/IndexCommandRunner.cs`, `tests/CodeIndex.Tests/IndexCommandRunnerTests.cs`.

### [1.0.3] - 2026-04-09

#### Changed

- **Structured MCP tool results** — MCP tool calls now return typed JSON in `structuredContent` and keep `content` to a short summary instead of a large plain-text dump. This makes AI integrations more reliable and easier to parse. Affected: `Mcp/McpServer.cs`, `tests/CodeIndex.Tests/McpServerTests.cs`, `README.md`, `DEVELOPER_GUIDE.md`, `CLAUDE.md`.

- **Opt-in raw FTS5 query syntax** — `search` now keeps literal-safe quoting by default but supports raw FTS5 syntax via CLI `--fts` and MCP `rawQuery`. This enables prefix and boolean queries without regressing safe defaults. Affected: `Database/DbReader.cs`, `Cli/QueryCommandRunner.cs`, `Cli/ConsoleUi.cs`, `Mcp/McpServer.cs`, `README.md`, `DEVELOPER_GUIDE.md`, `CLAUDE.md`, `tests/CodeIndex.Tests/DbReaderTests.cs`, `tests/CodeIndex.Tests/McpServerTests.cs`.

- **Split `Program.cs` into command runners** — Moved indexing flows and query command execution into focused `Cli/*Runner.cs` files, leaving `Program.cs` as a thin router. This reduces top-level complexity without changing CLI behavior. Affected: `Program.cs`, `Cli/CommandExitCodes.cs`, `Cli/IndexCommandRunner.cs`, `Cli/QueryCommandRunner.cs`, `DEVELOPER_GUIDE.md`, `CLAUDE.md`.

- **Human-readable search snippets center on matches** — `cdidx search` now shows a short snippet around the first matching line instead of always printing the first five lines of the stored chunk. This makes tail or middle-of-chunk matches visible in CLI output. Affected: `Cli/QueryCommandRunner.cs`, `Cli/SearchSnippetFormatter.cs`, `README.md`, `DEVELOPER_GUIDE.md`, `CLAUDE.md`, `tests/CodeIndex.Tests/SearchSnippetFormatterTests.cs`.

#### Fixed

- **Project-local default DB path for indexing** — `cdidx index <projectPath>` now stores the default database in `<projectPath>/.cdidx/codeindex.db` instead of resolving `.cdidx/codeindex.db` from the caller's current directory. This prevents indexing one project from mutating another project's default DB. Affected: `Cli/DbPathResolver.cs`, `Cli/IndexCommandRunner.cs`, `Cli/ConsoleUi.cs`, `README.md`, `DEVELOPER_GUIDE.md`, `CLAUDE.md`, `tests/CodeIndex.Tests/DbPathResolverTests.cs`.

- **Git worktree support for `.cdidx/` exclusion** — In a git worktree, `.git` is a file (not a directory), so the worktree root has no `.git/info/exclude` and auto-exclusion would silently skip writing — causing `.cdidx/` to appear as untracked. Fixed by using `GitHelper.ResolveGitCommonDir()` from the indexing runner to chase the worktree references and write to the shared `.git/info/exclude`. Affected: `Cli/GitHelper.cs`, `Cli/IndexCommandRunner.cs`, `DEVELOPER_GUIDE.md`, `CLAUDE.md`, `tests/CodeIndex.Tests/GitHelperTests.cs`.

### [1.0.2] - 2026-04-08

#### Added

- **Upgrade instructions** — Added `dotnet tool update -g cdidx` upgrade command to the Installation section of README. Affected: `README.md`.

#### Changed

- **CLAUDE.md template: update before search** — The code search rules template now instructs AI agents to update cdidx to the latest version (`dotnet tool update -g cdidx`) and refresh the index (`cdidx .`) before starting searches. Affected: `README.md`, `DEVELOPER_GUIDE.md`.

- **Deduplicate DEVELOPER_GUIDE.md** — Replaced duplicated CLAUDE.md template and exit codes table in DEVELOPER_GUIDE with references to README. Reduces maintenance burden when updating the template. Affected: `DEVELOPER_GUIDE.md`, `CLAUDE.md`.

#### Fixed

- **CLAUDE.md template: install vs update failure guidance** — Separated error handling for install failure and update failure. Update failure still leaves the existing cdidx usable; install failure falls back to `sqlite3` only if the database was already built. Affected: `README.md`.

### [1.0.1] - 2026-04-08

#### Added

- **Store index in `.cdidx/` directory** — Default DB path changed from `codeindex.db` to `.cdidx/codeindex.db`. The directory is created automatically on first `cdidx index`. The `.cdidx/` directory is auto-added to `.git/info/exclude`, so users don't need to edit `.gitignore`. Affected: `Program.cs`, `Cli/ConsoleUi.cs`.

#### Fixed

- **Progress bar spinner not visible** — Added a spinning braille character to the left of the progress bar. Easter egg themes (e.g. `--beer`) show themed frames (`🍺 Tapping...`, `🍺 Cheers!`, etc.) instead. `SetProgressTheme()` reuses frames from `GetSpinnerFrames()`. Affected: `Cli/ConsoleUi.cs`, `Program.cs`.

- **WARN/ERR messages overlapping progress bar** — Messages printed during indexing (e.g. invalid UTF-8 detection) no longer merge with the progress bar line. The bar is cleared before output and redrawn on the next update. `BuildRecord()` returns warnings as a return value instead of writing directly to stderr. Affected: `Cli/ConsoleUi.cs`, `Indexer/FileIndexer.cs`, `Program.cs`, `Mcp/McpServer.cs`.

#### Changed

- **README: PATH setup instructions restructured** — Moved "Add to PATH" under "Option B: Build from source" since it is unnecessary for NuGet installs. Fixed step numbering. Affected: `README.md`.

- **README: Git integration section** — Added section explaining `.git/info/exclude` auto-exclude behavior with examples of other tools that use this mechanism. Affected: `README.md`.

- **CLAUDE.md template: install instructions and offline fallback** — The code search rules template now guides AI agents to check for `cdidx` first, install via `dotnet tool install -g cdidx` if needed, and fall back to direct `sqlite3` queries when NuGet is unreachable. Affected: `README.md`, `DEVELOPER_GUIDE.md`.

- **CLAUDE.md: development rules** — Added "Rules for changes" section covering method signature updates, console output with progress bar, easter egg theme consistency, documentation sync, CHANGELOG style, PR conventions, and test requirements. Affected: `CLAUDE.md`.

### [1.0.0] - 2026-04-08

#### Added

- **MCP (Model Context Protocol) server** — Built-in MCP server (`cdidx mcp`) for AI coding tools (Claude Code, Cursor, Windsurf, Codex, GitHub Copilot). Implements JSON-RPC 2.0 over stdin/stdout with 5 tools: `search`, `symbols`, `files`, `status`, `index`. Protocol version 2024-11-05. Affected: `Mcp/McpServer.cs`, `Program.cs`, `Cli/ConsoleUi.cs`.

- **NuGet global tool support** — cdidx can now be installed via `dotnet tool install -g cdidx`. Added PackAsTool metadata and NuGet publish step to CI/CD pipeline (triggered on git tag). Affected: `CodeIndex.csproj`, `.github/workflows/release.yml`.

#### Fixed

- **TransactionScope.Commit() rollback safety** — Moved `_committed` flag assignment to after the actual commit/release operation. Previously, if `Commit()` or `RELEASE SAVEPOINT` threw an exception, the flag was already set to `true`, preventing `Dispose()` from rolling back the failed transaction. Affected: `Database/DbWriter.cs`.

- **`--commits`/`--files` argument parsing** — Fixed greedy argument consumption that swallowed single-dash options (e.g. `-h`, `-V`) by treating them as commit IDs or file paths. The parser now stops at any argument starting with `-` instead of only `--`. Affected: `Program.cs`.

- **Redundant rebuild logic** — Removed `File.Delete(dbPath)` before `DropAll()` in rebuild mode. The file deletion was redundant since `DropAll()` already drops and recreates all tables within the existing connection. Using `DropAll()` alone is cleaner and avoids unnecessary file-level operations. Affected: `Program.cs`.

#### Changed

- **Batch insert performance** — `InsertChunks()` and `InsertSymbols()` now prepare the SQL command once and reuse it across all rows, instead of creating a new command per row. This reduces per-row overhead from command parsing and parameter allocation. Affected: `Database/DbWriter.cs`.

- **Update mode skips unchanged files** — `RunUpdateMode` (used with `--commits` and `--files` flags) now checks `GetUnchangedFileId()` before re-indexing, consistent with full scan mode. Previously, specifying an unchanged file via `--files` would always trigger a full re-index. Affected: `Program.cs`.

- **Simplified file deletion** — `DeleteFileByPath()` and `PurgeStaleFiles()` now rely on `ON DELETE CASCADE` and FTS triggers instead of manually deleting chunks and symbols before the file row. This reduces redundant queries and better leverages the existing schema design. Affected: `Database/DbWriter.cs`.

#### Added

- **Core indexing engine** — Scans project directories recursively, detecting 33 file extensions across 24 languages (Python, JavaScript, TypeScript, C#, Go, Rust, Java, Kotlin, Swift, Ruby, PHP, C/C++, SQL, HTML, CSS, SCSS, Vue, Svelte, Terraform, Shell, Markdown, YAML, JSON, TOML). Skips common non-source directories (`.git`, `node_modules`, `__pycache__`, `venv`, `dist`, `build`, `.next`, `.idea`, `vendor`, etc.) and lock files (`package-lock.json`, `yarn.lock`, `pnpm-lock.yaml`). Affected: `Indexer/FileIndexer.cs`.

- **SQLite database with FTS5 full-text search** — Three core tables (`files`, `chunks`, `symbols`) with indexes on language, modified time, file_id, and symbol name. FTS5 virtual table (`fts_chunks`) with automatic sync triggers enables fast full-text search across all code chunks. WAL mode and busy_timeout enabled for concurrent access. Affected: `Database/DbContext.cs`, `Database/DbWriter.cs`.

- **Chunked content storage** — Files are split into 80-line chunks with 10-line overlap between consecutive chunks, enabling granular full-text search with sufficient context at chunk boundaries. Affected: `Indexer/ChunkSplitter.cs`.

- **Regex-based symbol extraction** — Extracts function, class, and import symbols from 13 languages: Python (`def`, `async def`, `class`), JavaScript/TypeScript (`function`, `class`, `import`, `export`), C# (`class`/`interface`/`enum`/`record`/`struct`, methods including `abstract`/`virtual`/`override`), Go (`func`, `type`), Rust (`fn`, `struct`, `enum`, `trait`, `impl`), Java/Kotlin (`class`, methods, `fun`), Ruby (`def`, `class`, `module`), C/C++ (functions, `struct`, `namespace`, `enum`), PHP (`function`, `class`, `interface`, `trait`), Swift (`func`, `class`, `struct`, `enum`, `protocol`). Affected: `Indexer/SymbolExtractor.cs`.

- **Incremental indexing** — Compares file modification timestamps and SHA256 checksums against the database; unchanged files are skipped entirely. Checksum fallback handles cases where timestamps change but content stays the same (e.g. `git checkout`). Affected: `Database/DbWriter.cs`, `Program.cs`.

- **Stale file purging for branch switching** — Automatically detects and removes database entries for files that no longer exist on disk (e.g., after `git checkout` to a different branch). Runs before indexing in incremental mode. Affected: `Database/DbWriter.cs`, `Program.cs`.

- **Batch commit optimization** — Database writes are committed in batches of 500 records per transaction, balancing memory usage and write performance. Affected: `Database/DbWriter.cs`.

- **CLI interface** — Subcommands (`index`, `search`, `symbols`, `files`, `status`) with `--db`, `--rebuild`, `--verbose`, `--json`, `--commits`, `--files` options. Displays progress every 50 files and a summary with file/chunk/symbol counts and elapsed time. Themed spinner easter eggs (`--sushi`, `--coffee`, `--ramen`, etc.). Affected: `Program.cs`, `Cli/ConsoleUi.cs`, `Cli/GitHelper.cs`.

- **FTS5 query sanitization** — User input to FTS5 MATCH is sanitized by quoting each token as a literal phrase, preventing syntax errors from special characters (`*`, `"`, `AND`, `OR`, `NOT`, `NEAR`). Affected: `Database/DbReader.cs`.

- **LIKE query escaping** — `%` and `_` in user queries for `SearchSymbols` and `ListFiles` are properly escaped with `ESCAPE` clause. Affected: `Database/DbReader.cs`.

- **Connection string safety** — Uses `SqliteConnectionStringBuilder` to prevent injection via paths containing `;`. Affected: `Database/DbContext.cs`.

- **Git argument validation** — Commit IDs passed to `git diff-tree` are validated with a regex whitelist and `--` option terminator. Affected: `Cli/GitHelper.cs`.

- **FTS sync via database triggers** — `AFTER INSERT/DELETE/UPDATE` triggers on the `chunks` table automatically keep `fts_chunks` in sync, preventing orphan FTS entries. `CleanExistingFileData()` removes old chunks and symbols before re-upserting. Affected: `Database/DbContext.cs`, `Database/DbWriter.cs`.

- **CLAUDE.md AI search prompt template** — Bilingual (English/Japanese) reference document with ready-to-use SQL queries for path search, full-text search, symbol lookup, language filtering, and file overview. Includes notes on branch switching and database staleness detection. Affected: `CLAUDE.md`.

- **Test suite** — 60 xUnit tests covering ChunkSplitter (6 tests), SymbolExtractor (18 tests), FileIndexer (8 tests), Database integration (14 tests including FTS orphan prevention and checksum-based detection), and DbReader queries (14 tests). Affected: `tests/CodeIndex.Tests/UnitTest1.cs`.

---

## 日本語

### [Unreleased]

### [1.1.0] - 2026-04-10

#### 変更

- **履歴改変後の安全な再インデックス手順を明確化** — README と `SELF_IMPROVEMENT.md` に、`git reset`、`git rebase`、`git commit --amend`、`git switch`、`git merge` の後は `cdidx .` を優先するルールを明記し、`--commits` の人間向け出力でも同じ案内を出すようにした。新しい CLI 注意文の回帰テストも追加した。対象: `README.md`, `SELF_IMPROVEMENT.md`, `src/CodeIndex/Cli/IndexCommandRunner.cs`, `tests/CodeIndex.Tests/IndexCommandRunnerTests.cs`.

- **Windows の SQLite ファイルロックに耐えるテスト後片付けへ強化** — `IndexCommandRunnerTests` で、一時ディレクトリ削除の再試行前に SQLite の pool をクリアするようにし、Windows で外部 DB ファイルがまだ開かれていて CI が落ちる問題を防いだ。あわせて、ファイルシステム、プロセス、SQLite ライフタイム変更におけるクロスプラットフォーム前提を AI 向け運用ドキュメントへ追記した。対象: `tests/CodeIndex.Tests/IndexCommandRunnerTests.cs`, `CLAUDE.md`, `SELF_IMPROVEMENT.md`.

- **拡張後のCLI/MCP機能面にREADME表を同期** — README の英語版・日本語版にある MCP ツール表とクエリオプション表を、現在のコマンドセットに合わせて更新した。`definition`、`references`、`callers`、`callees`、`excerpt`、`map`、`inspect`、MCP の `analyze_symbol` を反映している。対象: `README.md`.

- **AIエージェント向けの自己改善プレイブックを追加** — `SELF_IMPROVEMENT.md` を追加し、cdidx 自身を反復改善するための二言語の運用契約をまとめた。ブランチ/コミット規律、毎回の再ビルドとインデックス更新、破壊的変更の承認ゲート、言語差分を踏まえた検索指針を明文化し、README の導線と CLAUDE.md のチェックリスト/同期ルールも更新した。対象: `SELF_IMPROVEMENT.md`, `README.md`, `CLAUDE.md`.

- **1回で返すシンボル分析を追加** — `inspect` のCLIと、MCPの `analyze_symbol` ワークフローを追加し、主定義、近傍シンボル、参照、caller、callee、ファイルメタデータをまとめて返すようにした。AIクライアントが複数クエリを連鎖させずに一般的なシンボル調査を進められる。対象: `src/CodeIndex/Database/DbReader.cs`, `src/CodeIndex/Cli/QueryCommandRunner.cs`, `src/CodeIndex/Cli/ConsoleUi.cs`, `src/CodeIndex/Program.cs`, `src/CodeIndex/Mcp/McpServer.cs`, `tests/CodeIndex.Tests/DbReaderTests.cs`, `tests/CodeIndex.Tests/McpServerTests.cs`, `tests/CodeIndex.Tests/ConsoleUiTests.cs`, `README.md`, `DEVELOPER_GUIDE.md`, `CLAUDE.md`.

- **status / map / files に鮮度メタデータを追加** — `status` と `map` が `indexed_at`、`latest_modified`、`git_head`、`git_is_dirty` を返し、`files` がファイルごとの checksum と modified/indexed timestamp を返すようにした。古いDBを開く際は不足する file 列を可能な範囲で自動追加し、テスト後片付けではグローバルな SQLite pool reset に依存しないようにしてフレークを減らした。対象: `src/CodeIndex/Cli/DbPathResolver.cs`, `src/CodeIndex/Cli/GitHelper.cs`, `src/CodeIndex/Cli/WorkspaceMetadataEnricher.cs`, `src/CodeIndex/Cli/QueryCommandRunner.cs`, `src/CodeIndex/Database/DbContext.cs`, `src/CodeIndex/Database/DbReader.cs`, `src/CodeIndex/Mcp/McpServer.cs`, `tests/CodeIndex.Tests/DbReaderTests.cs`, `tests/CodeIndex.Tests/McpServerTests.cs`, `tests/CodeIndex.Tests/GitHelperTests.cs`, `tests/CodeIndex.Tests/DatabaseTests.cs`, `README.md`, `DEVELOPER_GUIDE.md`, `CLAUDE.md`.

- **AI向けの repo map 俯瞰を追加** — インデックス済みデータから言語、モジュール、主要ファイル、巨大ファイル、symbol/reference のホットスポット、推定エントリポイントをまとめて返す `map` のCLI/MCPワークフローを追加し、AIクライアントが深掘り前に全体像を把握できるようにした。対象: `src/CodeIndex/Database/DbReader.cs`, `src/CodeIndex/Cli/QueryCommandRunner.cs`, `src/CodeIndex/Cli/ConsoleUi.cs`, `src/CodeIndex/Program.cs`, `src/CodeIndex/Mcp/McpServer.cs`, `tests/CodeIndex.Tests/DbReaderTests.cs`, `tests/CodeIndex.Tests/McpServerTests.cs`, `tests/CodeIndex.Tests/ConsoleUiTests.cs`, `README.md`, `DEVELOPER_GUIDE.md`, `CLAUDE.md`.

- **AIクライアント向けの軽量検索スニペット** — `search --json` と MCP の `search` が、チャンク全文ではなく snippet range、match line、highlight、context count を持つ一致中心スニペットを返すようにした。さらに `--snippet-lines` を追加し、呼び出し側が先に抜粋サイズを制限できるようにしつつ、人間向け検索出力も同じウィンドウで中央寄せするようにした。対象: `src/CodeIndex/Cli/SearchSnippetFormatter.cs`, `src/CodeIndex/Cli/QueryCommandRunner.cs`, `src/CodeIndex/Cli/ConsoleUi.cs`, `src/CodeIndex/Mcp/McpServer.cs`, `tests/CodeIndex.Tests/SearchSnippetFormatterTests.cs`, `tests/CodeIndex.Tests/McpServerTests.cs`, `tests/CodeIndex.Tests/ConsoleUiTests.cs`, `README.md`, `DEVELOPER_GUIDE.md`, `CLAUDE.md`.

- **参照・caller・callee をインデックス化** — `symbol_references` テーブルと、対応言語向けの正規表現ベース参照抽出を追加し、`references`・`callers`・`callees` のCLI/MCPワークフローと、参照数を含む status/index サマリーを追加した。古いDBを新しいバイナリで開いた場合も、新しい参照テーブルを作成して pre-reference レイアウトでクラッシュしないようにした。対象: `src/CodeIndex/Indexer/ReferenceExtractor.cs`, `src/CodeIndex/Models/ReferenceRecord.cs`, `src/CodeIndex/Database/DbContext.cs`, `src/CodeIndex/Database/DbWriter.cs`, `src/CodeIndex/Database/DbReader.cs`, `src/CodeIndex/Cli/IndexCommandRunner.cs`, `src/CodeIndex/Cli/QueryCommandRunner.cs`, `src/CodeIndex/Cli/ConsoleUi.cs`, `src/CodeIndex/Program.cs`, `src/CodeIndex/Mcp/McpServer.cs`, `tests/CodeIndex.Tests/ReferenceExtractorTests.cs`, `tests/CodeIndex.Tests/DatabaseTests.cs`, `tests/CodeIndex.Tests/DbReaderTests.cs`, `tests/CodeIndex.Tests/McpServerTests.cs`, `tests/CodeIndex.Tests/ConsoleUiTests.cs`, `README.md`, `DEVELOPER_GUIDE.md`, `CLAUDE.md`.

- **パス考慮のクエリ絞り込みと source 優先ランキング** — `search`、`definition`、`symbols`、`files` に共通の `--path`、繰り返し指定できる `--exclude-path`、`--exclude-tests` を追加し、同じ制御をMCPにも公開した。さらに全文検索の順位付けを調整し、シンボル名やパスの exact match をブーストして tests/docs より実装ファイルを先に返しやすくした。対象: `src/CodeIndex/Database/DbReader.cs`, `src/CodeIndex/Cli/QueryCommandRunner.cs`, `src/CodeIndex/Cli/ConsoleUi.cs`, `src/CodeIndex/Mcp/McpServer.cs`, `tests/CodeIndex.Tests/DbReaderTests.cs`, `tests/CodeIndex.Tests/McpServerTests.cs`, `README.md`, `DEVELOPER_GUIDE.md`, `CLAUDE.md`.

- **リッチなシンボルメタデータと後方互換なシンボル読み取り** — シンボルのインデックス時に、言語パターンから推論できる範囲で定義範囲、本体範囲、シグネチャ、親シンボル、可視性、戻り値型も保存するようにした。古いDBに対しては、可能なら不足する列を自動追加し、読み取り経路でその場移行できない場合も旧スキーマへフォールバックしてクラッシュを避ける。対象: `src/CodeIndex/Indexer/SymbolExtractor.cs`, `src/CodeIndex/Models/SymbolRecord.cs`, `src/CodeIndex/Database/DbContext.cs`, `src/CodeIndex/Database/DbReader.cs`, `src/CodeIndex/Database/DbWriter.cs`, `src/CodeIndex/Cli/QueryCommandRunner.cs`, `src/CodeIndex/Mcp/McpServer.cs`, `tests/CodeIndex.Tests/SymbolExtractorTests.cs`, `tests/CodeIndex.Tests/DbReaderTests.cs`, `tests/CodeIndex.Tests/McpServerTests.cs`, `README.md`, `DEVELOPER_GUIDE.md`, `CLAUDE.md`.

- **definition / excerpt 取得フローを追加** — `definition` と `excerpt` のCLIコマンド、および対応するMCPツールを追加し、AIクライアントがソースファイルを直接開かなくても、再構成した宣言、本体、任意行範囲の抜粋をインデックスから取得できるようにした。対象: `src/CodeIndex/Program.cs`, `src/CodeIndex/Cli/ConsoleUi.cs`, `src/CodeIndex/Cli/QueryCommandRunner.cs`, `src/CodeIndex/Database/DbReader.cs`, `src/CodeIndex/Mcp/McpServer.cs`, `tests/CodeIndex.Tests/ConsoleUiTests.cs`, `tests/CodeIndex.Tests/DbReaderTests.cs`, `tests/CodeIndex.Tests/McpServerTests.cs`, `README.md`, `DEVELOPER_GUIDE.md`, `CLAUDE.md`.

### [1.0.5] - 2026-04-10

#### 変更

- **ドキュメントとパッケージ説明で cdidx の立ち位置を明確化** — README と NuGet パッケージ説明を、`cdidx` を CLI / MCP ワークフロー向けの AIネイティブなローカルコードインデックスとして打ち出す内容に整理し、冒頭に `cdidx` と `rg` の使い分けとコピペできるクイックスタートを追加して、用途が数秒で伝わるようにした。対象: `README.md`, `src/CodeIndex/CodeIndex.csproj`.

#### 修正

- **DBパスの `.git/info/exclude` 追記を常にリポジトリ相対パターン化** — `--db` に絶対パスを渡した場合でも、インデックス時にファイルシステム絶対パスを書き込まないよう修正。project 外のDBディレクトリは自動除外対象からスキップし、worktree でも共有 git common directory 側へ正しく追記される挙動を維持。自動生成マーカー行は英語のみとし、project 内絶対パス / project 外絶対パス / worktree 構成の回帰テストを追加。対象: `Cli/IndexCommandRunner.cs`, `tests/CodeIndex.Tests/IndexCommandRunnerTests.cs`.

### [1.0.4] - 2026-04-09

#### 変更

- **成功した help のときだけバナーを表示** — `cdidx --help` や `cdidx index --help` のような明示的な help は従来どおりバナーを表示する一方、呼び出し失敗時に出す usage ではバナーを表示しないようにした。あわせて help 出力からテーマ付きスピナーのイースターエッグ一覧を除外し、`index --commits` / `index --files` の更新フローを明示して使い方を分かりやすくした。対象: `Program.cs`, `Cli/ConsoleUi.cs`, `Cli/IndexCommandRunner.cs`, `tests/CodeIndex.Tests/ConsoleUiTests.cs`, `tests/CodeIndex.Tests/IndexCommandRunnerTests.cs`.

#### 修正

- **`--commits` 更新モードが `git diff-tree` 呼び出しで落ちる問題** — コミットIDから変更ファイルを解決する際の git 引数順を修正し、初回コミットでも変更ファイルを返せるよう `--root` を追加した。さらに commit 解決失敗時は未処理例外ではなく通常のCLIエラーとして返すようにした。対象: `Cli/GitHelper.cs`, `Cli/IndexCommandRunner.cs`, `tests/CodeIndex.Tests/GitHelperTests.cs`.

- **`--commits` で merge commit を扱えない問題** — commit 指定更新で `git diff-tree` に merge commit 展開を指示し、変更ファイルが 0 件になって更新が空振りする問題を修正した。対象: `Cli/GitHelper.cs`, `tests/CodeIndex.Tests/GitHelperTests.cs`.

- **`--files` が project 内の `..*` パスを project 外と誤判定する問題** — update モードで project 外判定を実際に `../` で外へ出るパスのみに限定し、`..hidden/file.cs` のような project 内の相対パスを正しく更新対象にした。対象: `Cli/IndexCommandRunner.cs`, `tests/CodeIndex.Tests/IndexCommandRunnerTests.cs`.

### [1.0.3] - 2026-04-09

#### 変更

- **MCPツール結果を構造化** — MCPツール呼び出しが、巨大なプレーンテキストダンプではなく `structuredContent` に型付きJSON、`content` に短い要約を返すよう変更。AI連携でのパース信頼性を高めた。対象: `Mcp/McpServer.cs`, `tests/CodeIndex.Tests/McpServerTests.cs`, `README.md`, `DEVELOPER_GUIDE.md`, `CLAUDE.md`。

- **生のFTS5クエリ構文を opt-in で解放** — `search` は既定のリテラル安全な引用を維持しつつ、CLI の `--fts` と MCP の `rawQuery` で生のFTS5構文を使えるよう変更。前方一致やブール検索を可能にしつつ安全なデフォルトを維持。対象: `Database/DbReader.cs`, `Cli/QueryCommandRunner.cs`, `Cli/ConsoleUi.cs`, `Mcp/McpServer.cs`, `README.md`, `DEVELOPER_GUIDE.md`, `CLAUDE.md`, `tests/CodeIndex.Tests/DbReaderTests.cs`, `tests/CodeIndex.Tests/McpServerTests.cs`。

- **`Program.cs` をコマンドランナーへ分割** — インデックス処理フローとクエリ系コマンド実行を責務別の `Cli/*Runner.cs` に移し、`Program.cs` は薄いルータに整理。CLIの挙動を変えずにトップレベル複雑度を下げた。対象: `Program.cs`, `Cli/CommandExitCodes.cs`, `Cli/IndexCommandRunner.cs`, `Cli/QueryCommandRunner.cs`, `DEVELOPER_GUIDE.md`, `CLAUDE.md`。

- **人間向け検索スニペットを一致箇所中心に表示** — `cdidx search` が、保存チャンクの先頭5行を固定で出す代わりに、最初の一致行の前後を短いスニペットとして表示するよう変更。チャンク後半や中央の一致箇所もCLI出力から確認しやすくした。対象: `Cli/QueryCommandRunner.cs`, `Cli/SearchSnippetFormatter.cs`, `README.md`, `DEVELOPER_GUIDE.md`, `CLAUDE.md`, `tests/CodeIndex.Tests/SearchSnippetFormatterTests.cs`。

#### 修正

- **インデックス時の既定DBパスをプロジェクト基準に変更** — `cdidx index <projectPath>` の既定DB保存先を、呼び出し元のカレントディレクトリ基準の `.cdidx/codeindex.db` ではなく `<projectPath>/.cdidx/codeindex.db` に変更。別プロジェクトをインデックスした際に、他プロジェクトの既定DBを壊す問題を防止。対象: `Cli/DbPathResolver.cs`, `Cli/IndexCommandRunner.cs`, `Cli/ConsoleUi.cs`, `README.md`, `DEVELOPER_GUIDE.md`, `CLAUDE.md`, `tests/CodeIndex.Tests/DbPathResolverTests.cs`。

- **git worktreeでの`.cdidx/`除外対応** — git worktreeでは`.git`がディレクトリではなくファイルのため、worktreeルートに`.git/info/exclude`が存在せず、自動除外が黙ってスキップされて `.cdidx/` が未追跡として見えていた。`GitHelper.ResolveGitCommonDir()` を index 実行側から使い、worktreeの参照チェーンを辿って共有 `.git/info/exclude` に書き込むよう修正。対象: `Cli/GitHelper.cs`, `Cli/IndexCommandRunner.cs`, `DEVELOPER_GUIDE.md`, `CLAUDE.md`, `tests/CodeIndex.Tests/GitHelperTests.cs`。

### [1.0.2] - 2026-04-08

#### 追加

- **アップグレード手順** — READMEのインストールセクションに `dotnet tool update -g cdidx` によるアップグレードコマンドを追加。対象: `README.md`。

#### 変更

- **CLAUDE.mdテンプレート: 検索前にアップデート** — AI向けコード検索ルールのテンプレートで、検索開始前にcdidxを最新版に更新（`dotnet tool update -g cdidx`）し、インデックスを最新化（`cdidx .`）するよう案内を追加。対象: `README.md`, `DEVELOPER_GUIDE.md`。

- **DEVELOPER_GUIDE.mdの重複排除** — DEVELOPER_GUIDEのCLAUDE.mdテンプレートと終了コード表をREADMEへの参照に置き換え。テンプレート更新時のメンテナンス負荷を軽減。対象: `DEVELOPER_GUIDE.md`, `CLAUDE.md`。

#### 修正

- **CLAUDE.mdテンプレート: インストール失敗と更新失敗の案内を分離** — 更新失敗時は既存バージョンがそのまま使える旨を明記。インストール失敗時はDBが構築済みの場合のみ `sqlite3` フォールバックを案内。対象: `README.md`。

### [1.0.1] - 2026-04-08

#### 追加

- **インデックスを `.cdidx/` ディレクトリに格納** — デフォルトDBパスを `codeindex.db` から `.cdidx/codeindex.db` に変更。ディレクトリは初回の `cdidx index` で自動作成。`.cdidx/` は `.git/info/exclude` に自動追加されるため `.gitignore` の編集が不要。対象: `Program.cs`, `Cli/ConsoleUi.cs`。

#### 修正

- **プログレスバーのスピナーが表示されない問題** — プログレスバー左側にブレイルスピナー文字を追加。イースターエッグテーマ（`--beer`等）使用時はテーマ付きフレーム（`🍺 Tapping...`、`🍺 Cheers!` 等）を表示。`SetProgressTheme()` は `GetSpinnerFrames()` のフレームを再利用。対象: `Cli/ConsoleUi.cs`, `Program.cs`。

- **WARN/ERRメッセージがプログレスバーと重なる問題** — インデックス中のメッセージ（無効なUTF-8検出等）がプログレスバーと同じ行に出力されなくなった。出力前にバー行をクリアし、次の更新で再描画。`BuildRecord()` は直接stderrに書き込む代わりに警告を戻り値で返すよう変更。対象: `Cli/ConsoleUi.cs`, `Indexer/FileIndexer.cs`, `Program.cs`, `Mcp/McpServer.cs`。

#### 変更

- **README: PATHセットアップ手順の構成変更** — 「PATHに追加」を「方法B: ソースからビルド」の配下に移動（NuGetインストール時は不要）。番号付けも修正。対象: `README.md`。

- **README: Git連携セクション** — `.git/info/exclude` 自動除外の動作と、この仕組みを利用する他ツールの例を追加。対象: `README.md`。

- **CLAUDE.mdテンプレート: インストール手順とオフラインフォールバック** — AI向けコード検索ルールのテンプレートで、`cdidx` の有無確認、`dotnet tool install -g cdidx` でのインストール試行、NuGetにアクセスできない場合の `sqlite3` フォールバックを案内。対象: `README.md`, `DEVELOPER_GUIDE.md`。

- **CLAUDE.md: 開発ルール** — 「変更時のルール」セクションを追加。メソッドシグネチャ変更時の全呼び出し元更新、プログレスバーとコンソール出力、イースターエッグテーマの一貫性、ドキュメント同期、CHANGELOGスタイル、PRの書き方、テスト要件をカバー。対象: `CLAUDE.md`。

### [1.0.0] - 2026-04-08

#### 追加

- **MCP（Model Context Protocol）サーバー** — AIコーディングツール（Claude Code、Cursor、Windsurf、Codex、GitHub Copilot）向けの組み込みMCPサーバー（`cdidx mcp`）。stdin/stdout上のJSON-RPC 2.0で5つのツール（`search`, `symbols`, `files`, `status`, `index`）を提供。プロトコルバージョン2024-11-05。対象: `Mcp/McpServer.cs`, `Program.cs`, `Cli/ConsoleUi.cs`。

- **NuGetグローバルツール対応** — `dotnet tool install -g cdidx`でインストール可能に。PackAsToolメタデータとCI/CDパイプラインへのNuGet公開ステップ（gitタグトリガー）を追加。対象: `CodeIndex.csproj`, `.github/workflows/release.yml`。

#### 修正

- **TransactionScope.Commit()のロールバック安全性** — `_committed`フラグの設定を実際のコミット/リリース操作の後に移動。以前は`Commit()`や`RELEASE SAVEPOINT`が例外を投げた場合、フラグが既に`true`に設定されていたため`Dispose()`でロールバックされなかった。対象: `Database/DbWriter.cs`。

- **`--commits`/`--files`引数解析** — 単一ハイフンのオプション（`-h`、`-V`等）をコミットIDやファイルパスとして誤って取り込む貪欲な引数消費を修正。パーサーが`--`だけでなく`-`で始まる引数でも停止するよう変更。対象: `Program.cs`。

- **冗長なリビルドロジック** — rebuildモードで`DropAll()`前の`File.Delete(dbPath)`を削除。`DropAll()`が既存の接続内で全テーブルを削除・再作成するためファイル削除は冗長だった。`DropAll()`のみの方がクリーンで不要なファイル操作を回避。対象: `Program.cs`。

#### 変更

- **バッチ挿入のパフォーマンス改善** — `InsertChunks()`と`InsertSymbols()`でSQLコマンドを1回だけ準備し全行で再利用するよう変更。行ごとのコマンド生成・パラメータ割り当てのオーバーヘッドを削減。対象: `Database/DbWriter.cs`。

- **更新モードで未変更ファイルをスキップ** — `RunUpdateMode`（`--commits`/`--files`使用時）でも`GetUnchangedFileId()`によるチェックを実施し、フルスキャンモードと動作を統一。以前は`--files`で未変更ファイルを指定すると常に再インデックスされていた。対象: `Program.cs`。

- **ファイル削除の簡素化** — `DeleteFileByPath()`と`PurgeStaleFiles()`がチャンクとシンボルを手動削除する代わりに`ON DELETE CASCADE`とFTSトリガーに委任するよう変更。冗長なクエリを削減し、既存のスキーマ設計をより活用。対象: `Database/DbWriter.cs`。

#### 追加

- **コアインデックスエンジン** — プロジェクトディレクトリを再帰的に走査し、24言語にわたる33種類のファイル拡張子を検出（Python, JavaScript, TypeScript, C#, Go, Rust, Java, Kotlin, Swift, Ruby, PHP, C/C++, SQL, HTML, CSS, SCSS, Vue, Svelte, Terraform, Shell, Markdown, YAML, JSON, TOML）。一般的な非ソースディレクトリ（`.git`, `node_modules`, `__pycache__`, `venv`, `dist`, `build`, `.next`, `.idea`, `vendor`等）とロックファイル（`package-lock.json`, `yarn.lock`, `pnpm-lock.yaml`）をスキップ。対象: `Indexer/FileIndexer.cs`。

- **FTS5全文検索対応SQLiteデータベース** — 3つのコアテーブル（`files`, `chunks`, `symbols`）に言語、更新日時、file_id、シンボル名のインデックスを設定。FTS5仮想テーブル（`fts_chunks`）と自動同期トリガーにより全コードチャンクの高速全文検索が可能。WALモードとbusy_timeoutで並行アクセスに対応。対象: `Database/DbContext.cs`, `Database/DbWriter.cs`。

- **チャンク分割コンテンツ保存** — ファイルを80行ごとに分割し、連続するチャンク間に10行の重複を持たせることで、チャンク境界での十分なコンテキストを保ちつつきめ細かい全文検索を実現。対象: `Indexer/ChunkSplitter.cs`。

- **正規表現によるシンボル抽出** — 13言語から関数、クラス、インポートシンボルを抽出: Python（`def`, `async def`, `class`）、JavaScript/TypeScript（`function`, `class`, `import`, `export`）、C#（`class`/`interface`/`enum`/`record`/`struct`、`abstract`/`virtual`/`override`対応メソッド）、Go（`func`, `type`）、Rust（`fn`, `struct`, `enum`, `trait`, `impl`）、Java/Kotlin（`class`, メソッド, `fun`）、Ruby（`def`, `class`, `module`）、C/C++（関数, `struct`, `namespace`, `enum`）、PHP（`function`, `class`, `interface`, `trait`）、Swift（`func`, `class`, `struct`, `enum`, `protocol`）。対象: `Indexer/SymbolExtractor.cs`。

- **インクリメンタルインデックス** — ファイルの更新日時とSHA256チェックサムをデータベースと比較し、未変更ファイルを完全にスキップ。チェックサムのフォールバックにより、タイムスタンプが変わっても内容が同じ場合（例: `git checkout`）を処理。対象: `Database/DbWriter.cs`, `Program.cs`。

- **ブランチ切り替え対応の古いファイルパージ** — ディスク上に存在しなくなったファイル（例：`git checkout`で別ブランチに切り替え後）のデータベースエントリを自動検出・削除。インクリメンタルモードではインデックス処理前に実行。対象: `Database/DbWriter.cs`, `Program.cs`。

- **バッチコミット最適化** — データベースへの書き込みを1トランザクションあたり500レコードのバッチでコミットし、メモリ使用量と書き込み性能のバランスを最適化。対象: `Database/DbWriter.cs`。

- **CLIインターフェース** — サブコマンド（`index`, `search`, `symbols`, `files`, `status`）と`--db`, `--rebuild`, `--verbose`, `--json`, `--commits`, `--files`オプションに対応。50ファイルごとに進捗を表示し、ファイル数・チャンク数・シンボル数と経過時間のサマリーを出力。テーマ付きスピナーのイースターエッグ（`--sushi`, `--coffee`, `--ramen`等）。対象: `Program.cs`, `Cli/ConsoleUi.cs`, `Cli/GitHelper.cs`。

- **FTS5クエリサニタイズ** — FTS5 MATCHへのユーザー入力を各トークンをリテラルフレーズとして引用しサニタイズ。特殊文字（`*`, `"`, `AND`, `OR`, `NOT`, `NEAR`）による構文エラーを防止。対象: `Database/DbReader.cs`。

- **LIKEクエリエスケープ** — `SearchSymbols`と`ListFiles`のクエリで`%`と`_`を`ESCAPE`句で適切にエスケープ。対象: `Database/DbReader.cs`。

- **接続文字列の安全性** — `SqliteConnectionStringBuilder`を使用し、`;`を含むパスによるインジェクションを防止。対象: `Database/DbContext.cs`。

- **Git引数バリデーション** — `git diff-tree`に渡されるコミットIDを正規表現ホワイトリストで検証し、`--`オプション終端を追加。対象: `Cli/GitHelper.cs`。

- **データベーストリガーによるFTS同期** — `chunks`テーブルの`AFTER INSERT/DELETE/UPDATE`トリガーで`fts_chunks`を自動同期し、FTS孤立エントリを防止。`CleanExistingFileData()`は再UPSERT前に古いチャンクとシンボルを削除。対象: `Database/DbContext.cs`, `Database/DbWriter.cs`。

- **CLAUDE.md AI検索プロンプトテンプレート** — 英語・日本語併記のリファレンスドキュメント。パス検索、全文検索、シンボル検索、言語フィルタリング、ファイル概要の即使用可能なSQLクエリを収録。ブランチ切り替えとデータベースの鮮度検出に関する注記を含む。対象: `CLAUDE.md`。

- **テストスイート** — 60件のxUnitテスト。ChunkSplitter（6件）、SymbolExtractor（18件）、FileIndexer（8件）、Database統合（14件、FTS孤立防止・チェックサム検出含む）、DbReaderクエリ（14件）をカバー。対象: `tests/CodeIndex.Tests/UnitTest1.cs`。

[Unreleased]: https://github.com/Widthdom/CodeIndex/compare/v1.1.0...HEAD
[1.1.0]: https://github.com/Widthdom/CodeIndex/compare/v1.0.5...v1.1.0
[1.0.5]: https://github.com/Widthdom/CodeIndex/compare/v1.0.4...v1.0.5
[1.0.4]: https://github.com/Widthdom/CodeIndex/compare/v1.0.3...v1.0.4
[1.0.3]: https://github.com/Widthdom/CodeIndex/compare/v1.0.2...v1.0.3
[1.0.2]: https://github.com/Widthdom/CodeIndex/compare/v1.0.1...v1.0.2
[1.0.1]: https://github.com/Widthdom/CodeIndex/compare/v1.0.0...v1.0.1
[1.0.0]: https://github.com/Widthdom/CodeIndex/releases/tag/v1.0.0

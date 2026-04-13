# cdidx (CodeIndex) — Development Guide for AI

## Project overview

cdidx is a .NET 8 CLI tool that indexes source code into a SQLite database (FTS5) for AI-powered search. It supports both human-readable and machine-readable (JSON) output, making it usable by both humans and AI agents. Assembly name is `cdidx` (like `rg` for ripgrep).

## Build & test

```bash
dotnet build
dotnet test
dotnet run --project src/CodeIndex -- <command> [options]
```

## CLI commands

```bash
# Indexing
cdidx index <projectPath> [--db <path>] [--rebuild] [--verbose] [--json]
cdidx <projectPath>                          # shorthand for 'index'

# Query (default output: human-readable; use --json for AI consumption)
cdidx search <query> [--db <path>] [--limit <n>] [--lang <lang>] [--path <pattern>] [--exclude-path <pattern>] [--exclude-tests] [--snippet-lines <n>] [--count] [--json]
cdidx definition <query> [--db <path>] [--kind <kind>] [--lang <lang>] [--limit <n>] [--path <pattern>] [--exclude-path <pattern>] [--exclude-tests] [--body] [--exact] [--json]
cdidx references <query> [--db <path>] [--kind <kind>] [--lang <lang>] [--limit <n>] [--path <pattern>] [--exclude-path <pattern>] [--exclude-tests] [--exact] [--json]
cdidx callers <query> [--db <path>] [--kind <kind>] [--lang <lang>] [--limit <n>] [--path <pattern>] [--exclude-path <pattern>] [--exclude-tests] [--exact] [--json]
cdidx callees <query> [--db <path>] [--kind <kind>] [--lang <lang>] [--limit <n>] [--path <pattern>] [--exclude-path <pattern>] [--exclude-tests] [--exact] [--json]
cdidx symbols [query] [--name <name>] [--exact] [--kind <kind>] [--lang <lang>] [--limit <n>] [--path <pattern>] [--exclude-path <pattern>] [--exclude-tests] [--since <datetime>]
cdidx files [query] [--lang <lang>] [--limit <n>] [--path <pattern>] [--exclude-path <pattern>] [--exclude-tests] [--since <datetime>]
cdidx excerpt <path> --start <line> [--end <line>] [--before <n>] [--after <n>] [--json]
cdidx map [--db <path>] [--limit <n>] [--lang <lang>] [--path <pattern>] [--exclude-path <pattern>] [--exclude-tests] [--json]
cdidx inspect <query> [--db <path>] [--limit <n>] [--lang <lang>] [--path <pattern>] [--exclude-path <pattern>] [--exclude-tests] [--body] [--exact] [--json]
cdidx outline <path> [--db <path>] [--json]
cdidx status [--json]
cdidx deps [--db <path>] [--limit <n>] [--lang <lang>] [--path <pattern>] [--exclude-path <pattern>] [--exclude-tests] [--json]
cdidx unused [--db <path>] [--limit <n>] [--kind <kind>] [--lang <lang>] [--path <pattern>] [--exclude-path <pattern>] [--exclude-tests] [--json]
cdidx hotspots [--db <path>] [--limit <n>] [--kind <kind>] [--lang <lang>] [--path <pattern>] [--exclude-path <pattern>] [--exclude-tests] [--json]
cdidx impact <query> [--db <path>] [--limit <n>] [--lang <lang>] [--path <pattern>] [--exclude-path <pattern>] [--exclude-tests] [--depth <n>] [--json]
cdidx languages [--json]

# MCP server (for AI tools: Claude Code, Cursor, Windsurf, etc.)
cdidx mcp [--db <path>]
```

## Architecture

```
src/CodeIndex/
  Program.cs               — Thin CLI entry point and command routing
  Cli/CommandExitCodes.cs  — Shared process exit codes
  Cli/ConsoleUi.cs         — Spinner, progress bar, banner, easter egg, version, usage text
  Cli/DbPathResolver.cs    — Resolve default DB paths for index commands
  Cli/GitHelper.cs         — Git helpers: diff-tree for --commits, worktree-aware common dir resolution
  Cli/IndexCommandRunner.cs — Index command execution, update/full-scan flows, git exclude helper
  Cli/QueryCommandRunner.cs — Search/definition/references/callers/callees/symbols/files/excerpt/map/inspect/outline/status/impact/unused/hotspots command execution and query arg parsing
  Cli/SearchSnippetFormatter.cs — Build compact match-centered search snippets for human/JSON output
  Cli/WorkspaceMetadataEnricher.cs — Enrich status/map/inspect with project root, git HEAD, dirty flag
  Cli/SuggestionStore.cs    — Local JSON storage for AI suggestions with SHA256 dedup
  Cli/SourceCodeDetector.cs — Heuristic source code leak prevention for suggestion submissions
  Cli/GitHubIssueReporter.cs — GitHub Issues API client for suggestion submission (best-effort)
  Database/DbContext.cs     — SQLite connection, schema init (WAL, FTS5, triggers, busy_timeout)
  Database/DbDebug.cs       — Opt-in reader diagnostics (CDIDX_DEBUG=1): tracks last SQL, params, and row snapshot, dumps to stderr on reader exceptions
  Database/DbWriter.cs      — UPSERT (ON CONFLICT DO UPDATE), batch insert, stale file purge, reference writes
  Database/DbReader.cs      — Core query operations (file listing, reference/caller/callee lookup, excerpt reconstruction, status, file-level deps)
  Database/DbSearchReader.cs — Full-text search operations (FTS5 search, deduplication) (partial class)
  Database/DbSymbolReader.cs — Symbol query operations (symbol search, definitions, outline, analyze bundle) (partial class)
  Database/RepoMapBuilder.cs — Repo-level overview builder (map command): file stats, entrypoint scoring, module grouping
  Indexer/FileIndexer.cs    — Directory scan, language detection, FileRecord building (returns warning via tuple)
  Indexer/ChunkSplitter.cs  — 80-line chunks with 10-line overlap
  Indexer/SymbolExtractor.cs — Regex-based symbol extraction (32 languages)
  Indexer/ReferenceExtractor.cs — Regex-based reference extraction (31 languages with graph queries)
  Indexer/ReferenceExtractor.cs — Regex-based reference extraction (language-aware)
  Mcp/McpServer.cs          — MCP server core (stdin/stdout JSON-RPC 2.0 protocol handling) (partial class)
  Mcp/McpToolDefinitions.cs — MCP tool schema definitions (partial class)
  Mcp/McpToolHandlers.cs    — MCP tool execution logic (partial class)
  Models/                   — FileRecord, ChunkRecord, SymbolRecord, ReferenceRecord, SuggestionRecord, QueryResults (plain DTOs)
tests/CodeIndex.Tests/
  ChunkSplitterTests.cs     — ChunkSplitter tests
  ReferenceExtractorTests.cs — ReferenceExtractor tests
  SymbolExtractorTests.cs   — SymbolExtractor tests (multi-language)
  FileIndexerTests.cs       — FileIndexer tests (scan, detect, build)
  DatabaseTests.cs          — DbContext/DbWriter integration tests
  DbReaderTests.cs          — DbReader query tests (FTS, symbols, files, outline, status)
  McpServerTests.cs         — MCP server JSON-RPC protocol and tool tests
  GitHelperTests.cs         — Git helper tests (normal repo, worktree, fallback cases)
  ConsoleUiTests.cs         — Console output, help text, spinner, version tests
  DbPathResolverTests.cs    — DB path resolution tests
  IndexCommandRunnerTests.cs — Index command integration tests
  QueryCommandRunnerTests.cs — Query CLI integration tests
  ConcurrencyTests.cs        — Concurrent access tests (WAL read/write)
  PerformanceTests.cs        — Large-scale data performance tests
  DbRecoveryTests.cs         — DB corruption recovery tests
  SearchSnippetFormatterTests.cs — Search snippet formatting tests
  WorkspaceMetadataEnricherTests.cs — Workspace metadata enrichment tests
  SuggestionStoreTests.cs   — Suggestion store unit tests (dedup, persistence, corruption recovery)
  SourceCodeDetectorTests.cs — Source code leak detection tests (allowed vs rejected inputs)
  GitHubIssueReporterTests.cs — GitHub token resolution tests
  TestProjectHelper.cs      — Shared helper for creating temp indexed projects
  TestConsoleLock.cs         — Shared lock for console-redirecting tests
```

## Key design decisions

- **No ORM** — Raw `Microsoft.Data.Sqlite` with parameterized queries. Keep it simple.
- **Incremental by default** — Compares `modified` timestamp and SHA256 checksum; skips unchanged files.
- **Stale file purge** — Before indexing, removes DB entries for files no longer on disk (branch switch support).
- **Batch commits** — 500 records per transaction for write performance. Supports nesting via SAVEPOINT.
- **FTS5** — `fts_chunks` virtual table mirrors `chunks.content` for full-text search. Sync via database triggers (AFTER INSERT/DELETE/UPDATE on chunks). FTS5 optimize runs after indexing.
- **Literal-safe search by default** — Search queries are quoted token-by-token to avoid FTS syntax errors by default. Raw FTS5 syntax is opt-in via `--fts` or MCP `rawQuery`.
- **Path-aware narrowing and ranking** — `search`, `definition`, `references`, `callers`, `callees`, `symbols`, and `files` share repeatable `--path` (multiple values are OR'd together), repeatable `--exclude-path`, and `--exclude-tests`. Query ordering prefers source files over tests/docs, and `search` boosts exact symbol-name and path matches.
- **Compact search snippets for AI** — `search --json` and MCP `search` return match-centered snippets with snippet ranges, match lines, highlights, and context counts instead of whole chunks. `--snippet-lines` lets clients cap payload size up front.
- **Repo map for first-pass orientation** — `map` summarizes languages, modules, top files, file hot spots, and likely entrypoints so AI clients can form an initial navigation plan before issuing deeper queries. Entrypoint inference falls back to known top-level entry files when symbol extraction does not yield an explicit `Main`-style symbol.
- **Freshness metadata for trust decisions** — `status` exposes whole-workspace freshness plus `git_head` / `git_is_dirty`. `map` keeps `indexed_at` / `latest_modified` scoped to the filtered result set and also exposes `workspace_indexed_at` / `workspace_latest_modified` for whole-workspace freshness. `inspect` mirrors those whole-workspace timestamps and git fields so symbol-oriented AI flows can judge trust without a separate `status` call. `files` exposes per-file checksum and timestamp metadata. Older DBs auto-add missing file columns when possible, and read paths avoid crashing if migration cannot happen in place. MCP zero-result responses include `indexed_file_count` and `indexed_at` so AI clients can self-diagnose stale or empty indexes without a separate `status` call.
- **Bundled symbol analysis** — `inspect` and MCP `analyze_symbol` combine definition, nearby symbols, references, callers, callees, file metadata, workspace trust metadata, and graph-support metadata so AI clients can answer common symbol questions with one request.
- **Transitive impact analysis** — `impact` and MCP `impact_analysis` compute the transitive caller chain using BFS. Key design constraints learned through adversarial review: (1) caller matching uses `lower(r.symbol_name) = lower(@symbolName)` — case-insensitive exact match avoids both LIKE substring expansion (`Run` matching `RunAsync`) and case-sensitivity brittleness (`run` missing `Run`); (2) symbol name is pre-resolved through definitions via `ResolveSymbolName` (case-insensitive with exact-case preference, no path/test filters) so definitions outside the caller-scoped path are still found; (3) the read path filters to graph-supported languages via IN clause to prevent stale edges from removed languages leaking into results; (4) BFS pages through callers post-deduplication with `maxFetchIterations` safety cap, and `truncated` flag is set on both limit cap and iteration cap; (5) `PurgeUnsupportedReferences` runs in all three indexing paths (CLI full scan, CLI update mode, MCP index) to clean up stale edges when languages lose graph support.
- **Language-aware reference extraction** — `references`, `callers`, `callees`, and `impact` are backed by an indexed reference table built only for languages where regex-based call/reference extraction is meaningful. Unsupported languages are expected to use `search` instead of receiving low-confidence pseudo-graph results. When a language is removed from graph support, `PurgeUnsupportedReferences` deletes its stale `symbol_references` rows on the next indexing run, and the read path additionally filters by supported languages to prevent stale edges from surviving between index runs.
- **Explicit graph-support hints** — `inspect`, MCP `analyze_symbol`, and direct MCP graph tools annotate unsupported language filters with graph-support metadata so AI clients can distinguish "unsupported language" from "supported but zero hits."
- **Regex symbol extraction** — Intentionally simple. Accuracy is secondary to speed and portability, but the index stores richer symbol metadata such as definition ranges, optional body ranges, signatures, enclosing symbols, visibility, and return types when patterns can infer them.
- **Granular symbol kinds** — Symbols use semantically precise kinds: `function`, `class`, `struct`, `interface`, `enum`, `property`, `event`, `delegate`, `namespace`, `import`. Languages map their constructs to the closest kind (e.g. Rust `trait` → `interface`, Swift `protocol` → `interface`, PHP `trait` → `interface`). Symbol search and definition results are ranked by visibility (public first).
- **DB index optimization** — SQLite indexes are actively maintained to match query patterns. When adding new query features (new commands, new ORDER BY clauses, new JOIN patterns), always evaluate whether a dedicated index would improve performance. Use `CREATE INDEX IF NOT EXISTS` for safe additive changes.
- **Human-readable default** — All commands default to human-readable output. Use `--json` for machine-readable JSON lines (AI-friendly).
- **Structured MCP responses** — MCP tools return typed JSON in `structuredContent` plus a short summary in `content`, so AI tools don't need to scrape large text blobs.
- **MCP tool annotations** — All tools emit `annotations` (`readOnlyHint`, `destructiveHint`, `idempotentHint`, `openWorldHint`) per the MCP spec so AI clients can auto-approve safe read-only queries.
- **MCP server instructions** — The `initialize` response includes an `instructions` string with tool-selection guidance so AI clients can choose the right tool on first connection.
- **Backward-compatible read schema** — Opening an older DB with a newer cdidx binary auto-adds missing symbol columns and creates newer reference tables when possible. If a symbol read path cannot migrate the DB in place, symbol queries fall back to the legacy column layout instead of crashing. Query paths use `TryMigrateForRead()` instead of `InitializeSchema()` so that read-only databases (e.g. on read-only filesystems) silently degrade rather than crashing.
- **Structured exit codes** — 0=success, 1=usage error, 2=not found, 3=database error.
- **No direct Console output from library code** — `FileIndexer.BuildRecord()` returns warnings as a return value `(FileRecord, string, string?)` instead of writing to stderr. The caller (`Cli/IndexCommandRunner.cs`) handles display, clearing the progress bar line first via `ConsoleUi.ClearProgressLine()`.
- **`.cdidx/` directory** — By default, `cdidx index` stores index files in `<projectPath>/.cdidx/codeindex.db` (not the caller's cwd). The directory is auto-created on first `cdidx index` and auto-added to `.git/info/exclude` so users don't touch `.gitignore`. In a git worktree, `.git` is a file (not a directory), so `GitHelper.ResolveGitCommonDir()` follows the chain to find the shared `.git/` where `info/exclude` lives. This is a standard Git mechanism (used by git-lfs, Husky, JetBrains IDEs, etc.).

  **Normal repo vs worktree structure:**
  ```
  # Normal repo — .git is a directory, info/exclude is right there
  /projects/my-app/                   ← project root
  ├── 📂 .git/                        ← directory
  │   └── 📂 info/
  │       └── exclude                 ← AddToGitExclude writes here
  └── 📂 .cdidx/
      └── codeindex.db

  # Worktree — .git is a file, need to chase references to find info/exclude
  /projects/my-app/                   ← main repo root
  └── 📂 .git/                        ← actual git directory (shared)
      ├── 📂 info/
      │   └── exclude                 ← AddToGitExclude writes here
      └── 📂 worktrees/
          └── 📂 feature-branch/
              └── commondir           ← contains "../.."

  /projects/my-app-feature/           ← worktree root
  ├── .git                            ← FILE containing "gitdir: /projects/my-app/.git/worktrees/feature-branch"
  └── 📂 .cdidx/
      └── codeindex.db
  ```

  **Resolution chain in worktree:**
  1. Read `.git` file → `gitdir: /projects/my-app/.git/worktrees/feature-branch`
  2. Read `commondir` file at that path → `../..`
  3. Resolve `../..` relative to `feature-branch/` dir:
     `feature-branch/` → `..` → `worktrees/` → `..` → `.git/`
  4. Write to `.git/info/exclude`

## Conventions

- Comments are bilingual (English / Japanese), e.g. `// Enable WAL mode / WALモードを有効化`
- Documentation (README, CHANGELOG) is structured: English first, then Japanese.
- **Never mix languages within a section.** English sections must contain only English text; Japanese sections must contain only Japanese text. Bilingual inline code comments (`// Enable WAL mode / WALモードを有効化`) are the only exception. When adding bilingual content (e.g. CLAUDE.md rules), write the English paragraph in the English section and the Japanese paragraph in the Japanese section — never both in the same section.
- No unnecessary packages — `System.CommandLine` was removed in favor of manual arg parsing.

## Rules for changes (important)

### Dependency rule
The only production dependency is **Microsoft.Data.Sqlite** — keep it that way. Do not add NuGet packages without explicit user approval. If a package would enable a significant improvement aligned with SELF_IMPROVEMENT.md goals, propose it to the user with a clear rationale before adding it.

### Absolute prohibition
Code review uses the **locally built binary** from the current commit (`dotnet ./src/CodeIndex/bin/Debug/net8.0/cdidx.dll`) to search and verify the codebase. This means the reviewer sees exactly what the code actually does — not what tests claim it does, not what documentation says it does, but what the running binary produces. **It is strictly forbidden to intentionally implement incomplete, hollow, or deceptive code that passes tests or review on paper but fails in practice.** Every feature must work correctly when exercised by the binary itself. Cutting corners to "pass review" defeats the purpose of the self-improvement loop and will be caught by dogfooding.

### Code search tools (Claude Code / AI harnesses)
Repo-tracked `.claude/settings.json` denies the full set of shell code-search and file-discovery commands (`rg`, `grep`, `egrep`, `fgrep`, `zgrep`, `rgrep`, `ripgrep`, `ag`, `ack`, `ack-grep`, `git grep`, `find`, `locate`, `mlocate`, `mdfind`, `cdidx`, `~/.local/bin/cdidx`) so Claude Code agents cannot silently fall back to any of them. The `~/.local/bin/cdidx` entry closes the absolute-path loophole used by `install.sh` on Linux / macOS. Cloud Claude Code sessions rely on that installed binary, so `CLOUD_BOOTSTRAP_PROMPT.md` instructs those sessions to strip the two `cdidx` deny entries from the workspace copy of `.claude/settings.json` (session-local, uncommitted). Use the built-in Grep / Glob tools and the **locally built binary** (`dotnet ./src/CodeIndex/bin/Debug/net8.0/cdidx.dll`) for searches — the global `cdidx` may have an older DB schema, missing query features, or stale extraction logic that silently returns wrong results. Claude Code treats tracked `deny` entries as absolute — `.claude/settings.local.json` allows cannot override them. Contributors who intentionally need shell-level access to a denied command must edit the workspace copy of `.claude/settings.json` for that session only and not commit the change (same mechanism as the Cloud Bootstrap Step 1.5).

### Method signature changes
When changing a method's return type or parameters (e.g. `BuildRecord` from `(FileRecord, string)` to `(FileRecord, string, string?)`), **update ALL callers** in the same commit:
- `Cli/IndexCommandRunner.cs` (full scan AND `--commits`/`--files` update mode)
- `Mcp/McpServer.cs`
- `tests/CodeIndex.Tests/` (use `_` to discard unused elements)

### Console output and progress bar
The progress bar uses `\r` without newline. On Windows, stdout and stderr share the cursor position. **Any output (WARN, ERR, verbose [OK]/[SKIP]) while the progress bar is active MUST call `ConsoleUi.ClearProgressLine()` first**, or the message will merge with the bar on the same line.

### Easter egg themes
Features that exist in the spinner (braille frames, themed emoji+text) must carry through to the progress bar. `ConsoleUi.SetProgressTheme()` reuses frames from `GetSpinnerFrames()` — don't duplicate the frame definitions.

### Per-commit checklist
Before every commit, check whether each of the following needs updating. Don't batch these up — evaluate and act on each commit:
1. **Tests** — Does this change break existing tests or require new ones? Search for affected method/class names in `tests/`.
2. **TESTING_GUIDE.md** — If test code, helpers, execution flow, or testing conventions changed, update both English and Japanese sections.
3. **CHANGELOG.md** — Does this change deserve an entry? Update both English and Japanese sections.
4. **README.md** — Does this change affect user-facing behavior, CLI options, defaults, or examples? Update both English and Japanese sections.
5. **README.md Code Search Rules** — Is the `# Code Search Rules` / `# コードベース検索ルール` template strong enough for AI use after this change? Update both instances if AI behavior should change.
6. **DEVELOPER_GUIDE.md** — Does this change affect architecture, design decisions, or AI integration guidance? **If language patterns changed, update the language pattern reference table.**
7. **SELF_IMPROVEMENT.md** — Does this change affect the AI self-improvement workflow, rebuild/index-refresh loop, or approval rules?
8. **CLAUDE.md** — Does this change affect architecture, design decisions, or development rules?
9. **PR description** — Does this commit change the scope of the PR? Update the title/description to reflect the final state.
10. **Broken characters** — Scan changed files for U+FFFD replacement characters. These appear when Python scripts or other tools split multi-byte UTF-8 characters at the wrong boundary. Especially likely after file-splitting operations. Run: `python3 -c "import sys; [print(f'{f}:{i}') for f in sys.argv[1:] for i,l in enumerate(open(f),1) if '\ufffd' in l]" <files>`

### Documentation — keep in sync
The following files contain overlapping content that must be updated together:
- **README.md** — English section AND Japanese section (both must match)
- **TESTING_GUIDE.md** — English section AND Japanese section (both must match); update when test helpers, structure, or conventions change
- **DEVELOPER_GUIDE.md** — References README for the CLAUDE.md template and exit codes. Has its own design decisions and architecture sections. **Update the language pattern reference table** when adding/changing language patterns in SymbolExtractor or ReferenceExtractor.
- **CHANGELOG.md** — English section AND Japanese section
- **SELF_IMPROVEMENT.md** — Dedicated operating contract for iterative AI-driven cdidx self-improvement
- **CLAUDE.md** — This file; update architecture/design sections when code changes

When modifying the CLAUDE.md template (code search rules for AI agents), update both instances in README (English and Japanese). DEVELOPER_GUIDE references README, so no separate update is needed there.

### CHANGELOG style
- **Always write new entries under `[Unreleased]`**, never under a versioned heading like `[1.2.0]`. Version headings are created only at release time by the maintainer. Writing entries directly into a version heading causes premature version claims and stale compare links.
- **Never delete, move, or modify entries under an existing versioned heading** (e.g. `[1.2.0]`). Those entries are part of a published release and must not be touched. If you accidentally wrote new entries under a versioned heading, move only your new entries to `[Unreleased]` — do not delete the version heading or disturb its original entries. Before editing CHANGELOG.md, always check the existing structure first so you know which headings and entries already exist.
- One entry per distinct change. Don't merge unrelated fixes into a single entry just to reduce line count.
- But don't write separate entries for iterative commits toward the same fix — consolidate them into one entry describing the final result.
- Use [Keep a Changelog](https://keepachangelog.com/) categories: Added, Changed, Fixed, Removed.
- Each entry: `**Bold title** — Description. Affected: \`file1\`, \`file2\`.`

### Pull requests
- Title and description in **English**.
- Structure: `## Summary` (bullet points grouped by theme) + `## Test plan` (checkbox list).
- When iterating on a PR, update the title/description to reflect the final state, not the history of changes.

### Tests
When changing public API signatures or adding new public methods, check if tests need updating. Run `dotnet test` to verify. If the build environment lacks .NET SDK, at minimum verify all callers are updated by searching for the method name.
When changing test code, shared test helpers, test execution flow, or testing conventions, update `TESTING_GUIDE.md` in the same commit.
When tests create temporary git repositories and run `git commit`, configure a repo-local `user.name` and `user.email` inside the test setup instead of assuming CI has a global git identity.

### Cross-platform changes
cdidx targets Windows, macOS, and Linux. When changing filesystem behavior, path handling, process execution, console output, SQLite lifetime, or test cleanup, explicitly consider cross-platform differences such as path separators, file locking, newline behavior, and shell/tool availability. Add or update tests and docs when behavior depends on the OS.
For Windows in particular, temp repo and SQLite cleanup may require clearing SQLite pools, normalizing file attributes, and retrying directory deletion for a short period before treating it as a real failure.

### README structure
- Section numbering must be consistent (don't have "2." without "1.").
- Instructions specific to one install method (e.g. PATH setup for build-from-source) belong under that method's section, not at the top level.
- Keep explanations simple and factual. Avoid over-explaining edge cases that are unlikely in practice.

---

# cdidx (CodeIndex) — AI向け開発ガイド

## プロジェクト概要

cdidxは、ソースコードをSQLiteデータベース（FTS5）にインデックスする.NET 8 CLIツール。人間向けとAIエージェント向け（JSON）の両方の出力に対応。アセンブリ名は`cdidx`（ripgrepの`rg`のように短縮）。

## ビルド・テスト

```bash
dotnet build
dotnet test
dotnet run --project src/CodeIndex -- <command> [options]
```

## CLIコマンド

```bash
# インデックス作成
cdidx index <projectPath> [--db <path>] [--rebuild] [--verbose] [--json]
cdidx <projectPath>                          # 'index'の省略形

# クエリ（デフォルト出力: 人間向け; --jsonでAI向け出力）
cdidx search <query> [--db <path>] [--limit <n>] [--lang <lang>] [--path <pattern>] [--exclude-path <pattern>] [--exclude-tests] [--snippet-lines <n>] [--count] [--json]
cdidx definition <query> [--db <path>] [--kind <kind>] [--lang <lang>] [--limit <n>] [--path <pattern>] [--exclude-path <pattern>] [--exclude-tests] [--body] [--exact] [--json]
cdidx references <query> [--db <path>] [--kind <kind>] [--lang <lang>] [--limit <n>] [--path <pattern>] [--exclude-path <pattern>] [--exclude-tests] [--exact] [--json]
cdidx callers <query> [--db <path>] [--kind <kind>] [--lang <lang>] [--limit <n>] [--path <pattern>] [--exclude-path <pattern>] [--exclude-tests] [--exact] [--json]
cdidx callees <query> [--db <path>] [--kind <kind>] [--lang <lang>] [--limit <n>] [--path <pattern>] [--exclude-path <pattern>] [--exclude-tests] [--exact] [--json]
cdidx symbols [query] [--name <name>] [--exact] [--kind <kind>] [--lang <lang>] [--limit <n>] [--path <pattern>] [--exclude-path <pattern>] [--exclude-tests] [--since <datetime>]
cdidx files [query] [--lang <lang>] [--limit <n>] [--path <pattern>] [--exclude-path <pattern>] [--exclude-tests] [--since <datetime>]
cdidx excerpt <path> --start <line> [--end <line>] [--before <n>] [--after <n>] [--json]
cdidx map [--db <path>] [--limit <n>] [--lang <lang>] [--path <pattern>] [--exclude-path <pattern>] [--exclude-tests] [--json]
cdidx inspect <query> [--db <path>] [--limit <n>] [--lang <lang>] [--path <pattern>] [--exclude-path <pattern>] [--exclude-tests] [--body] [--exact] [--json]
cdidx outline <path> [--db <path>] [--json]
cdidx status [--json]
cdidx deps [--db <path>] [--limit <n>] [--lang <lang>] [--path <pattern>] [--exclude-path <pattern>] [--exclude-tests] [--reverse] [--json]
cdidx unused [--db <path>] [--limit <n>] [--kind <kind>] [--lang <lang>] [--path <pattern>] [--exclude-path <pattern>] [--exclude-tests] [--json]
cdidx hotspots [--db <path>] [--limit <n>] [--kind <kind>] [--lang <lang>] [--path <pattern>] [--exclude-path <pattern>] [--exclude-tests] [--json]
cdidx impact <query> [--db <path>] [--limit <n>] [--lang <lang>] [--path <pattern>] [--exclude-path <pattern>] [--exclude-tests] [--depth <n>] [--json]
cdidx languages [--json]

# MCPサーバー（AIツール向け: Claude Code, Cursor, Windsurf等）
cdidx mcp [--db <path>]
```

## アーキテクチャ

```
src/CodeIndex/
  Program.cs               — 薄いCLIエントリポイントとコマンドルーティング
  Cli/CommandExitCodes.cs  — 共通のプロセス終了コード
  Cli/ConsoleUi.cs         — スピナー、プログレスバー、バナー、イースターエッグ、バージョン、使い方
  Cli/DbPathResolver.cs    — indexコマンド用の既定DBパスを解決
  Cli/GitHelper.cs         — --commitsオプション用のgit diff-treeヘルパー
  Cli/IndexCommandRunner.cs — indexコマンド実行、更新/フルスキャンフロー、git excludeヘルパー
  Cli/QueryCommandRunner.cs — search/definition/references/callers/callees/symbols/files/excerpt/map/inspect/outline/status/unused/hotspotsコマンド実行とクエリ引数解析
  Cli/SearchSnippetFormatter.cs — 人間向け/JSON向けの一致中心検索スニペットを構築
  Cli/WorkspaceMetadataEnricher.cs — status/map/inspectにプロジェクトルート・git HEAD・dirty flagを付加
  Cli/SuggestionStore.cs    — AI提案のSHA256重複排除付きローカルJSON蓄積
  Cli/SourceCodeDetector.cs — 提案送信時のヒューリスティックによるソースコード漏洩防止
  Cli/GitHubIssueReporter.cs — 提案送信用GitHub Issues APIクライアント（ベストエフォート）
  Database/DbContext.cs     — SQLite接続、スキーマ初期化（WAL, FTS5, トリガー, busy_timeout）
  Database/DbDebug.cs       — オプトインの reader 診断（CDIDX_DEBUG=1）: 直近 SQL・パラメータ・行スナップショットを追跡し、reader 例外時に stderr へ出力
  Database/DbWriter.cs      — UPSERT（ON CONFLICT DO UPDATE）、バッチ挿入、古いファイルのパージ、参照書き込み
  Database/DbReader.cs      — コアクエリ操作（ファイル一覧、参照/caller/callee検索、抜粋再構成、ステータス、ファイル間依存分析）
  Database/DbSearchReader.cs — 全文検索操作（FTS5検索、重複排除）（partial class）
  Database/DbSymbolReader.cs — シンボルクエリ操作（シンボル検索、定義、アウトライン、分析バンドル）（partial class）
  Database/RepoMapBuilder.cs — リポジトリ俯瞰ビルダー（mapコマンド）: ファイル統計、エントリポイント採点、モジュールグループ化
  Indexer/FileIndexer.cs    — ディレクトリ走査、言語検出、FileRecord構築（警告をタプルで返す）
  Indexer/ChunkSplitter.cs  — 80行チャンク（10行重複）
  Indexer/SymbolExtractor.cs — 正規表現によるシンボル抽出（32言語対応）
  Indexer/ReferenceExtractor.cs — 正規表現による参照抽出（言語差分を考慮）
  Mcp/McpServer.cs          — MCPサーバーコア（stdin/stdout JSON-RPC 2.0 プロトコル処理）（partial class）
  Mcp/McpToolDefinitions.cs — MCPツールスキーマ定義（partial class）
  Mcp/McpToolHandlers.cs    — MCPツール実行ロジック（partial class）
  Models/                   — FileRecord, ChunkRecord, SymbolRecord, ReferenceRecord, SuggestionRecord, QueryResults（プレーンDTO）
tests/CodeIndex.Tests/
  ChunkSplitterTests.cs     — ChunkSplitterテスト
  ReferenceExtractorTests.cs — ReferenceExtractorテスト
  SymbolExtractorTests.cs   — SymbolExtractorテスト（多言語対応）
  FileIndexerTests.cs       — FileIndexerテスト（走査、検出、構築）
  DatabaseTests.cs          — DbContext/DbWriter統合テスト
  DbReaderTests.cs          — DbReaderクエリテスト（FTS、シンボル、ファイル、アウトライン、ステータス）
  McpServerTests.cs         — MCPサーバーJSON-RPCプロトコル・ツールテスト
  GitHelperTests.cs         — Gitヘルパーテスト（通常repo、worktree、フォールバック）
  ConsoleUiTests.cs         — コンソール出力、ヘルプテキスト、スピナー、バージョンテスト
  DbPathResolverTests.cs    — DBパス解決テスト
  IndexCommandRunnerTests.cs — indexコマンド統合テスト
  QueryCommandRunnerTests.cs — クエリCLI統合テスト
  ConcurrencyTests.cs        — 並行アクセステスト（WAL読み書き）
  PerformanceTests.cs        — 大規模データパフォーマンステスト
  DbRecoveryTests.cs         — DB破損復旧テスト
  SearchSnippetFormatterTests.cs — 検索スニペット整形テスト
  WorkspaceMetadataEnricherTests.cs — ワークスペースメタデータ付加テスト
  SuggestionStoreTests.cs   — 提案ストアのユニットテスト（重複排除、永続化、破損復旧）
  SourceCodeDetectorTests.cs — ソースコード漏洩検出テスト（許容 vs 拒否入力）
  GitHubIssueReporterTests.cs — GitHubトークン解決テスト
  TestProjectHelper.cs      — 一時インデックスプロジェクト作成用の共有ヘルパー
  TestConsoleLock.cs         — コンソールリダイレクトテスト用の共有ロック
```

## 主要な設計判断

- **ORMなし** — `Microsoft.Data.Sqlite`でパラメータ化クエリを直接使用。シンプルに保つ。
- **デフォルトでインクリメンタル** — `modified`タイムスタンプとSHA256チェックサムを比較し、未変更ファイルをスキップ。
- **古いファイルのパージ** — インデックス前にディスク上に存在しないファイルをDBから削除（ブランチ切り替え対応）。
- **バッチコミット** — 書き込み性能のため1トランザクション500レコード。SAVEPOINTによるネスト対応。
- **FTS5** — `fts_chunks`仮想テーブルが`chunks.content`をミラーして全文検索を提供。データベーストリガー（chunksのAFTER INSERT/DELETE/UPDATE）で同期。インデックス後にFTS5 optimizeを実行。
- **デフォルトはリテラル安全検索** — 検索クエリは既定ではトークンごとに引用し、FTS構文エラーを避ける。生のFTS5構文は `--fts` またはMCPの `rawQuery` で明示 opt-in。
- **パス考慮の絞り込みとランキング** — `search`、`definition`、`references`、`callers`、`callees`、`symbols`、`files` は繰り返し指定できる `--path`（複数値は OR で結合）、繰り返し指定できる `--exclude-path`、`--exclude-tests` を共有する。クエリ結果は tests や docs より source を優先し、`search` はシンボル名やパスの exact match を追加ブーストする。
- **AI向けの軽量検索スニペット** — `search --json` と MCP の `search` は、チャンク全文ではなく snippet range、match line、highlight、context count を含む一致中心スニペットを返す。`--snippet-lines` でペイロード量を先に制限できる。
- **初動向けの repo map** — `map` は、言語、モジュール、主要ファイル、ホットスポット、推定エントリポイントを要約し、AIクライアントが深い検索前に移動計画を立てやすくする。シンボル抽出だけで入口が取れない場合も、既知のトップレベル実行ファイルへフォールバックして候補を補う。
- **信用判断のための鮮度メタデータ** — `status` はワークスペース全体の鮮度に加えて `git_head` / `git_is_dirty` を返す。`map` は `indexed_at` / `latest_modified` を絞り込み結果の鮮度として維持しつつ、`workspace_indexed_at` / `workspace_latest_modified` でワークスペース全体の鮮度も返す。`inspect` も同じワークスペース鮮度と git フィールドを返すため、シンボル中心の AI フローで `status` を別途呼ばずに済む。`files` はファイルごとの checksum と timestamp を返す。古いDBに不足する file 列は可能なら自動追加し、その場移行できない場合も読み取りをクラッシュさせない。MCP の 0 件レスポンスは `indexed_file_count` と `indexed_at` を含み、AIクライアントが別途 `status` を呼ばなくてもインデックスの古さや空を自己診断できる。
- **まとめて取るシンボル分析** — `inspect` と MCP の `analyze_symbol` は、定義、近傍シンボル、参照、caller、callee、ファイルメタデータ、ワークスペース信頼メタデータ、graph 対応メタデータをまとめて返し、AIクライアントが1回の問い合わせで一般的なシンボル調査を終えやすくする。
- **言語差分を考慮した参照抽出** — `references`、`callers`、`callees` は、正規表現ベースの call/reference 抽出が意味を持つ言語だけに対して構築する参照テーブルに支えられる。未対応言語には低信頼な疑似グラフ結果を返さず、`search` を使う前提にする。
- **graph 対応ヒントの明示** — `inspect`、MCP の `analyze_symbol`、直接の MCP graph ツールは、未対応言語フィルタに graph 対応メタデータを付けて返し、AI クライアントが「未対応言語」と「対応言語だが 0 件」を区別できるようにする。
- **正規表現シンボル抽出** — 意図的にシンプル。速度とポータビリティを精度より優先しつつ、パターンから推論できる範囲で定義範囲、本体範囲、シグネチャ、親シンボル、可視性、戻り値型もインデックスに保持する。
- **詳細なシンボル種別** — シンボルは意味的に正確な種別を使用: `function`、`class`、`struct`、`interface`、`enum`、`property`、`event`、`delegate`、`namespace`、`import`。各言語は最も近い種別にマッピングする（例: Rust の `trait` → `interface`、Swift の `protocol` → `interface`、PHP の `trait` → `interface`）。シンボル検索と定義の結果は可視性でランキングされる（public が最優先）。
- **DBインデックス最適化** — SQLite インデックスはクエリパターンに合わせて積極的に維持管理する。新しいクエリ機能（新コマンド、新 ORDER BY、新 JOIN パターン）を追加するときは、専用インデックスで性能改善できるかを常に評価する。安全な追加のため `CREATE INDEX IF NOT EXISTS` を使用する。
- **人間向けがデフォルト** — 全コマンドのデフォルト出力は人間向け。`--json`でAI向けJSONライン出力に切り替え。
- **構造化MCPレスポンス** — MCPツールは `structuredContent` に型付きJSON、`content` に短い要約を返し、AIツールが巨大なテキスト塊をパースせずに済むようにする。
- **MCPツールアノテーション** — 全ツールが MCP 仕様に沿った `annotations`（`readOnlyHint`、`destructiveHint`、`idempotentHint`、`openWorldHint`）を返し、AIクライアントが安全な読み取り専用クエリを自動承認できるようにする。
- **MCPサーバー instructions** — `initialize` レスポンスにツール選択ガイダンスの `instructions` 文字列を含め、AIクライアントが初回接続時に適切なツールを選べるようにする。
- **後方互換な読み取りスキーマ** — 新しいcdidxバイナリで古いDBを開いた場合は、可能なら不足するシンボル列を自動追加し、新しい参照テーブルも作成する。読み取り経路でその場移行できない場合も、シンボル検索は旧カラム構成へフォールバックしてクラッシュを避ける。クエリパスは `InitializeSchema()` ではなく `TryMigrateForRead()` を使い、読み取り専用DBでも黙って縮退する。
- **構造化終了コード** — 0=成功、1=引数エラー、2=未検出、3=DBエラー。
- **ライブラリコードから直接Console出力しない** — `FileIndexer.BuildRecord()`は警告を戻り値`(FileRecord, string, string?)`で返す。表示は呼び出し元（`Cli/IndexCommandRunner.cs`）が`ConsoleUi.ClearProgressLine()`でプログレスバーをクリアしてから行う。
- **`.cdidx/`ディレクトリ** — `cdidx index` の既定では、インデックスファイルは呼び出し元のcwdではなく `<projectPath>/.cdidx/codeindex.db` に格納される。初回の`cdidx index`でディレクトリを自動作成し、`.git/info/exclude`に自動追加するためユーザーが`.gitignore`を編集する必要なし。git worktreeでは`.git`がディレクトリではなくファイルのため、`GitHelper.ResolveGitCommonDir()`で解決チェーンを辿って`info/exclude`がある共通`.git/`を見つける。Git標準の仕組み（git-lfs、Husky、JetBrains IDE等が利用）。

  **通常リポジトリ vs worktreeの構造:**
  ```
  # 通常リポジトリ — .gitがディレクトリ、info/excludeはその直下
  /projects/my-app/                   ← プロジェクトルート
  ├── 📂 .git/                        ← ディレクトリ
  │   └── 📂 info/
  │       └── exclude                 ← AddToGitExcludeがここに書き込む
  └── 📂 .cdidx/
      └── codeindex.db

  # worktree — .gitがファイル、参照を辿ってinfo/excludeを見つける
  /projects/my-app/                   ← 元リポジトリのルート
  └── 📂 .git/                        ← 実体のgitディレクトリ（共有）
      ├── 📂 info/
      │   └── exclude                 ← AddToGitExcludeがここに書き込む
      └── 📂 worktrees/
          └── 📂 feature-branch/
              └── commondir           ← "../.."が入っている

  /projects/my-app-feature/           ← worktreeのルート
  ├── .git                            ← ファイル。中身は "gitdir: /projects/my-app/.git/worktrees/feature-branch"
  └── 📂 .cdidx/
      └── codeindex.db
  ```

  **worktreeでの解決チェーン:**
  1. `.git`ファイルを読む → `gitdir: /projects/my-app/.git/worktrees/feature-branch`
  2. そのパスの`commondir`ファイルを読む → `../..`
  3. `../..`を`feature-branch/`ディレクトリ起点で解決:
     `feature-branch/` → `..` → `worktrees/` → `..` → `.git/`
  4. `.git/info/exclude`に書き込む

## コーディング規約

- コメントは英日併記（例: `// Enable WAL mode / WALモードを有効化`）
- ドキュメント（README, CHANGELOG）は前半英語、後半日本語の構成。
- **セクション内で言語を混在させない。** 英語セクションには英語のみ、日本語セクションには日本語のみを記載する。バイリンガルのインラインコードコメント（`// Enable WAL mode / WALモードを有効化`）は唯一の例外。バイリンガルコンテンツ（CLAUDE.md のルール等）を追加するときは、英語パラグラフを英語セクションに、日本語パラグラフを日本語セクションに書く — 同一セクションに両方を入れない。
- 不要なパッケージは入れない — `System.CommandLine`は手動引数解析に置き換えて削除済み。

## 変更時のルール（重要）

### 依存関係ルール
本番依存は **Microsoft.Data.Sqlite** の1個のみ — これを維持すること。ユーザーの明示的な承認なしに NuGet パッケージを追加しないこと。SELF_IMPROVEMENT.md の目的に沿った大きな改善を可能にするパッケージがある場合は、明確な理由を添えてユーザーに提案してから追加すること。

### 絶対禁止事項
コードレビューは現在のコミットから**ローカルビルドしたバイナリ**（`dotnet ./src/CodeIndex/bin/Debug/net8.0/cdidx.dll`）を使ってコードベースを検索・検証します。つまりレビュアーは、テストが主張する動作でもドキュメントが述べる動作でもなく、実行中のバイナリが実際に出す結果を見ます。**テストやレビューを表面上パスするが実際には動作しない、不完全・中身のない・欺瞞的なコードを意図的に実装することは厳禁です。** すべての機能はバイナリ自身で実行したときに正しく動作しなければなりません。「レビューを通す」ための手抜きは自己改善ループの目的を損ない、ドッグフーディングで必ず発覚します。

### コード検索ツール（Claude Code / AI ハーネス）
リポジトリ追跡の `.claude/settings.json` で、shell のコード検索・ファイル探索系コマンドを網羅的に deny しています（`rg`、`grep`、`egrep`、`fgrep`、`zgrep`、`rgrep`、`ripgrep`、`ag`、`ack`、`ack-grep`、`git grep`、`find`、`locate`、`mlocate`、`mdfind`、`cdidx`、`~/.local/bin/cdidx`）。これにより Claude Code エージェントは、これらのいずれにも黙ってフォールバックできません。`~/.local/bin/cdidx` は Linux / macOS の `install.sh` が配置する絶対パスからの逃げ道を塞ぐためのエントリです。Cloud の Claude Code セッションはそのインストール済みバイナリに依存するため、`CLOUD_BOOTSTRAP_PROMPT.md` では該当セッションに対してワークスペース上の `.claude/settings.json` から `cdidx` 系 deny 2 行をセッション限定で削除（コミットしない）するよう案内しています。検索には組み込みの Grep / Glob ツール、または**ローカルビルドしたバイナリ**（`dotnet ./src/CodeIndex/bin/Debug/net8.0/cdidx.dll`）を使ってください — グローバル版 `cdidx` は DB スキーマが古い、クエリ機能が欠けている、抽出ロジックが古くて誤った結果を返す恐れがあります。Claude Code は追跡された `deny` を絶対視し `.claude/settings.local.json` の allow でも上書きできません。denyされたコマンドを意図的に shell レベルで使う必要がある貢献者は、そのセッションに限りワークスペース上の `.claude/settings.json` を編集してコミットしない方式を使ってください（Cloud Bootstrap の Step 1.5 と同じ手順）。

### メソッドシグネチャの変更
メソッドの戻り値やパラメータを変更した場合（例: `BuildRecord`を`(FileRecord, string)`から`(FileRecord, string, string?)`に変更）、**同じコミットで全ての呼び出し元を更新すること**:
- `Cli/IndexCommandRunner.cs`（フルスキャン AND `--commits`/`--files`更新モード）
- `Mcp/McpServer.cs`
- `tests/CodeIndex.Tests/`（不要な要素は`_`で破棄）

### コンソール出力とプログレスバー
プログレスバーは`\r`（改行なし）で出力する。Windowsではstdoutとstderrがカーソル位置を共有する。**プログレスバー表示中に何かを出力する場合（WARN、ERR、verbose [OK]/[SKIP]）は必ず先に`ConsoleUi.ClearProgressLine()`を呼ぶこと**。そうしないとメッセージがバーと同じ行に結合される。

### イースターエッグテーマ
スピナーに存在する機能（ブレイルフレーム、テーマ付き絵文字＋テキスト）はプログレスバーにも反映すること。`ConsoleUi.SetProgressTheme()`は`GetSpinnerFrames()`のフレームを再利用する — フレーム定義を重複させないこと。

### コミットごとのチェックリスト
コミットのたびに、以下の各項目について更新要否を判断すること。後回しにせず、各コミット単位で確認・対応する:
1. **テスト** — この変更で既存テストが壊れないか？新規テストが必要か？`tests/` 内で影響を受けるメソッド・クラス名を検索。
2. **TESTING_GUIDE.md** — テストコード、共有ヘルパー、実行フロー、テスト規約を変えたなら、英語・日本語の両セクションを更新。
3. **CHANGELOG.md** — この変更はエントリに値するか？英語・日本語の両セクションを更新。
4. **README.md** — ユーザー向けの動作、CLIオプション、デフォルト値、使用例に影響するか？英語・日本語の両セクションを更新。
5. **README.md のコードベース検索ルール** — `# Code Search Rules` / `# コードベース検索ルール` が今回の変更後もAIに十分か？AIの検索行動を変えるべきなら両方更新する。
6. **DEVELOPER_GUIDE.md** — アーキテクチャ、設計判断、AI連携ガイドに影響するか？**言語パターンを変更した場合は言語パターン参照表も更新する。**
7. **SELF_IMPROVEMENT.md** — AI自己改善フロー、再ビルド/再インデックス手順、承認ルールに影響するか？
8. **CLAUDE.md** — アーキテクチャ、設計判断、開発ルールに影響するか？
9. **PR説明** — このコミットでPRのスコープが変わったか？タイトル・説明を最終状態に合わせて更新。
10. **文字化けチェック** — 変更したファイルに U+FFFD 置換文字がないか確認。Pythonスクリプト等でマルチバイトUTF-8文字が途中で切れると発生する。特にファイル分割操作後に注意。

### ドキュメント — 同期を保つ
以下のファイルには重複する内容があり、同時に更新する必要がある:
- **README.md** — 英語セクション AND 日本語セクション（両方一致させる）
- **TESTING_GUIDE.md** — 英語セクション AND 日本語セクション（両方一致させる）。テストヘルパー、構成、規約を変えたら更新する
- **DEVELOPER_GUIDE.md** — CLAUDE.mdテンプレートと終了コードはREADMEを参照。設計判断・アーキテクチャは独自セクション。
- **CHANGELOG.md** — 英語セクション AND 日本語セクション
- **SELF_IMPROVEMENT.md** — AIが cdidx 自身を継続改善するときの専用運用契約
- **CLAUDE.md** — このファイル。コード変更時にアーキテクチャ・設計セクションも更新

CLAUDE.mdテンプレート（AI向けコード検索ルール）を変更する場合、READMEの両インスタンス（英語・日本語）を更新すること。DEVELOPER_GUIDEはREADMEを参照しているため個別の更新は不要。

### CHANGELOGのスタイル
- **新しいエントリは必ず `[Unreleased]` の下に書く**。`[1.2.0]` のようなバージョン見出しの下には絶対に書かない。バージョン見出しはリリース時にメンテナが作成する。バージョン見出しに直接書くと、早すぎるバージョン宣言と古い compare リンクの原因になる。
- **既存のバージョン見出し配下のエントリは絶対に削除・移動・変更しない**（例: `[1.2.0]`）。それらはリリース済みの記録であり、触れてはならない。もし誤ってバージョン見出しの下に新エントリを書いてしまった場合は、自分が追加した新エントリだけを `[Unreleased]` に移す — バージョン見出しを消したり元のエントリを巻き込んだりしないこと。CHANGELOG.md を編集する前に、まず既存の構造を確認し、どの見出しとエントリが既に存在するかを把握すること。
- 変更ごとに1エントリ。無関係な修正を行数削減のために1エントリにまとめない。
- ただし、同じ修正に向けた段階的なコミットは1エントリに統合し、最終結果を記述する。
- [Keep a Changelog](https://keepachangelog.com/)のカテゴリを使用: Added, Changed, Fixed, Removed（日本語: 追加, 変更, 修正, 削除）。
- 各エントリ: `**太字タイトル** — 説明。Affected: \`file1\`, \`file2\`.`（日本語: `対象:`）

### プルリクエスト
- タイトルと説明は**英語**で書く。
- 構成: `## Summary`（テーマ別の箇条書き）+ `## Test plan`（チェックボックスリスト）。
- PRを修正していく過程で、タイトル・説明は変更履歴ではなく**最終状態**を反映するよう更新する。

### テスト
公開APIのシグネチャ変更や新しい公開メソッド追加時はテストの更新要否を確認する。`dotnet test`で検証。ビルド環境に.NET SDKがない場合でも、最低限メソッド名を検索して全呼び出し元が更新されていることを確認する。
テストコード、共有テストヘルパー、テスト実行フロー、またはテスト規約を変更した場合は、同じコミットで `TESTING_GUIDE.md` も更新する。
テスト内で一時 git リポジトリを作って `git commit` する場合は、CI に global の git identity が入っている前提にせず、テストセットアップ内で repo-local の `user.name` と `user.email` を設定すること。

### クロスプラットフォーム変更
cdidx は Windows、macOS、Linux を対象にする。ファイルシステム挙動、パス処理、プロセス実行、コンソール出力、SQLite のライフタイム、テスト後片付けを変更するときは、パス区切り、ファイルロック、改行、shell/tool の有無など OS 差分を明示的に考慮すること。挙動が OS に依存する場合は、テストとドキュメントも更新する。
特に Windows では、一時 repo や SQLite の後片付けで SQLite pool の解放、ファイル属性の正規化、短時間の削除リトライが必要になることがある。すぐ失敗扱いにせず、OS 差分として吸収できるかを先に検討する。

### READMEの構成
- セクション番号は一貫させる（「1.」なしに「2.」を書かない）。
- 特定のインストール方法に固有の手順（ビルド時のPATH設定等）はそのセクション内に置き、トップレベルに出さない。
- 説明はシンプルかつ事実ベースに。実際に起こりにくいエッジケースを過剰に説明しない。

# Changelog

All notable changes to this project will be documented in this file.

The English section comes first, followed by a Japanese translation.
The format is based on [Keep a Changelog](https://keepachangelog.com/en/1.1.0/), and this project adheres to [Semantic Versioning](https://semver.org/spec/v2.0.0.html).

## English

### [Unreleased]

#### Fixed
- **`install.sh` now copies bundled SQLite native library and `version.json`** — The one-liner installer previously copied only the `cdidx` binary from the release tarball, leaving `libe_sqlite3.so` (or `libe_sqlite3.dylib` on macOS) and `version.json` behind. This caused every DB operation on a freshly installed cdidx to crash with `libe_sqlite3: cannot open shared object file`, and `cdidx --version` to silently fall back to reporting `v0.0.0`. The installer now copies every sibling runtime file shipped in the tarball next to the binary. Affected: `install.sh`.

### [1.8.0] - 2026-04-12

#### Added
- **Did-you-mean suggestions for unknown commands** — When a user types an unrecognized command (e.g. `cdidx serach`), cdidx now suggests the closest valid command using Levenshtein distance (threshold: 3). Affected: `Program.cs`, `ConsoleUi.cs`.
- **One-line summary in `status` output** — `status` now includes a `summary` field with a human-readable one-liner (file/symbol/ref counts, top languages, freshness, dirty state). Shown as the first line in human-readable mode and as a JSON field for AI agents. Affected: `QueryCommandRunner.cs`, `QueryResults.cs`.
- **`reference_count` in `files` output** — `files --json` and MCP `files` now include `reference_count` per file, complementing the existing `symbol_count`. Helps AI agents identify hot files that are heavily referenced. Affected: `DbReader.cs`, `QueryResults.cs`.
- **`impact` CLI command and `impact_analysis` MCP tool** — Compute transitive callers of a symbol using BFS — the ripple effect of changing it. Shows callers at each depth level with file paths and reference counts. `--depth` controls max BFS depth (default: 5). Available as both `cdidx impact <symbol>` CLI command and `impact_analysis` MCP tool. Affected: `DbReader.cs`, `QueryCommandRunner.cs`, `Program.cs`, `ConsoleUi.cs`, `McpToolDefinitions.cs`, `McpToolHandlers.cs`, `McpServer.cs`, `QueryResults.cs`.
- **Zig, CSS/SCSS reference extraction** — Two more languages now support call-graph queries. Zig uses `//` comments. CSS has no line comments (block comments `/* */` already handled). Affected: `ReferenceExtractor.cs`.
- **Gradle, Terraform, Protobuf, Dockerfile, Makefile reference extraction** — Five more languages now support call-graph queries. Gradle/Groovy uses `//` comments. Terraform, Dockerfile, Makefile, and Protobuf use `#` comments. Language-specific keywords (build system targets, Terraform blocks, etc.) added to the ignore list. Affected: `ReferenceExtractor.cs`.
- **R, PowerShell, Haskell reference extraction** — R, PowerShell, and Haskell now support call-graph queries (`references`, `callers`, `callees`). R uses `#` for comments with standard parenthesized call detection. PowerShell uses `#` for comments; cmdlet hyphenated names are split at hyphens. Haskell uses `--` for comments; only parenthesized calls are captured (space-separated calls are a known limitation). Language-specific keywords added to the ignore list. Affected: `ReferenceExtractor.cs`.

#### Changed
- **Impact analysis uses exact symbol matching and exposes truncation** — `GetTransitiveCallers` now uses a dedicated exact-match caller query (`=` instead of `LIKE %query%`) to prevent fuzzy expansion where querying `Run` would also pull callers of `RunAsync`, `ShouldRun`, etc. Per-hop limits are tied to the caller's requested limit instead of a hard-coded 100 cap. Results include a `truncated` flag in both CLI and MCP output so AI agents can tell when the graph is incomplete. Affected: `DbReader.cs`, `QueryCommandRunner.cs`, `McpToolHandlers.cs`.
- **Shell removed from graph-supported languages** — Shell scripts use command-style invocations (`foo arg1 arg2`) not parenthesized calls, so the regex-based extractor cannot meaningfully detect call edges. Advertising shell as graph-supported would mislead AI clients into trusting empty results as authoritative. Affected: `ReferenceExtractor.cs`.
- **`--count` support for `unused` and `hotspots`** — Both commands now support the `--count` flag for AI preflight, matching the pattern used by search/definition/symbols/references/callers/callees. Affected: `QueryCommandRunner.cs`.
- **Language support summary in status output** — `status` now shows a "Support" line with total detected languages, languages with symbol extraction, and languages with graph queries (e.g. "46 detected, 32 with symbols, 31 with graph"). Graph line also shows the count prefix. Affected: `QueryCommandRunner.cs`.
- **`--since` filter for `symbols` and `definition` commands** — Both `symbols` and `definition` now accept `--since <datetime>` to filter to recently modified files, matching the existing `files --since` pattern. MCP `symbols` tool also exposes the `since` parameter. Useful for AI agents tracking recent code changes. Affected: `DbSymbolReader.cs`, `QueryCommandRunner.cs`, `McpToolHandlers.cs`, `McpToolDefinitions.cs`.
- **Consistent `--lang` validation hints on zero-result commands** — `references`, `callers`, and `callees` now show a `WriteLangHint` when `--lang` produces zero results, matching the pattern used by `symbols`, `unused`, and `hotspots`. Affected: `QueryCommandRunner.cs`.

#### Fixed
- **Shell completions and help text missing `validate`, `deps`, `unused`, `hotspots`** — Added `validate` and `deps` to the shell completions command list and added usage lines and command descriptions for `validate`, `deps`, `unused`, and `hotspots` in help output. Affected: `ConsoleUi.cs`.
- **Kind hint shows all valid kinds, not just in-index kinds** — `--kind` validation now uses a static list of all 10 valid symbol kinds instead of querying only kinds present in the current index. Invalid kinds get an "Available:" list; valid-but-absent kinds get a separate "no X symbols in the index" message with the indexed kinds. Affected: `QueryCommandRunner.cs`.
- **Legacy removed-language edges no longer leak from graph readers, and impact paging is stable** — `references`, `callers`, `callees`, `impact`, and file dependency reads now share a graph-supported language predicate so pre-upgrade shell rows do not surface until reindexing runs. `impact` paging now uses SQL `OFFSET` with a deterministic order instead of replaying larger `LIMIT` windows, preventing skipped callers across BFS pages. Affected: `DbReader.cs`, `DbReaderTests.cs`.

### [1.7.0] - 2026-04-12

#### Added
- **Granular C# symbol kinds** — C# symbols now use semantically precise kinds: `property` (get/set and expression-bodied), `interface`, `enum`, `struct` (including `record struct`, `ref struct`, `readonly struct`), `event`, and `delegate`. Previously all mapped to generic `class` or `function`. AI agents can now filter by `--kind property`, `--kind interface`, etc. Shell completions updated for bash/zsh/fish. Affected: `SymbolExtractor.cs`, `ConsoleUi.cs`.
- **Granular Java/Kotlin symbol kinds** — Java interfaces and enums now use `kind="interface"` and `kind="enum"` instead of `kind="class"`. Kotlin sealed interfaces use `kind="interface"`, enum classes use `kind="enum"`, and `val`/`var` properties use `kind="property"`. Affected: `SymbolExtractor.cs`.
- **Granular symbol kinds for all typed languages** — TypeScript, Go, Rust, Swift, C, C++, PHP, Scala, Dart, GraphQL, and VB.NET now use semantically precise kinds: `struct`, `interface` (including traits/protocols), `enum`. Affected: `SymbolExtractor.cs`.
- **Zig symbol extraction** — Added symbol extraction patterns for Zig: `pub fn`/`fn` (function), `const struct` (struct), `const enum` (enum), `const union`/`error` (class), `test` blocks, and `@import`. Zig was previously mapped as a language but had zero extraction patterns. Affected: `SymbolExtractor.cs`.
- **PowerShell symbol extraction** — Added symbol extraction patterns for PowerShell (.ps1): `function`/`filter` (function), `class` (class), `enum` (enum), `Import-Module`/`using module` (import). Affected: `SymbolExtractor.cs`.
- **`unused` CLI command and `unused_symbols` MCP tool** — Find symbols defined but never referenced in the indexed codebase (potential dead code). Only meaningful for languages with reference extraction support. Available as `cdidx unused` CLI command and `unused_symbols` MCP tool. Affected: `DbSymbolReader.cs`, `QueryCommandRunner.cs`, `Program.cs`, `ConsoleUi.cs`, `McpToolDefinitions.cs`, `McpToolHandlers.cs`, `McpServer.cs`.
- **CSS/SCSS symbol extraction** — Added symbol extraction for CSS/SCSS: `.class` selectors (class), `#id` selectors (function), `@mixin` (function), `@keyframes` (function), `@import`/`@use` (import), `$variable` (property). Affected: `SymbolExtractor.cs`.
- **`hotspots` CLI command and `symbol_hotspots` MCP tool** — Find the most-referenced symbols in the codebase, ordered by reference count. Useful for identifying central, high-impact code that changes may affect widely. Affected: `DbSymbolReader.cs`, `QueryCommandRunner.cs`, `Program.cs`, `ConsoleUi.cs`, `McpToolDefinitions.cs`, `McpToolHandlers.cs`, `McpServer.cs`.

#### Changed
- **Visibility-weighted symbol ranking** — `symbols` and `definition` results now rank public symbols above private/internal ones. Ranking tiers: public/open/pub/export (0) > protected (1) > internal (2) > private (3) > unknown (4). AI agents searching for API surface get the most relevant results first. Affected: `DbReader.cs`, `DbSymbolReader.cs`.
- **Recently-modified tiebreaker in search ranking** — Among equally-ranked search results, recently modified files now appear first. Adds `f.modified DESC` before the final alphabetical tiebreaker. Affected: `DbSearchReader.cs`.
- **Additional DB indexes for new query patterns** — Added `symbols(kind)` for standalone `--kind` filter, `symbols(visibility)` for visibility-weighted ranking, and `symbol_references(symbol_name, reference_kind)` for hotspot/unused analysis performance. Affected: `DbContext.cs`.
- **Kind/lang validation hints for unused and hotspots** — `unused` and `hotspots` commands now show helpful hints when zero results are found (invalid kind, unknown language, stale index, filter suggestions). Affected: `QueryCommandRunner.cs`.
- **F# reference extraction** — F# now supports call-graph queries (`references`, `callers`, `callees`). Parenthesized calls like `someFunc(x)` and constructor calls `new ClassName()` are detected. F#-specific keywords added to the ignore list. Note: space-separated F# calls (`List.map f xs`) are not captured — this is a known limitation of regex-based extraction. Affected: `ReferenceExtractor.cs`.
- **MCP instructions: symbol kind filter guidance** — AI clients now receive guidance on filtering symbols by kind (function, class, struct, interface, enum, property, event, delegate) in the MCP initialize instructions. Affected: `McpToolHandlers.cs`.
- **SQL reference extraction** — SQL now supports call-graph queries. SQL function calls like `COALESCE()`, `LOWER()`, `LENGTH()`, `NOW()` are detected. SQL keywords (uppercase) added to the ignore list. Affected: `ReferenceExtractor.cs`.
- **README MCP tool table sync** — Updated MCP tool tables (English and Japanese) to list all 21 tools including `unused_symbols`, `symbol_hotspots`, `validate`, `ping`, and `suggest_improvement`. Updated tool count from 16 to 21. Affected: `README.md`.
- **ANSI color-coded symbol kinds** — `symbols`, `unused`, and `hotspots` CLI output now color-codes symbol kinds: cyan for class/struct, blue for interface, magenta for enum/delegate, yellow for function, green for property, red for event, dim for namespace/import. Colors degrade to plain text when output is piped. Affected: `ConsoleUi.cs`, `QueryCommandRunner.cs`.
- **Cross-language symbol kind consistency tests** — Added 32 parameterized test cases verifying that all typed languages (C#, Java, Kotlin, TypeScript, Go, Rust, Swift, C, C++, PHP, Scala, Dart, GraphQL) produce the expected symbol kinds (struct, interface, enum, property, delegate, event). Prevents regressions when modifying extraction patterns. Affected: `SymbolExtractorTests.cs`.
- **Cyclomatic complexity estimate** — `definition --body --json` and MCP `definition` with `includeBody` now include a `complexity` field: a regex-based cyclomatic complexity estimate (baseline 1, counting if/else/for/while/case/catch/??/&&/|| etc.). This is a heuristic, not a true CFG analysis, but helps AI agents identify refactoring targets. Affected: `SymbolExtractor.cs`, `DbSymbolReader.cs`, `QueryResults.cs`.
- **PowerShell .psm1/.psd1 file support** — Added `.psm1` (module) and `.psd1` (data) extensions to PowerShell language detection. Affected: `FileIndexer.cs`.

#### Fixed
- **README HTML tag rendering on NuGet** — Removed all `<details>` / `<summary>` HTML tags that NuGet's Markdown renderer displayed as raw text. Replaced collapsible sections with bold labels. Shortened Japanese comparison heading from `cdidx と rg の違い` to `rg との違い`. Affected: `README.md`.
- **unused/hotspots bare-name collision and unsupported-language false positives** — `unused` now defaults to graph-supported languages only, preventing unsupported languages (CSS, Zig, PowerShell, etc.) from producing false "all symbols unused" results. `hotspots` GROUP BY now includes container_name to distinguish same-named symbols in different classes. Both commands warn when querying unsupported languages. MCP `unused_symbols` includes `graph_supported` metadata. Affected: `DbSymbolReader.cs`, `QueryCommandRunner.cs`, `McpToolHandlers.cs`.
- **C# call sites misidentified as definitions (#40)** — Fixed C# method pattern and explicit interface implementation pattern matching call-site lines like `await FuncName(...)`, `return service.GetResult()`, `throw factory.Create(...)` as method definitions. Added negative lookahead for statement keywords (await, return, throw, yield, var, etc.) to both patterns. Affected: `SymbolExtractor.cs`.
- **C# generic method overloads not extracted (#41)** — Fixed C# method pattern not matching generic methods like `TryRaise<T>(...)` or `GetItems<TKey, TValue>(...)`. The pattern now allows optional type parameters `<...>` between the method name and the opening parenthesis. Also applied to explicit interface implementation pattern. Affected: `SymbolExtractor.cs`.
- **Empty query accepted by definition/references/callers/callees (#43)** — These commands now reject empty or whitespace-only queries with a clear error message (exit code 1), matching the existing behavior of `inspect`. Affected: `QueryCommandRunner.cs`.
- **`--since` with invalid date silently returns all files (#44)** — `--since` now uses strict invariant ISO 8601 parsing (`DateTimeOffset.TryParseExact` with `CultureInfo.InvariantCulture`), rejecting ambiguous locale-dependent formats like `01/02/2024`. Bare `--since` with no value is also rejected instead of silently dropping the filter. The parser is shared between CLI and MCP. Affected: `QueryCommandRunner.cs`, `McpToolHandlers.cs`.
- **`map --path` returns exit code 0 for nonexistent path (#45)** — `map` now returns exit code 2 with an error message when filters produce zero files, matching the pattern used by `outline` and `excerpt`. MCP `repo_map` adds freshness hints on zero results. Affected: `QueryCommandRunner.cs`, `McpToolHandlers.cs`.
- **`outline` throws database NULL error on certain files (#46)** — Fixed `GetOutline` crashing with "The data is NULL at ordinal 3" when a symbol has NULL `start_line`/`end_line`. Now falls back to `line` value, matching the pattern used by `SearchSymbols` and `GetNearbySymbols`. Affected: `DbSymbolReader.cs`.
- **`excerpt --start N --end M` silently returns single line when start > end (#47)** — CLI `excerpt` now rejects `--start > --end` with exit code 1. MCP `excerpt` already validated this. Affected: `QueryCommandRunner.cs`.

### [1.6.0] - 2026-04-12

#### Added
- **One-liner install script** — `install.sh` enables installing cdidx in containers and CI environments without .NET SDK. Downloads self-contained binaries from GitHub Releases with SHA256 checksum verification. Supports `linux-x64`, `linux-arm64`, and `osx-arm64` (glibc only). Detects musl-based Linux (e.g. Alpine) and fails fast with a clear error. Affected: `install.sh`.
- **linux-arm64 release builds** — Added `linux-arm64` to the release matrix for ARM container support (Apple Silicon Docker, ARM CI). Cross-compiled on x64 runners with test execution skipped for the cross-compiled target. Affected: `.github/workflows/release.yml`.
- **README installation overhaul** — Reorganized Installation sections (English and Japanese) to lead with the `curl | bash` installer. Removed standalone Prerequisites section; .NET SDK requirement moved to NuGet/build-from-source options. Updated Code Search Rules templates and 30-second quick start sections. Dockerfile examples use `CDIDX_INSTALL_DIR=/usr/local/bin` for correct PATH in container builds. Affected: `README.md`.

### [1.5.0] - 2026-04-12

#### Added
- **`suggest_improvement` MCP tool** — AI agents can report structured improvement suggestions or error reports (crash reports, unexpected errors, feature gaps). Suggestions are saved locally to `.cdidx/suggestions.json` with SHA256 dedup and file-level locking for concurrent access safety. When `CDIDX_GITHUB_TOKEN` is explicitly set, suggestions are also filed as GitHub Issues on widthdom/CodeIndex. Descriptions are validated by `SourceCodeDetector` (heuristic, not a security boundary) to reject common pasted code patterns. The tool only activates when explicitly called by an AI agent. Affected: `SuggestionRecord.cs`, `SuggestionStore.cs`, `SourceCodeDetector.cs`, `GitHubIssueReporter.cs`, `McpToolDefinitions.cs`, `McpToolHandlers.cs`, `McpServer.cs`.

#### Fixed
- **GitHub token safety** — Only `CDIDX_GITHUB_TOKEN` is accepted for suggestion submission. Generic `GITHUB_TOKEN` is no longer used, preventing ambient CI tokens from silently publishing to an external repository. Affected: `GitHubIssueReporter.cs`.
- **SourceCodeDetector fenced block bypass** — Added detection for markdown fenced code blocks (`` ``` ``), closing a bypass where short unindented code inside fences would pass all other heuristics. Affected: `SourceCodeDetector.cs`.
- **Atomic and locked suggestion storage** — `SuggestionStore` now uses write-to-temp-and-rename for crash-safe writes, file-level locking for concurrent access safety, and fail-closed behavior on transient I/O errors. Corrupt files are preserved as `.bak` instead of silently discarded. Affected: `SuggestionStore.cs`.

#### Changed
- **Privacy documentation aligned with actual behavior** — README and DEVELOPER_GUIDE now describe `SourceCodeDetector` as a heuristic guard (not a security boundary), honestly stating that short inline code examples are allowed by design. Affected: `README.md`, `DEVELOPER_GUIDE.md`.

### [1.4.1] - 2026-04-12

### Fixed
- Fix broken code blocks in `README.md`.

### [1.4.0] - 2026-04-12

#### Added

- **Final dogfooding verification** — Full rebuild and self-analysis: 61 files, 1254 symbols, 4798 references, 46 languages detected, 29 with symbol extraction, 18 with graph queries. 322 tests (320 pass + 2 skip). All documentation synchronized. Affected: `CHANGELOG.md`.

- **CLAUDE.md architecture: SymbolExtractor count updated to 29 languages** — Affected: `CLAUDE.md`.

- **README graph language list updated to 18 languages** — Added Lua and VB.NET to graph-supported language lists in both EN/JP sections. Affected: `README.md`.

- **DEVELOPER_GUIDE: VB.NET graph=yes** — Updated VB.NET to graph=yes in both EN/JP language tables (now 18 graph-supported languages). Affected: `DEVELOPER_GUIDE.md`.

- **VB.NET call-graph reference extraction** — VB.NET now supports `references`, `callers`, and `callees` queries. Added `'` comment stripping. Affected: `src/CodeIndex/Indexer/ReferenceExtractor.cs`.

- **batch_query supports ping tool** — The `ping` tool can now be called within `batch_query`. Added test. Affected: `src/CodeIndex/Mcp/McpToolHandlers.cs`, `tests/CodeIndex.Tests/McpServerTests.cs`.

- **DEVELOPER_GUIDE language table: Lua graph, Swift actor, PHP namespace** — Updated Lua to graph=yes, Swift to include actor/typealias, PHP to include readonly class/namespace/use/const. Both EN/JP sections. Affected: `DEVELOPER_GUIDE.md`.

- **MCP instructions mention search exact mode** — AI clients are now guided to use `search` with `exact: true` for case-sensitive matching. Affected: `src/CodeIndex/Mcp/McpToolHandlers.cs`.

- **PHP: readonly class, namespace, use, const, expanded modifiers** — PHP patterns now support `readonly class` (PHP 8.2+), `namespace`, `use` imports, `const` declarations, and enum types. Affected: `src/CodeIndex/Indexer/SymbolExtractor.cs`, `tests/CodeIndex.Tests/SymbolExtractorTests.cs`.

- **Swift: actor, distributed actor, typealias, expanded method modifiers** — Swift patterns now support `actor` (Swift 5.5+), `distributed actor`, `typealias`, and additional method modifiers (`nonisolated`, `mutating`, `nonmutating`). Affected: `src/CodeIndex/Indexer/SymbolExtractor.cs`, `tests/CodeIndex.Tests/SymbolExtractorTests.cs`.

- **Lua call-graph reference extraction** — Lua now supports `references`, `callers`, and `callees` queries. Added `--` comment stripping for Lua. Affected: `src/CodeIndex/Indexer/ReferenceExtractor.cs`, `tests/CodeIndex.Tests/ReferenceExtractorTests.cs`.

- **MCP `search` tool `exact` parameter** — The MCP `search` tool now accepts `exact` boolean for case-sensitive substring matching, matching the CLI `--exact` flag. Affected: `src/CodeIndex/Mcp/McpToolHandlers.cs`, `src/CodeIndex/Mcp/McpToolDefinitions.cs`.

- **`search --exact` flag for case-sensitive substring match** — New `--exact` option bypasses FTS5 tokenization and uses direct `instr()` for case-sensitive exact substring matching. Useful when FTS5's case-insensitive token matching returns too many results. Affected: `src/CodeIndex/Database/DbSearchReader.cs`, `src/CodeIndex/Cli/QueryCommandRunner.cs`, `src/CodeIndex/Cli/ConsoleUi.cs`.

- **README Code Search Rules updated with deps, --since, --dry-run, --count** — Query strategy sections (EN/JP) now cover `deps`, `--reverse`, `--since`, `--dry-run`, and `--count`. Updated symbol-aware language list to 29. Removed Shell/SQL/Terraform from "no symbol extraction" list. Affected: `README.md`.

- **DEVELOPER_GUIDE language pattern reference table** — Replaced the old 21-language table with a comprehensive 29-language table including Graph support column. Added maintenance rule to CLAUDE.md per-commit checklist: update the table when language patterns change. Affected: `DEVELOPER_GUIDE.md`, `CLAUDE.md`.

- **TESTING_GUIDE updated with new test files** — Added ConcurrencyTests, PerformanceTests, DbRecoveryTests to both English and Japanese test layout sections. Affected: `TESTING_GUIDE.md`.

- **Cross-platform path separator test** — Verified Windows-style backslash paths are normalized to forward slashes in file records. Affected: `tests/CodeIndex.Tests/FileIndexerTests.cs`.

- **C# attribute-decorated member test coverage** — Verified `[Serializable]`, `[Obsolete]`, `[HttpGet]` on lines before class/method do not block extraction. Affected: `tests/CodeIndex.Tests/SymbolExtractorTests.cs`.

- **MCP `ping` tool** — Lightweight connection check that returns server version, timestamp, DB path, and whether the DB exists. No database required. AI agents can verify MCP connectivity before issuing queries. Affected: `src/CodeIndex/Mcp/McpServer.cs`, `src/CodeIndex/Mcp/McpToolDefinitions.cs`, `src/CodeIndex/Mcp/McpToolHandlers.cs`, `tests/CodeIndex.Tests/McpServerTests.cs`.

- **MCP `search` tool `noDedup` parameter** — The MCP `search` tool now accepts a `noDedup` boolean to disable overlapping-chunk deduplication, matching the CLI `--no-dedup` flag. Affected: `src/CodeIndex/Mcp/McpToolHandlers.cs`, `src/CodeIndex/Mcp/McpToolDefinitions.cs`.

- **`search --since` filter** — The `--since` option now works on `search` (CLI and MCP), not just `files`. AI agents can search only within recently modified files, reducing noise in large repositories. Affected: `src/CodeIndex/Database/DbSearchReader.cs`, `src/CodeIndex/Cli/QueryCommandRunner.cs`, `src/CodeIndex/Mcp/McpToolHandlers.cs`, `src/CodeIndex/Mcp/McpToolDefinitions.cs`.

- **`--dry-run` flag for index command** — New `--dry-run` option scans files without writing to the database. Shows file count and language breakdown, useful for verifying what would be indexed. Affected: `src/CodeIndex/Cli/IndexCommandRunner.cs`, `src/CodeIndex/Cli/ConsoleUi.cs`.

- **DB compound indexes for query performance** — Added `symbols(file_id, kind)`, `files(lang, modified)`, and `symbol_references(container_name, reference_kind)` compound indexes to accelerate common query patterns. Affected: `src/CodeIndex/Database/DbContext.cs`.

- **Shell, SQL, Terraform symbol extraction** — Shell: bash/zsh function declarations. SQL: CREATE TABLE/VIEW/FUNCTION/PROCEDURE/TRIGGER/INDEX, ALTER TABLE. Terraform: resource, data, module, variable, output, locals. All three languages now support `symbols`, `definition`, and `outline` queries. Affected: `src/CodeIndex/Indexer/SymbolExtractor.cs`, `tests/CodeIndex.Tests/SymbolExtractorTests.cs`.

- **Ruby: attr_accessor/reader/writer and Rails DSL extraction** — Ruby patterns now extract `attr_accessor :name`, `attr_reader :email`, and Rails DSL (`has_many`, `has_one`, `belongs_to`, `scope`) as function symbols. Affected: `src/CodeIndex/Indexer/SymbolExtractor.cs`, `tests/CodeIndex.Tests/SymbolExtractorTests.cs`.

- **Rust: macro_rules!, mod, const/static, const fn, unsafe fn, union, type alias** — Rust patterns now support `macro_rules!`, `mod` modules, `const`/`static` items, `const fn`, `unsafe fn`, `extern "C" fn`, `union`, and `type` aliases. Affected: `src/CodeIndex/Indexer/SymbolExtractor.cs`, `tests/CodeIndex.Tests/SymbolExtractorTests.cs`.

- **TypeScript: abstract class, declare, namespace/module, readonly/override** — TypeScript patterns now support `abstract class`, `declare class/module/interface`, `namespace/module`, and `readonly`/`abstract`/`override` method modifiers. Affected: `src/CodeIndex/Indexer/SymbolExtractor.cs`, `tests/CodeIndex.Tests/SymbolExtractorTests.cs`.

- **Python: decorated and dunder method test coverage** — Added tests verifying `@dataclass`, `@property`, `@staticmethod` decorated classes/methods, dunder methods (`__init__`, `__str__`), and type hints in signatures are correctly extracted by existing patterns. Affected: `tests/CodeIndex.Tests/SymbolExtractorTests.cs`.

- **Go: type aliases, const block members, package-level var** — Go patterns now extract `type Name = OtherType` aliases, `const ( Name = value )` block members, and `var Name Type` package-level variables. Affected: `src/CodeIndex/Indexer/SymbolExtractor.cs`, `tests/CodeIndex.Tests/SymbolExtractorTests.cs`.

- **Kotlin: companion object, value/sealed/inner/annotation class, extension functions, val/var properties** — Expanded Kotlin patterns with companion object, value class, sealed interface, inner class, annotation class, extension functions (`fun Type.name()`), suspend/inline/infix/operator/tailrec modifiers, and const/lateinit val/var property extraction. Affected: `src/CodeIndex/Indexer/SymbolExtractor.cs`, `tests/CodeIndex.Tests/SymbolExtractorTests.cs`.

- **Java IgnoredCallNames expansion** — Added `instanceof`, `super`, `assert`, `throws`, `extends`, `implements`, `synchronized` to the reference extractor's ignore list to reduce false-positive call references in Java code. Affected: `src/CodeIndex/Indexer/ReferenceExtractor.cs`.

- **Java symbol extraction: record, sealed, @interface, static final, enum members, expanded modifiers** — Java patterns now support `record` (Java 16+), `sealed`/`non-sealed` classes (Java 17+), `@interface` annotation types, `static final` constants (C# const equivalent), enum members, and expanded method modifiers (`default`, `native`, `final`, `strictfp`). Cross-language parity with recent C# improvements. Affected: `src/CodeIndex/Indexer/SymbolExtractor.cs`, `tests/CodeIndex.Tests/SymbolExtractorTests.cs`.

- **Pattern externalization design note in DEVELOPER_GUIDE** — Documented the current inline-regex approach, trade-offs, and a future externalization path (JSON/TOML with schema: language, kind, regex, body style, capture groups). Both English and Japanese sections. Affected: `DEVELOPER_GUIDE.md`.

- **README TL;DR section and doc links** — Added collapsible TL;DR section at the top of README (EN/JP) with quick-start commands, feature counts, and links to DEVELOPER_GUIDE, SELF_IMPROVEMENT, and TESTING_GUIDE. Makes the GitHub/NuGet entry point more scannable without removing detailed content. Affected: `README.md`.

- **CLAUDE.md Japanese section sync** — Added missing `--reverse` to deps, updated architecture sections for file split (DbSearchReader, DbSymbolReader, McpToolDefinitions, McpToolHandlers, QueryResults), added new test files. Both English and Japanese sections now match. Affected: `CLAUDE.md`.

- **Unicode/CJK character tests** — Added tests verifying Japanese, Chinese, and Korean characters in file content, class names, and method names are correctly indexed and extracted. .NET `\w` regex matches Unicode letters. Affected: `tests/CodeIndex.Tests/FileIndexerTests.cs`.

- **DB corruption recovery tests** — Added `DbRecoveryTests.cs` with tests for corrupted DB handling (no crash), rebuild-after-corruption, and proper exit code on missing DB. Affected: `tests/CodeIndex.Tests/DbRecoveryTests.cs`.

- **`--rebuild` flag behavior tests** — Added tests verifying `--rebuild` drops and re-scans all files, and that `--rebuild` conflicts with `--commits`. Affected: `tests/CodeIndex.Tests/IndexCommandRunnerTests.cs`.

- **Large-scale performance tests (10K+ files)** — Added `PerformanceTests.cs` with 10K file insert benchmark and 1K file FTS5 search latency test. Affected: `tests/CodeIndex.Tests/PerformanceTests.cs`.

- **Concurrent access tests** — Added `ConcurrencyTests.cs` with tests for concurrent reads (WAL mode allows parallel readers) and concurrent read-during-write scenarios. Affected: `tests/CodeIndex.Tests/ConcurrencyTests.cs`.

- **"Why SQLite?" section in developer guide** — Documents the rationale for choosing SQLite over alternatives (PostgreSQL, DuckDB, LiteDB, Tantivy, vector DBs), what makes it the right fit, and when it would not be enough. Both English and Japanese sections. Affected: `DEVELOPER_GUIDE.md`.

#### Changed

- **Split DbReader.cs and McpServer.cs for maintainability** — Extracted 21 result DTOs from `DbReader.cs` into `Models/QueryResults.cs` (243 lines). Split `McpServer.cs` (1349 lines) into three partial-class files: core protocol handling (332 lines), tool definitions (299 lines), and tool execution handlers (749 lines). Further split `DbReader.cs` (1071 lines) into three partial-class files: core file/reference/metadata queries (651 lines), full-text search (116 lines), and symbol queries (327 lines). All public APIs unchanged. Affected: `src/CodeIndex/Database/DbReader.cs`, `src/CodeIndex/Database/DbSearchReader.cs`, `src/CodeIndex/Database/DbSymbolReader.cs`, `src/CodeIndex/Models/QueryResults.cs`, `src/CodeIndex/Mcp/McpServer.cs`, `src/CodeIndex/Mcp/McpToolDefinitions.cs`, `src/CodeIndex/Mcp/McpToolHandlers.cs`.

#### Fixed

- **Event subscription regex restricted to PascalCase identifiers** — `+=`/`-=` event subscription detection now requires both LHS and RHS to be PascalCase identifiers (e.g. `Click += OnClick`), preventing false positives from arithmetic like `count += 1` or `flags -= mask`. Affected: `src/CodeIndex/Indexer/ReferenceExtractor.cs`, `tests/CodeIndex.Tests/ReferenceExtractorTests.cs`.

- **Enum member pattern restricted to avoid object initializer false positives** — Tightened the C# enum member regex to only match when the optional `=` value is numeric, hex, or another PascalCase identifier (with optional `|` for flags). String and object assignments in initializers no longer produce false symbols. Affected: `src/CodeIndex/Indexer/SymbolExtractor.cs`, `tests/CodeIndex.Tests/SymbolExtractorTests.cs`.

- **`batch_query` now surfaces subquery validation errors** — When a subquery fails validation (e.g. `search` without `query`), the error message is now included in the batch result instead of silently returning `null`. Affected: `src/CodeIndex/Mcp/McpServer.cs`.

- **`deps` false edges from same-name symbols and missing exclude-path** — The `deps` query now uses DISTINCT triples `(source_path, target_path, symbol_name)` to avoid inflated reference counts from same-name symbols across files (e.g. multiple `Run` or `Dispose` methods). Also wired `excludePathPatterns` into the SQL and parameter binding — previously accepted but silently ignored. Affected: `src/CodeIndex/Database/DbReader.cs`.

- **`validate` command for encoding issue detection** — New `cdidx validate [--json] [--kind <kind>] [--path <pattern>]` CLI command and MCP `validate` tool that report encoding issues found during indexing: U+FFFD replacement characters, UTF-8 BOM markers, null bytes, and mixed line endings. Issues are stored in a new `file_issues` table during indexing and queryable any time. Affected: `src/CodeIndex/Indexer/FileIndexer.cs`, `src/CodeIndex/Models/FileIssue.cs`, `src/CodeIndex/Database/DbContext.cs`, `src/CodeIndex/Database/DbWriter.cs`, `src/CodeIndex/Database/DbReader.cs`, `src/CodeIndex/Cli/QueryCommandRunner.cs`, `src/CodeIndex/Cli/ConsoleUi.cs`, `src/CodeIndex/Cli/IndexCommandRunner.cs`, `src/CodeIndex/Program.cs`, `src/CodeIndex/Mcp/McpServer.cs`, `src/CodeIndex/Mcp/McpToolDefinitions.cs`, `src/CodeIndex/Mcp/McpToolHandlers.cs`, `tests/CodeIndex.Tests/McpServerTests.cs`.

#### Changed

- **DEVELOPER_GUIDE symbol extraction count updated to 26** — Reflects all newly supported languages. Affected: `DEVELOPER_GUIDE.md`.

- **C# `using` declaration reference exclusion test** — Verified that `using var x = ...;` does not generate false-positive references while real calls within the using scope are still captured. Affected: `tests/CodeIndex.Tests/ReferenceExtractorTests.cs`.

- **C# partial method test coverage** — Added test verifying C# 9 extended partial methods (`partial void OnInit();`, `public partial string GetName();`) are correctly extracted. Affected: `tests/CodeIndex.Tests/SymbolExtractorTests.cs`.

- **DEVELOPER_GUIDE architecture updated for deps and batch_query** — DbReader description mentions file-level deps; McpServer mentions batch_query. Affected: `DEVELOPER_GUIDE.md`.

- **C# `file` modifier in method patterns** — `file static void DoWork()` (C# 11 file-scoped members) is now extracted. Test added for file-scoped types. Affected: `src/CodeIndex/Indexer/SymbolExtractor.cs`, `tests/CodeIndex.Tests/SymbolExtractorTests.cs`.

- **CLAUDE.md CLI commands updated with --since and deps** — CLAUDE.md now reflects `--since` in `files` and includes the `deps` command. Affected: `CLAUDE.md`.

- **README option tables updated with --since, --no-dedup, --reverse, --top** — Both English and Japanese option tables now document all new query options. Affected: `README.md`.

- **README MCP tool tables updated with deps and batch_query** — Both English and Japanese MCP tool tables now include `deps` and `batch_query`. Affected: `README.md`.

- **Help text examples for deps, languages, --since, --reverse** — Added usage examples for `deps`, `deps --reverse`, `files --since`, and `languages` commands. Affected: `src/CodeIndex/Cli/ConsoleUi.cs`.

- **C# `unsafe` modifier in class/struct patterns** — `unsafe struct` and `unsafe class` are now extracted. Affected: `src/CodeIndex/Indexer/SymbolExtractor.cs`.

- **C# `ref struct` and `readonly ref struct` extraction** — Added `ref` to the class modifier list so `ref struct` and `readonly ref struct` types are now correctly extracted. Affected: `src/CodeIndex/Indexer/SymbolExtractor.cs`, `tests/CodeIndex.Tests/SymbolExtractorTests.cs`.

- **MCP instructions mention files `since` parameter** — The `initialize` instructions now guide AI clients to use `files` with `since` for finding recently modified files. Affected: `src/CodeIndex/Mcp/McpServer.cs`.

- **MCP `deps` tool reverse parameter** — The MCP `deps` tool now accepts a `reverse` boolean parameter for reverse dependency lookup, matching the CLI `--reverse` flag. Affected: `src/CodeIndex/Mcp/McpServer.cs`.

- **C# enum member extraction** — Enum members like `Red`, `Green = 1`, `Blue = 2` are now extracted as function symbols inside enum bodies. Enables `outline` and `symbols` to show enum values for navigation. Affected: `src/CodeIndex/Indexer/SymbolExtractor.cs`, `tests/CodeIndex.Tests/SymbolExtractorTests.cs`.

- **C# record variant test coverage** — Added tests for `record`, `sealed record class`, `readonly record struct` with parameter lists to verify signatures are correctly captured. Affected: `tests/CodeIndex.Tests/SymbolExtractorTests.cs`.

- **`--lang` validation with available languages hint** — When `files` with `--lang` returns zero results due to no files in that language, shows available languages from the index. Affected: `src/CodeIndex/Cli/QueryCommandRunner.cs`.

- **`--reverse` flag for `deps` command** — `deps --reverse --path <file>` shows which files depend on the specified file (reverse dependency lookup). Essential for refactoring impact analysis. Affected: `src/CodeIndex/Database/DbReader.cs`, `src/CodeIndex/Cli/QueryCommandRunner.cs`.

- **Help text shows --top alias** — `--help` output now shows `--top <n>` as an alias alongside `--limit <n>`. Affected: `src/CodeIndex/Cli/ConsoleUi.cs`.

- **MCP instructions updated for batch_query and deps** — The `initialize` response instructions now guide AI clients to use `batch_query` for multi-query round-trip reduction and `deps` for file-level dependency analysis. Affected: `src/CodeIndex/Mcp/McpServer.cs`.

- **Container breadcrumb in `definition` output** — Human-readable `definition` results now show the containing symbol (e.g. "in DbReader") so users can see the class/namespace context. Affected: `src/CodeIndex/Cli/QueryCommandRunner.cs`.

- **Symbol kind distribution in `status` output** — `status` now includes `symbol_kinds` with counts per kind (function, class, import, namespace) in both human-readable and JSON output. Helps AI agents understand the shape of the codebase. Affected: `src/CodeIndex/Database/DbReader.cs`, `src/CodeIndex/Cli/QueryCommandRunner.cs`.

- **`--no-dedup` flag to disable search deduplication** — Opt out of overlapping-chunk deduplication with `--no-dedup` when debugging or when raw FTS5 results are needed. Affected: `src/CodeIndex/Database/DbReader.cs`, `src/CodeIndex/Cli/QueryCommandRunner.cs`.

- **C# event subscription/unsubscription reference extraction** — `Click += Handler` and `Loaded -= Handler` patterns are now extracted as `subscribe` references. Enables `references` and `callers` to find event wiring in C# code. Affected: `src/CodeIndex/Indexer/ReferenceExtractor.cs`, `tests/CodeIndex.Tests/ReferenceExtractorTests.cs`.

- **`--top` alias for `--limit`** — More intuitive option name for limiting results. `cdidx symbols --top 5` is equivalent to `--limit 5`. Affected: `src/CodeIndex/Cli/QueryCommandRunner.cs`.

- **MCP `batch_query` tool for multi-query execution** — New MCP tool that accepts an array of `{tool, arguments}` objects and executes them all in a single call, returning an array of results. Dramatically reduces round-trips for AI agents that need multiple pieces of information (e.g. status + symbols + definition in one call). Max 10 queries per batch. Write operations (index) are blocked. Affected: `src/CodeIndex/Mcp/McpServer.cs`, `tests/CodeIndex.Tests/McpServerTests.cs`.

- **`deps` command for file-level dependency analysis** — New `cdidx deps` CLI command and MCP `deps` tool that computes file-level dependency edges from the indexed reference graph. Shows which files reference symbols defined in which other files, with reference counts and symbol lists. Helps AI agents understand project architecture in one call. Affected: `src/CodeIndex/Program.cs`, `src/CodeIndex/Database/DbReader.cs`, `src/CodeIndex/Cli/QueryCommandRunner.cs`, `src/CodeIndex/Cli/ConsoleUi.cs`, `src/CodeIndex/Mcp/McpServer.cs`, `tests/CodeIndex.Tests/McpServerTests.cs`.

- **C# `#region` extraction for outline navigation** — `#region Name` directives are now extracted as namespace symbols, appearing in `outline` and `symbols` output. Helps AI agents and developers quickly locate code sections in large C# files. Affected: `src/CodeIndex/Indexer/SymbolExtractor.cs`, `tests/CodeIndex.Tests/SymbolExtractorTests.cs`.

- **`--since` filter for time-based file queries** — New `--since <datetime>` option for the `files` CLI command and MCP `files` tool filters results to files modified after the given timestamp (ISO 8601). AI agents can ask "what changed in the last hour?" without scanning all files. Affected: `src/CodeIndex/Database/DbReader.cs`, `src/CodeIndex/Cli/QueryCommandRunner.cs`, `src/CodeIndex/Cli/ConsoleUi.cs`, `src/CodeIndex/Mcp/McpServer.cs`.

- **`--kind` validation with available kinds hint** — When `symbols` or `definition` with `--kind` returns zero results due to an invalid kind value, the CLI now shows the valid kinds from the index (e.g. "Available: class, function, import, namespace"). New `DbReader.GetDistinctKinds()` method. Affected: `src/CodeIndex/Database/DbReader.cs`, `src/CodeIndex/Cli/QueryCommandRunner.cs`.

#### Changed

- **Search result deduplication across overlapping chunks** — When adjacent chunks (which share 10 lines of overlap) both match a query, the lower-ranked duplicate is now removed. This prevents the same code region from appearing twice in search results and reduces token waste for AI agents. Affected: `src/CodeIndex/Database/DbReader.cs`, `tests/CodeIndex.Tests/DbReaderTests.cs`.

#### Fixed

- **Outline duplicates visibility when signature already contains it** — `outline` human-readable output showed `public public static class Foo` because `visibility` was prepended to `signature` even though the signature (the raw source line) already included the keyword. Now checks for duplication before prepending. Affected: `src/CodeIndex/Cli/QueryCommandRunner.cs`.

#### Added

- **C# `using` alias extraction** — `using Json = System.Text.Json;` and `global using Logging = Microsoft.Extensions.Logging;` alias declarations are now extracted as import symbols with the alias name. Previously the `=` character caused the pattern to skip these lines. Affected: `src/CodeIndex/Indexer/SymbolExtractor.cs`, `tests/CodeIndex.Tests/SymbolExtractorTests.cs`.

- **C# const and static readonly field extraction** — `const` fields and `static readonly` fields are now extracted as function symbols with their type. Regular mutable fields remain excluded. Important for navigating configuration constants like `MaxFileSize`, `SkipDirs`, etc. in the cdidx codebase itself. Affected: `src/CodeIndex/Indexer/SymbolExtractor.cs`, `tests/CodeIndex.Tests/SymbolExtractorTests.cs`.

- **C# expression-bodied property extraction** — `public int X => 42;` expression-bodied properties and read-only members are now extracted as function symbols with return type. Previously only `{ get; set; }` style properties were matched. Affected: `src/CodeIndex/Indexer/SymbolExtractor.cs`, `tests/CodeIndex.Tests/SymbolExtractorTests.cs`.

- **C# explicit interface implementation extraction** — `void IDisposable.Dispose()` and similar explicit interface implementations are now extracted as function symbols with their return type. Affected: `src/CodeIndex/Indexer/SymbolExtractor.cs`, `tests/CodeIndex.Tests/SymbolExtractorTests.cs`.

- **Expanded IgnoredCallNames for C# and other languages** — Added ~30 keywords to the reference extractor's ignore list: C# contextual keywords (`is`, `as`, `in`, `var`, `base`, `this`, `value`, `get`, `set`, `init`, `where`), LINQ keywords (`from`, `select`, `orderby`, `group`, etc.), type keywords (`struct`, `record`, `interface`, `delegate`, `event`), and utility keywords (`default`, `stackalloc`, `fixed`, `checked`). Reduces false-positive call references in C# code. Affected: `src/CodeIndex/Indexer/ReferenceExtractor.cs`, `tests/CodeIndex.Tests/ReferenceExtractorTests.cs`.

- **C# indexer extraction** — `this[int index]` indexer declarations are now extracted as function symbols with their return type. Affected: `src/CodeIndex/Indexer/SymbolExtractor.cs`, `tests/CodeIndex.Tests/SymbolExtractorTests.cs`.

- **C# operator overload and conversion operator extraction** — `operator +`, `operator ==`, `implicit operator`, `explicit operator` are now extracted as function symbols. Patterns are ordered before the general method pattern to prevent false matches on the return type. Affected: `src/CodeIndex/Indexer/SymbolExtractor.cs`, `tests/CodeIndex.Tests/SymbolExtractorTests.cs`.

- **C# static constructor and finalizer extraction** — `static ClassName()` and `~ClassName()` are now extracted as function symbols. Affected: `src/CodeIndex/Indexer/SymbolExtractor.cs`, `tests/CodeIndex.Tests/SymbolExtractorTests.cs`.

- **C# optional visibility and compound access modifiers** — Class, method, property, delegate, and event patterns no longer require an explicit visibility keyword, so `class Foo`, `static void Run()`, and other implicitly-internal members are now extracted. Compound modifiers `protected internal` and `private protected` are correctly captured as a single visibility value. Primary constructor signatures (C# 12) are verified in new tests. Affected: `src/CodeIndex/Indexer/SymbolExtractor.cs`, `tests/CodeIndex.Tests/SymbolExtractorTests.cs`.

- **`languages` CLI command and MCP tool** — New `cdidx languages [--json]` command and MCP `languages` tool that list all supported languages with their file extensions, symbol extraction support, and call-graph query support. Lets AI agents and new users discover cdidx capabilities at runtime without consulting documentation. Affected: `src/CodeIndex/Program.cs`, `src/CodeIndex/Cli/QueryCommandRunner.cs`, `src/CodeIndex/Cli/ConsoleUi.cs`, `src/CodeIndex/Mcp/McpServer.cs`, `src/CodeIndex/Indexer/FileIndexer.cs`, `src/CodeIndex/Indexer/SymbolExtractor.cs`, `tests/CodeIndex.Tests/McpServerTests.cs`.

#### Fixed

- **Docs and help text catch-up for new features** — Added `--count` to README option tables (English and Japanese), help text usage line, and `ConsoleUiTests` assertion. Updated graph-supported language lists in README (English and Japanese) to include Dart, Scala, and Elixir. Added Protobuf, GraphQL, Gradle, Makefile, Dockerfile, PowerShell, Batch, CMake to README supported-language tables. Updated DEVELOPER_GUIDE language count from 21 to 26. Affected: `README.md`, `DEVELOPER_GUIDE.md`, `src/CodeIndex/Cli/ConsoleUi.cs`, `tests/CodeIndex.Tests/ConsoleUiTests.cs`.

- **Shell completions: stale hardcoded language list and missing error exit** — `--completions` now generates the `--lang` value list dynamically from `FileIndexer.GetLanguageExtensions()` instead of a hardcoded 12-language subset. Unknown shell arguments now exit with code 1 (UsageError) instead of 0. Added test coverage for both valid and invalid shell names. Affected: `src/CodeIndex/Cli/ConsoleUi.cs`, `src/CodeIndex/Program.cs`, `tests/CodeIndex.Tests/ConsoleUiTests.cs`.

- **Gradle `plugins { id }` DSL not matched** — The Gradle import regex only handled `apply plugin: 'name'` but not the modern `plugins { id 'name' }` block form. Fixed regex to accept both whitespace and `(:` after `id`. Added test coverage for the `id` form. Affected: `src/CodeIndex/Indexer/SymbolExtractor.cs`, `tests/CodeIndex.Tests/SymbolExtractorTests.cs`.

#### Added

- **GraphQL and Gradle symbol extraction** — GraphQL schemas now get `type`, `interface`, `union`, `enum`, `scalar`, `input`, `query`, `mutation`, `subscription`, and `extend type` symbol extraction. Gradle build scripts get `task`/`def` function extraction and `apply plugin`/`id` import extraction. Enables `symbols`, `definition`, and `outline` for these ecosystems. Affected: `src/CodeIndex/Indexer/SymbolExtractor.cs`, `tests/CodeIndex.Tests/SymbolExtractorTests.cs`.

- **Makefile and Dockerfile symbol extraction** — Makefile targets (`all:`, `build:`, `test:`) are extracted as function symbols. Dockerfile `FROM` stages with `AS` aliases are extracted as function symbols, and unnamed `FROM` images as class symbols. Enables `symbols`, `definition`, and `outline` for these common project files. Affected: `src/CodeIndex/Indexer/SymbolExtractor.cs`, `tests/CodeIndex.Tests/SymbolExtractorTests.cs`.

- **Elixir call-graph support and Protobuf symbol extraction** — Elixir now supports `references`, `callers`, and `callees` queries (parenthesized calls work with the existing regex engine). Protobuf (`.proto`) files now get `message`, `enum`, `service`, `rpc`, and `import` symbol extraction, enabling `definition`, `symbols`, and `outline` queries for gRPC/protobuf projects. Affected: `src/CodeIndex/Indexer/ReferenceExtractor.cs`, `src/CodeIndex/Indexer/SymbolExtractor.cs`, `tests/CodeIndex.Tests/ReferenceExtractorTests.cs`, `tests/CodeIndex.Tests/SymbolExtractorTests.cs`.

- **Actionable hints on zero-result queries** — When `search` or `definition` return no results, the CLI now prints hints: suggests removing filters if any are active, proposes `search` as an alternative to `definition`, and warns if the index may be stale (>24h old) or empty. Reduces user confusion and helps AI agents self-correct. Affected: `src/CodeIndex/Cli/QueryCommandRunner.cs`.

- **Shell completion generation (bash/zsh/fish)** — New `cdidx --completions <shell>` generates tab-completion scripts for bash, zsh, and fish. Covers all commands, subcommand options, and value completions for `--lang` and `--kind`. Terminal-first users can add `eval "$(cdidx --completions bash)"` to their shell profile for instant command discovery. Affected: `src/CodeIndex/Program.cs`, `src/CodeIndex/Cli/ConsoleUi.cs`.

- **Graph-supported languages and version in `status` output** — `status` now includes `graph_supported_languages` (sorted list of languages with call-graph indexing) and `version` (cdidx binary version) in both human-readable and JSON output. AI agents can check upfront which languages support `callers`/`callees`/`references` without trial-and-error. Affected: `src/CodeIndex/Database/DbReader.cs`, `src/CodeIndex/Cli/QueryCommandRunner.cs`, `src/CodeIndex/Mcp/McpServer.cs`, `src/CodeIndex/Program.cs`.

- **`--count` flag for query commands** — New `--count` option for `search`, `definition`, `references`, `callers`, `callees`, `symbols`, and `files` that returns only the result count without full data. With `--json`, returns `{"count": N, "files": M}`. Lets AI agents estimate result size before fetching full data, saving tokens. Affected: `src/CodeIndex/Cli/QueryCommandRunner.cs`, `src/CodeIndex/Cli/ConsoleUi.cs`, `tests/CodeIndex.Tests/QueryCommandRunnerTests.cs`.

- **File count in CLI result summaries** — Human-readable output for `search`, `definition`, `references`, `callers`, `callees`, and `symbols` now shows "(N results in M files)" instead of just "(N results)", giving terminal users a quick sense of how spread the results are. Affected: `src/CodeIndex/Cli/QueryCommandRunner.cs`.

- **Protobuf, GraphQL, Dockerfile, Makefile, and more file types** — Added language detection for `.proto` (protobuf), `.graphql`/`.gql` (graphql), `.gradle`, `.cmake`/`CMakeLists.txt`, `.ps1` (powershell), `.bat`/`.cmd` (batch), `.bash`/`.zsh`/`.fish` (shell), and filename-based detection for `Dockerfile`, `Makefile`, `Justfile`, `Vagrantfile`, `.editorconfig`, `.gitignore`, `.dockerignore`. These common project files are now indexed for full-text search. Affected: `src/CodeIndex/Indexer/FileIndexer.cs`, `tests/CodeIndex.Tests/FileIndexerTests.cs`.

- **Cross-language feature expansion guidelines** — `SELF_IMPROVEMENT.md` now instructs AI agents to actively check whether language-specific enhancements (especially C# ↔ Java) can be ported to structurally similar languages. Also covers TypeScript/JavaScript, Kotlin/Java, and C/C++ pairs. Affected: `SELF_IMPROVEMENT.md`.

### [1.3.0] - 2026-04-11

#### Added

- **C# ecosystem enhancements** — Razor/Blazor (`.cshtml`, `.razor`) detected as csharp; VB.NET (`.vb`, `.vbs`) with Sub/Function/Class/Module symbol extraction; F# (`.fs`, `.fsx`, `.fsi`) with let/type/module/open extraction (graph queries not supported due to space-separated call syntax); XAML/MSBuild files (`.xaml`, `.axaml`, `.csproj`, `.fsproj`, `.vbproj`, `.props`, `.targets`) detected as xml. C# improvements: file-scoped namespace (C# 10+), `global using`, `using static`, `record struct`/`record class`, property extraction (get/set/init), delegate and event declarations, `file` class modifier. F#/VB.NET entrypoint hints added for `map`. Affected: `src/CodeIndex/Indexer/FileIndexer.cs`, `src/CodeIndex/Indexer/SymbolExtractor.cs`, `src/CodeIndex/Indexer/ReferenceExtractor.cs`, `src/CodeIndex/Database/RepoMapBuilder.cs`, `tests/`.

- **Dart, Scala, Elixir, Lua, and R language support** — Added language detection (`.dart`, `.scala`, `.sc`, `.r`, `.R`, `.ex`, `.exs`, `.lua`), symbol extraction for Dart (class/mixin/enum/extension/function/import), Scala (class/object/trait/case class/def/import), Elixir (defmodule/defprotocol/def/defp/import/alias/use), and Lua (function/local function/require). Dart and Scala also gain call-graph reference extraction and entrypoint hints for `map`. Affected: `src/CodeIndex/Indexer/FileIndexer.cs`, `src/CodeIndex/Indexer/SymbolExtractor.cs`, `src/CodeIndex/Indexer/ReferenceExtractor.cs`, `src/CodeIndex/Database/RepoMapBuilder.cs`, `tests/CodeIndex.Tests/FileIndexerTests.cs`, `tests/CodeIndex.Tests/SymbolExtractorTests.cs`, `tests/CodeIndex.Tests/ReferenceExtractorTests.cs`.

- **Skip additional lock files and build cache dirs** — Added `Gemfile.lock`, `Cargo.lock`, `composer.lock`, `poetry.lock`, `bun.lockb` to skip-files; `.terraform`, `.cargo`, `.pub-cache`, `_build` to skip-dirs. Affected: `src/CodeIndex/Indexer/FileIndexer.cs`, `tests/CodeIndex.Tests/FileIndexerTests.cs`.

- **`outline` command for single-file symbol structure** — New CLI command `cdidx outline <path>` and MCP tool `outline` that return all symbols in a file ordered by line, with kind, signature, visibility, container nesting, and body ranges. Lets AI agents understand file structure in one call instead of chaining `symbols` + `definition`. Affected: `src/CodeIndex/Database/DbReader.cs`, `src/CodeIndex/Cli/QueryCommandRunner.cs`, `src/CodeIndex/Cli/ConsoleUi.cs`, `src/CodeIndex/Program.cs`, `src/CodeIndex/Mcp/McpServer.cs`, `tests/CodeIndex.Tests/DbReaderTests.cs`, `tests/CodeIndex.Tests/McpServerTests.cs`.

- **R symbol extraction and Haskell/Zig language detection** — R now supports `name <- function()` and `library()`/`require()` extraction. Haskell (`.hs`, `.lhs`) gains type-signature, data/class/import extraction. Zig (`.zig`) is detected for text search. Affected: `src/CodeIndex/Indexer/FileIndexer.cs`, `src/CodeIndex/Indexer/SymbolExtractor.cs`, `tests/`.

- **MCP search summary includes file paths** — The MCP `search` tool now shows top file paths in its content summary for quick AI orientation. Affected: `src/CodeIndex/Mcp/McpServer.cs`.

#### Changed

- **README MCP diagrams render reliably in Markdown preview** — Replaced the ASCII box diagrams in the English and Japanese MCP sections with Mermaid flowcharts and removed stray code fences that could break the surrounding layout. Affected: `README.md`.

- **Entrypoint hints for 6 additional languages** — `map` entrypoint inference now covers C (`main.c`), C++ (`main.cpp`/`.cc`/`.cxx`), Haskell (`Main.hs`/`.lhs`), R (`main.R`), Lua (`main.lua`/`init.lua`), and Elixir (`application.ex`/`router.ex`). Affected: `src/CodeIndex/Database/RepoMapBuilder.cs`.

- **Code quality sweep** — Extract `IsProjectPathArg` helper in `Program.cs` for readability; replace magic numbers with named constants in `ConsoleUi` (`SpinnerFrameDelayMs`, `SpinnerStopDelayMs`, `ConsoleLineMargin`); use C# range syntax in `GitHelper`; deduplicate `WorkspaceMetadataEnricher` with a shared `Apply` helper; document FTS5 token normalization in `SearchSnippetFormatter`. Affected: `src/CodeIndex/Program.cs`, `src/CodeIndex/Cli/ConsoleUi.cs`, `src/CodeIndex/Cli/GitHelper.cs`, `src/CodeIndex/Cli/WorkspaceMetadataEnricher.cs`, `src/CodeIndex/Cli/SearchSnippetFormatter.cs`.

- **Clearer CLI error messages and help text** — `--rebuild` conflict error now explains "rebuild requires a full rescan"; database-not-found error shows the full absolute path via `Path.GetFullPath`; `--snippet-lines` help shows "1-20, default: 8" instead of "default: 8, max: 20". Affected: `src/CodeIndex/Cli/IndexCommandRunner.cs`, `src/CodeIndex/Cli/QueryCommandRunner.cs`, `src/CodeIndex/Cli/ConsoleUi.cs`, `tests/CodeIndex.Tests/QueryCommandRunnerTests.cs`, `tests/CodeIndex.Tests/ConsoleUiTests.cs`.

- **Additional test coverage** — Added truncation-marker tests for `SearchSnippetFormatter.Format` (both-sides, before-only, after-only, no-markers), and a `ConsoleUi.LoadVersion` test that verifies the real version is returned instead of the "0.0.0" fallback. Affected: `tests/CodeIndex.Tests/SearchSnippetFormatterTests.cs`, `tests/CodeIndex.Tests/ConsoleUiTests.cs`.

### [1.2.0] - 2026-04-11

#### Added

- **Freshness hints in zero-result MCP responses** — When MCP query tools (`search`, `definition`, `symbols`, `references`, `callers`, `callees`, `files`) return zero results, the response now includes `indexed_file_count` and `indexed_at` so AI clients can immediately tell whether the index is stale or empty without a separate `status` round-trip. Affected: `src/CodeIndex/Database/DbReader.cs`, `src/CodeIndex/Mcp/McpServer.cs`, `tests/CodeIndex.Tests/McpServerTests.cs`.

- **MCP server instructions and listChanged capability** — The MCP `initialize` response now includes an `instructions` string with tool-selection guidance (start with `map`, use `analyze_symbol` to bundle queries, graph tools only for supported languages, run `index` first if no DB exists, etc.) and sets `capabilities.tools.listChanged` to `false`. The supported-language list in instructions is derived from `ReferenceExtractor.GetSupportedLanguages()` to stay in sync automatically. Protocol version bumped from `2024-11-05` to `2025-03-26` to match the spec revision that introduced `instructions` and tool annotations. Affected: `src/CodeIndex/Mcp/McpServer.cs`, `tests/CodeIndex.Tests/McpServerTests.cs`.

- **Add a dedicated testing guide** — Added bilingual `TESTING_GUIDE.md` covering test suite layout, shared helpers, cross-platform rules, and test-writing conventions. Updated the maintenance checklists so test-code changes now explicitly review the testing guide in the same commit. Affected: `TESTING_GUIDE.md`, `README.md`, `DEVELOPER_GUIDE.md`, `SELF_IMPROVEMENT.md`, `CLAUDE.md`.

- **MCP tool annotations for AI client trust decisions** — All MCP tools now emit `annotations` with `readOnlyHint`, `destructiveHint`, `idempotentHint`, and `openWorldHint` per the MCP spec. Query tools are marked read-only and idempotent; the `index` tool is marked destructive and non-idempotent (it can drop the DB via `--rebuild` and replaces chunks/symbols per file). This helps AI clients decide which tools are safe to call without user confirmation. Affected: `src/CodeIndex/Mcp/McpServer.cs`, `tests/CodeIndex.Tests/McpServerTests.cs`.

#### Changed

- **Extract `RepoMapBuilder` from `DbReader`** — Moved the repo-map logic (~280 lines: `GetRepoMap`, file stats, entrypoint scoring, module grouping) into a dedicated `RepoMapBuilder` class, reducing `DbReader` from 1174 to 1073 lines. The public API (`DbReader.GetRepoMap`) is unchanged; it delegates to `RepoMapBuilder` internally. Shared query helpers (`AppendPathFilters`, `AddPathFilterParameters`, `EscapeLikeQuery`, `GetNullableDateTime`) became `internal static` for reuse. Affected: `src/CodeIndex/Database/DbReader.cs`, `src/CodeIndex/Database/RepoMapBuilder.cs`.

- **Treat the self-improvement loop as regression and monkey testing** — `SELF_IMPROVEMENT.md` now states that the loop is not only for implementing improvements but also for exercising the freshly built local binary as ongoing regression coverage and light monkey testing. Agents are instructed to actively use recent, less-common, and edge-path features instead of only safe happy-path workflows so crashes and integration defects are more likely to surface early. Affected: `SELF_IMPROVEMENT.md`.

- **Escalate local-binary failures in the self-improvement loop** — `SELF_IMPROVEMENT.md` now explicitly requires agents to report crashes, abnormal exits, or newly discovered defects in the freshly built local binary to the user instead of silently working around them or falling back to an older/global install. The loop must surface the concrete failure and propose a dedicated fix as the next task or next approved priority. Affected: `SELF_IMPROVEMENT.md`.

- **Add file-based entrypoint fallbacks to `map`** — Repo-map entrypoints now fall back to known top-level entry files such as `Program.cs` and `main.py` when symbol extraction does not emit an explicit `Main`-style symbol. This improves first-pass orientation for top-level script or top-level-statement projects without changing the `entrypoints` shape. Affected: `src/CodeIndex/Database/DbReader.cs`, `tests/CodeIndex.Tests/DbReaderTests.cs`, `README.md`, `DEVELOPER_GUIDE.md`, `CLAUDE.md`.

- **Expose unsupported-language hints in direct graph queries** — Human-readable `references`, `callers`, and `callees` now print an explicit note when `--lang` targets a language without indexed call-graph extraction. MCP graph tools also return `graph_language`, `graph_supported`, and `graph_support_reason` so zero-hit unsupported-language queries are distinguishable from real zero-hit supported-language searches. Affected: `src/CodeIndex/Cli/QueryCommandRunner.cs`, `src/CodeIndex/Mcp/McpServer.cs`, `tests/CodeIndex.Tests/McpServerTests.cs`, `README.md`, `DEVELOPER_GUIDE.md`, `CLAUDE.md`.

- **Make unsupported call-graph languages explicit in `inspect` / `analyze_symbol`** — Symbol analysis now returns `graph_language`, `graph_supported`, and `graph_support_reason`, so AI clients can distinguish "this language is not indexed for callers/callees/references" from "there were simply no graph hits." Human-readable `inspect` output also prints the same graph-support note. Affected: `src/CodeIndex/Indexer/ReferenceExtractor.cs`, `src/CodeIndex/Database/DbReader.cs`, `src/CodeIndex/Cli/QueryCommandRunner.cs`, `src/CodeIndex/Mcp/McpServer.cs`, `tests/CodeIndex.Tests/DbReaderTests.cs`, `tests/CodeIndex.Tests/McpServerTests.cs`, `README.md`, `DEVELOPER_GUIDE.md`, `CLAUDE.md`.

- **Use a proper rotating braille spinner sequence** — The default spinner and progress bar now share the 10-frame braille sequence `⠋ ⠙ ⠹ ⠸ ⠼ ⠴ ⠦ ⠧ ⠇ ⠏`, which reads as rotation instead of jitter. Added a regression test for the default frame list and removed the duplicated default frame definition so spinner and progress bar stay aligned. Affected: `src/CodeIndex/Cli/ConsoleUi.cs`, `tests/CodeIndex.Tests/ConsoleUiTests.cs`.

- **Expose workspace trust metadata in `inspect` / `analyze_symbol`** — `inspect --json` and MCP `analyze_symbol` now include `workspace_indexed_at`, `workspace_latest_modified`, `project_root`, `git_head`, and `git_is_dirty`, so AI clients can judge freshness and repository state during symbol analysis without a separate `status` call. Human-readable `inspect` output also prints the same trust signals before the bundled sections. Affected: `src/CodeIndex/Database/DbReader.cs`, `src/CodeIndex/Cli/WorkspaceMetadataEnricher.cs`, `src/CodeIndex/Cli/QueryCommandRunner.cs`, `src/CodeIndex/Mcp/McpServer.cs`, `tests/CodeIndex.Tests/DbReaderTests.cs`, `tests/CodeIndex.Tests/McpServerTests.cs`, `README.md`, `DEVELOPER_GUIDE.md`, `CLAUDE.md`.

- **Split scoped and workspace freshness in `map` output** — `map` keeps `indexed_at` and `latest_modified` scoped to the filtered result set for backward compatibility, and now also exposes `workspace_indexed_at` and `workspace_latest_modified` so AI clients can compare slice-level freshness with whole-workspace freshness without falling back to a separate `status` call. Human-readable `map` output now labels the scoped/workspace timestamps explicitly. Affected: `src/CodeIndex/Database/DbReader.cs`, `src/CodeIndex/Cli/QueryCommandRunner.cs`, `src/CodeIndex/Mcp/McpServer.cs`, `tests/CodeIndex.Tests/DbReaderTests.cs`, `tests/CodeIndex.Tests/McpServerTests.cs`, `README.md`, `DEVELOPER_GUIDE.md`, `CLAUDE.md`.

- **Consolidate `BuildGraphSupportReason` into `ReferenceExtractor`** — Moved the shared graph-support-reason message logic from duplicated private methods in `DbReader` and `McpServer` into a single `ReferenceExtractor.BuildGraphSupportReason()` static helper. `DbReader` adds a fallback message for the null-language case; `McpServer` passes through the null. Affected: `src/CodeIndex/Indexer/ReferenceExtractor.cs`, `src/CodeIndex/Database/DbReader.cs`, `src/CodeIndex/Mcp/McpServer.cs`.

#### Fixed

- **Tolerate read-only databases in query paths** — Query commands (`search`, `definition`, `inspect`, etc.) and MCP read tools now call `TryMigrateForRead()` instead of `InitializeSchema()`. `TryMigrateForRead()` creates the `symbol_references` table and indexes if missing, runs column migrations, and catches only `SQLITE_READONLY` errors so read-only filesystems silently degrade while other failures propagate. Affected: `src/CodeIndex/Database/DbContext.cs`, `src/CodeIndex/Cli/QueryCommandRunner.cs`, `src/CodeIndex/Mcp/McpServer.cs`.

- **Use exact-match query in `GetFileByPath`** — Replaced the substring `LIKE '%path%'` approach (via `ListFiles` + in-memory filter) with a direct `WHERE path = @path` query, eliminating false positives and unnecessary work. Affected: `src/CodeIndex/Database/DbReader.cs`.

- **Guard `GetRepoMap` against empty filter results** — `fileStats.Max()` now checks `fileStats.Count > 0` before aggregating, preventing `InvalidOperationException` when no files match the filter criteria. Affected: `src/CodeIndex/Database/DbReader.cs`.

- **Guard `WriteGraphSupportHint` against null language** — The CLI graph-support hint now skips printing when `--lang` is not specified, avoiding a confusing `"not indexed for ''"` message. Affected: `src/CodeIndex/Cli/QueryCommandRunner.cs`.

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

#### 修正
- **`install.sh` が同梱の SQLite ネイティブライブラリと `version.json` もコピーするように** — これまでワンライナーインストーラーはリリース tarball から `cdidx` バイナリのみをコピーし、`libe_sqlite3.so`（macOS では `libe_sqlite3.dylib`）と `version.json` を残していた。その結果、インストール直後の cdidx は DB 操作のたびに `libe_sqlite3: cannot open shared object file` でクラッシュし、`cdidx --version` は静かに `v0.0.0` へフォールバックしていた。インストーラーは tarball に同梱されたランタイムファイルをバイナリと同じディレクトリへ配置するようになった。対象: `install.sh`。

### [1.8.0] - 2026-04-12

#### 追加
- **不明コマンドに対する「もしかして」推薦** — 認識できないコマンド（例: `cdidx serach`）が入力されたとき、Levenshtein距離（閾値: 3）で最も近い有効コマンドを提案する。対象: `Program.cs`、`ConsoleUi.cs`。
- **`status` 出力に1行サマリー** — `status` にファイル・シンボル・参照数、主要言語、鮮度、dirty状態を含む `summary` フィールドを追加。人間向けモードでは先頭行に表示し、JSON モードでは AI エージェント向けフィールドとして出力。対象: `QueryCommandRunner.cs`、`QueryResults.cs`。
- **`files` 出力に `reference_count`** — `files --json` と MCP の `files` が、既存の `symbol_count` に加えて `reference_count` を含むようになった。参照が多いホットファイルを AI エージェントが特定しやすくなる。対象: `DbReader.cs`、`QueryResults.cs`。
- **`impact` CLI コマンドと `impact_analysis` MCP ツール** — BFS でシンボルの推移的呼び出し元（変更の波及効果）を算出。各深さレベルの呼び出し元をファイルパスと参照カウント付きで表示。`--depth` で最大 BFS 深さを制御（デフォルト: 5）。CLI の `cdidx impact <symbol>` と MCP の `impact_analysis` の両方で利用可能。対象: `DbReader.cs`、`QueryCommandRunner.cs`、`Program.cs`、`ConsoleUi.cs`、`McpToolDefinitions.cs`、`McpToolHandlers.cs`、`McpServer.cs`、`QueryResults.cs`。
- **Zig、CSS/SCSS 参照抽出** — 2言語追加でコールグラフクエリが利用可能に。Zig は `//` コメント。CSS には行コメントなし（ブロックコメント `/* */` は対応済み）。対象: `ReferenceExtractor.cs`。
- **Gradle、Terraform、Protobuf、Dockerfile、Makefile 参照抽出** — 5言語追加でコールグラフクエリが利用可能に。Gradle/Groovy は `//` コメント。Terraform、Dockerfile、Makefile、Protobuf は `#` コメント。各言語のキーワード（ビルドターゲット、Terraform ブロック等）を除外リストに追加。対象: `ReferenceExtractor.cs`。
- **R、PowerShell、Haskell 参照抽出** — R、PowerShell、Haskell でコールグラフクエリ（`references`、`callers`、`callees`）が利用可能に。R は `#` コメントと標準的な括弧付き呼び出し検出。PowerShell は `#` コメント; コマンドレットのハイフン名はハイフンで分割。Haskell は `--` コメント; 括弧付き呼び出しのみ（スペース区切り呼び出しは既知の制限）。各言語のキーワードを除外リストに追加。対象: `ReferenceExtractor.cs`。

#### 変更
- **impact 分析が完全一致と切り詰め通知を使用** — `GetTransitiveCallers` が `LIKE %query%` ではなく完全一致（`=`）の専用 caller 検索を使うようになり、`Run` を検索して `RunAsync` や `ShouldRun` の caller まで巻き込む曖昧展開を防止。ホップごとの上限はハードコード 100 ではなく呼び出し元の limit に連動。CLI と MCP の両出力に `truncated` フラグを追加し、AI エージェントがグラフの不完全性を判断できるようになった。対象: `DbReader.cs`、`QueryCommandRunner.cs`、`McpToolHandlers.cs`。
- **Shell をグラフ対応言語から除外** — Shell スクリプトはコマンド形式（`foo arg1 arg2`）で関数を呼び出すため、括弧付き呼び出しを前提とする正規表現ベースの抽出器ではコールエッジを意味のある精度で検出できない。graph-supported として広告すると、空結果を AI クライアントが信頼してしまうため除外。対象: `ReferenceExtractor.cs`。
- **`unused` と `hotspots` に `--count` サポート** — 両コマンドが AI プリフライト用の `--count` フラグに対応。search/definition/symbols/references/callers/callees と同じパターン。対象: `QueryCommandRunner.cs`。
- **status 出力に言語サポートサマリー** — `status` が "Support" 行で検出言語総数、シンボル抽出対応数、グラフクエリ対応数を表示するようになった（例: "46 detected, 32 with symbols, 31 with graph"）。Graph 行にもカウントプレフィックスを追加。対象: `QueryCommandRunner.cs`。
- **`symbols` と `definition` コマンドに `--since` フィルタ** — 両コマンドが `--since <datetime>` を受け付け、最近変更されたファイルのシンボル/定義だけに絞り込めるようになった。MCP `symbols` ツールも `since` パラメータを公開。対象: `DbSymbolReader.cs`、`QueryCommandRunner.cs`、`McpToolHandlers.cs`、`McpToolDefinitions.cs`。
- **ゼロ結果コマンドでの一貫した `--lang` 検証ヒント** — `references`、`callers`、`callees` が `--lang` でゼロ結果のとき `WriteLangHint` を表示するようになり、`symbols`、`unused`、`hotspots` と同じパターンに統一。対象: `QueryCommandRunner.cs`。

#### 修正
- **シェル補完とヘルプテキストに `validate`、`deps`、`unused`、`hotspots` が欠落** — シェル補完のコマンドリストに `validate` と `deps` を追加し、ヘルプ出力に `validate`、`deps`、`unused`、`hotspots` の usage 行とコマンド説明を追加。対象: `ConsoleUi.cs`。
- **kind ヒントがインデックス内のみでなく全有効種別を表示** — `--kind` 検証が、現インデックス内の種別のみではなく全10種の有効シンボル種別の静的リストを使うようになった。無効な kind には "Available:" 一覧を、有効だがインデックスに存在しない kind には "no X symbols in the index" とインデックス内の種別を表示。対象: `QueryCommandRunner.cs`。
- **削除済み言語の古いエッジがグラフ読取へ漏れず、impact のページングも安定化** — `references`、`callers`、`callees`、`impact`、ファイル依存の読取が共通の graph 対応言語フィルタを共有し、再インデックス前の古い shell 行が表に出ないようになった。`impact` のページングは大きい `LIMIT` の再実行ではなく、決定的な順序と SQL `OFFSET` を使うように変更し、BFS ページ境界で caller を取りこぼさないようにした。対象: `DbReader.cs`、`DbReaderTests.cs`。

### [1.7.0] - 2026-04-12

#### 追加
- **C#シンボル種別の細分化** — C#シンボルに意味的に正確な種別を導入: `property`（get/set・式本体）、`interface`、`enum`、`struct`（`record struct`・`ref struct`・`readonly struct`含む）、`event`、`delegate`。従来は汎用的な `class` や `function` に分類されていた。AIエージェントが `--kind property`、`--kind interface` 等でフィルタ可能に。bash/zsh/fishのシェル補完も更新。対象: `SymbolExtractor.cs`、`ConsoleUi.cs`。
- **Java/Kotlinシンボル種別の細分化** — Javaのインターフェースとenumが `kind="interface"` / `kind="enum"` に（従来は `kind="class"`）。Kotlinの sealed interface が `kind="interface"`、enum class が `kind="enum"`、`val`/`var` プロパティが `kind="property"` に。対象: `SymbolExtractor.cs`。
- **全型付き言語のシンボル種別細分化** — TypeScript、Go、Rust、Swift、C、C++、PHP、Scala、Dart、GraphQL、VB.NETで意味的に正確な種別を導入: `struct`、`interface`（trait/protocol含む）、`enum`。対象: `SymbolExtractor.cs`。
- **Zigシンボル抽出** — Zig向けシンボル抽出パターンを追加: `pub fn`/`fn`（関数）、`const struct`（構造体）、`const enum`（列挙型）、`const union`/`error`（クラス）、`test`ブロック、`@import`。従来はZigは言語として登録されていたが抽出パターンがゼロだった。対象: `SymbolExtractor.cs`。
- **PowerShellシンボル抽出** — PowerShell (.ps1) 向けシンボル抽出パターンを追加: `function`/`filter`（関数）、`class`（クラス）、`enum`（列挙型）、`Import-Module`/`using module`（インポート）。対象: `SymbolExtractor.cs`。
- **`unused` CLIコマンドと `unused_symbols` MCPツール** — インデックス済みコードベースで定義されているが一度も参照されていないシンボルを検索する（潜在的なデッドコード）。参照抽出対応言語でのみ意味がある。`cdidx unused` CLIコマンドと `unused_symbols` MCPツールとして利用可能。対象: `DbSymbolReader.cs`、`QueryCommandRunner.cs`、`Program.cs`、`ConsoleUi.cs`、`McpToolDefinitions.cs`、`McpToolHandlers.cs`、`McpServer.cs`。
- **CSS/SCSSシンボル抽出** — CSS/SCSS向けシンボル抽出を追加: `.class`セレクタ（class）、`#id`セレクタ（function）、`@mixin`（function）、`@keyframes`（function）、`@import`/`@use`（import）、`$variable`（property）。対象: `SymbolExtractor.cs`。
- **`hotspots` CLIコマンドと `symbol_hotspots` MCPツール** — コードベースで最も参照されるシンボルを参照回数順に検索する。変更が広範囲に影響する中心的なコードの特定に有用。対象: `DbSymbolReader.cs`、`QueryCommandRunner.cs`、`Program.cs`、`ConsoleUi.cs`、`McpToolDefinitions.cs`、`McpToolHandlers.cs`、`McpServer.cs`。

#### 変更
- **可視性に基づくシンボルランキング** — `symbols` と `definition` の結果が public シンボルを private/internal より上位に表示するようになった。ランキング階層: public/open/pub/export (0) > protected (1) > internal (2) > private (3) > 不明 (4)。AIエージェントが API サーフェスを探す際に最も関連性の高い結果が先に得られる。対象: `DbReader.cs`、`DbSymbolReader.cs`。
- **検索ランキングの最終更新日タイブレーカー** — 同ランクの検索結果の中で、最近変更されたファイルが先に表示されるようになった。最終的なアルファベット順タイブレーカーの前に `f.modified DESC` を追加。対象: `DbSearchReader.cs`。
- **新しいクエリパターン用の追加DBインデックス** — スタンドアロン `--kind` フィルタ用の `symbols(kind)`、可視性ランキング用の `symbols(visibility)`、ホットスポット/未使用分析用の `symbol_references(symbol_name, reference_kind)` を追加。対象: `DbContext.cs`。
- **unused/hotspots の kind/lang 検証ヒント** — `unused` と `hotspots` で0件時に有用なヒント（不正な kind、不明な言語、古いインデックス、フィルタ提案）を表示。対象: `QueryCommandRunner.cs`。
- **F# 参照抽出** — F# でコールグラフクエリ（`references`、`callers`、`callees`）が利用可能に。`someFunc(x)` のような括弧付き呼び出しと `new ClassName()` のコンストラクタ呼び出しを検出。F# 固有キーワードを除外リストに追加。注意: スペース区切りの F# 呼び出し（`List.map f xs`）は検出できない — 正規表現ベース抽出の既知の制限。対象: `ReferenceExtractor.cs`。
- **MCP instructions: シンボル種別フィルタのガイダンス** — AIクライアントが MCP initialize 応答で、シンボルの種別フィルタ（function, class, struct, interface, enum, property, event, delegate）の使い方を案内されるようになった。対象: `McpToolHandlers.cs`。
- **SQL 参照抽出** — SQL でコールグラフクエリが利用可能に。`COALESCE()`、`LOWER()`、`LENGTH()`、`NOW()` 等の SQL 関数呼び出しを検出。SQL キーワード（大文字）を除外リストに追加。対象: `ReferenceExtractor.cs`。
- **README MCPツール表の同期** — MCPツール表（英語・日本語）を全21ツール（`unused_symbols`、`symbol_hotspots`、`validate`、`ping`、`suggest_improvement` を含む）にリストアップ。ツール数を16から21に更新。対象: `README.md`。
- **ANSIカラーコード付きシンボル種別** — `symbols`、`unused`、`hotspots` のCLI出力でシンボル種別を色分け表示: シアン（class/struct）、青（interface）、マゼンタ（enum/delegate）、黄（function）、緑（property）、赤（event）、暗灰（namespace/import）。パイプ時は無色テキストに自動退化。対象: `ConsoleUi.cs`、`QueryCommandRunner.cs`。
- **クロス言語シンボル種別一貫性テスト** — 全型付き言語（C#、Java、Kotlin、TypeScript、Go、Rust、Swift、C、C++、PHP、Scala、Dart、GraphQL）が期待通りのシンボル種別（struct、interface、enum、property、delegate、event）を出力することを検証する32件のパラメータ化テストを追加。抽出パターン変更時のリグレッションを防止。対象: `SymbolExtractorTests.cs`。
- **サイクロマティック複雑度推定** — `definition --body --json` および MCP の `definition`（`includeBody` 指定時）が `complexity` フィールドを含むようになった。if/else/for/while/case/catch/??/&&/|| 等をカウントする正規表現ベースのヒューリスティック推定（基準値1）。AIエージェントがリファクタリング対象を特定するのに有用。対象: `SymbolExtractor.cs`、`DbSymbolReader.cs`、`QueryResults.cs`。
- **PowerShell .psm1/.psd1 ファイル対応** — PowerShell 言語検出に `.psm1`（モジュール）と `.psd1`（データ）拡張子を追加。対象: `FileIndexer.cs`。

#### 修正
- **NuGetでのREADME HTMLタグ表示問題** — NuGetのMarkdownレンダラが生テキストとして表示してしまう `<details>` / `<summary>` HTMLタグを全て除去。折りたたみセクションを太字ラベルに置換。日本語の比較見出しを `cdidx と rg の違い` から `rg との違い` に簡潔化。対象: `README.md`。
- **unused/hotspots の名前衝突と未対応言語の偽陽性を修正** — `unused` がデフォルトでグラフ対応言語のみに制限されるようになり、未対応言語（CSS、Zig、PowerShell等）の「全シンボル未使用」偽陽性を防止。`hotspots` の GROUP BY に container_name を追加し、異なるクラスの同名シンボルを区別。未対応言語クエリ時に警告を表示。MCP `unused_symbols` に `graph_supported` メタデータを追加。対象: `DbSymbolReader.cs`、`QueryCommandRunner.cs`、`McpToolHandlers.cs`。
- **C# 呼び出し箇所が定義として誤検出される問題を修正 (#40)** — `await FuncName(...)`、`return service.GetResult()`、`throw factory.Create(...)` のような呼び出し行がメソッド定義や明示的インターフェース実装としてマッチしていた問題を修正。メソッドパターンと明示的インターフェース実装パターンの両方にステートメントキーワード（await, return, throw, yield, var 等）の negative lookahead を追加。対象: `SymbolExtractor.cs`。
- **C# ジェネリックメソッドオーバーロードが抽出されない問題を修正 (#41)** — `TryRaise<T>(...)` や `GetItems<TKey, TValue>(...)` のようなジェネリックメソッドがメソッドパターンにマッチしなかった問題を修正。メソッド名と開き括弧の間にオプションの型パラメータ `<...>` を許容するようパターンを更新。明示的インターフェース実装パターンにも適用。対象: `SymbolExtractor.cs`。
- **definition/references/callers/callees が空クエリを受け入れる問題を修正 (#43)** — これらのコマンドが空文字列・空白のみのクエリを明確なエラーメッセージ（終了コード1）で拒否するようになった。`inspect` の既存動作と一致。対象: `QueryCommandRunner.cs`。
- **`--since` に無効な日付を指定すると全ファイルが返される問題を修正 (#44)** — `--since` が厳密なインバリアントISO 8601パース（`DateTimeOffset.TryParseExact` + `CultureInfo.InvariantCulture`）を使うようになり、`01/02/2024` のようなロケール依存の曖昧な形式を拒否する。値なしの `--since` もフィルタを無視せずエラーを返す。パーサーはCLIとMCPで共有。対象: `QueryCommandRunner.cs`、`McpToolHandlers.cs`。
- **`map --path` が存在しないパスで終了コード0を返す問題を修正 (#45)** — `map` がフィルタ結果0件のとき終了コード2とエラーメッセージを返すようになった。`outline` や `excerpt` のパターンと一致。MCP の `repo_map` も0件時に鮮度ヒントを付加。対象: `QueryCommandRunner.cs`、`McpToolHandlers.cs`。
- **`outline` が特定ファイルでデータベースNULLエラーを投げる問題を修正 (#46)** — `GetOutline` がシンボルの `start_line`/`end_line` がNULLのとき "The data is NULL at ordinal 3" でクラッシュしていた問題を修正。`SearchSymbols` や `GetNearbySymbols` と同じく `line` 値にフォールバックする。対象: `DbSymbolReader.cs`。
- **`excerpt --start N --end M` で start > end のとき1行だけ返す問題を修正 (#47)** — CLI の `excerpt` が `--start > --end` を終了コード1で拒否するようになった。MCP の `excerpt` は既に検証済み。対象: `QueryCommandRunner.cs`。

### [1.6.0] - 2026-04-12

#### 追加
- **ワンライナーインストールスクリプト** — `install.sh` により、.NET SDK なしでコンテナや CI 環境に cdidx をインストール可能。GitHub Releases から self-contained バイナリをダウンロードし、SHA256 チェックサムを検証する。`linux-x64`, `linux-arm64`, `osx-arm64`（glibc のみ）に対応。musl ベースの Linux（Alpine 等）は検出して明確なエラーで拒否する。対象: `install.sh`。
- **linux-arm64 リリースビルド** — ARM コンテナサポート（Apple Silicon Docker, ARM CI）のため、リリースマトリクスに `linux-arm64` を追加。x64 ランナー上でクロスコンパイルし、クロスコンパイル対象のテスト実行はスキップ。対象: `.github/workflows/release.yml`。
- **README インストールセクション刷新** — Installation セクション（英語・日本語）を `curl | bash` インストーラーを先頭に再構成。独立した前提条件セクションを削除し、.NET SDK 要件を NuGet/ソースビルド方法に移動。Code Search Rules テンプレートと30秒クイックスタートも更新。Dockerfile 例は `CDIDX_INSTALL_DIR=/usr/local/bin` を使いコンテナビルドで PATH を正しく設定。対象: `README.md`。

### [1.5.0] - 2026-04-12

#### 追加
- **`suggest_improvement` MCPツール** — AIエージェントが構造化された改善提案やエラー報告（クラッシュ報告、予期せぬエラー、機能ギャップ）を送信できる。提案は `.cdidx/suggestions.json` にSHA256重複排除とファイルレベルロック付きでローカル保存される。`CDIDX_GITHUB_TOKEN` が明示的に設定されている場合、widthdom/CodeIndex に GitHub Issue としても報告される。説明は `SourceCodeDetector`（ヒューリスティック、セキュリティ境界ではない）により一般的なコードコピペパターンを拒否するよう検証される。このツールはAIエージェントが明示的に呼んだときのみ動作する。対象: `SuggestionRecord.cs`, `SuggestionStore.cs`, `SourceCodeDetector.cs`, `GitHubIssueReporter.cs`, `McpToolDefinitions.cs`, `McpToolHandlers.cs`, `McpServer.cs`。

#### 修正
- **GitHubトークン安全性** — 提案送信には `CDIDX_GITHUB_TOKEN` のみを受け付ける。汎用の `GITHUB_TOKEN` は使用しなくなり、CIの環境トークンが意図せず外部リポジトリに公開されることを防ぐ。対象: `GitHubIssueReporter.cs`。
- **SourceCodeDetector フェンスドブロック回避対策** — マークダウンフェンスドコードブロック（`` ``` ``）の検出を追加。フェンス内の短い非インデントコードが全ヒューリスティックを通過する問題を修正。対象: `SourceCodeDetector.cs`。
- **アトミック＆ロック付き提案蓄積** — `SuggestionStore` が一時ファイル→リネームによるクラッシュセーフな書き込み、ファイルレベルロックによる並行アクセス安全性、一時的I/Oエラー時のfail-closed動作を使用するようになった。破損ファイルはサイレントに破棄されず `.bak` として保存される。対象: `SuggestionStore.cs`。

#### 変更
- **プライバシー記述を実態に合わせて修正** — README と DEVELOPER_GUIDE が `SourceCodeDetector` をヒューリスティックなガード（セキュリティ境界ではない）として記述し、短いインラインコード例が設計上許容されることを正直に記載するようになった。対象: `README.md`, `DEVELOPER_GUIDE.md`。

### [1.4.1] - 2026-04-12

### 修正
- `README.md` のコードブロック崩れを修正。

### [1.4.0] - 2026-04-12

#### 追加

- **最終ドッグフーディング検証** — 全再構築と自己分析: 61ファイル、1254シンボル、4798参照、46言語検出、29シンボル抽出対応、18グラフクエリ対応。322テスト（320パス+2スキップ）。全ドキュメント同期完了。対象: `CHANGELOG.md`.

- **CLAUDE.md アーキテクチャ: SymbolExtractor を29言語に更新** — 対象: `CLAUDE.md`.

- **README graph 言語リストを18言語に更新** — Lua と VB.NET を graph 対応言語リストに追加（英語・日本語両セクション）。対象: `README.md`.

- **DEVELOPER_GUIDE: VB.NET graph=yes** — 言語表でVB.NETを graph=yes に更新（graph 対応は18言語に）。対象: `DEVELOPER_GUIDE.md`.

- **VB.NET コールグラフ参照抽出** — VB.NET が `references`、`callers`、`callees` クエリに対応。`'` コメント除去も追加。対象: `src/CodeIndex/Indexer/ReferenceExtractor.cs`.

- **batch_query で ping ツールに対応** — `batch_query` 内で `ping` ツールを呼べるようになった。テスト追加。対象: `src/CodeIndex/Mcp/McpToolHandlers.cs`, `tests/CodeIndex.Tests/McpServerTests.cs`.

- **DEVELOPER_GUIDE 言語表: Lua graph, Swift actor, PHP namespace** — Lua を graph=yes に更新、Swift に actor/typealias を追加、PHP に readonly class/namespace/use/const を追加。英語・日本語両セクション。対象: `DEVELOPER_GUIDE.md`.

- **MCP instructions に search exact モードの案内追加** — AI クライアントに `search` の `exact: true` で大文字小文字区別一致を案内。対象: `src/CodeIndex/Mcp/McpToolHandlers.cs`.

- **PHP: readonly class, namespace, use, const, 拡張修飾子** — PHP パターンに `readonly class`（PHP 8.2+）、`namespace`、`use` インポート、`const` 宣言、enum 型を追加。対象: `src/CodeIndex/Indexer/SymbolExtractor.cs`, `tests/CodeIndex.Tests/SymbolExtractorTests.cs`.

- **Swift: actor, distributed actor, typealias, 拡張メソッド修飾子** — Swift パターンに `actor`（Swift 5.5+）、`distributed actor`、`typealias`、追加メソッド修飾子（`nonisolated`、`mutating`、`nonmutating`）を追加。対象: `src/CodeIndex/Indexer/SymbolExtractor.cs`, `tests/CodeIndex.Tests/SymbolExtractorTests.cs`.

- **Lua コールグラフ参照抽出** — Lua が `references`、`callers`、`callees` クエリに対応。`--` コメント除去も追加。対象: `src/CodeIndex/Indexer/ReferenceExtractor.cs`, `tests/CodeIndex.Tests/ReferenceExtractorTests.cs`.

- **MCP `search` ツールに `exact` パラメータ** — MCP の `search` ツールに `exact` ブーリアンを追加し、CLI の `--exact` フラグと同等の大文字小文字区別一致に対応。対象: `src/CodeIndex/Mcp/McpToolHandlers.cs`, `src/CodeIndex/Mcp/McpToolDefinitions.cs`.

- **`search --exact` フラグで大文字小文字区別の部分一致検索** — FTS5 トークナイズをバイパスし、`instr()` による大文字小文字区別の完全部分一致検索を行う `--exact` オプション。FTS5 の大文字小文字無視マッチが多すぎる場合に有用。対象: `src/CodeIndex/Database/DbSearchReader.cs`, `src/CodeIndex/Cli/QueryCommandRunner.cs`, `src/CodeIndex/Cli/ConsoleUi.cs`.

- **README Code Search Rules を deps, --since, --dry-run, --count で更新** — クエリ戦略セクション（英語・日本語）に `deps`、`--reverse`、`--since`、`--dry-run`、`--count` を追加。シンボル対応言語リストを29に更新。Shell/SQL/Terraform を「シンボル抽出なし」リストから削除。対象: `README.md`.

- **DEVELOPER_GUIDE 言語パターン参照表** — 旧21言語表を Graph 対応列を含む29言語の包括的な表に差し替え。CLAUDE.md のコミットごとチェックリストに言語パターン変更時の表更新ルールを追加。対象: `DEVELOPER_GUIDE.md`, `CLAUDE.md`.

- **TESTING_GUIDE に新テストファイルを追記** — ConcurrencyTests、PerformanceTests、DbRecoveryTests を英語・日本語のテストレイアウトセクションに追加。対象: `TESTING_GUIDE.md`.

- **クロスプラットフォームパスセパレータテスト** — Windows 形式のバックスラッシュパスがファイルレコードでフォワードスラッシュに正規化されることを検証。対象: `tests/CodeIndex.Tests/FileIndexerTests.cs`.

- **C# 属性付きメンバーのテストカバレッジ** — `[Serializable]`、`[Obsolete]`、`[HttpGet]` がクラス/メソッド前行にあっても抽出を妨げないことを検証。対象: `tests/CodeIndex.Tests/SymbolExtractorTests.cs`.

- **MCP `ping` ツール** — サーバーバージョン、タイムスタンプ、DBパス、DB存在有無を返す軽量接続チェック。DB不要。AI エージェントがクエリ発行前に MCP 接続を確認できる。対象: `src/CodeIndex/Mcp/McpServer.cs`, `src/CodeIndex/Mcp/McpToolDefinitions.cs`, `src/CodeIndex/Mcp/McpToolHandlers.cs`, `tests/CodeIndex.Tests/McpServerTests.cs`.

- **MCP `search` ツールに `noDedup` パラメータ** — MCP の `search` ツールに `noDedup` ブーリアンを追加し、CLI の `--no-dedup` フラグと同等のオーバーラップチャンク重複排除無効化に対応。対象: `src/CodeIndex/Mcp/McpToolHandlers.cs`, `src/CodeIndex/Mcp/McpToolDefinitions.cs`.

- **`search --since` フィルタ** — `--since` オプションが `files` だけでなく `search`（CLI・MCP 両方）でも使えるようになった。AI エージェントが最近変更されたファイル内のみを検索でき、大規模リポジトリでのノイズ削減に有効。対象: `src/CodeIndex/Database/DbSearchReader.cs`, `src/CodeIndex/Cli/QueryCommandRunner.cs`, `src/CodeIndex/Mcp/McpToolHandlers.cs`, `src/CodeIndex/Mcp/McpToolDefinitions.cs`.

- **index コマンドに `--dry-run` フラグ** — DBに書き込まずにファイルスキャンのみ行う `--dry-run` オプション。ファイル数と言語内訳を表示。対象: `src/CodeIndex/Cli/IndexCommandRunner.cs`, `src/CodeIndex/Cli/ConsoleUi.cs`.

- **DB 複合インデックスでクエリ高速化** — `symbols(file_id, kind)`、`files(lang, modified)`、`symbol_references(container_name, reference_kind)` の複合インデックスを追加。対象: `src/CodeIndex/Database/DbContext.cs`.

- **Shell、SQL、Terraform シンボル抽出** — Shell: bash/zsh 関数宣言。SQL: CREATE TABLE/VIEW/FUNCTION/PROCEDURE/TRIGGER/INDEX、ALTER TABLE。Terraform: resource、data、module、variable、output、locals。3言語すべてで `symbols`、`definition`、`outline` が使えるようになった。対象: `src/CodeIndex/Indexer/SymbolExtractor.cs`, `tests/CodeIndex.Tests/SymbolExtractorTests.cs`.

- **Ruby: attr_accessor/reader/writer と Rails DSL 抽出** — Ruby パターンに `attr_accessor :name`、`attr_reader :email`、Rails DSL（`has_many`、`has_one`、`belongs_to`、`scope`）を function シンボルとして追加。対象: `src/CodeIndex/Indexer/SymbolExtractor.cs`, `tests/CodeIndex.Tests/SymbolExtractorTests.cs`.

- **Rust: macro_rules!, mod, const/static, const fn, unsafe fn, union, type alias** — Rust パターンに `macro_rules!`、`mod` モジュール、`const`/`static` アイテム、`const fn`、`unsafe fn`、`extern "C" fn`、`union`、`type` エイリアスを追加。対象: `src/CodeIndex/Indexer/SymbolExtractor.cs`, `tests/CodeIndex.Tests/SymbolExtractorTests.cs`.

- **TypeScript: abstract class, declare, namespace/module, readonly/override** — TypeScript パターンに `abstract class`、`declare class/module/interface`、`namespace/module`、`readonly`/`abstract`/`override` メソッド修飾子を追加。対象: `src/CodeIndex/Indexer/SymbolExtractor.cs`, `tests/CodeIndex.Tests/SymbolExtractorTests.cs`.

- **Python: デコレータ付き・dunder メソッドのテストカバレッジ** — `@dataclass`、`@property`、`@staticmethod` 付きクラス/メソッド、dunder メソッド（`__init__`、`__str__`）、型ヒント付きシグネチャが既存パターンで正しく抽出されることのテスト追加。対象: `tests/CodeIndex.Tests/SymbolExtractorTests.cs`.

- **Go: 型エイリアス、const ブロックメンバー、パッケージレベル var** — Go パターンに `type Name = OtherType` エイリアス、`const ( Name = value )` ブロックメンバー、`var Name Type` パッケージレベル変数を追加。対象: `src/CodeIndex/Indexer/SymbolExtractor.cs`, `tests/CodeIndex.Tests/SymbolExtractorTests.cs`.

- **Kotlin: companion object, value/sealed/inner/annotation class, 拡張関数, val/var プロパティ** — Kotlin パターンに companion object、value class、sealed interface、inner class、annotation class、拡張関数（`fun Type.name()`）、suspend/inline/infix/operator/tailrec 修飾子、const/lateinit val/var プロパティ抽出を追加。対象: `src/CodeIndex/Indexer/SymbolExtractor.cs`, `tests/CodeIndex.Tests/SymbolExtractorTests.cs`.

- **Java IgnoredCallNames 拡張** — `instanceof`、`super`、`assert`、`throws`、`extends`、`implements`、`synchronized` を参照抽出器の無視リストに追加し、Java コードの偽陽性参照を削減。対象: `src/CodeIndex/Indexer/ReferenceExtractor.cs`.

- **Java シンボル抽出: record, sealed, @interface, static final, enum メンバー, 拡張修飾子** — Java パターンに `record`（Java 16+）、`sealed`/`non-sealed`（Java 17+）、`@interface` アノテーション型、`static final` 定数（C# const 相当）、enum メンバー、拡張メソッド修飾子（`default`、`native`、`final`、`strictfp`）を追加。C# 改善のクロス言語横展開。対象: `src/CodeIndex/Indexer/SymbolExtractor.cs`, `tests/CodeIndex.Tests/SymbolExtractorTests.cs`.

- **DEVELOPER_GUIDE にパターン外部化の設計メモ** — 現在のインライン正規表現アプローチ、トレードオフ、将来の外部化パス（JSON/TOML、スキーマ: 言語、種別、正規表現、本体スタイル、キャプチャグループ）を文書化。英語・日本語両セクション。対象: `DEVELOPER_GUIDE.md`.

- **README に TL;DR セクションとドキュメントリンクを追加** — README 冒頭（英語・日本語）に折りたたみ式の TL;DR を追加。クイックスタートコマンド、機能数、DEVELOPER_GUIDE/SELF_IMPROVEMENT/TESTING_GUIDE へのリンクを含む。GitHub/NuGet の入口をスキャンしやすくしつつ、詳細コンテンツは維持。対象: `README.md`.

- **CLAUDE.md 日本語セクション同期** — deps に `--reverse` 追加、ファイル分割後のアーキテクチャ更新（DbSearchReader, DbSymbolReader, McpToolDefinitions, McpToolHandlers, QueryResults）、新テストファイル追加。英語・日本語セクション一致。対象: `CLAUDE.md`.

- **Unicode/CJK文字テスト** — 日本語・中国語・韓国語の文字がファイル内容・クラス名・メソッド名で正しくインデックス・抽出されることのテスト追加。.NET の `\w` は Unicode 文字にマッチ。対象: `tests/CodeIndex.Tests/FileIndexerTests.cs`.

- **DB破損復旧テスト** — 破損DBのクラッシュ回避、破損後の再構築、欠損DBへの適切な終了コードのテストを `DbRecoveryTests.cs` に追加。対象: `tests/CodeIndex.Tests/DbRecoveryTests.cs`.

- **`--rebuild` フラグ動作テスト** — `--rebuild` が全ファイル再スキャンすること、`--commits` と競合することのテスト追加。対象: `tests/CodeIndex.Tests/IndexCommandRunnerTests.cs`.

- **大規模パフォーマンステスト（10K+ファイル）** — 10Kファイル挿入ベンチマークと1KファイルFTS5検索レイテンシテストを `PerformanceTests.cs` に追加。対象: `tests/CodeIndex.Tests/PerformanceTests.cs`.

- **並行アクセステスト** — 並行読み取り（WALモードによる並列リーダー）と書き込み中読み取りシナリオのテストを `ConcurrencyTests.cs` に追加。対象: `tests/CodeIndex.Tests/ConcurrencyTests.cs`.

- **開発者ガイドに「なぜSQLiteなのか？」セクションを追加** — PostgreSQL、DuckDB、LiteDB、Tantivy、ベクトルDB等の代替案との比較を含め、SQLiteを採用した理由、SQLiteが最適な根拠、SQLiteでは足りなくなるケースを文書化。英語・日本語の両セクション。対象: `DEVELOPER_GUIDE.md`。

#### 変更

- **DbReader.cs と McpServer.cs の保守性向上のための分割** — `DbReader.cs` から21個の結果DTOを `Models/QueryResults.cs`（243行）に抽出。`McpServer.cs`（1349行）をpartial classで3ファイルに分割: コアプロトコル処理（332行）、ツール定義（299行）、ツール実行ハンドラ（749行）。さらに `DbReader.cs`（1071行）を3ファイルに分割: コアクエリ（651行）、全文検索（116行）、シンボルクエリ（327行）。公開APIは変更なし。対象: `src/CodeIndex/Database/DbReader.cs`, `src/CodeIndex/Database/DbSearchReader.cs`, `src/CodeIndex/Database/DbSymbolReader.cs`, `src/CodeIndex/Models/QueryResults.cs`, `src/CodeIndex/Mcp/McpServer.cs`, `src/CodeIndex/Mcp/McpToolDefinitions.cs`, `src/CodeIndex/Mcp/McpToolHandlers.cs`.

#### 修正

- **イベント購読 regex を PascalCase 識別子に制限** — `+=`/`-=` イベント購読検出で LHS と RHS の両方を PascalCase 識別子に限定（例: `Click += OnClick`）。`count += 1` や `flags -= mask` の算術代入からの偽陽性を防止。対象: `src/CodeIndex/Indexer/ReferenceExtractor.cs`, `tests/CodeIndex.Tests/ReferenceExtractorTests.cs`.

- **enum メンバーパターンをオブジェクト初期化子の偽陽性回避で制限** — C# enum メンバーの正規表現を、`=` 後の値が数値・16進数・他の PascalCase 識別子（フラグ用 `|` 含む）の場合のみマッチするよう厳格化。初期化子の文字列・オブジェクト代入が偽シンボルを生まなくなった。対象: `src/CodeIndex/Indexer/SymbolExtractor.cs`, `tests/CodeIndex.Tests/SymbolExtractorTests.cs`.

- **`batch_query` サブクエリのバリデーションエラーを表面化** — サブクエリがバリデーション失敗した場合（例: `search` に `query` なし）、`null` の代わりにエラーメッセージをバッチ結果に含めるようにした。対象: `src/CodeIndex/Mcp/McpServer.cs`.

- **`deps` の同名シンボルによる誤エッジと exclude-path 未反映の修正** — `deps` クエリで `(source_path, target_path, symbol_name)` の DISTINCT トリプルを使い、同名シンボル（複数の `Run` や `Dispose` 等）による参照数膨張を防止。また `excludePathPatterns` を SQL とパラメータバインドに反映 — 従来は受け取るだけで無視されていた。対象: `src/CodeIndex/Database/DbReader.cs`.

- **`validate` コマンドでエンコーディング問題検出** — 新コマンド `cdidx validate [--json] [--kind <kind>] [--path <pattern>]` と MCP ツール `validate` を追加。インデックス時に検出した U+FFFD 置換文字、UTF-8 BOM マーカー、NULL バイト、混在改行コードを報告する。問題は新テーブル `file_issues` に保存し、いつでもクエリ可能。対象: 多数ファイル（FileIndexer, DbContext, DbWriter, DbReader, CLI, MCP）。

#### 変更

- **DEVELOPER_GUIDE シンボル抽出言語数を 26 に更新** — 新しく対応した全言語を反映。対象: `DEVELOPER_GUIDE.md`.

- **C# `using` 宣言の参照除外テスト** — `using var x = ...;` が偽陽性参照を生成せず、using スコープ内の実呼び出しは正しく抽出されることを検証。対象: `tests/CodeIndex.Tests/ReferenceExtractorTests.cs`.

- **C# partial メソッドのテストカバレッジ** — C# 9 拡張 partial メソッド（`partial void OnInit();`、`public partial string GetName();`）が正しく抽出されることのテスト追加。対象: `tests/CodeIndex.Tests/SymbolExtractorTests.cs`.

- **DEVELOPER_GUIDE アーキテクチャに deps と batch_query を反映** — DbReader の説明にファイル間依存、McpServer に batch_query を追記。対象: `DEVELOPER_GUIDE.md`.

- **C# `file` 修飾子のメソッドパターン対応** — `file static void DoWork()`（C# 11 のファイルスコープメンバー）を抽出可能に。ファイルスコープ型のテストも追加。対象: `src/CodeIndex/Indexer/SymbolExtractor.cs`, `tests/CodeIndex.Tests/SymbolExtractorTests.cs`.

- **CLAUDE.md CLI コマンドに --since と deps を反映** — CLAUDE.md の `files` に `--since` を追加し、`deps` コマンドを記載。対象: `CLAUDE.md`.

- **README オプション表に --since, --no-dedup, --reverse, --top を追加** — 英語・日本語のオプション表を新しいクエリオプションで更新。対象: `README.md`.

- **README MCP ツール表に deps と batch_query を追加** — 英語・日本語の MCP ツール表に `deps` と `batch_query` を追加。対象: `README.md`.

- **ヘルプテキストに deps, languages, --since, --reverse の使用例追加** — 対象: `src/CodeIndex/Cli/ConsoleUi.cs`.

- **C# `unsafe` 修飾子のクラス/構造体パターン** — `unsafe struct` や `unsafe class` を正しく抽出。対象: `src/CodeIndex/Indexer/SymbolExtractor.cs`.

- **C# `ref struct` / `readonly ref struct` 抽出** — クラス修飾子リストに `ref` を追加し、`ref struct` と `readonly ref struct` を正しく抽出。対象: `src/CodeIndex/Indexer/SymbolExtractor.cs`, `tests/CodeIndex.Tests/SymbolExtractorTests.cs`.

- **MCP instructions に files `since` パラメータの案内追加** — `initialize` の instructions で `files` の `since` パラメータの活用を AI クライアントに案内。対象: `src/CodeIndex/Mcp/McpServer.cs`.

- **MCP `deps` ツールに reverse パラメータ** — MCP の `deps` ツールに `reverse` ブーリアンパラメータを追加し、CLI の `--reverse` フラグと同等の逆引き依存検索に対応。対象: `src/CodeIndex/Mcp/McpServer.cs`.

- **C# enum メンバー抽出** — `Red`、`Green = 1`、`Blue = 2` のような enum メンバーを enum 本体内の function シンボルとして抽出。`outline` と `symbols` で enum 値をナビゲーション可能に。対象: `src/CodeIndex/Indexer/SymbolExtractor.cs`, `tests/CodeIndex.Tests/SymbolExtractorTests.cs`.

- **C# record バリアントのテストカバレッジ** — `record`、`sealed record class`、`readonly record struct` のパラメータリスト付き定義がシグネチャに正しく含まれることのテストを追加。対象: `tests/CodeIndex.Tests/SymbolExtractorTests.cs`.

- **`--lang` バリデーションと利用可能言語ヒント** — `files` で `--lang` に該当ファイルがない場合、インデックス内の言語一覧を表示。対象: `src/CodeIndex/Cli/QueryCommandRunner.cs`.

- **`deps` コマンドに `--reverse` フラグ** — `deps --reverse --path <file>` で指定ファイルに依存しているファイルを表示（逆引き依存関係）。リファクタリング影響分析に不可欠。対象: `src/CodeIndex/Database/DbReader.cs`, `src/CodeIndex/Cli/QueryCommandRunner.cs`.

- **ヘルプテキストに --top エイリアスを表示** — `--help` 出力で `--limit <n>` の横に `--top <n>` エイリアスを表示。対象: `src/CodeIndex/Cli/ConsoleUi.cs`.

- **MCP instructions に batch_query と deps を追加** — `initialize` レスポンスの instructions に `batch_query`（往復回数削減）と `deps`（ファイル間依存分析）の案内を追加。対象: `src/CodeIndex/Mcp/McpServer.cs`.

- **`definition` 出力にコンテナパンくず表示** — 人間向け `definition` 出力で、包含するシンボル（例: "in DbReader"）を表示。クラス/名前空間のコンテキストが見えるようになった。対象: `src/CodeIndex/Cli/QueryCommandRunner.cs`.

- **`status` にシンボル種別分布を追加** — `status` の出力に `symbol_kinds`（function、class、import、namespace ごとのカウント）を追加。AI エージェントがコードベースの構成を把握するのに有用。対象: `src/CodeIndex/Database/DbReader.cs`, `src/CodeIndex/Cli/QueryCommandRunner.cs`.

- **`--no-dedup` フラグで検索重複排除をスキップ** — デバッグ時や生の FTS5 結果が必要なときに `--no-dedup` で重複排除を無効化。対象: `src/CodeIndex/Database/DbReader.cs`, `src/CodeIndex/Cli/QueryCommandRunner.cs`.

- **C# イベント購読・解除の参照抽出** — `Click += Handler` や `Loaded -= Handler` パターンを `subscribe` 参照として抽出。C# コードのイベント配線を `references` と `callers` で発見可能に。対象: `src/CodeIndex/Indexer/ReferenceExtractor.cs`, `tests/CodeIndex.Tests/ReferenceExtractorTests.cs`.

- **`--top` を `--limit` のエイリアスとして追加** — 結果数制限のより直感的なオプション名。`cdidx symbols --top 5` は `--limit 5` と同等。対象: `src/CodeIndex/Cli/QueryCommandRunner.cs`.

- **MCP `batch_query` ツールで複数クエリ一括実行** — `{tool, arguments}` の配列を受け取り、1回の呼び出しで全て実行して結果配列を返す新 MCP ツール。AI エージェントの往復回数を劇的に削減（例: status + symbols + definition を1回で取得）。1バッチ最大10クエリ。書き込み操作（index）はブロック。対象: `src/CodeIndex/Mcp/McpServer.cs`, `tests/CodeIndex.Tests/McpServerTests.cs`.

- **`deps` コマンドでファイル間依存関係分析** — 新コマンド `cdidx deps` と MCP ツール `deps` を追加。インデックス済み参照グラフからファイル間の依存エッジを算出し、参照数とシンボルリスト付きで返す。AIエージェントが1回の呼び出しでプロジェクトアーキテクチャを把握できる。対象: `src/CodeIndex/Program.cs`, `src/CodeIndex/Database/DbReader.cs`, `src/CodeIndex/Cli/QueryCommandRunner.cs`, `src/CodeIndex/Cli/ConsoleUi.cs`, `src/CodeIndex/Mcp/McpServer.cs`, `tests/CodeIndex.Tests/McpServerTests.cs`.

- **C# `#region` 抽出でアウトラインナビゲーション** — `#region Name` ディレクティブを namespace シンボルとして抽出し、`outline` と `symbols` に表示。大きな C# ファイルでのコードセクション特定をAIエージェントと開発者に提供。対象: `src/CodeIndex/Indexer/SymbolExtractor.cs`, `tests/CodeIndex.Tests/SymbolExtractorTests.cs`.

- **`--since` フィルタで時間ベースのファイル問い合わせ** — `files` CLI コマンドと MCP `files` ツールに `--since <datetime>` オプションを追加。指定タイムスタンプ以降に変更されたファイルのみに結果を絞る。AI エージェントが「直近1時間の変更は？」と聞けるようになる。対象: `src/CodeIndex/Database/DbReader.cs`, `src/CodeIndex/Cli/QueryCommandRunner.cs`, `src/CodeIndex/Cli/ConsoleUi.cs`, `src/CodeIndex/Mcp/McpServer.cs`.

- **`--kind` バリデーションと有効値ヒント** — `symbols` や `definition` で `--kind` に無効な値を指定して 0 件になった場合、インデックス内の有効な kind 一覧を表示するようにした（例: "Available: class, function, import, namespace"）。`DbReader.GetDistinctKinds()` メソッドを追加。対象: `src/CodeIndex/Database/DbReader.cs`, `src/CodeIndex/Cli/QueryCommandRunner.cs`.

#### 変更

- **検索結果のオーバーラップチャンク間重複排除** — 10行のオーバーラップを持つ隣接チャンクが両方同じクエリにマッチした場合、ランクの低い重複を除去するようにした。同じコード領域が検索結果に2回出ることを防ぎ、AIエージェントのトークン消費を削減。対象: `src/CodeIndex/Database/DbReader.cs`, `tests/CodeIndex.Tests/DbReaderTests.cs`.

#### 修正

- **outline の visibility 重複表示** — `outline` の人間向け出力で `public public static class Foo` のように visibility がシグネチャに既に含まれているのに重複して表示されていた問題を修正。表示前に重複チェックを追加。対象: `src/CodeIndex/Cli/QueryCommandRunner.cs`.

#### 追加

- **C# `using` エイリアス抽出** — `using Json = System.Text.Json;` や `global using Logging = ...;` のエイリアス宣言をエイリアス名の import シンボルとして抽出。従来は `=` 文字によりスキップされていた。対象: `src/CodeIndex/Indexer/SymbolExtractor.cs`, `tests/CodeIndex.Tests/SymbolExtractorTests.cs`.

- **C# const・static readonly フィールド抽出** — `const` フィールドと `static readonly` フィールドを型付きの function シンボルとして抽出。通常の可変フィールドは引き続き除外。cdidx 自身のコードベースで `MaxFileSize`、`SkipDirs` 等の設定定数へのナビゲーションに有用。対象: `src/CodeIndex/Indexer/SymbolExtractor.cs`, `tests/CodeIndex.Tests/SymbolExtractorTests.cs`.

- **C# 式本体プロパティの抽出** — `public int X => 42;` の式本体プロパティ・読み取り専用メンバーを戻り値型付きの function シンボルとして抽出。従来は `{ get; set; }` スタイルのみ対応だった。対象: `src/CodeIndex/Indexer/SymbolExtractor.cs`, `tests/CodeIndex.Tests/SymbolExtractorTests.cs`.

- **C# 明示的インターフェース実装の抽出** — `void IDisposable.Dispose()` 等の明示的インターフェース実装を戻り値型付きの function シンボルとして抽出。対象: `src/CodeIndex/Indexer/SymbolExtractor.cs`, `tests/CodeIndex.Tests/SymbolExtractorTests.cs`.

- **C# 偽陽性参照の削減: IgnoredCallNames 拡張** — 参照抽出器の無視リストに約30のキーワードを追加: C# 文脈キーワード（`is`、`as`、`in`、`var`、`base`、`this`、`value`、`get`、`set`、`init`、`where`）、LINQ キーワード（`from`、`select`、`orderby`、`group` 等）、型キーワード（`struct`、`record`、`interface`、`delegate`、`event`）、ユーティリティキーワード（`default`、`stackalloc`、`fixed`、`checked`）。C# コードの偽陽性参照を低減。対象: `src/CodeIndex/Indexer/ReferenceExtractor.cs`, `tests/CodeIndex.Tests/ReferenceExtractorTests.cs`.

- **C# インデクサ抽出** — `this[int index]` のインデクサ宣言を戻り値型付きの function シンボルとして抽出。対象: `src/CodeIndex/Indexer/SymbolExtractor.cs`, `tests/CodeIndex.Tests/SymbolExtractorTests.cs`.

- **C# 演算子オーバーロード・変換演算子抽出** — `operator +`、`operator ==`、`implicit operator`、`explicit operator` を function シンボルとして抽出。戻り値型との誤マッチを防ぐため、一般メソッドパターンより前に配置。対象: `src/CodeIndex/Indexer/SymbolExtractor.cs`, `tests/CodeIndex.Tests/SymbolExtractorTests.cs`.

- **C# 静的コンストラクタ・ファイナライザ抽出** — `static ClassName()` と `~ClassName()` を function シンボルとして抽出するパターンを追加。対象: `src/CodeIndex/Indexer/SymbolExtractor.cs`, `tests/CodeIndex.Tests/SymbolExtractorTests.cs`.

- **C# visibility 省略可・複合アクセス修飾子対応** — クラス・メソッド・プロパティ・デリゲート・イベントの各パターンで visibility キーワードを省略可にし、`class Foo`、`static void Run()` 等の暗黙 internal メンバーを抽出できるようにした。`protected internal`・`private protected` の複合修飾子を単一の visibility 値として正しく取得。C# 12 の primary constructor シグネチャの検証テストも追加。対象: `src/CodeIndex/Indexer/SymbolExtractor.cs`, `tests/CodeIndex.Tests/SymbolExtractorTests.cs`.

- **`languages` CLI コマンドと MCP ツール** — 新コマンド `cdidx languages [--json]` と MCP ツール `languages` を追加。対応言語の一覧を拡張子・シンボル抽出対応・コールグラフ対応の情報付きで返す。AI エージェントや新規ユーザーがドキュメントを参照せずに cdidx の対応範囲を実行時に確認できる。対象: `src/CodeIndex/Program.cs`, `src/CodeIndex/Cli/QueryCommandRunner.cs`, `src/CodeIndex/Cli/ConsoleUi.cs`, `src/CodeIndex/Mcp/McpServer.cs`, `src/CodeIndex/Indexer/FileIndexer.cs`, `src/CodeIndex/Indexer/SymbolExtractor.cs`, `tests/CodeIndex.Tests/McpServerTests.cs`.

#### 修正

- **新機能に対するドキュメント・ヘルプテキストの追従** — README オプション表（英語・日本語）、ヘルプテキスト使用例、`ConsoleUiTests` に `--count` を追加。README のグラフ対応言語リスト（英語・日本語）に Dart、Scala、Elixir を追加。README の対応言語表に Protobuf、GraphQL、Gradle、Makefile、Dockerfile、PowerShell、Batch、CMake を追加。DEVELOPER_GUIDE の言語数を 21 から 26 に更新。対象: `README.md`, `DEVELOPER_GUIDE.md`, `src/CodeIndex/Cli/ConsoleUi.cs`, `tests/CodeIndex.Tests/ConsoleUiTests.cs`.

- **シェル補完: 古いハードコード言語リストとエラー終了の欠落** — `--completions` の `--lang` 値リストを12言語のハードコードから `FileIndexer.GetLanguageExtensions()` による動的生成に変更。不明なシェル名で終了コード0ではなく1（UsageError）を返すよう修正。有効・無効シェル名のテストを追加。対象: `src/CodeIndex/Cli/ConsoleUi.cs`, `src/CodeIndex/Program.cs`, `tests/CodeIndex.Tests/ConsoleUiTests.cs`.

- **Gradle `plugins { id }` DSL が未対応だった問題** — Gradle の import 正規表現が `apply plugin: 'name'` のみ対応し、現代的な `plugins { id 'name' }` ブロック形式を取りこぼしていた。`id` の後にスペースも `(:` も受け付けるよう修正し、`id` 形式のテストも追加。対象: `src/CodeIndex/Indexer/SymbolExtractor.cs`, `tests/CodeIndex.Tests/SymbolExtractorTests.cs`.

#### 追加

- **GraphQL・Gradle シンボル抽出** — GraphQL スキーマで `type`、`interface`、`union`、`enum`、`scalar`、`input`、`query`、`mutation`、`subscription`、`extend type` のシンボル抽出に対応。Gradle ビルドスクリプトで `task`/`def` の function 抽出と `apply plugin`/`id` の import 抽出に対応。`symbols`、`definition`、`outline` がこれらのエコシステムで使えるようになった。対象: `src/CodeIndex/Indexer/SymbolExtractor.cs`, `tests/CodeIndex.Tests/SymbolExtractorTests.cs`.

- **Makefile・Dockerfile シンボル抽出** — Makefile のターゲット（`all:`、`build:`、`test:`）を function シンボルとして抽出。Dockerfile の `FROM ... AS` ステージは function、名前なし `FROM` イメージは class として抽出。`symbols`、`definition`、`outline` がこれらのファイルで使えるようになった。対象: `src/CodeIndex/Indexer/SymbolExtractor.cs`, `tests/CodeIndex.Tests/SymbolExtractorTests.cs`.

- **Elixir コールグラフ対応と Protobuf シンボル抽出** — Elixir が `references`、`callers`、`callees` クエリに対応（括弧付き呼び出しが既存の正規表現エンジンで動作）。Protobuf（`.proto`）ファイルで `message`、`enum`、`service`、`rpc`、`import` のシンボル抽出に対応し、gRPC/protobuf プロジェクトで `definition`、`symbols`、`outline` が使えるようになった。対象: `src/CodeIndex/Indexer/ReferenceExtractor.cs`, `src/CodeIndex/Indexer/SymbolExtractor.cs`, `tests/`.

- **0件クエリ時のアクション可能なヒント** — `search` や `definition` が 0 件を返したとき、CLI がヒントを表示するようにした: フィルタが有効なら解除を提案、`definition` の代わりに `search` を提案、インデックスが古い（24時間超）か空なら警告。ユーザーの混乱を減らし、AI エージェントが自己修正しやすくなる。対象: `src/CodeIndex/Cli/QueryCommandRunner.cs`.

- **シェル補完スクリプト生成（bash/zsh/fish）** — `cdidx --completions <shell>` で bash、zsh、fish 向けのタブ補完スクリプトを生成。全コマンド、サブコマンドオプション、`--lang`/`--kind` の値補完に対応。`eval "$(cdidx --completions bash)"` をシェルプロファイルに追加すれば即座にコマンド補完が有効になる。対象: `src/CodeIndex/Program.cs`, `src/CodeIndex/Cli/ConsoleUi.cs`.

- **`status` に graph 対応言語とバージョンを追加** — `status` の出力に `graph_supported_languages`（コールグラフ対応言語のソート済みリスト）と `version`（cdidx バイナリのバージョン）を追加。人間向け・JSON 向け両方に対応。AI エージェントが試行錯誤なしで `callers`/`callees`/`references` を使える言語を事前に確認できる。対象: `src/CodeIndex/Database/DbReader.cs`, `src/CodeIndex/Cli/QueryCommandRunner.cs`, `src/CodeIndex/Mcp/McpServer.cs`, `src/CodeIndex/Program.cs`.

- **クエリコマンドに `--count` フラグ追加** — `search`、`definition`、`references`、`callers`、`callees`、`symbols`、`files` に `--count` を追加。結果全体ではなくカウントだけを返す。`--json` 併用で `{"count": N, "files": M}` 形式。AI エージェントが全データ取得前に結果量を見積もれるため、トークン節約に効果的。対象: `src/CodeIndex/Cli/QueryCommandRunner.cs`, `src/CodeIndex/Cli/ConsoleUi.cs`, `tests/CodeIndex.Tests/QueryCommandRunnerTests.cs`.

- **CLI 結果サマリにファイル数を追加** — `search`、`definition`、`references`、`callers`、`callees`、`symbols` の人間向け出力で「(N results)」の代わりに「(N results in M files)」を表示し、結果の散らばり具合を素早く把握できるようにした。対象: `src/CodeIndex/Cli/QueryCommandRunner.cs`.

- **Protobuf、GraphQL、Dockerfile、Makefile 等のファイル種別追加** — `.proto`（protobuf）、`.graphql`/`.gql`（graphql）、`.gradle`、`.cmake`/`CMakeLists.txt`、`.ps1`（powershell）、`.bat`/`.cmd`（batch）、`.bash`/`.zsh`/`.fish`（shell）の言語検出と、`Dockerfile`、`Makefile`、`Justfile`、`Vagrantfile`、`.editorconfig`、`.gitignore`、`.dockerignore` のファイル名ベース検出を追加。これらの一般的なプロジェクトファイルが全文検索の対象になる。対象: `src/CodeIndex/Indexer/FileIndexer.cs`, `tests/CodeIndex.Tests/FileIndexerTests.cs`.

- **言語横展開の指針** — `SELF_IMPROVEMENT.md` に、ある言語向けの強化（特に C# ↔ Java）を構文的に近い言語にも横展開するよう AI エージェントに指示するガイドラインを追加。TypeScript/JavaScript、Kotlin/Java、C/C++ のペアも対象。対象: `SELF_IMPROVEMENT.md`。

### [1.3.0] - 2026-04-11

#### 追加

- **C# エコシステム強化** — Razor/Blazor（`.cshtml`、`.razor`）を csharp として検出、VB.NET（`.vb`、`.vbs`）の Sub/Function/Class/Module シンボル抽出、F#（`.fs`、`.fsx`、`.fsi`）の let/type/module/open 抽出（スペース区切りの呼び出し構文のため graph クエリは非対応）、XAML/MSBuild ファイル（`.xaml`、`.axaml`、`.csproj`、`.fsproj`、`.vbproj`、`.props`、`.targets`）を xml として検出。C# 改善: file-scoped namespace（C# 10+）、`global using`、`using static`、`record struct`/`record class`、プロパティ抽出（get/set/init）、delegate/event 宣言、`file` クラス修飾子。F#/VB.NET の `map` 向けエントリポイントヒントも追加。対象: `src/CodeIndex/Indexer/`, `src/CodeIndex/Database/RepoMapBuilder.cs`, `tests/`.

- **Dart、Scala、Elixir、Lua、R 言語サポート** — 言語検出（`.dart`、`.scala`、`.sc`、`.r`、`.R`、`.ex`、`.exs`、`.lua`）、Dart（class/mixin/enum/extension/function/import）・Scala（class/object/trait/case class/def/import）・Elixir（defmodule/defprotocol/def/defp/import/alias/use）・Lua（function/local function/require）のシンボル抽出を追加。Dart と Scala は call graph 参照抽出と `map` 向けエントリポイントヒントにも対応。対象: `src/CodeIndex/Indexer/FileIndexer.cs`, `src/CodeIndex/Indexer/SymbolExtractor.cs`, `src/CodeIndex/Indexer/ReferenceExtractor.cs`, `src/CodeIndex/Database/RepoMapBuilder.cs`, `tests/`.

- **ロックファイルとビルドキャッシュディレクトリの追加除外** — `Gemfile.lock`、`Cargo.lock`、`composer.lock`、`poetry.lock`、`bun.lockb` をスキップファイルに、`.terraform`、`.cargo`、`.pub-cache`、`_build` をスキップディレクトリに追加。対象: `src/CodeIndex/Indexer/FileIndexer.cs`, `tests/CodeIndex.Tests/FileIndexerTests.cs`.

- **`outline` コマンドで1ファイルのシンボル構造を取得** — 新 CLI コマンド `cdidx outline <path>` と MCP ツール `outline` を追加。1ファイル内の全シンボルを行順に、種別・シグネチャ・可視性・コンテナネスト・本体範囲付きで返す。`symbols` + `definition` のチェーンを1回で置き換え。対象: `src/CodeIndex/Database/DbReader.cs`, `src/CodeIndex/Cli/QueryCommandRunner.cs`, `src/CodeIndex/Cli/ConsoleUi.cs`, `src/CodeIndex/Program.cs`, `src/CodeIndex/Mcp/McpServer.cs`, `tests/`.

- **R シンボル抽出と Haskell/Zig 言語検出** — R は `name <- function()` と `library()`/`require()` のシンボル抽出に対応。Haskell（`.hs`、`.lhs`）は型シグネチャ・data/class/import の抽出に対応。Zig（`.zig`）はテキスト検索用に検出のみ。対象: `src/CodeIndex/Indexer/FileIndexer.cs`, `src/CodeIndex/Indexer/SymbolExtractor.cs`, `tests/`.

- **MCP search サマリにファイルパスを含める** — MCP の `search` ツールが content サマリにトップファイルパスを表示し、AI が構造化結果をパースする前に素早く位置を把握できるようにした。対象: `src/CodeIndex/Mcp/McpServer.cs`.

#### 変更

- **README の MCP 図が Markdown プレビューで安定表示されるよう改善** — 英語版・日本語版の MCP セクションにあった ASCII 罫線の図を Mermaid フローチャートへ置き換え、周辺レイアウトを壊していた余分なコードフェンスも削除した。対象: `README.md`.

- **6言語のエントリポイントヒントを追加** — `map` のエントリポイント推定が C（`main.c`）、C++（`main.cpp`/`.cc`/`.cxx`）、Haskell（`Main.hs`/`.lhs`）、R（`main.R`）、Lua（`main.lua`/`init.lua`）、Elixir（`application.ex`/`router.ex`）にも対応。対象: `src/CodeIndex/Database/RepoMapBuilder.cs`.

- **コード品質の一括改善** — `Program.cs` のパス検出ロジックを `IsProjectPathArg` に抽出して可読性向上、`ConsoleUi` のマジックナンバーに名前付き定数（`SpinnerFrameDelayMs`、`SpinnerStopDelayMs`、`ConsoleLineMargin`）を導入、`GitHelper` で C# range syntax を使用、`WorkspaceMetadataEnricher` の重複ロジックを `Apply` ヘルパーに共通化、`SearchSnippetFormatter` のトークン正規化にコメント追加。対象: `src/CodeIndex/Program.cs`, `src/CodeIndex/Cli/ConsoleUi.cs`, `src/CodeIndex/Cli/GitHelper.cs`, `src/CodeIndex/Cli/WorkspaceMetadataEnricher.cs`, `src/CodeIndex/Cli/SearchSnippetFormatter.cs`.

- **CLI エラーメッセージとヘルプの明確化** — `--rebuild` の競合エラーに理由（full rescan が必要）を追加、DB 未検出エラーに `Path.GetFullPath` でフルパスを表示、`--snippet-lines` のヘルプを "1-20, default: 8" に変更。対象: `src/CodeIndex/Cli/IndexCommandRunner.cs`, `src/CodeIndex/Cli/QueryCommandRunner.cs`, `src/CodeIndex/Cli/ConsoleUi.cs`, `tests/CodeIndex.Tests/QueryCommandRunnerTests.cs`, `tests/CodeIndex.Tests/ConsoleUiTests.cs`.

- **テストカバレッジの追加** — `SearchSnippetFormatter.Format` のトランケーションマーカーテスト（両側・前のみ・後ろのみ・なし）と、`ConsoleUi.LoadVersion` が "0.0.0" フォールバックではなく実バージョンを返すことを検証するテストを追加。対象: `tests/CodeIndex.Tests/SearchSnippetFormatterTests.cs`, `tests/CodeIndex.Tests/ConsoleUiTests.cs`.

### [1.2.0] - 2026-04-11

#### 追加

- **0件 MCP レスポンスに鮮度ヒントを追加** — MCP クエリツール（`search`、`definition`、`symbols`、`references`、`callers`、`callees`、`files`）が 0 件を返すとき、レスポンスに `indexed_file_count` と `indexed_at` を含めるようにした。AI クライアントが別途 `status` を呼ばなくても、インデックスの古さや空を即座に判断できる。対象: `src/CodeIndex/Database/DbReader.cs`, `src/CodeIndex/Mcp/McpServer.cs`, `tests/CodeIndex.Tests/McpServerTests.cs`.

- **MCP サーバー instructions と listChanged ケイパビリティ** — MCP の `initialize` レスポンスにツール選択ガイダンスの `instructions` 文字列（`map` から始める、`analyze_symbol` でクエリをまとめる、graph ツールは対応言語のみ、DB 未作成時は `index` を先に実行等）を追加し、`capabilities.tools.listChanged` を `false` に設定した。instructions 内の対応言語リストは `ReferenceExtractor.GetSupportedLanguages()` から動的に生成して自動同期する。プロトコルバージョンを `2024-11-05` から `2025-03-26` に更新し、`instructions` とツールアノテーションを導入した仕様改訂に合わせた。対象: `src/CodeIndex/Mcp/McpServer.cs`, `tests/CodeIndex.Tests/McpServerTests.cs`.

- **専用のテストガイドを追加** — テストスイート構成、共有ヘルパー、クロスプラットフォーム上の注意点、テスト作法をまとめた英日併記の `TESTING_GUIDE.md` を追加した。あわせて保守チェックリストを更新し、今後はテストコード変更時に同じコミットでテストガイドも明示的に確認・更新する運用にした。対象: `TESTING_GUIDE.md`, `README.md`, `DEVELOPER_GUIDE.md`, `SELF_IMPROVEMENT.md`, `CLAUDE.md`.

- **MCP ツールアノテーションで AI クライアントの信頼判断を支援** — 全 MCP ツールが MCP 仕様に沿った `annotations`（`readOnlyHint`、`destructiveHint`、`idempotentHint`、`openWorldHint`）を返すようになった。クエリツールは読み取り専用かつ冪等に、`index` ツールは破壊的かつ非冪等にマークされる（`--rebuild` で DB を削除でき、再インデックスでファイルごとにチャンク・シンボルを置き換えるため）。これにより AI クライアントがユーザー確認なしに安全に呼べるツールを判断しやすくなる。対象: `src/CodeIndex/Mcp/McpServer.cs`, `tests/CodeIndex.Tests/McpServerTests.cs`.

#### 変更

- **`RepoMapBuilder` を `DbReader` から分離** — repo map ロジック（約280行: `GetRepoMap`、ファイル統計、エントリポイント採点、モジュールグループ化）を専用の `RepoMapBuilder` クラスに移動し、`DbReader` を 1174 行から 1073 行に縮小した。公開 API（`DbReader.GetRepoMap`）は変更なし、内部で `RepoMapBuilder` に委譲する。共有クエリヘルパー（`AppendPathFilters`、`AddPathFilterParameters`、`EscapeLikeQuery`、`GetNullableDateTime`）は再利用のため `internal static` に変更。対象: `src/CodeIndex/Database/DbReader.cs`, `src/CodeIndex/Database/RepoMapBuilder.cs`.

- **自己改善ループをリグレッションテスト兼モンキーテストとして扱う方針を明記** — `SELF_IMPROVEMENT.md` に、このループが単なる改善実装だけでなく、ビルドしたばかりのローカル版バイナリに対する継続的なリグレッション確認と軽いモンキーテストも兼ねることを明記した。最も安全な happy path だけでなく、新機能、利用頻度の低い機能、エッジ寄りの経路も積極的に使い、クラッシュや統合不具合を早めに表面化させるよう指示した。対象: `SELF_IMPROVEMENT.md`.

- **自己改善ループでローカル版バイナリの失敗を明示的にエスカレーション** — `SELF_IMPROVEMENT.md` に、ビルド直後のローカル版バイナリがクラッシュしたり異常終了したり新しい不具合を見せた場合、黙って回避したり古い版・グローバル版へ逃げたりせず、具体的な失敗内容をユーザーへ通知して、次タスクまたは次の承認済み優先事項として修正提案を出すことを明記した。対象: `SELF_IMPROVEMENT.md`.

- **`map` にファイルベースのエントリポイント補完を追加** — repo map の entrypoints は、シンボル抽出が `Main` 系シンボルを出さない場合でも、`Program.cs` や `main.py` のような既知のトップレベル実行ファイルへフォールバックするようになった。`entrypoints` の形は変えずに、トップレベルスクリプトや top-level statements のプロジェクトでも初動の入口把握を改善する。対象: `src/CodeIndex/Database/DbReader.cs`, `tests/CodeIndex.Tests/DbReaderTests.cs`, `README.md`, `DEVELOPER_GUIDE.md`, `CLAUDE.md`.

- **直接の graph クエリでも未対応言語ヒントを返すよう改善** — 人間向けの `references`、`callers`、`callees` は、`--lang` が call graph 非対応言語を指しているときに明示的な補足メモを出すようにした。MCP の graph ツールも `graph_language`、`graph_supported`、`graph_support_reason` を返し、未対応言語の 0 件結果と、対応言語の本当の 0 件を区別できるようにした。対象: `src/CodeIndex/Cli/QueryCommandRunner.cs`, `src/CodeIndex/Mcp/McpServer.cs`, `tests/CodeIndex.Tests/McpServerTests.cs`, `README.md`, `DEVELOPER_GUIDE.md`, `CLAUDE.md`.

- **`inspect` / `analyze_symbol` で未対応言語の call graph 非対応を明示** — シンボル分析が `graph_language`、`graph_supported`、`graph_support_reason` を返すようになり、AIクライアントが「この言語では callers/callees/references が未対応」なのか「単にヒットが無い」だけなのかを区別できるようにした。人間向け `inspect` 出力でも同じ graph 対応メモを表示する。対象: `src/CodeIndex/Indexer/ReferenceExtractor.cs`, `src/CodeIndex/Database/DbReader.cs`, `src/CodeIndex/Cli/QueryCommandRunner.cs`, `src/CodeIndex/Mcp/McpServer.cs`, `tests/CodeIndex.Tests/DbReaderTests.cs`, `tests/CodeIndex.Tests/McpServerTests.cs`, `README.md`, `DEVELOPER_GUIDE.md`, `CLAUDE.md`.

- **既定スピナーを回転して見えるブライユ列へ変更** — 既定のスピナーと進捗バーが、揺れて見える 6 コマではなく `⠋ ⠙ ⠹ ⠸ ⠼ ⠴ ⠦ ⠧ ⠇ ⠏` の 10 コマ列を共有するようにした。既定フレーム列の回帰テストも追加し、スピナーと進捗バーで定義がずれないよう重複を除去した。対象: `src/CodeIndex/Cli/ConsoleUi.cs`, `tests/CodeIndex.Tests/ConsoleUiTests.cs`.

- **`inspect` / `analyze_symbol` にワークスペース信頼メタデータを追加** — `inspect --json` と MCP の `analyze_symbol` が `workspace_indexed_at`、`workspace_latest_modified`、`project_root`、`git_head`、`git_is_dirty` を返すようになり、AIクライアントがシンボル分析中に別途 `status` を呼ばなくても鮮度とリポジトリ状態を判断できるようにした。人間向け `inspect` 出力でも、まとめられた各セクションの前に同じ信頼シグナルを表示する。対象: `src/CodeIndex/Database/DbReader.cs`, `src/CodeIndex/Cli/WorkspaceMetadataEnricher.cs`, `src/CodeIndex/Cli/QueryCommandRunner.cs`, `src/CodeIndex/Mcp/McpServer.cs`, `tests/CodeIndex.Tests/DbReaderTests.cs`, `tests/CodeIndex.Tests/McpServerTests.cs`, `README.md`, `DEVELOPER_GUIDE.md`, `CLAUDE.md`.

- **`map` 出力の鮮度を絞り込み範囲とワークスペース全体で分離** — 後方互換のため `map` の `indexed_at` と `latest_modified` は絞り込み結果に対する値のまま維持しつつ、`workspace_indexed_at` と `workspace_latest_modified` を追加し、AIクライアントが別途 `status` を呼ばなくても「この範囲だけ古い」のか「ワークスペース全体が古い」のかを比較できるようにした。人間向け `map` 出力でも scoped/workspace の時刻ラベルを明示した。対象: `src/CodeIndex/Database/DbReader.cs`, `src/CodeIndex/Cli/QueryCommandRunner.cs`, `src/CodeIndex/Mcp/McpServer.cs`, `tests/CodeIndex.Tests/DbReaderTests.cs`, `tests/CodeIndex.Tests/McpServerTests.cs`, `README.md`, `DEVELOPER_GUIDE.md`, `CLAUDE.md`.

- **`BuildGraphSupportReason` を `ReferenceExtractor` に統合** — `DbReader` と `McpServer` に重複していた graph 対応理由メッセージのロジックを、`ReferenceExtractor.BuildGraphSupportReason()` 静的ヘルパーに一本化した。`DbReader` は null 言語時のフォールバックメッセージを付加し、`McpServer` は null をそのまま返す。対象: `src/CodeIndex/Indexer/ReferenceExtractor.cs`, `src/CodeIndex/Database/DbReader.cs`, `src/CodeIndex/Mcp/McpServer.cs`.

#### 修正

- **読み取り専用 DB でのクエリパスを安全化** — クエリコマンド (`search`、`definition`、`inspect` 等) と MCP 読み取りツールが、`InitializeSchema()` の代わりに `TryMigrateForRead()` を呼ぶようにした。`TryMigrateForRead()` は `symbol_references` テーブルとインデックスが無ければ作成し、列の移行も行い、`SQLITE_READONLY` エラーだけを無視して読み取り専用 FS では黙って縮退する。それ以外のエラーは伝播する。対象: `src/CodeIndex/Database/DbContext.cs`, `src/CodeIndex/Cli/QueryCommandRunner.cs`, `src/CodeIndex/Mcp/McpServer.cs`.

- **`GetFileByPath` を完全一致クエリに修正** — `ListFiles` 経由のサブストリング `LIKE '%path%'` + メモリフィルタを、直接 `WHERE path = @path` クエリに置き換え、誤ヒットと不要な処理を除去した。対象: `src/CodeIndex/Database/DbReader.cs`.

- **`GetRepoMap` で空のフィルタ結果によるクラッシュを防止** — `fileStats.Max()` 呼び出し前に `fileStats.Count > 0` をチェックし、条件に一致するファイルがゼロの場合の `InvalidOperationException` を防いだ。対象: `src/CodeIndex/Database/DbReader.cs`.

- **`WriteGraphSupportHint` の null 言語ガードを追加** — `--lang` 未指定時に graph サポートヒントの出力をスキップするようにし、`"not indexed for ''"` という紛らわしいメッセージを防いだ。対象: `src/CodeIndex/Cli/QueryCommandRunner.cs`.

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

[Unreleased]: https://github.com/Widthdom/CodeIndex/compare/v1.8.0...HEAD
[1.8.0]: https://github.com/Widthdom/CodeIndex/compare/v1.7.0...v1.8.0
[1.7.0]: https://github.com/Widthdom/CodeIndex/compare/v1.6.0...v1.7.0
[1.6.0]: https://github.com/Widthdom/CodeIndex/compare/v1.5.0...v1.6.0
[1.5.0]: https://github.com/Widthdom/CodeIndex/compare/v1.4.1...v1.5.0
[1.4.1]: https://github.com/Widthdom/CodeIndex/compare/v1.4.0...v1.4.1
[1.4.0]: https://github.com/Widthdom/CodeIndex/compare/v1.3.0...v1.4.0
[1.3.0]: https://github.com/Widthdom/CodeIndex/compare/v1.2.0...v1.3.0
[1.2.0]: https://github.com/Widthdom/CodeIndex/compare/v1.1.0...v1.2.0
[1.1.0]: https://github.com/Widthdom/CodeIndex/compare/v1.0.5...v1.1.0
[1.0.5]: https://github.com/Widthdom/CodeIndex/compare/v1.0.4...v1.0.5
[1.0.4]: https://github.com/Widthdom/CodeIndex/compare/v1.0.3...v1.0.4
[1.0.3]: https://github.com/Widthdom/CodeIndex/compare/v1.0.2...v1.0.3
[1.0.2]: https://github.com/Widthdom/CodeIndex/compare/v1.0.1...v1.0.2
[1.0.1]: https://github.com/Widthdom/CodeIndex/compare/v1.0.0...v1.0.1
[1.0.0]: https://github.com/Widthdom/CodeIndex/releases/tag/v1.0.0

# Testing Guide

> **[日本語版はこちら / Japanese version](#テストガイド)**

This document explains how the `cdidx` test suite is organized, how to add or update tests safely, and which conventions to follow when the behavior or test infrastructure changes.

If you change test code, test helpers, test execution flow, or testing conventions, update this document in the same commit.

## Quick Start

```bash
dotnet test
dotnet test tests/CodeIndex.Tests/CodeIndex.Tests.csproj
dotnet test tests/CodeIndex.Tests/CodeIndex.Tests.csproj --settings tests/CodeIndex.Tests/CodeIndex.Tests.runsettings --blame-crash --blame-hang --blame-hang-timeout 5m
dotnet test --filter "FullyQualifiedName~GitHelperTests"
```

Use the full suite by default. Use targeted filters only while iterating locally, then finish with `dotnet test`.

## Test Stack

- Framework: xUnit
- Target frameworks: `net8.0` and `net9.0`
- Main test project: `tests/CodeIndex.Tests/CodeIndex.Tests.csproj`
- Common direct test-only packages: `Microsoft.NET.Test.Sdk`, `xunit`, `xunit.runner.visualstudio`, `coverlet.collector`, `Microsoft.Data.Sqlite`, `FsCheck.Xunit`
- These test-only packages are separate from the production dependency rule in `src/CodeIndex`, which still allows only `Microsoft.Data.Sqlite` at runtime.
- `FsCheck.Xunit` is reserved for property-based tests that assert universal invariants (never-throws contracts, idempotence, "output is parseable by downstream consumer") across randomly generated inputs. Use it to complement, not replace, the example-based `[Fact]` / `[Theory]` tests — pick FsCheck when the property is a universally quantified claim, and an example test when a specific concrete case is the contract.
- Test parallelism: enabled by default across independent test classes. Tests that touch process-global state such as SQLite pool resets, environment variables, or current-directory overrides must use an explicit non-parallel collection, and tests that swap `Console.Out` / `Console.Error` must lock on `TestConsoleLock.Gate`.
- CI runs the test project through `tests/CodeIndex.Tests/CodeIndex.Tests.runsettings`, enables VSTest blame crash and hang collection, applies a 45-minute session timeout plus 60-second xUnit long-running diagnostics, and reruns the suite once after an initial failure. If the retry passes, CI uploads `TestResults/flaky-retry.txt` with the TRX and blame artifacts so the run is treated as suspect instead of silently trusted. TRX telemetry summaries and test-result artifact uploads run only for failed or pass-on-retry lanes, not for clean first-pass success lanes. XPlat Code Coverage collection is limited to the `ubuntu-latest` / `net8.0` lane so every OS/framework lane still exercises the full suite without paying collector overhead. Test execution runs with `--no-restore --no-build` after the locked restore and Release build steps. The `ubuntu-latest` / `net8.0` lane also reuses the earlier Release solution build instead of running the per-framework test-project build again, and uses `make lint` as the single formatting verifier. The NuGet cache key is based on `packages.lock.json` and `global.json` instead of every project file; locked restore still catches package-input drift, while test-only project edits no longer evict the package cache.
- Keep the CI initial test run and its single retry routed through one workflow helper so logger, blame, coverage, and result-directory arguments cannot drift. When a PowerShell helper returns the test exit code, keep streamed test output off the function success stream so assignments capture only the numeric exit code.
- Keep package audit, primary-lane build/lint, coverage collection, coverage artifact upload, publish, and build artifact upload keyed to the workflow lane-selection output (`primary_lane`); do not duplicate the matrix predicate in later steps.

## Test Layout

The test project mirrors the production areas closely.

- `ChunkSplitterTests.cs`, `SymbolExtractorTests.cs`, `ReferenceExtractorTests.cs`, `SearchSnippetFormatterTests.cs`, `DbPathResolverTests.cs`, `ConsoleUiTests.cs`
  Pure or mostly pure behavior tests with in-memory inputs.
- `SymbolExtractor*Tests.cs` and `ReferenceExtractor*Tests.cs`
  Extractor coverage is split by language or feature area with partial test classes, while shared helpers remain on the root `SymbolExtractorTests` / `ReferenceExtractorTests` parts.
- `FileIndexerTests.cs`, `FileIndexerContentLoadingTests.cs`, `FileIndexerTestSupport.cs`
  File scanning, language detection, scan-result language reuse, content-sensitive header safeguards, content loading/canonicalization, checksum, Git LFS pointer detection, and record-building behavior, including extensionless shebang detection's 256-byte first-line cap, binary/NUL-byte rejection, and Windows-only >=260-character path walker/purge coverage. Shared `FileIndexerTests` helpers live in `FileIndexerTestSupport.cs`.
- `DatabaseTests.cs`, `DbReaderTests.cs`
  SQLite schema, write paths, migrations, and query behavior.
- `LegacySchemaMigrationTests.cs`
  End-to-end upgrade path: seeds a pre-column legacy DB, opens it through `TryMigrateForRead`, and exercises the read paths that touch nullable symbol ordinals (outline, symbol search, nearby, unused, analyze bundle) to lock in the real-world failure mode behind #58 / #49.
- `IndexCommandRunnerTests.cs`, `QueryCommandRunner*Tests.cs`, `ProgramCliTests.cs`, `InstallScriptTests.cs`
  CLI parsing, command execution, and installer behavior. Query command coverage is split by command family with partial `QueryCommandRunnerTests` classes so shared console and fixture helpers stay centralized. `ProgramCliTests.cs` covers top-level entrypoint behavior that must be exercised through a subprocess, while `InstallScriptTests.cs` runs focused bash snippets against `install.sh` in library mode to lock in release-installer regressions without performing real network installs.
  `InstallScriptTests.RunInstallerSnippet` enforces a bounded timeout and kills the snippet process tree on timeout, so installer regressions fail with captured output instead of hanging the suite.
- `CiWorkflowTests.cs`, `ReleaseWorkflowTests.cs`, `ReleaseWorkflowTests.PackageHelpers.cs`
  CI and release workflow contract tests. Release workflow package-normalization ZIP fixture helpers live in `ReleaseWorkflowTests.PackageHelpers.cs` so workflow assertions stay near the workflow contracts.
- `IndexCommandRunnerTests.Run_CancelDuringFreshIndex_ReturnsInterruptedJson`, `Run_CancelDuringDryRunScan_ReturnsInterruptedJson`, and `Run_CancelBeforeFreshScan_ReturnsInterruptedJson`
  exercise the same in-process cancellation paths used after Ctrl-C/SIGINT wiring, including scan-time cancellation, so interrupted index runs keep returning the canonical JSON error contract.
- `IndexCommandRunnerTests.SymbolExtractionWorker_LegacyEnvironmentHooksAreIgnored_Issue3398`
  launches the isolated symbol worker to prove legacy worker environment variables are ignored. Its callback budget includes process startup and is intentionally wider than ordinary in-process checks so local process load does not turn the legacy-env regression check into a timeout flake (#3863).
- `IndexCommandRunnerTests` and `FileIndexerTests` also cover `CSharpStaticInterfacePrepass` text, raw-byte, chunked raw-token, and streaming file contract probes. Keep byte-array, chunked, and file-level probe coverage aligned so the prepass can avoid whole-file allocation without losing UTF-8 / UTF-16 static-interface contract candidates.
- `IndexWatchRunnerTests.RunCore_CancellationToken_StopsImmediately` and `RunCore_EmitsHumanFriendlyStartStop_WhenJsonDisabled`
  exercise watch-loop startup and shutdown under redirected console output. These tests wait for the watch start line before cancelling and always cancel/drain the dedicated watch task before restoring `Console.Out` / `Console.Error`; do not replace that synchronization with fixed sleeps because full-suite load can delay the long-running task startup.
- `SymbolExtractorTests.Extract_CSharp_InstallScriptFixture_CompletesWithinPracticalBudget`
  is a coarse runaway guard for the real `InstallScriptTests.cs` C# extraction fixture. Its wall-clock budget is intentionally broader than a benchmark so slower or noisy CI hosts do not fail the suite for ordinary variance.
- `SymbolExtractorTests.Extract_JavaScriptLargeExportedObjectLiteralProperties_CompletesWithinPracticalBudget` and `Extract_CSharp_ReferenceExtractorFixture_CompletesWithinPracticalBudget`
  are broad runaway guards for known large symbol-extraction fixtures. Keep their budgets generous enough for full-suite load; tighten them only with focused optimization evidence, not as benchmark thresholds.
- `ReferenceExtractorTests.Extract_CSharpLargePlainCallFile_CompletesWithinPracticalBudget`
  is a broad runaway guard for high-volume C# reference extraction on ordinary call lines. Treat its budget as a regression tripwire, not a benchmark target; keep it wide enough for noisy CI unless a focused optimization change justifies tightening it.
- `IndexCommandRunnerTests.RunBackfillFold_PublishedTrimmedBinary_SerializesSuccessAndErrorJson`
  publishes a trimmed RID-specific CLI and runs whichever entry point the SDK emits (`cdidx.dll` through `dotnet` or the native `cdidx`/`cdidx.exe` apphost). Its publish smoke disables NuGet vulnerability auditing because package advisory validation is covered by the normal build/test workflow's package vulnerability check, not by this runtime serialization test. It is reported as skipped on macOS arm64 while SDK/ILLink can crash before exercising `cdidx` (#2586). Do not assume every SDK/runtime pair writes a `cdidx.dll` into self-contained publish output.
- `QueryCommandRunnerTests.RunPublishedTrimmedCli_SerializesQueryJsonAndSupportsRazorAliases`
  uses one trimmed RID-specific publish output for query JSON coverage and both `cshtml` / `razor` C# Razor language aliases, writes publish-specific lock files under the test's temporary intermediate directory, disables NuGet vulnerability auditing for the publish smoke, and runs whichever `cdidx` entry point the SDK emits so the test does not depend on source-tree lock-file mutation, advisory-feed availability, or a DLL-only publish layout. If `dotnet publish` reaches an SDK/ILLink tool that requires an unavailable `Microsoft.NETCore.App` runtime, the test is reported as skipped with that missing-runtime diagnostic instead of failing before it can exercise `cdidx` (#3571). It is also reported as skipped on macOS arm64 because the SDK/ILLink crash happens before the test reaches `cdidx` (#2586).
- `McpServerTests.cs`
  MCP JSON-RPC behavior and tool outputs.
- `HttpMcpTransportTests.cs`
  HTTP MCP transport behavior, including authentication responses, warm server reuse, concurrent requests, and request logging. Request-log assertions must validate recorded contents without assuming callback order between independently handled HTTP requests.
- `GitHelperTests.cs`
  Git-specific behavior, including worktrees, commit-based updates, and cancellation of git subprocesses. Timeout and cancellation wall-clock assertions should stay below the fake git scripts' natural completion while leaving room for macOS CI scheduling and process-cleanup overhead. Fake git scripts that run after commit-ref validation should echo the verified commit argument for `rev-parse --verify <ref>^{commit}` so timeout tests reach the intended git command.
- `WorkspaceMetadataEnricherTests.cs`
  Workspace freshness and git metadata enrichment behavior.
- `SuggestionStoreTests.cs`
  Local suggestion JSON storage: dedup hashing, persistence, corruption recovery, atomic writes.
- `SourceCodeDetectorTests.cs`
  Source code leak prevention: allowed natural-language inputs vs rejected code blocks (fenced, indented, import runs, etc.).
- `PostExtractionHookTests.cs`
  Post-extraction hook discovery, mutation, diagnostics, callback budgets, and collectible hook assembly cleanup. These tests mutate hook-related environment variables and test-only callback budget state, so the class belongs to the `SQLite pool sensitive` non-parallel collection.
- `GitHubIssueReporterTests.cs`
  GitHub token resolution logic (CDIDX_GITHUB_TOKEN only; generic GITHUB_TOKEN is ignored), outbound code scrubbing, idempotency checks, and rate-limit diagnostics.
- `PackagesLockTests.cs`
  NuGet lock-file guard coverage for direct package references that must remain synchronized across all target frameworks, including the net9.0 compatibility references that keep locked CI restore green.
- `ConcurrencyTests.cs`
  Concurrent read and read-during-write scenarios (WAL mode validation), including the issue #180 bug-catching snapshot-isolation regressions for all three multi-statement reader entry points: (1) `GetStatus` seeds `refs == files * refsPerFile` and asserts every concurrent observation preserves that invariant; (2) `AnalyzeSymbol` seeds one symbol `S` plus matching reference/caller pairs, toggles a second file symmetrically, and asserts `references.Count == callers.Count` across every `inspect`/`analyze_symbol` bundle; (3) `GetRepoMap` seeds a baseline modified timestamp and toggles a newer file, asserting `latest_modified == workspace_latest_modified` across every map call. Each test fails without the DEFERRED-transaction wrap on the matching reader and passes with it.
- `PerformanceTests.cs`
  Bounded CI smoke coverage plus large-scale data benchmarks. `CiPerformanceSmoke_IndexAndSearchSmallFixture_StaysWithinBudget` runs in the default suite, so it is a blocking PR/CI check, but its broad budgets are intended to catch only severe indexing/search regressions rather than act as a benchmark. The 10K+ large-scale tests remain skip-by-default; run them manually with `--filter`.
- `DbRecoveryTests.cs`
  Database corruption recovery and graceful degradation behavior. Filesystem setup failures for `cdidx index` (read-only DB files and unwritable DB parent directories) are covered in `IndexCommandRunnerTests.cs` so they exercise the same CLI JSON/stderr boundary users see.
- `JsonOutputSnapshotTests.cs`, `JsonOutputSnapshotHelper.cs`
  Golden-file regression fixtures for the CLI `--json` output contracts (issue #1548). Each test runs one command (`status`, `search`, `references`, `impact`, `excerpt`) against a deterministic in-memory fixture, normalizes volatile fields (timestamps, absolute paths, commit SHAs, FTS5 scores, SQLite page counts), and diffs against the matching file under `tests/CodeIndex.Tests/golden/`. Renames, removals, reordered arrays, or new keys fail the snapshot so the contract change is forced to land alongside an intentional golden update. See "JSON `--json` output snapshots" below for the update procedure.
- `PropertyBasedParserTests.cs`
  FsCheck-driven property tests for parser-heavy paths called out in issue #1572: `ArgHelper.WantsHelp` and `ProgramRunner.IsProjectPathArg` never throw on arbitrary inputs; `FileIndexer.NormalizePathSeparators` is idempotent under double application; the literal-safe FTS5 sanitizer (`DbReader.SanitizeFtsQuery`) always emits a query that a real in-memory FTS5 virtual table can parse. They complement, not replace, the example-based tests in `ArgHelperTests.cs` / `QueryCommandRunnerTests.cs`.
- `TestProjectHelper.cs`, `RepositoryTestPaths.cs`, `TestConsoleLock.cs`
  Shared test helpers.

## Conventions

- Keep test names descriptive. The current suite mostly uses `Method_Scenario_ExpectedBehavior`.
- Keep tests deterministic. Do not depend on machine-global git config, locale-specific output, or ambient files.
- Prefer small fixtures and explicit assertions over broad snapshot-style checks. The one narrow exception is the `--json` output contract harness (`JsonOutputSnapshotTests`), which pins the full field shape on purpose — see "JSON `--json` output snapshots" below.
- When repeated expected-value construction obscures a boundary contract such as raw bytes vs canonical content, use a narrowly named local helper instead of duplicating the low-level expression at each assertion.
- When a production comment or error string is bilingual, preserve that expectation in tests where it matters.
- If a behavior change is user-visible, update tests, `CHANGELOG.md`, and any affected docs together.

### Shared state and parallelism audit

Use the inventory below before adding or moving a test class:

- SQLite pool resets, direct `SqliteConnection.ClearAllPools()` calls, process current-directory changes, or process-global environment variable mutation: put the class in the `SQLite pool sensitive` non-parallel collection.
- Environment variables: use `EnvironmentVariableScope.Capture(...)` so setup failures and assertion failures restore the original values through one cleanup path.
- `Console.Out` or `Console.Error` replacement: lock `TestConsoleLock.Gate` around the whole capture/swap window.
- Temporary repositories and files: create them through `TestProjectHelper` when practical, and do not depend on user-level git config.
- Long-running or performance-oriented tests: keep them skipped by default or give them broad deterministic budgets; if CI reports them in xUnit long-running diagnostics, first check runner load before tightening thresholds.

## Shared Helpers

### `TestProjectHelper`

Prefer the existing helper before writing new setup code.

- `CreateTempProject(prefix)` creates a unique temp workspace.
- Use `CreateTempProject(prefix)` instead of adding local `Path.GetTempPath()` / `Guid.NewGuid()` directory helpers; keep any local wrapper as a thin prefix-specific delegate only when it preserves existing call-site readability.
- `InitializeGitRepo(projectRoot)` initializes git and sets repo-local `user.name` and `user.email`.
- `CreateProjectDb(projectRoot)` creates `<projectRoot>/.cdidx/codeindex.db`, initializes schema, and seeds `codeindex_meta.indexed_project_root` to match the project root.
- `InsertIndexedFile(...)` inserts a realistic indexed file with content-derived checksum, chunks, symbols, and references, and now passes the file path into Python symbol extraction so `__init__.py`-based re-export tests can exercise qualified package names.
- `RunGit(...)` executes git without shell quoting issues.
- `DeleteDirectory(path)` retries temp-project cleanup and normalizes attributes. To avoid process-global cross-test interference, it only requests SQLite pool cleanup through `SqlitePoolCleanup` as a Windows-specific retry fallback after a delete failure.
- Use `DeleteDirectory(path)` in temp-workspace `finally` / `Dispose` cleanup paths, including tests that intentionally remove the workspace earlier in the scenario.
- Do not reimplement recursive temp-directory cleanup in individual test files; keep local wrappers as thin delegates to `TestProjectHelper.DeleteDirectory` when a shorter call keeps existing tests readable.
- `DeleteFile(path)` retries standalone temp-DB cleanup and uses the same Windows-specific SQLite pool release fallback when pooled handles block deletion.
- DB maintenance test files may keep thin local helpers such as `InitializeEmptyDb`, `ReleaseSqlitePools`, and `DeleteDbFile` when a class owns standalone `.db` files directly; keep those wrappers delegated to `DbContext`, `SqliteConnection.ClearAllPools()`, and `TestProjectHelper.DeleteFile` so pool-release intent remains explicit.
- `SqlitePoolCleanup` centralizes the Windows SQLite pool workaround for tests. Tests that own a temporary SQLite file for their whole lifetime can enter an exclusive owner lease and dispose it idempotently before deleting the file, instead of calling `SqliteConnection.ClearAllPools()` directly from `Dispose`.
- Tests that intentionally call `SqliteConnection.ClearAllPools()`, mutate process-global environment variables, or override the process current directory are grouped into the non-parallel `SQLite pool sensitive` xUnit collection. Add new tests with those hazards to that collection instead of letting them run in parallel with unrelated classes.
- Tests that mutate process-global environment variables should use `EnvironmentVariableScope.Capture(...)` so the original values are restored from a single disposable cleanup path even if setup or assertions fail.

Use these helpers when possible so test behavior stays consistent across files and operating systems.

### `RepositoryTestPaths`

Use `RepositoryTestPaths` for tests that inspect checked-in repository files such as GitHub workflows, `global.json`, or documentation. Prefer `ReadText(...)`, `ReadWorkflow(...)`, and `Combine(...)` over repeating repository-root discovery and `Path.Combine` chains inside individual test classes.

### `TestConsoleLock`

Any test that swaps `Console.Out` or `Console.Error` must lock on `TestConsoleLock.Gate`.

This prevents parallel console redirection from corrupting captured output and avoids flaky assertions in CLI and console UI tests.

Keep the console lock even when a test class already belongs to a non-parallel collection: it documents the process-global console hazard locally and protects shared helper code if the class is ever moved out of that collection later.

## Writing Tests

### Adding coverage

Add or update tests whenever you change:

- CLI argument parsing or output shape
- database schema, migrations, or query semantics
- symbol or reference extraction rules
- indexing skip/update/purge behavior
- MCP tool output or JSON structure
- console/progress behavior
- git/worktree behavior
- workspace freshness or trust metadata

Prefer extending the closest existing `*Tests.cs` file. Create a new test file only when the area does not fit an existing one cleanly.
For boundary tests, use the smallest fixture that still crosses the boundary. If the behavior only needs one page, chunk, cache, or offset overflow, do not scale synthetic data far past that point unless the larger size is part of the contract.

### CLI and console tests

- Capture stdout and stderr explicitly.
- Prefer `ConsoleCapture` for simple stdout/stderr capture, and lock direct console mutations with `TestConsoleLock.Gate`.
- Assert exit codes with `CommandExitCodes`.
- For JSON output, parse it with `JsonDocument` instead of asserting raw strings.

### JSON `--json` output snapshots

`JsonOutputSnapshotTests` and `JsonOutputSnapshotHelper` form a small golden-file harness that catches accidental shape drift in CLI `--json` output (renamed keys, removed keys, reordered top-level arrays, new keys without a contract update). Use them alongside the narrower assertion-style JSON tests in `QueryCommandRunnerTests`; they complement each other rather than replace it.

- Goldens live at `tests/CodeIndex.Tests/golden/<command>.json` and are checked in to the source tree.
- `JsonOutputSnapshotHelper` normalizes volatile fields before comparison: `indexed_at` / `latest_modified` / other timestamp keys → `<TIMESTAMP>`; `git_head` / `indexed_head_commit` / other commit-SHA keys → `<COMMIT_SHA>`; `project_root` → `<PROJECT_ROOT>`; `version` → `<VERSION>`; per-result `score` (BM25, FTS5-implementation-sensitive) → `<SCORE>`; SQLite `page_count` → `<COUNT>`. Per-test temp paths are redacted via the helper's `BuildPathReplacements`.
- When a shape change is intentional, regenerate the matching golden(s) by setting `UPDATE_SNAPSHOTS=1` and re-running only the snapshot tests, then review the diff before committing:

  ```bash
  UPDATE_SNAPSHOTS=1 dotnet test tests/CodeIndex.Tests/CodeIndex.Tests.csproj \
      --filter "FullyQualifiedName~JsonOutputSnapshotTests"
  git diff tests/CodeIndex.Tests/golden/
  ```

- Treat any unintentional snapshot diff as a contract regression: either fix the production code, or update the golden together with the schema/docs/changelog in the same PR.
- Keep fixtures minimal and deterministic. If a new `--json` output joins the contract, add a dedicated snapshot test plus a golden file in the same change.

### Git tests

- Never assume global git identity exists.
- Configure repo-local `user.name` and `user.email` inside the test setup.
- Disable repo-local commit/tag signing for fixture repositories so global signing settings cannot prompt or fail non-interactively.
- Use helper methods or `ProcessStartInfo.ArgumentList`; do not depend on shell-specific quoting behavior.

### Database tests

- Prefer isolated temporary databases per test.
- Initialize schema explicitly when the test needs real DB behavior.
- If the scenario touches read compatibility, verify both the normal path and any fallback or migration path that matters.

## Cross-Platform Rules

- Use `Path.Combine` and relative paths that work on Windows, macOS, and Linux.
- Normalize newline-sensitive fixtures when the assertion is about content rather than platform line endings.
- Be careful with file cleanup on Windows. SQLite connections and file attributes can delay deletion.
- Do not assume shell tools, path separators, or process behavior are identical across platforms.
- If a platform workaround is required, document it in the test and in this guide when it affects future contributors.

## Before You Commit a Test Change

Check the following:

1. The affected production behavior is covered by a focused test.
2. The suite still passes with `dotnet test`.
3. Temporary file, git, and SQLite cleanup paths are robust.
4. Console capture is serialized when needed.
5. This document still matches the current test structure and conventions.

---

<a id="テストガイド"></a>
# テストガイド

このドキュメントは、`cdidx` のテストスイートがどう構成されているか、どのように安全にテストを追加・更新するか、そして挙動やテスト基盤を変更したときに従うべき規約をまとめたものです。

テストコード、テストヘルパー、テストの実行フロー、またはテスト規約を変更した場合は、このドキュメントも同じコミットで更新してください。

## クイックスタート

```bash
dotnet test
dotnet test tests/CodeIndex.Tests/CodeIndex.Tests.csproj
dotnet test tests/CodeIndex.Tests/CodeIndex.Tests.csproj --settings tests/CodeIndex.Tests/CodeIndex.Tests.runsettings --blame-crash --blame-hang --blame-hang-timeout 5m
dotnet test --filter "FullyQualifiedName~GitHelperTests"
```

基本はフルスイートを実行してください。手元での反復中だけ対象を絞り、最後は `dotnet test` で締めます。

## テストスタック

- フレームワーク: xUnit
- メインのテストプロジェクト: `tests/CodeIndex.Tests/CodeIndex.Tests.csproj`
- 対象フレームワーク: `net8.0` と `net9.0`
- 主な直接参照の test-only package: `Microsoft.NET.Test.Sdk`、`xunit`、`xunit.runner.visualstudio`、`coverlet.collector`、`Microsoft.Data.Sqlite`、`FsCheck.Xunit`
- これらの test-only package は `src/CodeIndex` の本番依存ルールとは別であり、runtime 側は引き続き `Microsoft.Data.Sqlite` のみを許容する。
- `FsCheck.Xunit` はランダム生成入力に対する普遍的不変条件（never-throws、idempotence、"出力が downstream consumer で parse 可能" 等）を表明する property-based テスト専用です。例ベースの `[Fact]` / `[Theory]` を置き換えるのではなく補完するもので、普遍量化された主張なら FsCheck、特定の具体ケースが契約なら例ベースという形で使い分けてください。
- テスト並列実行: 独立したテストクラス間ではデフォルトで有効です。SQLite pool の解放、環境変数の変更、カレントディレクトリの上書きのような process-global 状態を触るテストは、明示的な non-parallel collection に入れてください。`Console.Out` / `Console.Error` を差し替えるテストは `TestConsoleLock.Gate` で lock してください。
- CI は `tests/CodeIndex.Tests/CodeIndex.Tests.runsettings` 経由でテストプロジェクトを実行し、VSTest の blame crash / hang 収集、45分のセッションタイムアウト、60秒の xUnit long-running 診断を有効にします。初回失敗時は suite を1回だけ再実行し、再実行で成功した場合は TRX / blame artifact と一緒に `TestResults/flaky-retry.txt` を upload して、その実行を疑わしい flaky run として扱います。TRX telemetry summary と test-result artifact upload は失敗または retry 成功 lane だけで実行し、初回で clean に成功した lane では実行しません。XPlat Code Coverage の収集は `ubuntu-latest` / `net8.0` lane に限定し、すべての OS/framework lane で full suite を実行しつつ collector overhead を避けます。テスト実行は locked restore と Release build の後に `--no-restore --no-build` で走らせます。`ubuntu-latest` / `net8.0` lane では、直前の Release solution build を再利用し、per-framework の test-project build は再実行しません。また、formatting verifier は `make lint` だけを使います。NuGet cache key は全 project file ではなく `packages.lock.json` と `global.json` に基づくため、package 入力の drift は locked restore で検出しつつ、テスト用 project だけの変更では package cache を失効させません。
- CI の初回テスト実行と1回だけの retry は同じ workflow helper 経由にし、logger、blame、coverage、result-directory 引数が drift しないようにしてください。PowerShell helper がテストの exit code を返す場合は、stream されたテスト出力を関数の success stream に載せず、代入で数値の exit code だけを受け取れるようにします。
- package audit、primary-lane build/lint、coverage の収集、coverage artifact upload、publish、build artifact upload は workflow lane-selection output (`primary_lane`) に揃え、後続 step で matrix 条件を重複させないでください。

## テスト構成

テストプロジェクトは、本番コードの責務にかなり近い形で分かれています。

- `ChunkSplitterTests.cs`、`SymbolExtractorTests.cs`、`ReferenceExtractorTests.cs`、`SearchSnippetFormatterTests.cs`、`DbPathResolverTests.cs`、`ConsoleUiTests.cs`
  インメモリ入力中心の、純粋またはほぼ純粋な振る舞いのテスト。
- `SymbolExtractor*Tests.cs` と `ReferenceExtractor*Tests.cs`
  extractor のカバレッジは言語または機能領域ごとの partial test class に分割し、共有 helper は root 側の `SymbolExtractorTests` / `ReferenceExtractorTests` に残します。
- `FileIndexerTests.cs`、`FileIndexerContentLoadingTests.cs`、`FileIndexerTestSupport.cs`
  ファイル走査、言語判定、scan result 言語の再利用、content loading / canonicalization、checksum、レコード構築のテスト。拡張子なし shebang 判定の「先頭物理行 256 byte 上限」、binary/NUL byte 除外、Windows 専用の 260 文字以上 path walker/purge カバレッジも含みます。共有 `FileIndexerTests` helper は `FileIndexerTestSupport.cs` に置きます。
- `DatabaseTests.cs`、`DbReaderTests.cs`
  SQLite スキーマ、書き込み経路、マイグレーション、クエリ挙動のテスト。
- `LegacySchemaMigrationTests.cs`
  エンドツーエンドのアップグレード経路: カラム追加前のレガシー DB を用意し、`TryMigrateForRead` 経由で開いてから NULL になりうるシンボル列を触る read path（outline、シンボル検索、近傍、unused、analyze バンドル）を一通り叩き、#58 / #49 の実機失敗モードを固定する。
- `IndexCommandRunnerTests.cs`、`QueryCommandRunner*Tests.cs`、`ProgramCliTests.cs`、`InstallScriptTests.cs`
  CLI の引数解析、コマンド実行、installer 挙動のテスト。Query command coverage は command family ごとの partial `QueryCommandRunnerTests` class に分割し、共有 console / fixture helper は一箇所に保ちます。`ProgramCliTests.cs` はグローバル引数の解釈や完全な CLI 起動フローのように subprocess 経由で確認すべき Program エントリポイント挙動を扱い、`InstallScriptTests.cs` は `install.sh` を library mode で source した bash snippet を実行して、実ネットワーク install を行わずに release installer の回帰を固定する。
  `InstallScriptTests.RunInstallerSnippet` は bounded timeout を強制し、timeout 時は snippet の process tree を kill するため、installer 回帰は suite を hang させずに captured output 付きで失敗します。
- `CiWorkflowTests.cs`、`ReleaseWorkflowTests.cs`、`ReleaseWorkflowTests.PackageHelpers.cs`
  CI と release workflow の契約テスト。Release workflow の package-normalization ZIP fixture helper は `ReleaseWorkflowTests.PackageHelpers.cs` に置き、workflow assertion が workflow 契約の近くに残るようにします。
- `IndexCommandRunnerTests.Run_CancelDuringFreshIndex_ReturnsInterruptedJson`、`Run_CancelDuringDryRunScan_ReturnsInterruptedJson`、`Run_CancelBeforeFreshScan_ReturnsInterruptedJson`
  Ctrl-C/SIGINT 配線後に使われる in-process cancellation 経路を、scan 中のキャンセルも含めて検証し、interrupted index run が標準の JSON error contract を返し続けることを固定する。
- `IndexCommandRunnerTests.SymbolExtractionWorker_LegacyEnvironmentHooksAreIgnored_Issue3398`
  isolated symbol worker を起動し、legacy worker 環境変数が無視されることを検証します。この callback budget はプロセス起動時間も含むため、通常の in-process チェックより意図的に広く取り、ローカル負荷で legacy-env 回帰テストが timeout flake にならないようにします（#3863）。
- `IndexCommandRunnerTests` と `FileIndexerTests` は `CSharpStaticInterfacePrepass` のテキスト判定、raw-byte、chunked raw-token、streaming file 契約 probe も扱います。prepass がファイル全体の割り当てを避けても UTF-8 / UTF-16 の static-interface 契約候補を落とさないよう、byte-array、chunked、file-level probe のカバレッジを揃えてください。
- `IndexWatchRunnerTests.RunCore_CancellationToken_StopsImmediately` と `RunCore_EmitsHumanFriendlyStartStop_WhenJsonDisabled`
  リダイレクトした console 出力の下で watch loop の起動と停止を検証する。これらのテストは watch start 行を待ってからキャンセルし、`Console.Out` / `Console.Error` を戻す前に専用 watch task を必ず cancel/drain する。full suite の負荷で long-running task の起動が遅れることがあるため、この同期を固定 sleep に戻さないこと。
- `SymbolExtractorTests.Extract_CSharp_InstallScriptFixture_CompletesWithinPracticalBudget`
  は実ファイル `InstallScriptTests.cs` を C# 抽出に通す coarse な runaway guard です。wall-clock の予算は benchmark より意図的に広く取り、遅い / 混雑した CI host で通常の揺れだけにより suite が失敗しないようにしています。
- `SymbolExtractorTests.Extract_JavaScriptLargeExportedObjectLiteralProperties_CompletesWithinPracticalBudget` と `Extract_CSharp_ReferenceExtractorFixture_CompletesWithinPracticalBudget`
  は既知の大きな symbol extraction fixture に対する広めの runaway guard です。full suite の負荷に耐えるよう budget は十分広く保ち、benchmark 閾値としてではなく、焦点を絞った最適化根拠がある場合にだけ締めてください。
- `IndexCommandRunnerTests.RunBackfillFold_PublishedTrimmedBinary_SerializesSuccessAndErrorJson`
  は trimmed な RID 固有 CLI を publish し、SDK が生成した entry point（`dotnet` 経由の `cdidx.dll`、または native の `cdidx`/`cdidx.exe` apphost）を実行します。この publish smoke は NuGet 脆弱性監査を無効化します。package advisory の検証は通常の build/test workflow の package vulnerability check が担い、この runtime serialization テストの責務ではないためです。macOS arm64 では SDK/ILLink が `cdidx` に到達する前にクラッシュし得るため、このテストは skipped として報告されます（#2586）。self-contained publish output に常に `cdidx.dll` が出るとは仮定しないでください。
- `QueryCommandRunnerTests.RunPublishedTrimmedCli_SerializesQueryJsonAndSupportsRazorAliases`
  は 1 つの trimmed RID 固有 publish output で query JSON coverage と `cshtml` / `razor` の C# Razor 言語 alias を検証し、publish 専用の lock file をテストの一時 intermediate directory 配下に書き、publish smoke の NuGet 脆弱性監査を無効化し、SDK が生成した `cdidx` entry point を実行します。source tree の lock file 変更、advisory feed の可用性、DLL 固定の publish layout には依存しません。`dotnet publish` が、利用できない `Microsoft.NETCore.App` runtime を必要とする SDK/ILLink tool に到達した場合は、`cdidx` を実行する前に失敗させるのではなく、その missing-runtime diagnostic を付けて skipped として報告します（#3571）。このテストも macOS arm64 では、`cdidx` に到達する前に SDK/ILLink がクラッシュし得るため skipped として報告されます（#2586）。
- `McpServerTests.cs`
  MCP の JSON-RPC 挙動とツール出力のテスト。
- `HttpMcpTransportTests.cs`
  HTTP MCP transport の挙動。認証レスポンス、warm server reuse、並行リクエスト、リクエストログを含みます。リクエストログの assertion は、独立に処理される HTTP リクエスト間の callback 順序を仮定せず、記録内容を検証してください。
- `GitHelperTests.cs`
  worktree や commit ベース更新、git subprocess の cancellation を含む Git まわりのテスト。Timeout と cancellation の wall-clock assertion は fake git script の自然完了より短く保ちつつ、macOS CI の scheduling や process cleanup の遅れを許容する余裕を持たせます。commit-ref validation 後に使う fake git script は `rev-parse --verify <ref>^{commit}` の検証対象 commit 引数を返し、timeout テストが意図した git command まで到達するようにします。
- `WorkspaceMetadataEnricherTests.cs`
  ワークスペース鮮度と git メタデータ付与のテスト。
- `SuggestionStoreTests.cs`
  ローカル提案JSON蓄積: ハッシュ重複排除、永続化、破損復旧、アトミック書き込み。
- `SourceCodeDetectorTests.cs`
  ソースコード漏洩防止: 許容される自然言語入力 vs 拒否されるコードブロック（フェンス、インデント、import連打等）。
- `PostExtractionHookTests.cs`
  post-extraction hook の discovery、mutation、diagnostics、callback budget、collectible hook assembly cleanup のテスト。hook 関連の環境変数と test-only callback budget 状態を変更するため、このクラスは non-parallel な `SQLite pool sensitive` collection に入れます。
- `GitHubIssueReporterTests.cs`
  GitHubトークン解決ロジック（CDIDX_GITHUB_TOKENのみ。汎用GITHUB_TOKENは無視）、送信前のコード scrubbing、冪等性チェック、rate-limit diagnostics を扱います。
- `PackagesLockTests.cs`
  すべての target framework で同期が必要な direct package reference の NuGet lock-file guard。CI の locked restore を通すための net9.0 compatibility reference も対象です。
- `ConcurrencyTests.cs`
  並行読み取りと書き込み中読み取りシナリオ（WALモード検証）。issue #180 の bug-catching な snapshot 隔離回帰テストを 3 つの multi-statement reader 経路について含む。(1) `GetStatus` は `refs == files * refsPerFile` の seed 不変条件を立て、並行観測が常にこの条件を維持することを要求する。(2) `AnalyzeSymbol` はシンボル `S` に対して reference/caller を対称に 1 対 1 で seed し、もう 1 ファイルを対称に toggle することで `inspect` / `analyze_symbol` bundle の `references.Count == callers.Count` を常に保証する。(3) `GetRepoMap` はベースラインの modified と新しい toggle 対象ファイルを用意し、`latest_modified == workspace_latest_modified` が常に一致することを要求する。各テストは対応する reader の DEFERRED transaction を外すと落ち、戻すと通ることを確認済み。
- `PerformanceTests.cs`
  bounded な CI smoke と大規模データベンチマークを扱います。`CiPerformanceSmoke_IndexAndSearchSmallFixture_StaysWithinBudget` は通常 suite で実行されるため PR / CI の blocking check ですが、benchmark ではなく重大な indexing/search 退行だけを拾う広めの budget を使います。10K+ の大規模テストは引き続きデフォルト Skip で、`--filter` で手動実行します。
- `DbRecoveryTests.cs`
  DB破損からの復旧とグレースフル劣化のテスト。`cdidx index` の filesystem setup failure（read-only DB file や書き込み不可の DB 親ディレクトリ）は、ユーザーが見る CLI JSON/stderr 境界を通すため `IndexCommandRunnerTests.cs` で扱います。
- `JsonOutputSnapshotTests.cs`、`JsonOutputSnapshotHelper.cs`
  CLI の `--json` 出力契約に対するゴールデンファイル回帰フィクスチャ (issue #1548)。各テストは `status` / `search` / `references` / `impact` / `excerpt` を決定的なインメモリ fixture に対して実行し、揺らぐフィールド（timestamp、絶対パス、commit SHA、FTS5 score、SQLite page count など）を正規化したうえで `tests/CodeIndex.Tests/golden/` 配下のファイルと差分比較します。フィールドの rename / 削除 / 並び替え / 新規追加が起きると snapshot が失敗するため、契約変更は意図的な golden 更新と同じ PR で揃えざるを得ません。更新手順は下記「JSON `--json` 出力 snapshot」を参照してください。
- `PropertyBasedParserTests.cs`
  issue #1572 で挙げられたパーサー系経路に対する FsCheck 駆動の property テスト: `ArgHelper.WantsHelp` と `ProgramRunner.IsProjectPathArg` が任意入力で例外を投げないこと、`FileIndexer.NormalizePathSeparators` が二重適用で idempotent であること、literal-safe な FTS5 サニタイザ (`DbReader.SanitizeFtsQuery`) が常にインメモリ FTS5 仮想テーブルで parse 可能なクエリを出力すること。`ArgHelperTests.cs` / `QueryCommandRunnerTests.cs` の例ベーステストを置き換えるものではなく補完します。
- `TestProjectHelper.cs`、`RepositoryTestPaths.cs`、`TestConsoleLock.cs`
  共有テストヘルパー。

## 規約

- テスト名は説明的にする。現在のスイートは `Method_Scenario_ExpectedBehavior` 形式が中心です。
- テストは決定的に保つ。マシン全体の git 設定、ロケール依存出力、外部の残存ファイルに依存しないこと。
- 広いスナップショット風の検証より、小さなフィクスチャと明示的な assertion を優先する。例外は `--json` 出力契約の harness (`JsonOutputSnapshotTests`) で、こちらは意図的にフィールド形状全体を固定します（下記「JSON `--json` 出力 snapshot」参照）。
- raw bytes と canonical content のような境界契約で期待値生成が重複して読みづらくなる場合は、各 assertion に低レベル式を複製せず、契約名が分かる小さな local helper に寄せてください。
- 境界を証明するテストでは、その境界をまたぐ最小の fixture を使う。1 ページ、1 chunk、1 cache、1 offset overflow で十分なら、それ以上に synthetic data を増やさない。ただし、より大きいサイズ自体が契約の一部なら例外です。
- 本番コードのコメントやエラー文字列が英日併記前提なら、重要な箇所ではその期待もテストに反映する。
- ユーザーに見える挙動を変えたら、テストに加えて `CHANGELOG.md` と関連ドキュメントも同じ変更に含める。

### 共有状態と並列実行の監査

テストクラスを追加または移動する前に、次の一覧を確認してください。

- SQLite pool reset、`SqliteConnection.ClearAllPools()` の直接呼び出し、プロセスの current directory 変更、process-global な環境変数変更: クラスを non-parallel な `SQLite pool sensitive` collection に入れる。
- 環境変数: `EnvironmentVariableScope.Capture(...)` を使い、setup failure や assertion failure でも単一の cleanup 経路で元の値に戻す。
- `Console.Out` / `Console.Error` の差し替え: capture / swap 期間全体を `TestConsoleLock.Gate` で lock する。
- 一時 repo / file: 可能な限り `TestProjectHelper` 経由で作り、user-level の git config に依存しない。
- 長時間または performance 系テスト: デフォルト skip にするか、決定的で十分広い budget を与える。CI の xUnit long-running 診断に出た場合は、閾値を締める前に runner 負荷を確認する。

## 共通ヘルパー

### `TestProjectHelper`

新しいセットアップコードを書く前に、既存ヘルパーを優先してください。

- `CreateTempProject(prefix)` は一意な一時ワークスペースを作成します。
- 独自に `Path.GetTempPath()` / `Guid.NewGuid()` を組み合わせた directory helper を増やさず、`CreateTempProject(prefix)` を使ってください。既存呼び出し側の読みやすさを保つ場合だけ、local wrapper は prefix 固有の薄い委譲に留めます。
- `InitializeGitRepo(projectRoot)` は git を初期化し、repo-local の `user.name` と `user.email` を設定します。
- `CreateProjectDb(projectRoot)` は `<projectRoot>/.cdidx/codeindex.db` を作成し、スキーマを初期化したうえで `codeindex_meta.indexed_project_root` に project root を書き込みます。
- `InsertIndexedFile(...)` は内容由来の checksum、chunks、symbols、references を含む現実的なインデックス済みファイルを挿入し、Python の symbol extraction には file path も渡すため、`__init__.py` ベースの再エクスポートテストで package 修飾名を扱えます。
- `RunGit(...)` は shell の quoting 問題に依存せず git を実行します。
- `DeleteDirectory(path)` は temp project cleanup のリトライと属性正規化を扱います。プロセス全体への干渉を避けるため、SQLite pool の解放は Windows で削除に失敗した場合のリトライ時だけに限定します。
- 一時 workspace の `finally` / `Dispose` cleanup では、そのテストシナリオ内で workspace を意図的に先に削除する場合も含めて、`DeleteDirectory(path)` を使ってください。
- 個別のテストファイルで再帰的な一時ディレクトリ cleanup を再実装しないでください。短い呼び出し名で既存テストの読みやすさを保ちたい場合も、ローカル wrapper は `TestProjectHelper.DeleteDirectory` への薄い委譲に留めます。
- `DeleteFile(path)` は standalone な temp DB cleanup をリトライし、pooled handle が削除を妨げる場合は同じ Windows 向け SQLite pool 解放フォールバックを使います。
- DB maintenance 系のテストファイルが standalone な `.db` ファイルを直接所有する場合は、`InitializeEmptyDb`、`ReleaseSqlitePools`、`DeleteDbFile` のような薄い local helper を置いて構いません。ただし wrapper は `DbContext`、`SqliteConnection.ClearAllPools()`、`TestProjectHelper.DeleteFile` へ委譲し、pool 解放の意図が見える名前にしてください。
- `SqlitePoolCleanup` は Windows 向け SQLite pool workaround を集約します。テストの生存期間中ずっと一時 SQLite ファイルを所有するテストは、`SqliteConnection.ClearAllPools()` を直接呼ぶ代わりに exclusive owner lease に入り、削除前に冪等に dispose できます。
- `SqliteConnection.ClearAllPools()` を意図的に呼ぶテスト、process-global な環境変数を変更するテスト、プロセスのカレントディレクトリを上書きするテストは、xUnit の non-parallel collection `SQLite pool sensitive` にまとめます。これらのハザードを持つ新しいテストも、この collection に入れて無関係なクラスとの並列実行を避けてください。
- Process-global な環境変数を変更するテストでは `EnvironmentVariableScope.Capture(...)` を使い、setup や assertion が失敗しても単一の disposable cleanup 経路で元の値へ戻してください。

テスト挙動をファイル間・OS間で揃えるため、可能な限りこれらを使ってください。

### `RepositoryTestPaths`

GitHub workflow、`global.json`、ドキュメントなど、checked-in されたリポジトリ内ファイルを検査するテストでは `RepositoryTestPaths` を使ってください。各テストクラス内で repository-root 探索や `Path.Combine` の連鎖を繰り返す代わりに、`ReadText(...)`、`ReadWorkflow(...)`、`Combine(...)` を優先します。

### `TestConsoleLock`

`Console.Out` や `Console.Error` を差し替えるテストは、必ず `TestConsoleLock.Gate` で lock してください。

これにより、並列実行時のコンソール出力取り込みの衝突を防ぎ、CLI や console UI テストの flaky な失敗を避けられます。

テストクラス自体が non-parallel collection に入っている場合でも、console lock は残してください。process-global な console ハザードを各テストの近くで明示でき、将来そのクラスやヘルパーが collection 外に移った場合の保険にもなります。

## テストの書き方

### 追加・更新が必要なケース

次を変更したら、テストを追加または更新してください。

- CLI の引数解析や出力形式
- DB スキーマ、マイグレーション、クエリ意味論
- シンボル抽出や参照抽出のルール
- インデックスの skip / update / purge 挙動
- MCP ツールの出力や JSON 構造
- コンソールやプログレス表示
- Git / worktree 挙動
- ワークスペース鮮度や trust メタデータ

基本は最も近い既存の `*Tests.cs` を拡張してください。既存ファイルに自然に収まらない場合だけ新しいテストファイルを作ります。

### CLI / コンソール系テスト

- stdout と stderr を明示的にキャプチャする。
- 単純な stdout/stderr capture では `ConsoleCapture` を優先し、直接コンソールを差し替える場合は `TestConsoleLock.Gate` で直列化する。
- 終了コードは `CommandExitCodes` で検証する。
- JSON 出力は生文字列比較ではなく `JsonDocument` で解析して検証する。

### JSON `--json` 出力 snapshot

`JsonOutputSnapshotTests` と `JsonOutputSnapshotHelper` は CLI `--json` 出力の形状ドリフト（キーの rename、削除、トップレベル配列の並び替え、契約更新を伴わない新規キー）を検出する小さなゴールデンファイル harness です。既存の `QueryCommandRunnerTests` 内の絞り込みアサーション形式の JSON テストを置き換えるものではなく、補完するものとして併用してください。

- ゴールデンファイルは `tests/CodeIndex.Tests/golden/<command>.json` に置かれ、ソースツリーに checked in されています。
- `JsonOutputSnapshotHelper` は比較前に揺らぐフィールドを正規化します: `indexed_at` / `latest_modified` などの timestamp 系キー → `<TIMESTAMP>`、`git_head` / `indexed_head_commit` などの commit SHA 系キー → `<COMMIT_SHA>`、`project_root` → `<PROJECT_ROOT>`、`version` → `<VERSION>`、各 result の `score`（BM25、FTS5 実装依存）→ `<SCORE>`、SQLite の `page_count` → `<COUNT>`。テストごとの temp パスは helper の `BuildPathReplacements` で除去されます。
- 形状の変更が意図的な場合は、`UPDATE_SNAPSHOTS=1` を設定して snapshot テストだけを再実行し、生成された差分をレビューしてからコミットしてください:

  ```bash
  UPDATE_SNAPSHOTS=1 dotnet test tests/CodeIndex.Tests/CodeIndex.Tests.csproj \
      --filter "FullyQualifiedName~JsonOutputSnapshotTests"
  git diff tests/CodeIndex.Tests/golden/
  ```

- 意図しない snapshot 差分は契約の回帰として扱ってください: 本番コードを直すか、ゴールデンを schema / docs / changelog と同じ PR で更新するかのどちらかです。
- フィクスチャは最小・決定的に保ってください。新しい `--json` 出力が契約に加わる場合は、同じ変更内で対応する snapshot テストとゴールデンファイルを追加します。

### Git 系テスト

- global の git identity がある前提にしない。
- テストセットアップ内で repo-local の `user.name` と `user.email` を設定する。
- fixture リポジトリでは repo-local の commit/tag signing を無効化し、global signing 設定が非対話実行でプロンプトや失敗を起こさないようにする。
- shell 依存の quoting ではなく、ヘルパーや `ProcessStartInfo.ArgumentList` を使う。

### DB 系テスト

- テストごとに分離された一時 DB を優先する。
- 実DB挙動を検証する場合はスキーマ初期化を明示する。
- 読み取り互換性に触れる変更なら、通常経路に加えて必要な fallback / migration 経路も検証する。

## クロスプラットフォームのルール

- Windows、macOS、Linux すべてで成立するよう `Path.Combine` と相対パスを使う。
- 改行自体が論点でない場合は、改行依存のフィクスチャを正規化して扱う。
- Windows では SQLite 接続やファイル属性の影響で削除が遅れることがあるため、後片付けを甘く見ない。
- shell ツール、パス区切り、プロセス挙動が各 OS で同じとは仮定しない。
- OS 固有の回避策が必要なら、将来の保守者のためにテスト内コメントとこのガイドの両方へ理由を残す。

## テスト変更をコミットする前の確認

次を確認してください。

1. 変更した本番挙動に対して、焦点の合ったテストがある。
2. `dotnet test` が通る。
3. 一時ファイル、git、SQLite の後片付けが堅牢である。
4. 必要なコンソールキャプチャが直列化されている。
5. このドキュメントが現在のテスト構成と規約を正しく反映している。

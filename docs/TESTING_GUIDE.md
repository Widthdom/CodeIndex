# Testing Guide

> **[日本語版はこちら / Japanese version](#テストガイド)**

This document explains how the `cdidx` test suite is organized, how to add or update tests safely, and which conventions to follow when the behavior or test infrastructure changes.

If you change test code, test helpers, test execution flow, or testing conventions, update this document in the same commit.

## Quick Start

```bash
dotnet test
dotnet test tests/CodeIndex.Tests/CodeIndex.Tests.csproj
dotnet test --filter "FullyQualifiedName~GitHelperTests"
```

Use the full suite by default. Use targeted filters only while iterating locally, then finish with `dotnet test`.

## Test Stack

- Framework: xUnit
- Target framework: `net8.0`
- Main test project: `tests/CodeIndex.Tests/CodeIndex.Tests.csproj`
- Common direct test-only packages: `Microsoft.NET.Test.Sdk`, `xunit`, `xunit.runner.visualstudio`, `coverlet.collector`, `Microsoft.Data.Sqlite`
- These test-only packages are separate from the production dependency rule in `src/CodeIndex`, which still allows only `Microsoft.Data.Sqlite` at runtime.
- Test parallelism: enabled by default across independent test classes. Tests that touch process-global state such as SQLite pool resets, environment variables, or current-directory overrides must use an explicit non-parallel collection, and tests that swap `Console.Out` / `Console.Error` must lock on `TestConsoleLock.Gate`.

## Test Layout

The test project mirrors the production areas closely.

- `ChunkSplitterTests.cs`, `SymbolExtractorTests.cs`, `ReferenceExtractorTests.cs`, `SearchSnippetFormatterTests.cs`, `DbPathResolverTests.cs`, `ConsoleUiTests.cs`
  Pure or mostly pure behavior tests with in-memory inputs.
- `FileIndexerTests.cs`
  File scanning, language detection, and record-building behavior.
- `DatabaseTests.cs`, `DbReaderTests.cs`
  SQLite schema, write paths, migrations, and query behavior.
- `LegacySchemaMigrationTests.cs`
  End-to-end upgrade path: seeds a pre-column legacy DB, opens it through `TryMigrateForRead`, and exercises the read paths that touch nullable symbol ordinals (outline, symbol search, nearby, unused, analyze bundle) to lock in the real-world failure mode behind #58 / #49.
- `IndexCommandRunnerTests.cs`, `QueryCommandRunnerTests.cs`, `ProgramCliTests.cs`, `InstallScriptTests.cs`
  CLI parsing, command execution, and installer behavior. `ProgramCliTests.cs` covers top-level entrypoint behavior that must be exercised through a subprocess, while `InstallScriptTests.cs` runs focused bash snippets against `install.sh` in library mode to lock in release-installer regressions without performing real network installs.
- `McpServerTests.cs`
  MCP JSON-RPC behavior and tool outputs.
- `GitHelperTests.cs`
  Git-specific behavior, including worktrees and commit-based updates.
- `WorkspaceMetadataEnricherTests.cs`
  Workspace freshness and git metadata enrichment behavior.
- `SuggestionStoreTests.cs`
  Local suggestion JSON storage: dedup hashing, persistence, corruption recovery, atomic writes.
- `SourceCodeDetectorTests.cs`
  Source code leak prevention: allowed natural-language inputs vs rejected code blocks (fenced, indented, import runs, etc.).
- `GitHubIssueReporterTests.cs`
  GitHub token resolution logic (CDIDX_GITHUB_TOKEN only; generic GITHUB_TOKEN is ignored).
- `ConcurrencyTests.cs`
  Concurrent read and read-during-write scenarios (WAL mode validation), including the issue #180 bug-catching snapshot-isolation regressions for all three multi-statement reader entry points: (1) `GetStatus` seeds `refs == files * refsPerFile` and asserts every concurrent observation preserves that invariant; (2) `AnalyzeSymbol` seeds one symbol `S` plus matching reference/caller pairs, toggles a second file symmetrically, and asserts `references.Count == callers.Count` across every `inspect`/`analyze_symbol` bundle; (3) `GetRepoMap` seeds a baseline modified timestamp and toggles a newer file, asserting `latest_modified == workspace_latest_modified` across every map call. Each test fails without the DEFERRED-transaction wrap on the matching reader and passes with it.
- `PerformanceTests.cs`
  Large-scale data benchmarks (10K+ files). Skip-by-default; run manually with `--filter`.
- `DbRecoveryTests.cs`
  Database corruption recovery and graceful degradation behavior.
- `TestProjectHelper.cs`, `TestConsoleLock.cs`
  Shared test helpers.

## Conventions

- Keep test names descriptive. The current suite mostly uses `Method_Scenario_ExpectedBehavior`.
- Keep tests deterministic. Do not depend on machine-global git config, locale-specific output, or ambient files.
- Prefer small fixtures and explicit assertions over broad snapshot-style checks.
- When a production comment or error string is bilingual, preserve that expectation in tests where it matters.
- If a behavior change is user-visible, update tests, `CHANGELOG.md`, and any affected docs together.

## Shared Helpers

### `TestProjectHelper`

Prefer the existing helper before writing new setup code.

- `CreateTempProject(prefix)` creates a unique temp workspace.
- `InitializeGitRepo(projectRoot)` initializes git and sets repo-local `user.name` and `user.email`.
- `CreateProjectDb(projectRoot)` creates `<projectRoot>/.cdidx/codeindex.db`, initializes schema, and seeds `codeindex_meta.indexed_project_root` to match the project root.
- `InsertIndexedFile(...)` inserts a realistic indexed file with content-derived checksum, chunks, symbols, and references, and now passes the file path into Python symbol extraction so `__init__.py`-based re-export tests can exercise qualified package names.
- `RunGit(...)` executes git without shell quoting issues.
- `DeleteDirectory(path)` retries temp-project cleanup and normalizes attributes. To avoid process-global cross-test interference, it only clears SQLite pools as a Windows-specific retry fallback after a delete failure.
- `DeleteFile(path)` retries standalone temp-DB cleanup and uses the same Windows-specific SQLite pool release fallback when pooled handles block deletion.
- Tests that intentionally call `SqliteConnection.ClearAllPools()`, mutate process-global environment variables, or override the process current directory are grouped into the non-parallel `SQLite pool sensitive` xUnit collection. Add new tests with those hazards to that collection instead of letting them run in parallel with unrelated classes.

Use these helpers when possible so test behavior stays consistent across files and operating systems.

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
- Lock console mutations with `TestConsoleLock.Gate`.
- Assert exit codes with `CommandExitCodes`.
- For JSON output, parse it with `JsonDocument` instead of asserting raw strings.

### Git tests

- Never assume global git identity exists.
- Configure repo-local `user.name` and `user.email` inside the test setup.
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
dotnet test --filter "FullyQualifiedName~GitHelperTests"
```

基本はフルスイートを実行してください。手元での反復中だけ対象を絞り、最後は `dotnet test` で締めます。

## テストスタック

- フレームワーク: xUnit
- 対象フレームワーク: `net8.0`
- メインのテストプロジェクト: `tests/CodeIndex.Tests/CodeIndex.Tests.csproj`
- 主な直接参照の test-only package: `Microsoft.NET.Test.Sdk`、`xunit`、`xunit.runner.visualstudio`、`coverlet.collector`、`Microsoft.Data.Sqlite`
- これらの test-only package は `src/CodeIndex` の本番依存ルールとは別であり、runtime 側は引き続き `Microsoft.Data.Sqlite` のみを許容する。
- テスト並列実行: 独立したテストクラス間ではデフォルトで有効です。SQLite pool の解放、環境変数の変更、カレントディレクトリの上書きのような process-global 状態を触るテストは、明示的な non-parallel collection に入れてください。`Console.Out` / `Console.Error` を差し替えるテストは `TestConsoleLock.Gate` で lock してください。

## テスト構成

テストプロジェクトは、本番コードの責務にかなり近い形で分かれています。

- `ChunkSplitterTests.cs`、`SymbolExtractorTests.cs`、`ReferenceExtractorTests.cs`、`SearchSnippetFormatterTests.cs`、`DbPathResolverTests.cs`、`ConsoleUiTests.cs`
  インメモリ入力中心の、純粋またはほぼ純粋な振る舞いのテスト。
- `FileIndexerTests.cs`
  ファイル走査、言語判定、レコード構築のテスト。
- `DatabaseTests.cs`、`DbReaderTests.cs`
  SQLite スキーマ、書き込み経路、マイグレーション、クエリ挙動のテスト。
- `LegacySchemaMigrationTests.cs`
  エンドツーエンドのアップグレード経路: カラム追加前のレガシー DB を用意し、`TryMigrateForRead` 経由で開いてから NULL になりうるシンボル列を触る read path（outline、シンボル検索、近傍、unused、analyze バンドル）を一通り叩き、#58 / #49 の実機失敗モードを固定する。
- `IndexCommandRunnerTests.cs`、`QueryCommandRunnerTests.cs`、`ProgramCliTests.cs`、`InstallScriptTests.cs`
  CLI の引数解析、コマンド実行、installer 挙動のテスト。`ProgramCliTests.cs` はグローバル引数の解釈や完全な CLI 起動フローのように subprocess 経由で確認すべき Program エントリポイント挙動を扱い、`InstallScriptTests.cs` は `install.sh` を library mode で source した bash snippet を実行して、実ネットワーク install を行わずに release installer の回帰を固定する。
- `McpServerTests.cs`
  MCP の JSON-RPC 挙動とツール出力のテスト。
- `GitHelperTests.cs`
  worktree や commit ベース更新を含む Git まわりのテスト。
- `WorkspaceMetadataEnricherTests.cs`
  ワークスペース鮮度と git メタデータ付与のテスト。
- `SuggestionStoreTests.cs`
  ローカル提案JSON蓄積: ハッシュ重複排除、永続化、破損復旧、アトミック書き込み。
- `SourceCodeDetectorTests.cs`
  ソースコード漏洩防止: 許容される自然言語入力 vs 拒否されるコードブロック（フェンス、インデント、import連打等）。
- `GitHubIssueReporterTests.cs`
  GitHubトークン解決ロジック（CDIDX_GITHUB_TOKENのみ。汎用GITHUB_TOKENは無視）。
- `ConcurrencyTests.cs`
  並行読み取りと書き込み中読み取りシナリオ（WALモード検証）。issue #180 の bug-catching な snapshot 隔離回帰テストを 3 つの multi-statement reader 経路について含む。(1) `GetStatus` は `refs == files * refsPerFile` の seed 不変条件を立て、並行観測が常にこの条件を維持することを要求する。(2) `AnalyzeSymbol` はシンボル `S` に対して reference/caller を対称に 1 対 1 で seed し、もう 1 ファイルを対称に toggle することで `inspect` / `analyze_symbol` bundle の `references.Count == callers.Count` を常に保証する。(3) `GetRepoMap` はベースラインの modified と新しい toggle 対象ファイルを用意し、`latest_modified == workspace_latest_modified` が常に一致することを要求する。各テストは対応する reader の DEFERRED transaction を外すと落ち、戻すと通ることを確認済み。
- `PerformanceTests.cs`
  大規模データベンチマーク（10K+ファイル）。デフォルトSkip。`--filter` で手動実行。
- `DbRecoveryTests.cs`
  DB破損からの復旧とグレースフル劣化のテスト。
- `TestProjectHelper.cs`、`TestConsoleLock.cs`
  共有テストヘルパー。

## 規約

- テスト名は説明的にする。現在のスイートは `Method_Scenario_ExpectedBehavior` 形式が中心です。
- テストは決定的に保つ。マシン全体の git 設定、ロケール依存出力、外部の残存ファイルに依存しないこと。
- 広いスナップショット風の検証より、小さなフィクスチャと明示的な assertion を優先する。
- 境界を証明するテストでは、その境界をまたぐ最小の fixture を使う。1 ページ、1 chunk、1 cache、1 offset overflow で十分なら、それ以上に synthetic data を増やさない。ただし、より大きいサイズ自体が契約の一部なら例外です。
- 本番コードのコメントやエラー文字列が英日併記前提なら、重要な箇所ではその期待もテストに反映する。
- ユーザーに見える挙動を変えたら、テストに加えて `CHANGELOG.md` と関連ドキュメントも同じ変更に含める。

## 共通ヘルパー

### `TestProjectHelper`

新しいセットアップコードを書く前に、既存ヘルパーを優先してください。

- `CreateTempProject(prefix)` は一意な一時ワークスペースを作成します。
- `InitializeGitRepo(projectRoot)` は git を初期化し、repo-local の `user.name` と `user.email` を設定します。
- `CreateProjectDb(projectRoot)` は `<projectRoot>/.cdidx/codeindex.db` を作成し、スキーマを初期化したうえで `codeindex_meta.indexed_project_root` に project root を書き込みます。
- `InsertIndexedFile(...)` は内容由来の checksum、chunks、symbols、references を含む現実的なインデックス済みファイルを挿入し、Python の symbol extraction には file path も渡すため、`__init__.py` ベースの再エクスポートテストで package 修飾名を扱えます。
- `RunGit(...)` は shell の quoting 問題に依存せず git を実行します。
- `DeleteDirectory(path)` は temp project cleanup のリトライと属性正規化を扱います。プロセス全体への干渉を避けるため、SQLite pool の解放は Windows で削除に失敗した場合のリトライ時だけに限定します。
- `DeleteFile(path)` は standalone な temp DB cleanup をリトライし、pooled handle が削除を妨げる場合は同じ Windows 向け SQLite pool 解放フォールバックを使います。
- `SqliteConnection.ClearAllPools()` を意図的に呼ぶテスト、process-global な環境変数を変更するテスト、プロセスのカレントディレクトリを上書きするテストは、xUnit の non-parallel collection `SQLite pool sensitive` にまとめます。これらのハザードを持つ新しいテストも、この collection に入れて無関係なクラスとの並列実行を避けてください。

テスト挙動をファイル間・OS間で揃えるため、可能な限りこれらを使ってください。

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
- コンソール差し替えは `TestConsoleLock.Gate` で直列化する。
- 終了コードは `CommandExitCodes` で検証する。
- JSON 出力は生文字列比較ではなく `JsonDocument` で解析して検証する。

### Git 系テスト

- global の git identity がある前提にしない。
- テストセットアップ内で repo-local の `user.name` と `user.email` を設定する。
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

# Test and Documentation Maintenance Plan

> **[日本語版はこちら / Japanese version](#テストとドキュメントの保守計画)**

This plan tracks the issue #4307 backlog for oversized test files, active
skipped tests, and large documentation files. It is a planning document only:
each follow-up PR should move one fixture family or documentation boundary at a
time, preserve test intent, and keep release-note generation conventions intact.

## Baseline

### Oversized Test Files

| File | Current pressure | Split boundary |
|---|---|---|
| `tests/CodeIndex.Tests/ReferenceExtractorTests.cs` | 14,735 lines of cross-language extraction fixtures and shared setup. | Split by language or reference family, keeping `ExtractSymbolsAndReferences(...)` and shared graph helpers on the root partial. |
| `tests/CodeIndex.Tests/SymbolExtractorTests.cs` | 14,217 lines of language fixtures, scanner edge cases, and practical-budget guards. | Split by language family, build-file format, or scanner feature while keeping semantic assertion helpers centralized. |
| `tests/CodeIndex.Tests/QueryCommandRunnerReferencesTests.cs` | 12,051 lines of references/goto/impact fixtures and CLI output assertions. | Split command-family output modes from shared seeded database helpers. |
| `tests/CodeIndex.Tests/QueryCommandRunnerSearchTests.cs` | 10,309 lines of search ranking, recipe metadata, query parsing, and output coverage. | Split search ranking/dedup, audit recipe metadata, parser behavior, and JSON/human output fixtures. |
| `tests/CodeIndex.Tests/McpServerToolsCallTests.cs` | 9,490 lines of MCP tool-call behavior over many tool families. | Split query, index, status/diagnostic, and maintenance tool-call suites while keeping protocol envelope helpers shared. |

### Oversized Documentation Files

| File | Current pressure | Split boundary |
|---|---|---|
| `CHANGELOG.md` | 11,189 lines of generated release notes. | Keep the generated historical changelog intact; continue using `changelog.d/unreleased/*` fragments and release tooling instead of hand-editing release entries. |
| `USER_GUIDE.md` | 5,508 lines of user workflows and command examples. | Keep onboarding, command discovery, and stable overview content in the top-level guide; move deep worked examples only behind linked docs pages. |
| `DEVELOPER_GUIDE.md` | 4,380 lines of architecture, workflow, release, and maintenance guidance. | Keep the top-level architecture map and workflow links; move detailed maintenance procedures to focused docs only when the guide keeps a clear pointer. |

## Issue #4307 Snapshot

Issue #4307 recorded these local dogfood baselines on 2026-07-07:

| Signal | Count |
|---|---:|
| `phrase-risk-patterns` recipe matches | 81 results / 32 files |
| `active-test-skip-assignment` recipe matches | 27 results / 17 files |
| `task-result-property-review` recipe matches | 46 results / 11 files |
| `unsafe-keyword-code` recipe matches | 4 results / 1 file |
| `version-project-config` recipe matches | 3 results / 2 files |
| `obsolete-production-code` recipe matches | 1 result / 1 file |
| `async-void`, `throw-new-exception-code`, `readalltext-call-site`, `todo-production-comment` | 0 results |

The test-isolation inventory from the same snapshot is:

| Fixture or marker | Count |
|---|---:|
| `EnvironmentVariableScope` | 150 |
| `tests` path `Collection` usage | 281 |
| `Retry` | 38 |
| `Skip` | 186 |
| `[Fact]` | 3,159 |
| `[Theory]` | 245 |
| `RepositoryTestPaths` | 25 |
| `TemporaryDirectory` | 19 |
| `ProcessRunner` | 7 |
| `TempRoot` | 3 |
| `CollectionDefinition` | 1 |
| `TestOutputHelper` | 0 |

## Active Skip Classification

The exact-substring `Skip =` inventory from issue #4307 found 35 matches across
23 files. The broad FTS query found 382 matches across 126 files, but that count
includes comments, fixtures, string literals, and documentation. Use the
exact-substring query as the active-governance baseline, then inspect every
candidate before changing a skip.

| Category | Current examples | Governance |
|---|---|---|
| Target-framework or platform-specific | `ProductionCliFactAttribute`, `ProductionCliTheoryAttribute`, `ExternalProcessFactAttribute`, `ExternalProcessTheoryAttribute`, and practical-budget guards that run only on the production `net8.0` target. | Keep the shared skip-reason constants and their contract tests beside the attributes. Do not duplicate literal reasons at call sites. |
| External process or toolchain limitation | Published/trimmed CLI and installer paths that can be reported as skipped when SDK/ILLink/runtime availability prevents the test from reaching `cdidx` (#2586, #3571). | Keep the tracking issue in the reason or surrounding guide text, and prefer narrowing the environment guard over disabling broader coverage. |
| Performance-only or manual benchmark | `PerformanceTests` large-scale checks such as `Insert10KFiles` and extractor stress tests with manual `dotnet test --filter ...` instructions. | Keep them skipped by default, keep the command in the reason, and do not treat them as required PR gates. |
| Temporary investigation skip | Skips with owner, expiration, and `blocked by #NNNN` metadata. | A temporary skip must cite a tracking issue, owner, and expiry date. If that metadata is missing, remove the skip or open the tracking issue before adding it. |
| Intentionally disabled coverage | No standing class of untracked intentional disables should exist. | If coverage must remain disabled, create an issue first and use the temporary-skip format until the replacement coverage lands. |

Before adding or retaining a skip, run:

```bash
dotnet ./src/CodeIndex/bin/Debug/net8.0/cdidx.dll search --exact-substring --query "Skip =" --path tests/CodeIndex.Tests --limit 80 --json
```

## Split Sequence

1. `SymbolExtractorTests.cs`
   - Move language-specific fixtures into partials that match production
     extractor boundaries.
   - Keep broad `*CompletesWithinPracticalBudget` guards on the production
     `net8.0` target unless a target-specific regression is being tested.

2. `ReferenceExtractorTests.cs`
   - Split by language or graph-edge family.
   - Keep symbol-then-reference setup centralized so fixture moves do not copy
     extractor orchestration.

3. `QueryCommandRunnerSearchTests.cs`
   - Split ranking/deduplication, audit recipe metadata, parser behavior, and
     output-shape tests into focused partials.
   - Keep JSON contract changes paired with snapshot or shape assertions.

4. `QueryCommandRunnerReferencesTests.cs`
   - Split references, goto/definition, impact, and output-mode assertions by
     command family.
   - Keep seeded DB helpers near the root partial so command-family files do
     not fork fixture construction.

5. `McpServerToolsCallTests.cs`
   - Split by MCP tool family while keeping request/response envelope helpers
     shared.
   - Preserve schema names, annotations, stability markers, and error envelopes
     in every move.

6. Large documentation files
   - Keep `CHANGELOG.md` generated from fragments and release tooling.
   - Move deep `USER_GUIDE.md` and `DEVELOPER_GUIDE.md` examples only after the
     top-level guide keeps navigation to the new page in both English and
     Japanese sections.
   - Preserve release-note wording conventions and fragment categories when a
     documentation split changes user-visible guidance.

## Review Gates

Each follow-up PR should include:

- a narrow diff that moves one test fixture family or one documentation
  boundary;
- no test semantic changes unless the PR explicitly states and verifies them;
- a refreshed `Skip =` inventory when adding, removing, or reclassifying skips;
- bilingual documentation updates when a bilingual guide is touched;
- a changelog fragment when the split changes docs, workflows, CLI/MCP output,
  or user-visible behavior;
- an adversarial review focused on lost coverage, stale docs, and release-note
  drift.

---

<a id="テストとドキュメントの保守計画"></a>
# テストとドキュメントの保守計画

この計画は、issue #4307 で扱う巨大テストファイル、有効な skip 付きテスト、
巨大ドキュメントの backlog を追跡するためのものです。これは計画文書であり、
後続 PR は fixture family またはドキュメント境界を 1 つずつ移動し、
テスト意図を保ち、リリースノート生成の規約を壊さないようにしてください。

## ベースライン

### 巨大テストファイル

| ファイル | 現在の圧力 | 分割境界 |
|---|---|---|
| `tests/CodeIndex.Tests/ReferenceExtractorTests.cs` | 言語横断の extraction fixture と共有 setup が 14,735 行ある。 | 言語または reference family ごとに分割し、`ExtractSymbolsAndReferences(...)` と共有 graph helper は root partial に残す。 |
| `tests/CodeIndex.Tests/SymbolExtractorTests.cs` | language fixture、scanner edge case、practical-budget guard が 14,217 行ある。 | language family、build-file 形式、scanner feature ごとに分割し、意味を表す assertion helper は集約しておく。 |
| `tests/CodeIndex.Tests/QueryCommandRunnerReferencesTests.cs` | references/goto/impact fixture と CLI output assertion が 12,051 行ある。 | command family の出力モードと共有 seeded database helper を分ける。 |
| `tests/CodeIndex.Tests/QueryCommandRunnerSearchTests.cs` | search ranking、recipe metadata、query parsing、output coverage が 10,309 行ある。 | search ranking/dedup、audit recipe metadata、parser behavior、JSON/human output fixture を分ける。 |
| `tests/CodeIndex.Tests/McpServerToolsCallTests.cs` | 多くの tool family にまたがる MCP tool-call behavior が 9,490 行ある。 | query、index、status/diagnostic、maintenance tool-call suite に分け、protocol envelope helper は共有する。 |

### 巨大ドキュメントファイル

| ファイル | 現在の圧力 | 分割境界 |
|---|---|---|
| `CHANGELOG.md` | 生成済みリリースノートが 11,189 行ある。 | 過去分の changelog は生成物として保ち、release entry を手で編集せず `changelog.d/unreleased/*` fragment と release tooling を使い続ける。 |
| `USER_GUIDE.md` | user workflow と command example が 5,508 行ある。 | onboarding、command discovery、安定した overview は top-level guide に残し、深い worked example だけを linked docs page に移す。 |
| `DEVELOPER_GUIDE.md` | architecture、workflow、release、maintenance guidance が 4,380 行ある。 | top-level architecture map と workflow link は残し、詳細な maintenance procedure は guide から明確に辿れる場合だけ focused docs に移す。 |

## Issue #4307 のスナップショット

issue #4307 では、2026-07-07 のローカル dogfood baseline として次が記録されています。

| シグナル | 件数 |
|---|---:|
| `phrase-risk-patterns` recipe match | 81 件 / 32 ファイル |
| `active-test-skip-assignment` recipe match | 27 件 / 17 ファイル |
| `task-result-property-review` recipe match | 46 件 / 11 ファイル |
| `unsafe-keyword-code` recipe match | 4 件 / 1 ファイル |
| `version-project-config` recipe match | 3 件 / 2 ファイル |
| `obsolete-production-code` recipe match | 1 件 / 1 ファイル |
| `async-void`、`throw-new-exception-code`、`readalltext-call-site`、`todo-production-comment` | 0 件 |

同じ snapshot のテスト分離棚卸しは次のとおりです。

| fixture または marker | 件数 |
|---|---:|
| `EnvironmentVariableScope` | 150 |
| `tests` path の `Collection` usage | 281 |
| `Retry` | 38 |
| `Skip` | 186 |
| `[Fact]` | 3,159 |
| `[Theory]` | 245 |
| `RepositoryTestPaths` | 25 |
| `TemporaryDirectory` | 19 |
| `ProcessRunner` | 7 |
| `TempRoot` | 3 |
| `CollectionDefinition` | 1 |
| `TestOutputHelper` | 0 |

## 有効な Skip の分類

issue #4307 の exact-substring `Skip =` inventory は 23 ファイルに 35 件でした。
広い FTS query では 126 ファイルに 382 件ありますが、コメント、fixture、
文字列リテラル、ドキュメントも含まれます。skip governance の baseline には
exact-substring query を使い、変更前に候補を 1 件ずつ確認してください。

| 分類 | 現在の例 | ガバナンス |
|---|---|---|
| 対象フレームワークまたはプラットフォーム固有 | `ProductionCliFactAttribute`、`ProductionCliTheoryAttribute`、`ExternalProcessFactAttribute`、`ExternalProcessTheoryAttribute`、production `net8.0` target だけで走る practical-budget guard。 | 共有 skip-reason constant とその contract test を attribute の近くに置く。call site に literal reason を重複させない。 |
| 外部プロセスまたはツールチェーン制約 | SDK/ILLink/runtime の可用性により `cdidx` に到達する前に skipped として報告されうる published/trimmed CLI と installer 経路（#2586、#3571）。 | reason または周辺 guide text に tracking issue を残し、広い coverage を止めるより environment guard を狭める。 |
| 性能専用または手動ベンチマーク | `PerformanceTests` の `Insert10KFiles` などの大規模チェックと、手動 `dotnet test --filter ...` 指示付き extractor stress test。 | 既定では skipped のままにし、reason に実行コマンドを残し、PR 必須 gate として扱わない。 |
| 一時調査用 skip | owner、expiration、`blocked by #NNNN` metadata を持つ skip。 | temporary skip は tracking issue、owner、expiry date を必ず持つ。metadata がなければ skip を削除するか、追加前に tracking issue を起票する。 |
| 意図的な無効化 | 未追跡の intentional disable を常設カテゴリとして持たない。 | coverage を無効化したままにする必要がある場合は先に issue を作り、replacement coverage が入るまで temporary-skip format を使う。 |

skip を追加または維持する前に次を実行してください。

```bash
dotnet ./src/CodeIndex/bin/Debug/net8.0/cdidx.dll search --exact-substring --query "Skip =" --path tests/CodeIndex.Tests --limit 80 --json
```

## 分割順序

1. `SymbolExtractorTests.cs`
   - production extractor boundary と揃う partial に language-specific fixture を移す。
   - target-specific regression を検証する場合を除き、広い `*CompletesWithinPracticalBudget`
     guard は production `net8.0` target に残す。

2. `ReferenceExtractorTests.cs`
   - 言語または graph-edge family ごとに分割する。
   - fixture の移動で extractor orchestration が複製されないよう、
     symbol-then-reference setup は集約したままにする。

3. `QueryCommandRunnerSearchTests.cs`
   - ranking/deduplication、audit recipe metadata、parser behavior、output-shape test を
     focused partial に分ける。
   - JSON contract の変更は snapshot または shape assertion と同じ変更に入れる。

4. `QueryCommandRunnerReferencesTests.cs`
   - references、goto/definition、impact、output-mode assertion を command family ごとに分ける。
   - command-family ファイルごとに fixture construction が分岐しないよう、
     seeded DB helper は root partial の近くに保つ。

5. `McpServerToolsCallTests.cs`
   - MCP tool family ごとに分割し、request/response envelope helper は共有する。
   - すべての move で schema name、annotation、stability marker、error envelope を維持する。

6. 巨大ドキュメントファイル
   - `CHANGELOG.md` は fragment と release tooling から生成する。
   - `USER_GUIDE.md` と `DEVELOPER_GUIDE.md` の深い example は、top-level guide の英語版と
     日本語版の両方から新しいページへ辿れる場合だけ移す。
   - documentation split が user-visible guidance を変える場合は、release-note wording convention
     と fragment category を維持する。

## レビューゲート

各後続 PR には次を含めてください。

- test fixture family または documentation boundary を 1 つだけ動かす狭い diff;
- 明示して検証する場合を除き、test semantic change を含めないこと;
- skip を追加、削除、再分類する場合の更新済み `Skip =` inventory;
- bilingual guide を触った場合の英語版と日本語版の両方の更新;
- docs、workflow、CLI/MCP output、user-visible behavior が変わる場合の changelog fragment;
- lost coverage、stale docs、release-note drift に焦点を当てた adversarial review。

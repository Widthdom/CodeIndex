---
category: changed
affected:
  - src/CodeIndex/Cli/IndexCommandRunner.Parse.cs
  - src/CodeIndex/Cli/ConsoleUi.cs
  - src/CodeIndex/Cli/CliFlagSchema.cs
  - src/CodeIndex/Cli/EnvironmentVariableInventory.cs
  - src/CodeIndex/Database/DbWriter.References.cs
  - src/CodeIndex/Cli/IndexCommandRunner.FullScan.cs
  - src/CodeIndex/Cli/IndexCommandRunner.Update.cs
  - tests/CodeIndex.Tests/IndexCommandRunnerFullScanTests.cs
  - tests/CodeIndex.Tests/IndexCommandRunnerTests.cs
  - USER_GUIDE.md
  - DEVELOPER_GUIDE.md
  - TESTING_GUIDE.md
---

## English

- **Large-index diagnostics now identify expensive phases** — `index --json --memory-trace` reports C# prepass, extraction, reference-graph, text-index, finalization, and full-scan commit boundaries, while partial updates report the corresponding shared phases.
- **Automatic full-scan parallelism avoids high-core contention** — the default worker count now follows the CPU count up to eight instead of sixteen, while `--parallelism` and `CDIDX_INDEX_PARALLELISM` still accept explicit values through sixteen.
- **Reference resolution consolidates repeated graph lookups** — candidate counts, unique target families, target IDs, and stable keys are now derived by one aggregate per reference instead of repeated correlated subqueries across every supported language.

## 日本語

- **大規模 index の高コスト phase を診断しやすくしました** — `index --json --memory-trace` は C# prepass、extraction、reference graph、text index、finalize、full-scan commit の各境界を返し、partial update でも対応する共通 phase を返します。
- **高コア環境で automatic full-scan の競合を抑えました** — 既定 worker 数は CPU 数に追従しつつ上限を16から8へ下げました。`--parallelism` と `CDIDX_INDEX_PARALLELISM` の明示値は引き続き16まで指定できます。
- **reference resolution の重複 graph lookup を集約しました** — 全対応言語で candidate count、unique target family、target ID、stable key を reference ごとに1回の aggregate から導出し、相関 subquery の繰り返しをなくしました。

---
category: changed
affected:
  - src/CodeIndex/Cli/IndexCommandRunner.FullScan.cs
  - src/CodeIndex/Cli/IndexCommandRunner.Update.cs
  - tests/CodeIndex.Tests/IndexCommandRunnerFullScanTests.cs
  - USER_GUIDE.md
  - TESTING_GUIDE.md
---

## English

- **Large-index diagnostics now identify expensive phases** — `index --json --memory-trace` reports C# prepass, extraction, reference-graph, text-index, finalization, and full-scan commit boundaries, while partial updates report the corresponding shared phases.

## 日本語

- **大規模 index の高コスト phase を診断しやすくしました** — `index --json --memory-trace` は C# prepass、extraction、reference graph、text index、finalize、full-scan commit の各境界を返し、partial update でも対応する共通 phase を返します。

---
category: changed
affected:
  - src/CodeIndex/Cli/IndexCommandRunner.cs
  - src/CodeIndex/Cli/IndexCommandRunner.WorkItems.cs
  - src/CodeIndex/Cli/IndexCommandRunner.FullScan.ExtractionWorkers.cs
  - src/CodeIndex/Cli/IndexCommandRunner.FullScan.FilePersistence.cs
  - tests/CodeIndex.Tests/IndexCommandRunnerTests.cs
  - tests/CodeIndex.Tests/IndexCommandRunnerFullScanTests.cs
  - tests/CodeIndex.Tests/IndexCommandRunnerInitialFullIndexPerformanceTests.cs
  - tests/CodeIndex.Tests/QueryCommandRunnerPartialGroupingIssue4566Tests.cs
  - TESTING_GUIDE.md
  - DEVELOPER_GUIDE.md
---

## English

- **Parallel full scans reuse symbol preparation completed by extraction workers** — the single persistence consumer now receives each file's applied family-scope state and completed C# source observation instead of resolving and observing them again. Explicit completion flags distinguish a legitimately null scope from an unprepared capped payload while preserving generated-code suppression, serial hook mutation, cancellation, and scoped-update behavior.

## 日本語

- **parallel full scanがextraction workerで完了したsymbol preparationを再利用するようになりました** — single persistence consumerはfileごとの適用済みfamily-scope状態と完了済みC# source observationを受け取り、同じ解決と観測を繰り返しません。明示的な完了flagにより、正当にnullのscopeと未準備のcap payloadを区別しつつ、generated-code suppression、serial hook mutation、cancellation、scoped updateの挙動を維持します。

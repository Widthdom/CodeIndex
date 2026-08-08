---
category: changed
affected:
  - src/CodeIndex/Cli/CliFlagSchema.cs
  - src/CodeIndex/Cli/EnvironmentVariableInventory.cs
  - src/CodeIndex/Cli/IndexCommandRunner.cs
  - src/CodeIndex/Cli/IndexCommandRunner.FullScan.Interruption.cs
  - src/CodeIndex/Cli/IndexCommandRunner.Update.FileLoop.cs
  - src/CodeIndex/Cli/IndexCommandRunner.Update.FilePersistence.cs
  - src/CodeIndex/Cli/IndexCommandRunner.Update.ParallelExtraction.cs
  - src/CodeIndex/Cli/IndexCommandRunner.WorkItems.cs
  - src/CodeIndex/Indexer/Hooks/PostExtractionHooks.cs
  - tests/CodeIndex.Tests/IndexCommandRunnerUpdateTests.cs
  - DEVELOPER_GUIDE.md
  - TESTING_GUIDE.md
---

## English

- **Large authoritative C# scoped updates now extract files concurrently within bounded ordered windows** — eligible static-interface workspace updates reuse a fixed worker pool, cap each window at twice the worker count, and keep SQLite persistence, hooks, readiness, byte accounting, and progress on one target-ordered consumer. Three file-stat barriers, nullable language re-detection, serial probe/hook/filter fallbacks, phase-aware cancellation and stall handling, and source-contract candidate ordering preserve the existing update semantics while reducing extraction time on large C# workspaces.

## 日本語

- **大規模な authoritative C# scoped update が、上限付き ordered window 内で file extraction を並列実行するようになりました** — static-interface workspace の対象 update は固定 worker pool を再利用し、各 window を worker 数の2倍までに制限します。SQLite persistence、hook、readiness、byte accounting、progress は target 順の single consumer に維持します。3段階の file-stat barrier、nullable language re-detection、probe / hook / filter の serial fallback、phase-aware な cancellation / stall 処理、source-contract candidate の順序制御により既存の update semantics を保ちながら、大規模 C# workspace の extraction 時間を短縮します。

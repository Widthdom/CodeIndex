---
category: changed
affected:
  - src/CodeIndex/Database/DbWriter.CSharpMetadataTargets.cs
  - src/CodeIndex/Cli/IndexCommandRunner.FullScan.cs
  - src/CodeIndex/Cli/IndexCommandRunner.Update.cs
  - src/CodeIndex/Mcp/McpToolHandlers.Indexing.cs
  - tests/CodeIndex.Tests/DbWriterCSharpMetadataTargetPerformanceTests.cs
  - TESTING_GUIDE.md
---

## English

- **C# metadata-target propagation now scales linearly through deep inheritance chains** — indexing builds reverse dependency edges once and visits each newly resolved target through a work queue, avoiding repeated repository-wide class scans on reverse-ordered codebases. Stable reruns also skip unchanged metadata-target row writes while cancellation remains wired through CLI and MCP finalization.

## 日本語

- **C# metadata-target の伝播が深い継承 chain でも線形に処理されるようになりました** — indexing は逆依存 edge を一度だけ構築し、新たに解決した target を work queue で処理するため、逆順に保存された巨大 codebase で class 全体を繰り返し走査しません。安定 rerun では未変更の metadata-target row write も省略し、CLI / MCP finalization のキャンセル伝播は維持します。

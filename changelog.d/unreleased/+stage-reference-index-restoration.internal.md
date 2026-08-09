---
category: internal
affected:
  - src/CodeIndex/Database/ReferenceSecondaryIndexSql.cs
  - src/CodeIndex/Database/ReferenceSecondaryIndexBulkLoadGuard.cs
  - src/CodeIndex/Database/DbWriter.ReferenceGraphRefreshScope.cs
  - src/CodeIndex/Database/DbWriter.References.cs
  - src/CodeIndex/Cli/IndexCommandRunner.FullScan.cs
  - src/CodeIndex/Cli/IndexCommandRunner.Update.cs
  - src/CodeIndex/Mcp/McpToolHandlers.Indexing.Execution.cs
  - tests/CodeIndex.Tests/ReferenceSecondaryIndexBulkLoadGuardTests.cs
  - tests/CodeIndex.Tests/IndexCommandRunnerReferenceIndexBulkLoadTests.cs
  - tests/CodeIndex.Tests/McpServerToolsCallTests.cs
  - tests/CodeIndex.Tests/DatabaseTests.cs
  - DEVELOPER_GUIDE.md
  - TESTING_GUIDE.md
---

## English

- **Reference-index restoration is staged around graph finalization** — large CLI and MCP bulk loads now keep query-only reference indexes absent through identity resolution, restore only three reverse-edge indexes for mutual-recursion evaluation, and rebuild the remaining query indexes afterwards. Active scoped graph refreshes are promoted to full plans before deferral, while cancellation, rollback, recovery, heartbeat, and SQLite `changes()` contracts remain intact.

## 日本語

- **reference index の復元を graph finalization の前後に段階化しました** — 大規模な CLI / MCP bulk load は identity resolution 中も query-only reference index を外したままにし、mutual-recursion 評価には reverse-edge 用3本だけを復元して、残りの query index はその後に再構築します。active な scoped graph refresh は遅延前に full plan へ昇格し、cancellation、rollback、recovery、heartbeat、SQLite `changes()` の各契約を維持します。

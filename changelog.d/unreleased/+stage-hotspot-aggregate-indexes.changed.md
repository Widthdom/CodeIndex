---
category: changed
affected:
  - src/CodeIndex/Database/HotspotReferenceAggregateSql.cs
  - src/CodeIndex/Database/DbWriter.HotspotReferenceRefreshScope.cs
  - src/CodeIndex/Database/DbContext.SchemaInitialization.cs
  - src/CodeIndex/Database/DbContext.ReadMigrations.cs
  - src/CodeIndex/Cli/IndexCommandRunner.FullScan.cs
  - src/CodeIndex/Cli/IndexCommandRunner.Update.cs
  - src/CodeIndex/Mcp/McpToolHandlers.Indexing.Execution.cs
---

## English

- **Large indexing runs rebuild hotspot aggregate query indexes once instead of maintaining four trees row by row** — Full scans, rebuilds, high-churn scoped updates, and MCP bulk indexing now stage the cross-language hotspot indexes inside the existing aggregate refresh transaction. Small updates retain their indexes, and success, cancellation, and failure preserve the same ready state and final schema.

## 日本語

- **大規模 indexing では hotspot aggregate の query index 4本を行ごとに保守せず、最後に1回だけ再構築するようになりました** — full scan、rebuild、高 churn の scoped update、MCP bulk indexing は、既存の aggregate refresh transaction 内で全言語共通の hotspot index を遅延します。小規模 update は index を維持し、成功・cancellation・失敗の各経路で同じ ready state と最終 schema を保ちます。

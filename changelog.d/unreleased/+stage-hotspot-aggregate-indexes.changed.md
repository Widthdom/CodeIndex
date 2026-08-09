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

- **Large indexing runs rebuild hotspot aggregate query indexes once instead of maintaining four trees row by row** — Full scans, rebuilds, high-churn scoped updates, and MCP bulk indexing now stage the cross-language hotspot indexes inside the existing aggregate refresh transaction when dirty aggregate rows cover most of the table, including an empty pre-refresh aggregate during fresh/rebuild work. A bounded row probe keeps the indexes for small or highly skewed updates, and success, cancellation, and failure preserve the same ready state and final schema.

## 日本語

- **大規模 indexing では hotspot aggregate の query index 4本を行ごとに保守せず、最後に1回だけ再構築するようになりました** — full scan、rebuild、高 churn の scoped update、MCP bulk indexing は、dirty aggregate rowがtableの大半を占める場合に既存のaggregate refresh transaction内で全言語共通のhotspot indexを遅延し、fresh / rebuildでrefresh前aggregateが空の場合も対象にします。bounded row probeにより小規模または偏りの大きいupdateではindexを維持し、成功・cancellation・失敗の各経路で同じready stateと最終schemaを保ちます。

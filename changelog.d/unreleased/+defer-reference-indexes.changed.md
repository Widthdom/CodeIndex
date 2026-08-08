---
category: changed
affected:
  - src/CodeIndex/Database/ReferenceSecondaryIndexBulkLoadGuard.cs
  - src/CodeIndex/Database/ReferenceSecondaryIndexSql.cs
  - src/CodeIndex/Cli/IndexCommandRunner.FullScan.cs
  - src/CodeIndex/Mcp/McpToolHandlers.Indexing.Execution.cs
---

## English

- **Fresh and rebuilt indexes persist large reference sets without maintaining every query index row by row** — CLI indexing now defers reference query and graph secondary indexes inside its outer transaction and restores them once before graph finalization. Empty-database MCP indexing uses the same optimization with recoverable cleanup, while maintenance indexes, rollback safety, read repair, and the final database schema remain unchanged.

## 日本語

- **fresh / rebuild index が、大量の reference を query index ごとに逐次更新せず永続化するようになりました** — CLI indexing は外側 transaction 内で reference query / graph 用 secondary index を遅延し、graph finalization 前に1回だけ復元します。空 database から始める MCP indexing も recoverable cleanup 付きで同じ最適化を使い、保守用 index、rollback safety、read repair、最終 database schema は従来どおり維持します。

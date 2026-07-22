---
category: changed
affected:
  - src/CodeIndex/Database/DbWriter.HotspotReferenceRefreshScope.cs
  - src/CodeIndex/Database/DbWriter.Transactions.cs
  - src/CodeIndex/Database/DbWriter.ReadyFlags.cs
  - src/CodeIndex/Database/DbWriter.References.cs
  - src/CodeIndex/Database/DbWriter.Files.cs
  - src/CodeIndex/Database/DbWriter.FileCleanup.cs
  - src/CodeIndex/Database/DbWriter.FilePurge.cs
  - src/CodeIndex/Database/DbWriter.UnsupportedReferences.cs
  - src/CodeIndex/Database/HotspotReferenceAggregateSql.cs
  - src/CodeIndex/Cli/IndexCommandRunner.FullScan.cs
  - src/CodeIndex/Cli/IndexCommandRunner.Update.cs
  - src/CodeIndex/Mcp/McpToolHandlers.Indexing.cs
  - src/CodeIndex/Database/DbWriter.TypeScriptAugmentations.cs
  - tests/CodeIndex.Tests/DatabaseTests.cs
  - tests/CodeIndex.Tests/HotspotReferenceAggregateTests.cs
  - tests/CodeIndex.Tests/IndexCommandRunnerFullScanTests.cs
  - tests/CodeIndex.Tests/McpServerToolsCallTests.cs
  - TESTING_GUIDE.md
---

## English

- **Large indexing batches now refresh hotspot reference aggregates once instead of once per file** — Full scan, scoped update, MCP indexing, and TypeScript augmentation rebuild collect dirty file IDs behind transaction/savepoint checkpoints, discard failed-file work, and run one set-based aggregate refresh at finalization. Readiness is still rechecked and cleared inside every mutation transaction; refresh and conditional trust restoration share one transaction, so cancellation remains safely degraded and a previously unready database is never promoted.

## 日本語

- **巨大なindexing batchでhotspot reference aggregateをfileごとではなく最後に1回だけrefreshするようになりました** — full scan、scoped update、MCP indexing、TypeScript augmentation rebuildはtransaction/savepoint checkpointの背後でdirty file IDを集約し、失敗fileの作業を破棄して、finalization時に1回のset-based aggregate refreshを実行します。readinessは各mutation transaction内で引き続き再確認・clearされ、refreshと条件付きtrust復元は同一transactionで行われるため、cancel時は安全にdegradedのまま、従来unreadyだったdatabaseを誤って昇格させません。

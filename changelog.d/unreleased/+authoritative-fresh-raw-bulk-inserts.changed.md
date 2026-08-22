---
category: changed
affected:
  - src/CodeIndex/Cli/IndexCommandRunner.FullScan.cs
  - src/CodeIndex/Cli/IndexCommandRunner.FullScan.FilePersistence.cs
  - src/CodeIndex/Database/DbWriter.AuthoritativeFreshBulkInsert.cs
  - src/CodeIndex/Database/DbWriter.ChunkSymbolBatches.cs
  - src/CodeIndex/Database/DbWriter.Issues.cs
  - src/CodeIndex/Database/DbWriter.References.cs
  - tests/CodeIndex.Tests/AuthoritativeFreshRawBulkInsertTests.cs
  - tests/CodeIndex.Tests/PerformanceTests.cs
  - DEVELOPER_GUIDE.md
  - TESTING_GUIDE.md
---

## English

- **Authoritative empty CLI scans now bind their highest-volume DONE-only inserts through SQLitePCLRaw** — chunk, symbol, new-file issue, and atomic fresh-reference batches reuse a bounded native statement cache during extraction, removing row-count-proportional Microsoft.Data.Sqlite command and parameter allocation while retaining the existing 32-parameter statement/tail boundaries, transaction atomicity, row-skip replay, cancellation, hooks, Unicode/NULL semantics, and provider-owned connection lifetime. Reference-line and file `RETURNING` materialization plus rebuild, incremental, replacement, symbols-only, race-fallback, MCP, and public-writer paths continue to use Microsoft.Data.Sqlite.

## 日本語

- **authoritative empty CLI scanの高頻度なDONE-only insertをSQLitePCLRawでbindするようになりました** — extraction中にchunk、symbol、new-file issue、atomic fresh-reference batchがbounded native statement cacheを再利用し、既存の32 parameter statement / tail境界、transaction atomicity、row-skip replay、cancellation、hook、Unicode / NULL semantics、provider所有connection lifetimeを保ったまま、row数に比例するMicrosoft.Data.Sqlite command / parameter allocationを除きます。reference-line / fileの`RETURNING` materialization、およびrebuild、incremental、replacement、symbols-only、race fallback、MCP、public writer経路は引き続きMicrosoft.Data.Sqliteを使います。

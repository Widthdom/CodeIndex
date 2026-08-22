---
category: changed
affected:
  - src/CodeIndex/Cli/IndexCommandRunner.FullScan.cs
  - src/CodeIndex/Cli/IndexCommandRunner.FullScan.FilePersistence.cs
  - src/CodeIndex/Database/DbWriter.AuthoritativeFreshBulkInsert.cs
  - src/CodeIndex/Database/DbWriter.AuthoritativeFreshReturningInsert.cs
  - src/CodeIndex/Database/DbWriter.ChunkSymbolBatches.cs
  - src/CodeIndex/Database/DbWriter.Files.cs
  - src/CodeIndex/Database/DbWriter.Issues.cs
  - src/CodeIndex/Database/DbWriter.References.cs
  - tests/CodeIndex.Tests/AuthoritativeFreshRawBulkInsertTests.cs
  - tests/CodeIndex.Tests/IndexCommandRunnerFullScanTests.cs
  - tests/CodeIndex.Tests/IndexCommandRunnerInitialFullIndexPerformanceTests.cs
  - tests/CodeIndex.Tests/PerformanceTests.cs
  - DEVELOPER_GUIDE.md
  - TESTING_GUIDE.md
---

## English

- **Authoritative empty CLI scans now bind their complete fresh-file persistence sequence through SQLitePCLRaw** — file and fresh reference-line `RETURNING` inserts join chunk, symbol, new-file issue, and atomic fresh-reference batches in a bounded native statement cache during extraction, removing row-count-proportional Microsoft.Data.Sqlite command, reader, and parameter allocation while retaining the existing 32-parameter statement/tail boundaries, transaction atomicity, row-skip replay, cancellation, hooks, date/Unicode/NULL semantics, and provider-owned connection lifetime. Returned IDs and input ordinals are fully buffered and validated before publication; malformed, incomplete, duplicate, failed, or cancelled result streams discard the statement and roll back through the per-file savepoint. Rebuild, incremental, replacement, symbols-only, race-fallback, MCP, and public-writer paths continue to use Microsoft.Data.Sqlite.

## 日本語

- **authoritative empty CLI scanのfresh-file永続化全体をSQLitePCLRawでbindするようになりました** — file / fresh reference-lineの`RETURNING` insertをchunk、symbol、new-file issue、atomic fresh-reference batchと同じbounded native statement cacheへ加え、既存の32 parameter statement / tail境界、transaction atomicity、row-skip replay、cancellation、hook、date / Unicode / NULL semantics、provider所有connection lifetimeを保ったまま、row数に比例するMicrosoft.Data.Sqlite command / reader / parameter allocationを除きます。返却IDとinput ordinalは公開前に全件buffer / validationし、不正、欠落、重複、失敗、cancelされたresult streamではstatementを破棄してfile単位SAVEPOINTからrollbackします。rebuild、incremental、replacement、symbols-only、race fallback、MCP、public writer経路は引き続きMicrosoft.Data.Sqliteを使います。

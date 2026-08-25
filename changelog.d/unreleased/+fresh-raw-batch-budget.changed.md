---
category: changed
affected:
  - src/CodeIndex/Database/DbWriter.BatchSql.cs
  - src/CodeIndex/Database/DbWriter.ChunkSymbolBatches.cs
  - src/CodeIndex/Database/DbWriter.Issues.cs
  - src/CodeIndex/Database/DbWriter.References.cs
  - src/CodeIndex/Database/DbWriter.AuthoritativeFreshBulkInsert.cs
  - src/CodeIndex/Database/DbWriter.AuthoritativeFreshReturningInsert.cs
  - tests/CodeIndex.Tests/AuthoritativeFreshRawBulkInsertTests.cs
  - TESTING_GUIDE.md
  - DEVELOPER_GUIDE.md
---

## English

- **Fresh full indexes persist every language in substantially larger native SQLite batches** — the authoritative empty-database path now uses a dedicated 512-parameter budget for chunks, symbols, issues, reference lines, and references instead of inheriting the provider-oriented 32-parameter ceiling. RETURNING validation remains bounded and atomic while duplicate-ID checks are linear, and incremental, rebuild, MCP, and public writer paths retain their existing provider contracts.

## 日本語

- **空DBの初回フルインデックスが、全言語を大幅に大きいnative SQLite batchで永続化するようになりました** — authoritativeな空DB経路ではchunk、symbol、issue、reference line、referenceにprovider向け32 parameter上限を継承せず、専用の512 parameter budgetを使います。RETURNING検証のbounded性とatomic性を保ったままduplicate ID検査を線形化し、incremental、rebuild、MCP、public writer経路のprovider契約は変更しません。

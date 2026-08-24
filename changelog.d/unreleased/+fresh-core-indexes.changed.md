---
category: changed
affected:
  - src/CodeIndex/Cli/IndexCommandRunner.FullScan.cs
  - src/CodeIndex/Database/CoreSecondaryIndexBulkLoadGuard.cs
  - src/CodeIndex/Database/CoreSecondaryIndexSql.cs
  - src/CodeIndex/Database/DbContext.ReadMigrations.cs
  - src/CodeIndex/Database/DbContext.SchemaInitialization.cs
  - src/CodeIndex/Database/DbWriter.cs
  - tests/CodeIndex.Tests/CoreSecondaryIndexBulkLoadGuardTests.cs
  - tests/CodeIndex.Tests/IndexCommandRunnerReferenceIndexBulkLoadTests.cs
  - DEVELOPER_GUIDE.md
  - TESTING_GUIDE.md
---

## English

- **Fresh full indexes build core secondary indexes after bulk persistence** — authoritative empty-database CLI runs now defer 22 language-neutral secondary indexes on files, chunks, issues, and symbols, then build each B-tree once after native inserts finalize and before graph reads begin. UNIQUE constraints and the per-file symbol index required by fresh-reference source lookup remain active, while cancellation rolls the schema back atomically; rebuild, incremental, claim-race fallback, and MCP writes retain their indexes.

## 日本語

- **初回full indexでcore secondary indexをbulk永続化後に構築するようになりました** — authoritativeな空databaseのCLI runではfiles、chunks、issues、symbolsの言語共通secondary index 22本を遅延し、native insertのfinalize後かつgraph read前に各B-treeを1回だけ構築します。UNIQUE constraintとfresh-referenceのsource lookupに必要なfile単位symbol indexは維持し、cancel時はschemaをatomicにrollbackします。rebuild、incremental、claim-race fallback、MCPのwriteはindexを維持します。

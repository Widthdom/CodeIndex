---
category: changed
affected:
  - src/CodeIndex/Cli/IndexCommandRunner.FullScan.cs
  - src/CodeIndex/Database/DbContext.ReadMigrations.cs
  - src/CodeIndex/Database/DbContext.SchemaInitialization.cs
  - src/CodeIndex/Database/ReferenceSecondaryIndexBulkLoadGuard.cs
  - src/CodeIndex/Database/ReferenceSecondaryIndexSql.cs
  - tests/CodeIndex.Tests/IndexCommandRunnerFullScanTests.cs
  - tests/CodeIndex.Tests/IndexCommandRunnerReferenceIndexBulkLoadTests.cs
  - tests/CodeIndex.Tests/McpServerToolsCallTests.cs
  - tests/CodeIndex.Tests/ReferenceSecondaryIndexBulkLoadGuardTests.cs
  - tests/CodeIndex.Tests/DatabaseTests.cs
  - DEVELOPER_GUIDE.md
---

## English

- **Authoritative empty-database CLI indexing now defers two reference-persistence indexes during the raw load** — after transaction-local fresh-ownership revalidation, the initial full scan omits the reference-line foreign-key and file/line lookup indexes until candidate or graph work begins, then restores both inside the same outer transaction. Rebuilds, existing-database updates, raced fresh claims, and MCP indexing keep both indexes available, while cancellation and failure retain atomic schema rollback.

## 日本語

- **authoritativeな空DB初回CLI indexで、raw load中のreference永続化index 2本を遅延するようになりました** — transaction-localなfresh ownership再確認後、初回full scanはreference-line foreign-key用とfile/line lookup用のindexをcandidateまたはgraph処理の開始まで外し、同じouter transaction内で2本とも復元します。rebuild、既存DB update、競合で失効したfresh claim、MCP indexingでは常時維持し、cancellationや失敗時もschema rollbackの原子性を保ちます。

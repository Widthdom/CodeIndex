---
category: changed
affected:
  - src/CodeIndex/Database/DbWriter.cs
  - src/CodeIndex/Database/DbWriter.References.cs
  - src/CodeIndex/Cli/IndexCommandRunner.FullScan.cs
  - src/CodeIndex/Cli/IndexCommandRunner.Update.cs
  - src/CodeIndex/Mcp/McpToolHandlers.Indexing.cs
  - src/CodeIndex/Database/DbWriter.TypeScriptAugmentations.cs
  - tests/CodeIndex.Tests/DatabaseTests.cs
  - tests/CodeIndex.Tests/IndexCommandRunnerFullScanTests.cs
  - tests/CodeIndex.Tests/McpServerToolsCallTests.cs
  - tests/CodeIndex.Tests/PerformanceTests.cs
  - TESTING_GUIDE.md
---

## English

- **Large reference files no longer create a nested SAVEPOINT for every 71 references during production indexing** — Full scan, scoped update, MCP indexing, and TypeScript augmentation rebuild now explicitly delegate reference-batch atomicity to their existing file transaction. Public writer APIs retain their #1518 per-batch rollback contract, while guarded internal paths roll back all reference and context rows with the caller's file transaction. A controlled repository-scale model fixes the reduction from 5,009 reference-batch scopes to zero across 321,352 references in 856 files.

## 日本語

- **production indexingで巨大な参照fileが71参照ごとにnested SAVEPOINTを作成しなくなりました** — full scan、scoped update、MCP indexing、TypeScript augmentation rebuildは、既存file transactionへreference batchの原子性を明示委譲します。public writer APIは#1518のbatch単位rollback契約を維持し、guard付きinternal pathは呼出元file transactionによって全reference/context rowをrollbackします。自己規模modelは856 files・321,352 refsでreference batch scopeが5,009回から0回になることを固定します。

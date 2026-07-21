---
category: changed
affected:
  - src/CodeIndex/Database/DbWriter.cs
  - src/CodeIndex/Database/DbWriter.ReferenceSql.cs
  - src/CodeIndex/Database/DbWriter.References.cs
  - tests/CodeIndex.Tests/DatabaseTests.cs
  - tests/CodeIndex.Tests/IndexCommandRunnerFullScanTests.cs
  - tests/CodeIndex.Tests/McpServerToolsCallTests.cs
  - TESTING_GUIDE.md
---

## English

- **Large atomic-file reference writes materialize context rows in wider windows** — full-scan, scoped-update, and MCP indexing now group complete 71-reference batches for reference-line insertion and lookup, bounded by SQLite's 333-row parameter limit and 32 batches, while preserving reference INSERT, progress, cancellation, and rollback boundaries. A controlled 321K-reference persistence benchmark reduced elapsed time by about 12% and current-thread allocations by about 9%.

## 日本語

- **大規模な atomic-file 参照書き込みで context 行をより広い window 単位に永続化するようになりました** — full scan、scoped update、MCP indexing は、reference INSERT・progress・cancellation・rollback の境界を維持したまま、完全な71参照batchをSQLiteの333行parameter上限と32 batchを境界としてreference-lineの挿入・検索用にまとめます。32.1万参照の制御benchmarkでは永続化時間を約12%、current-thread allocationを約9%削減しました。

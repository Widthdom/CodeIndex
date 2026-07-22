---
category: changed
affected:
  - src/CodeIndex/Database/DbWriter.FileReuse.cs
  - src/CodeIndex/Cli/IndexCommandRunner.FullScan.cs
  - src/CodeIndex/Mcp/McpToolHandlers.Indexing.cs
  - tests/CodeIndex.Tests/DatabaseTests.cs
  - tests/CodeIndex.Tests/PerformanceTests.cs
  - TESTING_GUIDE.md
---

## English

- **Large-repository unchanged-file snapshots now materialize in one pass** — CLI and MCP indexing pre-size the reusable-stat dictionary from counts already available after discovery, reject malformed SQLite stat types in the snapshot query, and avoid both a temporary candidate list and duplicate path values.

## 日本語

- **巨大リポジトリの未変更 file snapshot を1 passで materializeするようになりました** — CLI / MCP indexing は discovery 後に既に得られている件数で再利用 stat dictionary を事前確保し、snapshot query で不正な SQLite stat 型を除外するとともに、一時 candidate list と path value の重複をなくします。

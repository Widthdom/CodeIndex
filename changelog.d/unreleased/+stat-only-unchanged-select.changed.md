---
category: changed
affected:
  - src/CodeIndex/Database/DbWriter.cs
  - src/CodeIndex/Cli/IndexCommandRunner.cs
  - src/CodeIndex/Mcp/McpToolHandlers.cs
  - tests/CodeIndex.Tests/DatabaseTests.cs
  - tests/CodeIndex.Tests/PreparedCommandCacheTests.cs
---

## English

- **Stat-only unchanged indexing checks now avoid SQLite writes** — CLI and MCP indexing reuse unchanged files with a read-only lookup when filesystem metadata is sufficient, reducing writer work during large no-op or mostly unchanged index runs.

## 日本語

- **stat だけで判断できる unchanged indexing 判定で SQLite 書き込みを避けるようになりました** — CLI / MCP の indexing は filesystem metadata だけで十分な unchanged file を read-only lookup で再利用し、大規模な no-op またはほぼ unchanged な index 実行時の writer work を削減します。

---
category: changed
affected:
  - src/CodeIndex/Mcp/McpToolHandlers.cs
---

## English

- **MCP no-op indexing skips FTS optimization** — the MCP `index` tool now avoids `OptimizeFts()` when an index pass reuses existing rows and performs no FTS-affecting purge, delete, or upsert.

## 日本語

- **MCP の no-op index が FTS optimize を skip します** — MCP `index` ツールは、既存 row を再利用し、FTS に影響する purge / delete / upsert がない場合に `OptimizeFts()` を避けるようになりました。

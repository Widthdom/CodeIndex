---
category: changed
affected:
  - src/CodeIndex/Mcp/McpToolHandlers.cs
---

## English

- **MCP no-op indexing skips TypeScript augmentation rebuilds** — the MCP `index` tool now avoids rebuilding all TypeScript augmentation references when an index run reuses existing rows, keeps the same project root, and already has a current augmentation contract stamp.

## 日本語

- **MCP の no-op index が TypeScript augmentation rebuild を skip します** — MCP `index` ツールは、既存 row を再利用し、project root が同じで、augmentation contract stamp が現行の場合に、全 TypeScript augmentation reference の再構築を避けるようになりました。

---
category: changed
affected:
  - src/CodeIndex/Mcp/McpToolHandlers.cs
---

## English

- **MCP indexing skips unchanged files before content loading** — the MCP `index` tool now reuses stat-matched rows before reading file contents, and its C# static-interface prepass can reuse unchanged C# symbols directly from the existing index.

## 日本語

- **MCP index が content load 前に未変更ファイルを skip します** — MCP `index` ツールは、ファイル内容を読む前に stat が一致する row を再利用し、C# static-interface prepass も未変更 C# symbols を既存 index から直接再利用できるようになりました。

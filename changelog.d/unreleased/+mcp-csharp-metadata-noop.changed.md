---
category: changed
affected:
  - src/CodeIndex/Mcp/McpToolHandlers.cs
---

## English

- **MCP no-op indexing skips unchanged C# metadata resolution** — the MCP `index` tool now avoids the all-workspace C# metadata-target resolver when no C# rows were rewritten or purged and the existing metadata-target contract stamp is current.

## 日本語

- **MCP の no-op index が未変更 C# metadata 解決を skip します** — MCP `index` ツールは、C# row の書き換えや purge がなく、既存の metadata-target contract stamp が現行の場合に、全ワークスペースの C# metadata-target resolver を避けるようになりました。

---
category: changed
affected:
  - src/CodeIndex/Database/DbWriter.cs
  - src/CodeIndex/Cli/IndexCommandRunner.FullScan.cs
  - src/CodeIndex/Cli/IndexCommandRunner.Update.cs
  - src/CodeIndex/Mcp/McpToolHandlers.cs
---

## English

- **Reusable-row validation combines cap and generated-code checks** — CLI and MCP indexing now validate extraction caps and generated-code suppression state for unchanged rows with one SQLite query.

## 日本語

- **再利用 row の cap / generated-code 判定をまとめました** — CLI と MCP の indexing は、未変更 row の extraction cap と generated-code 抑止状態を単一の SQLite クエリで検証するようになりました。

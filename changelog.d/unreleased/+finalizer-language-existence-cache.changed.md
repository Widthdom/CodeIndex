---
category: changed
affected:
  - src/CodeIndex/Cli/IndexCommandRunner.FullScan.cs
  - src/CodeIndex/Cli/IndexCommandRunner.Update.cs
  - src/CodeIndex/Mcp/McpToolHandlers.cs
---

## English

- **Index finalizers reuse language-existence checks** — CLI full-scan, CLI scoped update, and MCP index finalizers now query indexed C# / SQL language presence once per run instead of repeating the same SQLite existence checks while stamping readiness.

## 日本語

- **index finalizer が言語存在チェックを再利用します** — CLI full-scan、CLI scoped update、MCP index の finalizer は、readiness stamp 中に同じ SQLite 言語存在チェックを繰り返さず、C# / SQL の indexed file 有無を run ごとに一度だけ読むようになりました。

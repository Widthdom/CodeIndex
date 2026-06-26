---
category: changed
affected:
  - src/CodeIndex/Cli/IndexCommandRunner.FullScan.cs
  - src/CodeIndex/Mcp/McpToolHandlers.cs
---

## English

- **Full-scan target setup avoids repeated LINQ passes** — CLI full scans and MCP indexing now build target arrays, retained path sets, language counts, and C# prepass inputs with sized loops, reducing setup allocation for very large file lists.

## 日本語

- **full scan の対象準備で LINQ の反復を減らしました** — CLI full scan と MCP indexing は target 配列、retained path set、言語カウント、C# prepass 入力をサイズ既知のループで構築し、巨大なファイル一覧での準備段階の allocation を減らします。

---
category: changed
affected:
  - src/CodeIndex/Cli/IndexCommandRunner.FullScan.cs
  - src/CodeIndex/Cli/IndexCommandRunner.Update.cs
  - src/CodeIndex/Mcp/McpToolHandlers.cs
---

## English

- **Unchanged JavaScript and TypeScript files reuse existing index rows** — CLI and MCP indexing now let unchanged JS/TS files take the same row-reuse path as other languages while final TypeScript augmentation rebuilding still runs when an index pass actually mutates the graph.

## 日本語

- **未変更の JavaScript / TypeScript ファイルが既存 index 行を再利用するようになりました** — CLI と MCP の indexing で、未変更の JS/TS ファイルも他言語と同じ row reuse 経路を使えるようにしつつ、index 実行が graph を変更した場合の TypeScript augmentation 再構築は維持します。

---
category: changed
affected:
  - src/CodeIndex/Indexer/Scanning/FileContentLoader.cs
  - src/CodeIndex/Indexer/Scanning/NormalizedContentFacts.cs
  - src/CodeIndex/Indexer/Scanning/ChunkSplitter.cs
  - src/CodeIndex/Indexer/Scanning/FileIndexer.ContentValidation.cs
  - src/CodeIndex/Cli/IndexCommandRunner.WorkItems.cs
  - src/CodeIndex/Mcp/McpToolHandlers.Indexing.Execution.cs
---

## English

- **Large multi-language files are normalized, analyzed, chunked, and validated with one shared content scan** — full scans, scoped updates, dry runs, and MCP indexing now carry normalized line, size, conflict-marker, replacement-character, FTS-token, and chunk-boundary facts through every language-independent path instead of rediscovering them in each consumer. Short files avoid unused boundary/token tracking, and high-ratio invalid UTF-8 input no longer retains one replacement-line entry per damaged line.

## 日本語

- **大規模な multi-language file の正規化・解析・chunk 分割・validation を1回の共有 content scan にまとめました** — full scan、scoped update、dry-run、MCP indexing は、正規化後の行数、size、conflict marker、replacement character、FTS token、chunk 境界の facts を全言語共通経路で引き回し、consumer ごとの再検出を省きます。短い file では不要な境界 / token 追跡を避け、高比率の invalid UTF-8 input では破損行ごとの replacement-line entry を保持しません。

---
category: changed
affected:
  - src/CodeIndex/Indexer/Scanning/ChunkSplitter.cs
  - src/CodeIndex/Cli/IndexCommandRunner.FullScan.cs
  - src/CodeIndex/Cli/IndexCommandRunner.Update.cs
  - src/CodeIndex/Mcp/McpToolHandlers.cs
---

## English

- **Chunking reuses known line counts during indexing** — CLI and MCP indexing now pass the loader's normalized line count into chunk splitting so large files avoid repeated line-offset list growth while preserving the same chunk output.

## 日本語

- **chunking が indexing 済みの行数情報を再利用するようになりました** — CLI と MCP の indexing は loader が持つ正規化後 line count を chunk 分割に渡し、大きなファイルで line offset list の再確保を避けつつ従来と同じ chunk 出力を保ちます。

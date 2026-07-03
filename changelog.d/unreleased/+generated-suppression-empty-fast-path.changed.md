---
category: changed
affected:
  - src/CodeIndex/Indexer/Scanning/GeneratedCodePatternMatcher.cs
  - src/CodeIndex/Indexer/Scanning/FileIndexer.GeneratedCode.cs
  - src/CodeIndex/Cli/IndexCommandRunner.FullScan.cs
  - src/CodeIndex/Mcp/McpToolHandlers.cs
---

## English

- **Skipped generated-suppression precompute when no patterns are configured** - CLI and MCP indexing now avoid building per-file generated-code suppression dictionaries when the project has no generated-code extraction patterns.

## 日本語

- **generated suppression pattern が未設定の場合の precompute を skip しました** - CLI と MCP の indexing は、project に generated-code extraction pattern が無い場合、file ごとの generated-code suppression dictionary を作らないようになりました。

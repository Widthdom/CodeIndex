---
category: internal
affected:
  - src/CodeIndex/Indexer/Scanning/FileIndexer.ProjectMarkers.cs
  - src/CodeIndex/Cli/IndexCommandRunner.cs
  - src/CodeIndex/Mcp/McpToolHandlers.QueryTools.cs
---

## English

- **Project-marker fingerprints now share one cross-language tree traversal** — full/update CLI and MCP indexing compute C#, VB, F#, and MSBuild marker fingerprints in one bounded directory-tree walk. Distinct marker globs retain their platform matching behavior, while per-language budgets, truncation warnings, ignore and repository boundaries, and authorization failures keep their prior contracts.

## 日本語

- **project marker fingerprintが言語横断で1回のtree traversalを共有するようになりました** — full/update CLIとMCP indexingは、C#、VB、F#、MSBuildのmarker fingerprintを上限付きの1回のdirectory-tree走査で計算します。固有marker globはplatform matching挙動を維持し、言語別budget、truncation warning、ignore/repository境界、authorization failureも従来契約を保ちます。

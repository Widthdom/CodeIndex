---
category: changed
affected:
  - src/CodeIndex/Indexer/Scanning/FileIndexer.ProjectMarkers.cs
  - src/CodeIndex/Indexer/Scanning/FileIndexer.ScanDiagnostics.cs
  - src/CodeIndex/Cli/IndexFreshnessChecker.cs
  - src/CodeIndex/Indexer/Symbols/SymbolExtractor.TypeScriptPathAliases.cs
---

## English

- **Indexing callers share the relative path fast path** — project-marker scopes, scan diagnostics, freshness checks, and TypeScript path-alias normalization now avoid `Path.GetRelativePath` when the path is already under the expected directory prefix.

## 日本語

- **indexing caller で relative path fast path を共有** — project marker scope、scan diagnostics、freshness check、TypeScript path-alias normalization は、期待する directory prefix 配下の path では `Path.GetRelativePath` を避けるようにしました。

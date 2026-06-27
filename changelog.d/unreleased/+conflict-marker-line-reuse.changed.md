---
category: changed
affected:
  - src/CodeIndex/Indexer/Scanning/FileContentLoader.cs
  - src/CodeIndex/Indexer/Scanning/FileIndexer.cs
  - src/CodeIndex/Indexer/Scanning/LoadedFileRecord.cs
  - src/CodeIndex/Indexer/Symbols/SymbolExtractor.cs
  - src/CodeIndex/Indexer/Symbols/SymbolExtractionWorker.cs
  - src/CodeIndex/Indexer/References/ReferenceExtractionContext.cs
  - src/CodeIndex/Indexer/References/ReferenceExtractor.cs
  - src/CodeIndex/Indexer/References/ReferenceExtractor.Core.cs
  - src/CodeIndex/Indexer/References/ReferenceExtractor.Preparation.cs
  - src/CodeIndex/Indexer/References/ReferenceExtractor.StructuralMetadata.cs
  - src/CodeIndex/Cli/IndexCommandRunner.cs
  - src/CodeIndex/Cli/IndexCommandRunner.FullScan.cs
  - src/CodeIndex/Cli/IndexCommandRunner.Update.cs
  - src/CodeIndex/Mcp/McpToolHandlers.cs
  - tests/CodeIndex.Tests/FileIndexerContentLoadingTests.cs
---

## English

- **Conflict-marker detection is reused across extraction** — loaded normalized content now carries the detected conflict-marker line into validation, symbol extraction, and reference extraction, avoiding repeated marker scans for each file in CLI, MCP, and isolated symbol-worker indexing paths.

## 日本語

- **conflict-marker 検出を extraction 全体で再利用します** — 読み取り済み normalized content が conflict marker 行を validation、symbol extraction、reference extraction へ渡し、CLI/MCP/isolated symbol worker の各 index 経路でファイルごとの marker 再走査を避けます。

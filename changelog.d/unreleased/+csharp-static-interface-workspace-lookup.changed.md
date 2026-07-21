---
category: changed
affected:
  - src/CodeIndex/Indexer/CSharpStaticInterfacePrepass.cs
  - src/CodeIndex/Indexer/References/ReferenceExtractionContext.cs
  - src/CodeIndex/Indexer/References/ReferenceExtractor.cs
  - src/CodeIndex/Indexer/References/ReferenceExtractor.CoreExtraction.cs
  - src/CodeIndex/Indexer/References/ReferenceExtractor.TypeReferences.cs
  - src/CodeIndex/Cli/IndexCommandRunner.FullScan.cs
  - src/CodeIndex/Cli/IndexCommandRunner.Update.cs
  - src/CodeIndex/Mcp/McpToolHandlers.Indexing.cs
  - tests/CodeIndex.Tests/IndexCommandRunnerFullScanTests.cs
  - TESTING_GUIDE.md
---

## English

- **Large C# workspaces now build static-interface contract lookups once per indexing snapshot** — full scan, scoped update, and MCP indexing reuse the immutable lookup produced by the C# prepass instead of rescanning every workspace symbol for every C# file. Direct reference-extractor calls retain their existing per-call behavior.

## 日本語

- **大規模なC# workspaceでstatic-interface contract lookupをindexing snapshotごとに1回だけ構築するようになりました** — full scan、scoped update、MCP indexingはC# prepassが作成したimmutable lookupを再利用し、C# fileごとにworkspace全symbolを再走査しません。reference extractorの直接呼び出しは従来の呼び出し単位の挙動を維持します。

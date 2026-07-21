---
category: changed
affected:
  - src/CodeIndex/Indexer/References/ReferenceExtractor.TypeReferences.cs
  - tests/CodeIndex.Tests/ReferenceExtractorCSharpTests.cs
  - tests/CodeIndex.Tests/PerformanceTests.cs
  - TESTING_GUIDE.md
---

## English

- **C# static-interface lookup construction now parses generic declarations only for contract-owning interfaces** — the shared full-scan, scoped-update, and MCP workspace lookup first discovers contract containers, returns immediately when none exist, and then scans only matching interface declarations. A 20,000-interface fixture reduces measured lookup allocation from 6,529,440 bytes to 872 bytes while preserving partial declarations, duplicate names, generic substitution, and contract order.

## 日本語

- **C# static-interface lookup構築がcontractを持つinterfaceのgeneric宣言だけを解析するようになりました** — full scan・scoped update・MCPで共有するworkspace lookupは、最初にcontract containerを検出し、存在しなければ即座に返し、その後は一致するinterface宣言だけを走査します。20,000 interfaceのfixtureでlookup allocationの実測値を6,529,440 bytesから872 bytesへ削減しつつ、partial宣言・同名重複・generic置換・contract順を維持します。

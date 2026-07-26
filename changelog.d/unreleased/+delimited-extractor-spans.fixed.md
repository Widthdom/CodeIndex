---
category: fixed
affected:
  - src/CodeIndex/Indexer/DelimitedSpanEnumerable.cs
  - src/CodeIndex/Indexer/Symbols/SymbolExtractor.RepositoryMetadata.cs
  - src/CodeIndex/Indexer/References/Languages/RepositoryMetadataReferenceExtractor.cs
  - src/CodeIndex/Indexer/References/Languages/HdlReferenceExtractor.cs
  - src/CodeIndex/Indexer/References/Languages/HdlReferenceExtractor.Scopes.cs
  - src/CodeIndex/Indexer/References/Languages/ShaderReferenceExtractor.cs
  - tests/CodeIndex.Tests/PerformanceTests.cs
  - DEVELOPER_GUIDE.md
  - TESTING_GUIDE.md
---

## English

- **Removed split-array growth from delimiter-only extractor paths** —
  Repository metadata, application manifests, VHDL declarations and package
  imports, and CUDA kernel parameters now share an allocation-free span walker
  while preserving trimming and empty-entry behavior.

## 日本語

- **delimiter-only extractor 経路の split-array 増加を解消しました** —
  repository metadata、application manifest、VHDL declaration / package import、
  CUDA kernel parameter は、trim と empty-entry の意味論を維持しつつ allocation-free
  な span walker を共有します。

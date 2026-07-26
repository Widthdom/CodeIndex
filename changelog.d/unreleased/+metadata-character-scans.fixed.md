---
category: fixed
affected:
  - src/CodeIndex/Indexer/SpanCharacterSearch.cs
  - src/CodeIndex/Indexer/Symbols/SymbolExtractor.RepositoryMetadata.cs
  - src/CodeIndex/Indexer/References/Languages/RepositoryMetadataReferenceExtractor.cs
  - tests/CodeIndex.Tests/PerformanceTests.cs
  - DEVELOPER_GUIDE.md
  - TESTING_GUIDE.md
---

## English

- **Reduced repository-metadata and manifest validation overhead** —
  Long metadata candidates now use allocation-free span character scans, while
  application manifests track dependency ancestry by XML depth instead of
  rescanning an ancestor stack for every identity.

## 日本語

- **repository metadata と manifest の validation overhead を削減しました** —
  長い metadata candidate は allocation-free な span character scan を使い、
  application manifest は identity ごとの ancestor stack 再走査ではなく XML depth
  で dependency ancestry を追跡します。

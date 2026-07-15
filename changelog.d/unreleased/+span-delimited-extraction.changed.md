---
category: changed
affected:
  - src/CodeIndex/Indexer/References/Languages/PythonImportBindingResolver.cs
  - src/CodeIndex/Indexer/References/Languages/GitHubActionsReferenceExtractor.cs
  - src/CodeIndex/Indexer/References/Languages/JsonReferenceExtractor.cs
  - src/CodeIndex/Indexer/Symbols/SymbolExtractor.ExtractCore.cs
  - tests/CodeIndex.Tests/PerformanceTests.cs
---

## English

- **Dense delimited extraction no longer allocates temporary split arrays** —
  Python import aliases, GitHub Actions needs lists, JSON repository paths, and
  Fortran procedure declarations now use index/span walks while preserving
  trimming and quote semantics. A four-language allocation guard covers the hot
  shapes.

## 日本語

- **密な delimiter list の抽出で一時 split array を作らなくなりました** — Python の
  import alias、GitHub Actions の needs list、JSON repository path、Fortran procedure
  declaration は、trim / quote semantics を維持した index / span walk を使います。
  4言語の hot shape を allocation guard で保護します。

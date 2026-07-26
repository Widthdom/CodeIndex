---
category: fixed
affected:
  - src/CodeIndex/Indexer/SpanCharacterSearch.cs
  - src/CodeIndex/Indexer/References/Languages/FunctionalLanguageReferenceExtractor.cs
  - src/CodeIndex/Indexer/References/Languages/CssReferenceExtractor.AnimationsAndSelectors.cs
  - src/CodeIndex/Indexer/Symbols/SymbolExtractor.CSharpScanner.cs
  - src/CodeIndex/Indexer/Symbols/SymbolExtractor.Java.cs
  - tests/CodeIndex.Tests/PerformanceTests.cs
  - DEVELOPER_GUIDE.md
  - TESTING_GUIDE.md
---

## English

- **Removed padded suffix copies across declaration scanners** — CSS selector
  continuations, C# and Java body-less declarations, and functional-language
  sentinels now share allocation-free trimmed span comparisons.

## 日本語

- **declaration scanner 横断で padded suffix copy を解消しました** —
  CSS selector continuation、C# / Java body-less declaration、functional-language
  sentinel は allocation-free な trimmed span 比較を共有します。

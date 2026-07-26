---
category: fixed
affected:
  - src/CodeIndex/Indexer/SourceLineSplitter.cs
  - src/CodeIndex/Indexer/Symbols/SymbolExtractor.cs
  - src/CodeIndex/Indexer/References/ReferenceExtractor.Configuration.cs
  - tests/CodeIndex.Tests/PerformanceTests.cs
  - DEVELOPER_GUIDE.md
  - TESTING_GUIDE.md
---

## English

- **Reduced all-language extraction allocations for large source files** —
  Symbol and reference extraction now share an exact-capacity line splitter
  that avoids the temporary separator-index arrays created by generic splitting.

## 日本語

- **巨大 source file の全言語 extraction allocation を削減しました** —
  symbol / reference extraction は exact-capacity line splitter を共有し、generic split
  が作る一時 separator-index array を回避します。

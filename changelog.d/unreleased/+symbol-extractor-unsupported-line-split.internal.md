---
category: internal
affected:
  - src/CodeIndex/Indexer/Symbols/SymbolExtractor.cs
---

## English

- **Skipped symbol line splitting for unsupported languages** - generic symbol extraction now returns before splitting file content when a language has no line-based extractor.

## 日本語

- **未対応言語の symbol 行分割を省略** - 汎用 symbol 抽出で line-based extractor を持たない言語は、ファイル本文を行配列に分割する前に返すようにしました。

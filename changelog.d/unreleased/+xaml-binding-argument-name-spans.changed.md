---
category: changed
affected:
  - src/CodeIndex/Indexer/Symbols/SymbolExtractor.Markup.cs
---

## English

- **XAML binding argument name checks avoid string allocations** — Path, Property, and ElementName comparisons now use trimmed spans during XML symbol extraction.

## 日本語

- **XAML binding argument 名判定で string allocation を避けるようになりました** — XML symbol 抽出時の Path、Property、ElementName 比較は trim 済み span で行います。

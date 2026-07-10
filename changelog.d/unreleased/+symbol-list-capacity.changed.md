---
category: changed
affected:
  - src/CodeIndex/Indexer/Symbols/SymbolExtractor.cs
  - src/CodeIndex/Indexer/Symbols/SymbolExtractor.StructuredData.cs
  - src/CodeIndex/Indexer/Symbols/SymbolExtractor.Markup.cs
  - src/CodeIndex/Indexer/Symbols/SymbolExtractor.Lisp.cs
---

## English

- **Large-file symbol extraction seeds symbol list capacity conservatively** - JSON, YAML, XAML, and Lisp symbol extraction now avoid repeated list growth for long files while preserving the allocation-light path for small or symbol-free files.

## 日本語

- **大きなファイルの symbol extraction が symbol list capacity を控えめに先取りするようになりました** - JSON、YAML、XAML、Lisp の symbol extraction は長いファイルで list growth の繰り返しを避け、小さいファイルや symbol のないファイルでは従来の軽い allocation 経路を維持します。

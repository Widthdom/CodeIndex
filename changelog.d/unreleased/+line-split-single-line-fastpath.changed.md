---
category: changed
affected:
  - src/CodeIndex/Indexer/Symbols/SymbolExtractor.cs
  - src/CodeIndex/Indexer/References/ReferenceExtractor.cs
  - src/CodeIndex/Indexer/References/ReferenceExtractor.Preparation.cs
  - src/CodeIndex/Indexer/References/ReferenceExtractor.StructuralMetadata.cs
---

## English

- **Single-line symbol and reference extraction inputs avoid the general line-splitting path** - Indexing now reuses a direct one-line array for files without line breaks across symbol and reference extraction.

## 日本語

- **単一行の symbol / reference 抽出入力が汎用 line splitting 経路を避けるようになりました** - 改行を含まないファイルの indexing では、symbol / reference extraction 全体で直接 1 行配列を使います。

---
category: changed
affected:
  - src/CodeIndex/Indexer/Symbols/SymbolExtractor.cs
---

## English

- **C# container path assignment now avoids repeated LINQ materialization** — symbol extraction now builds effective container paths, container-qualified names, and partial-type family keys with direct stack/list traversal, reducing per-symbol allocation work when indexing very large C# files.

## 日本語

- **C# の container path 割り当てで繰り返しの LINQ materialization を避けるようになりました** — シンボル抽出は effective container path、container-qualified name、partial type family key を stack/list の直接走査で構築するようになり、巨大な C# ファイルをインデックスするときのシンボルごとの割り当て処理を削減します。

---
category: changed
affected:
  - src/CodeIndex/Indexer/LineRangeText.cs
  - src/CodeIndex/Indexer/References/Languages/CSharpReferenceExtractor.Support.cs
  - src/CodeIndex/Indexer/References/Languages/CSharpReferenceExtractor.ValueReceivers.cs
  - src/CodeIndex/Indexer/Symbols/SymbolExtractor.cs
---

## English

- **Avoid iterator allocation when joining indexed line ranges** — C# reference extraction and SQL routine symbol extraction now join contiguous line ranges with an indexed helper instead of `Skip`/`Take` iterator chains.

## 日本語

- **行範囲の結合時の iterator allocation を避けます** — C# 参照抽出と SQL routine symbol 抽出で、連続した行範囲を `Skip` / `Take` iterator chain ではなくインデックス指定のヘルパーで結合します。

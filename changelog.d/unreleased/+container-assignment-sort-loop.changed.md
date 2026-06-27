---
category: changed
affected:
  - src/CodeIndex/Indexer/Symbols/SymbolExtractor.cs
---

## English

- **Reduce container-assignment sort overhead** — symbol container assignment now builds and sorts compact entries directly instead of allocating LINQ projection objects for every extracted symbol.

## 日本語

- **container assignment の sort overhead を削減します** — symbol container assignment で、抽出symbolごとの LINQ projection object を作らず、compact entry を直接作ってsortします。

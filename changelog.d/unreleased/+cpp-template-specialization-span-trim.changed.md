---
category: changed
affected:
  - src/CodeIndex/Indexer/Symbols/SymbolExtractor.cs
---

## English

- **C++ template specialization checks avoid trim allocations** — template-prefix checks now trim signatures and preceding lines as spans during symbol extraction.

## 日本語

- **C++ template specialization 判定で trim allocation を避けるようになりました** — symbol 抽出時の template prefix 判定は、signature と直前行を span として trim します。

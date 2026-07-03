---
title: Lazily allocate Scala companion classification lookups
category: changed
affected:
  - src/CodeIndex/Indexer/Symbols/SymbolExtractor.cs
---

## English

- **Scala companion classification now allocates class lookup dictionaries only when needed** — Scala files without top-level classes skip empty companion lookup setup during indexing.

## 日本語

- **Scala companion 分類が必要時だけ class lookup dictionary を割り当てるようになりました** — top-level class が無い Scala ファイルでは、indexing 中の空 companion lookup 準備を避けます。

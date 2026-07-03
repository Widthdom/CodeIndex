---
title: Lazily allocate C++ same-line member name sets
category: changed
affected:
  - src/CodeIndex/Indexer/Symbols/SymbolExtractor.Cpp.cs
---

## English

- **C++ same-line class extraction now allocates member-name sets only when needed** — class bodies without existing or newly recognized members skip unused duplicate-tracking sets during indexing.

## 日本語

- **C++ same-line class 抽出が必要時だけ member-name set を割り当てるようになりました** — 既存 member や新規認識 member が無い class body では、indexing 中の未使用 duplicate tracking set を避けます。

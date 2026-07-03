---
title: Reuse empty reference definition line lookups
category: changed
affected:
  - src/CodeIndex/Indexer/References/ReferenceExtractor.cs
---

## English

- **Reference extraction now reuses empty line-based definition lookups** — files with no symbols skip allocating per-line definition dictionaries during indexing.

## 日本語

- **reference 抽出が空の line-based definition lookup を再利用するようになりました** — symbol が無いファイルでは、indexing 中の per-line definition dictionary 割り当てを避けます。

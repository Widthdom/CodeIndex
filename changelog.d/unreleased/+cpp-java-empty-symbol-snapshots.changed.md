---
title: Avoid empty C++ and Java symbol snapshots
category: changed
affected:
  - src/CodeIndex/Indexer/Symbols/SymbolExtractor.Cpp.cs
  - src/CodeIndex/Indexer/Symbols/SymbolExtractor.Java.cs
---

## English

- **C++ same-line class and Java module symbol extraction now skips empty snapshots** — files without matching declarations avoid temporary snapshot lists and sort buffers during indexing.

## 日本語

- **C++ same-line class / Java module の symbol 抽出が空 snapshot を作らないようになりました** — 対象 declaration が無いファイルでは、indexing 中の一時 snapshot list と sort buffer を避けます。

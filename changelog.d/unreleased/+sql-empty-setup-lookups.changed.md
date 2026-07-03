---
title: Avoid empty SQL setup lookups
category: changed
affected:
  - src/CodeIndex/Indexer/References/Languages/SqlReferenceExtractor.cs
---

## English

- **SQL reference extraction now skips empty setup lookups** — SQL files without definition leaf spans or window-function suppression sites no longer allocate unused empty dictionaries or sets during per-file setup.

## 日本語

- **SQL 参照抽出が空の setup lookup を作らないようになりました** — definition leaf span や window-function suppression site を持たない SQL ファイルで、ファイル単位の準備中に未使用の空 dictionary / set を割り当てないようにしました。

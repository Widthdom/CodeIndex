---
title: Lazily allocate Stylus variable definition sets
category: changed
affected:
  - src/CodeIndex/Indexer/References/Languages/CssReferenceExtractor.cs
---

## English

- **Stylus reference extraction now lazily allocates variable definition sets** — Stylus files without variable definitions avoid an unused empty `HashSet` during per-file reference setup.

## 日本語

- **Stylus 参照抽出が variable definition set を遅延確保するようになりました** — variable 定義を含まない Stylus ファイルで、ファイル単位の参照準備中に未使用の空 `HashSet` を割り当てないようにしました。

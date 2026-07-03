---
title: Reuse empty reference definition name sets
category: changed
affected:
  - src/CodeIndex/Indexer/References/ReferenceExtractor.cs
  - src/CodeIndex/Indexer/References/Languages/CssReferenceExtractor.cs
---

## English

- **Reference extraction now reuses empty definition-name sets** — files with no symbols avoid allocating all-definition and Razor file-definition lookup sets while indexing.

## 日本語

- **reference 抽出が空 definition-name set を再利用するようになりました** — symbol が無いファイルでは、indexing 中に all-definition / Razor file-definition lookup set を割り当てないようにしました。

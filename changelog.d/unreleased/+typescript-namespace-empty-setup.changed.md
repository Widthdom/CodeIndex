---
title: Reuse empty TypeScript namespace alias setup data
category: changed
affected:
  - src/CodeIndex/Indexer/References/Languages/TypeScriptReferenceExtractor.cs
---

## English

- **TypeScript namespace alias setup now reuses empty lookup data** — files without local declaration shadows or parameter shadow ranges avoid unused empty dictionaries and lists while preparing namespace alias references.

## 日本語

- **TypeScript namespace alias 準備が空 lookup data を再利用するようになりました** — local declaration shadow や parameter shadow range が無いファイルで、namespace alias 参照準備中に未使用の空 dictionary / list を割り当てないようにしました。

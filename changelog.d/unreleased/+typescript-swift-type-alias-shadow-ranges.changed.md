---
title: Lazily allocate TypeScript and Swift type alias shadow ranges
category: changed
affected:
  - src/CodeIndex/Indexer/References/Languages/TypeScriptReferenceExtractor.cs
  - src/CodeIndex/Indexer/References/Languages/SwiftReferenceExtractor.cs
---

## English

- **TypeScript and Swift type alias shadow tracking now allocates ranges only when needed** — files whose aliases are not shadowed skip empty `LineRange` list creation during reference extraction.

## 日本語

- **TypeScript / Swift の type alias shadow tracking が必要時だけ range を割り当てるようになりました** — alias が shadow されていないファイルでは、reference 抽出中の空 `LineRange` list 作成を避けます。

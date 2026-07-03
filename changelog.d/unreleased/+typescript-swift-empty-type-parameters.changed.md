---
title: Reuse empty TypeScript and Swift type-parameter sets
category: changed
affected:
  - src/CodeIndex/Indexer/References/Languages/TypeScriptReferenceExtractor.cs
  - src/CodeIndex/Indexer/References/Languages/SwiftReferenceExtractor.cs
---

## English

- **TypeScript and Swift type alias extraction now reuses empty type-parameter sets** — aliases without generic parameters avoid short-lived empty `HashSet` allocations during large repository indexing.

## 日本語

- **TypeScript / Swift の type alias 抽出が空の type parameter 集合を再利用するようになりました** — generic parameter を持たない alias で、大規模リポジトリのインデックス化中に発生する短命な空 `HashSet` 割り当てを避けます。

---
title: Reuse empty Java and Kotlin generic parameter sets
category: changed
affected:
  - src/CodeIndex/Indexer/References/Languages/JavaReferenceExtractor.cs
  - src/CodeIndex/Indexer/References/Languages/KotlinReferenceExtractor.cs
---

## English

- **Java and Kotlin reference extraction now reuses empty generic-parameter sets** — non-generic declarations and malformed generic clauses avoid short-lived empty `HashSet` allocations while indexing large Java/Kotlin repositories.

## 日本語

- **Java / Kotlin の参照抽出が空の generic parameter 集合を再利用するようになりました** — generic ではない宣言や malformed な generic 句で、大規模な Java / Kotlin リポジトリのインデックス化中に発生する短命な空 `HashSet` 割り当てを避けます。

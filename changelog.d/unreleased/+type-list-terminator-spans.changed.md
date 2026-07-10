---
category: changed
affected:
  - src/CodeIndex/Indexer/References/ReferenceExtractor.PatternTypeReferences.cs
  - src/CodeIndex/Indexer/References/Languages/JavaReferenceExtractor.cs
---

## English

- **Type-list terminator scans now accept spans** — C# `where` clauses and Java `throws` clauses no longer need to copy the trailing line text before finding list terminators.

## 日本語

- **type-list terminator が span 入力を扱うようになりました** — C# `where` clause と Java `throws` clause で list terminator 探索前に行末部分をコピーしないようにしました。

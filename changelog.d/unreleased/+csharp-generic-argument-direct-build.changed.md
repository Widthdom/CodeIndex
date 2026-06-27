---
category: changed
affected:
  - src/CodeIndex/Indexer/References/ReferenceExtractor.TypeReferences.cs
---

## English

- **C# generic interface argument extraction now builds lists directly** — type-reference extraction now avoids chained LINQ materialization when reading generic interface parameters and implemented interface arguments, and only normalizes mapped arguments that are actually used.

## 日本語

- **C# generic interface 引数抽出がリストを直接構築するようになりました** — 型参照抽出は generic interface parameter と implemented interface argument を読む際の連鎖した LINQ materialization を避け、実際に対応付けに使う引数だけを正規化するようになりました。

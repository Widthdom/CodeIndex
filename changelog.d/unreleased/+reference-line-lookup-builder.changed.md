---
category: changed
affected:
  - src/CodeIndex/Database/DbWriter.cs
---

## English

- **Reference-line lookup SQL avoids predicate lists** — reference insertion now builds batched lookup predicates directly into a SQL builder instead of allocating one string per predicate and joining them.

## 日本語

- **reference-line lookup SQL で predicate list を避けます** — reference insertion は batch lookup の predicate を1件ごとの string と `Join` で作らず、SQL builder へ直接構築するようになりました。

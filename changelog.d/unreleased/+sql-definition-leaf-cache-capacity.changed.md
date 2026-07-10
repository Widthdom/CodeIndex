---
category: changed
affected:
  - src/CodeIndex/Indexer/References/Languages/SqlReferenceExtractor.cs
---

## English

- **SQL definition leaf lookups now pre-size local caches** — SQL reference extraction gives definition leaf pattern and line-span buckets bounded initial capacities, reducing dictionary and list growth in large SQL indexes.

## 日本語

- **SQL definition leaf lookup が local cache の初期容量を指定するようになりました** — SQL reference extraction で definition leaf pattern と line-span bucket に上限付きの初期容量を持たせ、大規模 SQL index 時の dictionary/list 拡張を減らしました。

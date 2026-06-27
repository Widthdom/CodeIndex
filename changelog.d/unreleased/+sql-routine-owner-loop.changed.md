---
category: changed
affected:
  - src/CodeIndex/Indexer/Symbols/SymbolExtractor.cs
---

## English

- **Avoid per-routine SQL owner sorting** — SQL routine result-column extraction now finds the owning routine symbol with a single scan instead of allocating and sorting a LINQ query for each routine header.

## 日本語

- **SQL routine ごとの owner sort を避けます** — SQL routine result-column 抽出で、routine header ごとに LINQ query を割り当てて sort せず、単一走査で所有 routine symbol を見つけます。

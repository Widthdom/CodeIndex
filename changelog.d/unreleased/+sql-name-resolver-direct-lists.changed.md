---
category: changed
affected:
  - src/CodeIndex/Indexer/References/Support/SqlNameResolver.cs
---

## English

- **SQL name resolution now avoids transient LINQ lists** — SQL reference resolution now builds distinct qualified-name candidates and column prefix segment lists directly, reducing allocation overhead in large SQL contexts with many qualified identifiers.

## 日本語

- **SQL name resolution が一時的な LINQ list を避けるようになりました** — SQL reference resolution は distinct な qualified-name 候補と column prefix segment list を直接構築し、qualified identifier が多い大きな SQL context での割り当てオーバーヘッドを削減します。

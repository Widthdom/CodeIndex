---
category: changed
affected:
  - src/CodeIndex/Indexer/References/Support/SqlNameResolver.cs
---

## English

- **SQL qualified-name parsing now pre-sizes segment buffers** — SQL reference resolution starts common segment lists with a small capacity to reduce list growth while indexing large SQL-heavy workspaces.

## 日本語

- **SQL qualified-name parsing が segment buffer の初期容量を指定するようになりました** — SQL reference resolution で一般的な segment list を小さな容量から開始し、SQL の多い大規模 workspace を index する際の list 拡張を減らしました。

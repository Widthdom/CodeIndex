---
category: internal
affected: Indexer
---

## English

- Kept symbol-extraction duplicate and line-identity bookkeeping local to each extraction run so parallel tests and concurrent indexing no longer expose mutable static per-file state.

## 日本語

- symbol extraction の duplicate/line identity 管理を抽出実行ごとの局所状態に戻し、並列テストや同時 indexing で mutable static な per-file 状態が露出しないようにしました。

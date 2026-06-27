---
category: changed
affected:
  - src/CodeIndex/Indexer/References/ReferenceExtractor.cs
---

## English

- **Trim Swift property reference snapshot allocation** — Swift property definition lookup now sorts per-line candidates in place and builds the result dictionary directly instead of using LINQ materialization.

## 日本語

- **Swift property reference snapshot の allocation を削減します** — Swift property definition lookup で、行ごとの候補を in-place にソートし、LINQ materialization ではなく結果 dictionary を直接構築します。

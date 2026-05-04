---
category: internal
affected:
  - src/CodeIndex/Indexer/ReferenceExtractor.cs
  - src/CodeIndex/Indexer/JavaReferenceExtractor.cs
  - docs/DEVELOPER_GUIDE.md
---

## English

- **Moved remaining Java type-position reference extraction into the Java helper** — Java `extends` / `implements`, generic bounds, `throws`, declaration types, and `instanceof` references now live in `JavaReferenceExtractor` while preserving existing reference output.

## 日本語

- **残っていた Java type-position 参照抽出を Java helper へ移しました** — Java の `extends` / `implements`、generic bound、`throws`、宣言型、`instanceof` 参照を `JavaReferenceExtractor` に移し、既存の参照出力は維持しました。

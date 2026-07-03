---
category: internal
affected:
  - src/CodeIndex/Indexer/References/Languages/SqlReferenceExtractor.cs
---

## English

- **Streamed SQL top-level comma segments** - SQL MERGE column extraction now yields comma-separated segments directly instead of materializing an intermediate list during reference indexing.

## 日本語

- **SQL の top-level comma segment をストリーム化** - SQL MERGE の column 抽出で参照インデックス作成中に中間リストを作らず、comma 区切り segment を直接列挙するようにしました。

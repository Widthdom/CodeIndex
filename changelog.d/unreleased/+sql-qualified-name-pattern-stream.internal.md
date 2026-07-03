---
category: internal
affected:
  - src/CodeIndex/Indexer/References/Languages/SqlReferenceExtractor.cs
---

## English

- **Streamed SQL qualified-name pattern building** - SQL definition leaf matching now builds qualified-name regex patterns directly instead of materializing an intermediate segment list during indexing.

## 日本語

- **SQL qualified-name pattern 構築をストリーム化** - SQL definition leaf の照合で、インデックス作成中に中間 segment list を作らず qualified-name regex pattern を直接構築するようにしました。

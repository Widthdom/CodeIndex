---
category: changed
affected:
  - src/CodeIndex/Indexer/References/ReferenceExtractor.cs
  - src/CodeIndex/Indexer/References/ReferenceExtractor.Core.cs
  - src/CodeIndex/Indexer/References/ReferenceExtractor.StructuralMetadata.cs
---

## English

- **Large-file reference extraction seeds duplicate tracking capacity** - Reference extraction now gives the per-file `seen` set a conservative initial capacity for long files to reduce rehashing.

## 日本語

- **大きなファイルの reference extraction が duplicate tracking capacity を先取りするようになりました** - 長いファイルでは per-file の `seen` set に控えめな初期容量を与え、rehash を減らします。

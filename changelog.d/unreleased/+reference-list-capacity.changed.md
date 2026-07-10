---
category: changed
affected:
  - src/CodeIndex/Indexer/References/ReferenceExtractor.cs
  - src/CodeIndex/Indexer/References/ReferenceExtractor.Core.cs
  - src/CodeIndex/Indexer/References/ReferenceExtractor.StructuralMetadata.cs
---

## English

- **Large-file reference extraction seeds reference list capacity conservatively** - Reference extraction now avoids repeated list growth for long source files while keeping small files on the allocation-light path.

## 日本語

- **大きなファイルの reference extraction が reference list capacity を控えめに先取りするようになりました** - 長い source file では list growth の繰り返しを避け、小さいファイルは従来の軽い allocation 経路を維持します。

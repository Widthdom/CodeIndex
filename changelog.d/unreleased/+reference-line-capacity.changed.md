---
category: changed
affected:
  - src/CodeIndex/Database/DbWriter.cs
---

## English

- **Reference-line batching pre-sizes lookup collections** — reference insertion now sizes per-batch dictionaries and predicate lists from the known batch length, reducing reallocation while indexing files with many references.

## 日本語

- **reference-line batch の lookup collection を事前サイズ指定します** — reference insertion は既知の batch 長から batch 単位の dictionary と predicate list をサイズ指定し、多数の参照を持つファイルの indexing 中の再 allocation を減らします。

---
title: Lazily allocate CSharp static interface implementation lookups
category: changed
affected:
  - src/CodeIndex/Indexer/References/ReferenceExtractor.TypeReferences.cs
---

## English

- **C# static interface implementation reference setup now allocates file lookups only when needed** — files without candidate types or static members skip empty implementation lists and dictionaries during indexing.

## 日本語

- **C# static interface implementation 参照準備が必要時だけ file lookup を割り当てるようになりました** — candidate type や static member が無いファイルでは、indexing 中の空 implementation list / dictionary を避けます。

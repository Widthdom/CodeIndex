---
category: changed
affected:
  - src/CodeIndex/Indexer/References/ReferenceExtractor.TypeReferences.cs
---

## English

- **C# type-reference extraction now avoids more collection growth** — static-interface lookups, implemented-interface lists, and primary-constructor container ranges start with small capacities, and non-C# files skip the primary-constructor range list entirely.

## 日本語

- **C# type-reference extraction が collection 拡張をさらに避けるようになりました** — static-interface lookup、implemented-interface list、primary-constructor container range に小さな初期容量を持たせ、非 C# file では primary-constructor range list 自体を作らないようにしました。

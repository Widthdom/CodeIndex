---
category: changed
affected:
  - src/CodeIndex/Indexer/References/Languages/CSharpReferenceExtractor.Support.cs
---

## English

- **Reduce C# member conflict-set allocations** — C# reference extraction now builds enum and constant-pattern conflict sets with a shared direct loop instead of repeated LINQ iterator chains while indexing large files.

## 日本語

- **C# member conflict set の allocation を削減します** — 大きなファイルのインデックス化時、C# 参照抽出が enum と constant pattern の衝突セットを重複した LINQ iterator chain ではなく共通の直接ループで構築します。

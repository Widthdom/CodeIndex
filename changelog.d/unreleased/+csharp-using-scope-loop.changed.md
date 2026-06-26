---
category: changed
affected:
  - src/CodeIndex/Indexer/References/Languages/CSharpReferenceExtractor.Support.cs
---

## English

- **Trim C# using-scope materialization allocations** — C# reference extraction now builds namespace scopes and duplicate alias checks with direct loops instead of LINQ iterator chains while indexing large C# files.

## 日本語

- **C# using scope materialization の allocation を削減します** — 大きな C# ファイルの参照抽出で、namespace scope と alias 重複判定を LINQ iterator chain ではなく直接ループで構築します。

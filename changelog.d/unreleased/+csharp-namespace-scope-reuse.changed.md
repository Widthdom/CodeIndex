---
category: changed
affected:
  - src/CodeIndex/Indexer/References/ReferenceExtractor.Core.cs
  - src/CodeIndex/Indexer/References/Languages/CSharpReferenceExtractor.Support.cs
---

## English

- **Reuse C# namespace scopes across using extraction** — C# reference extraction now builds namespace-scope metadata once and shares it across using alias, namespace, and static import passes for large files.

## 日本語

- **C# namespace scope を using 抽出間で再利用します** — 大きな C# ファイルの参照抽出で、namespace scope metadata を一度だけ構築し、using alias / namespace / static import の各パスで共有します。

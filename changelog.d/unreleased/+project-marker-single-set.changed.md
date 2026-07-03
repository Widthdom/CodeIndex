---
category: changed
affected:
  - src/CodeIndex/Indexer/Scanning/FileIndexer.ProjectMarkers.cs
---

## English

- **Project marker scope checks skip duplicate marker enumeration for single-set languages** — C#, VB, and F# indexing no longer probes the same project marker patterns twice per ancestor directory when deriving hotspot family scopes.

## 日本語

- **単一 marker set の言語では project marker scope 判定の重複列挙を省略** — C#、VB、F# の indexing で hotspot family scope を導出する際、祖先ディレクトリごとに同じ project marker pattern を二度確認しないようにしました。

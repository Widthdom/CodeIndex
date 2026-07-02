---
category: changed
affected:
  - src/CodeIndex/Indexer/Scanning/FileIndexer.PathComparison.cs
---

## English

- **Reduced path-comparison allocations** - scan directory identity normalization now walks path segments with spans instead of allocating a split segment array for each directory.

## 日本語

- **path comparison の allocation を削減しました** - scan の directory identity 正規化で、directory ごとに split segment array を確保せず span で path segment を走査します。

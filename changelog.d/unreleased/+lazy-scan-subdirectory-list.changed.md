---
category: changed
affected:
  - src/CodeIndex/Indexer/Scanning/FileIndexer.DirectoryEnumeration.cs
---

## English

- **Reduced per-directory scan allocations** - file discovery now allocates the child-directory list only for directories that actually contain subdirectories, lowering traversal overhead in large trees with many leaf directories.

## 日本語

- **directory scan ごとの allocation を削減しました** - file discovery は実際に subdirectory を含む directory の場合だけ child-directory list を確保するようになり、leaf directory が多い巨大 tree の traversal overhead を抑えます。

---
category: changed
affected:
  - src/CodeIndex/Indexer/Scanning/FileIndexer.DirectoryEnumeration.cs
  - src/CodeIndex/Indexer/Scanning/FileIndexer.DirectoryLinks.cs
  - src/CodeIndex/Indexer/Scanning/FileIndexer.DirectoryTraversal.cs
---

## English

- **Reduced subdirectory traversal probes** - full scans now carry directory attributes from the initial filesystem enumeration into symlink and skip checks, avoiding repeated attribute probes for every subdirectory.

## 日本語

- **subdirectory traversal の probe を削減しました** - full scan では最初の filesystem enumeration で得た directory attributes を symlink / skip 判定へ持ち回し、各 subdirectory の属性 probe の繰り返しを避けます。

---
category: changed
affected:
  - src/CodeIndex/Indexer/Scanning/FileIndexer.DirectoryEnumeration.cs
  - src/CodeIndex/Indexer/Scanning/FileIndexer.FileAcceptance.cs
---

## English

- **Reduced duplicate-path scan overhead** - case-insensitive directory deduplication now reuses already-enumerated full paths during normal file discovery instead of normalizing every file path again.

## 日本語

- **duplicate path scan の overhead を削減しました** - case-insensitive directory の重複排除で、通常の file discovery 中は列挙済みの full path を再利用し、各 file path の再正規化を避けます。

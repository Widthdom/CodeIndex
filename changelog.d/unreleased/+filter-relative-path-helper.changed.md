---
category: changed
affected:
  - src/CodeIndex/Indexer/Scanning/FileIndexer.Paths.cs
  - src/CodeIndex/Indexer/Scanning/FileIndexer.PathFiltering.cs
  - src/CodeIndex/Indexer/Scanning/FileIndexer.IgnoreRules.cs
---

## English

- **Path filtering reuses the directory-prefix relative path helper** — scan filtering and ignore-rule candidate matching now avoid `Path.GetRelativePath` when the absolute path already has the expected directory prefix.

## 日本語

- **path filtering で directory-prefix relative path helper を再利用** — scan filtering と ignore-rule candidate matching で、絶対パスが期待する directory prefix を持つ場合は `Path.GetRelativePath` を避けるようにしました。

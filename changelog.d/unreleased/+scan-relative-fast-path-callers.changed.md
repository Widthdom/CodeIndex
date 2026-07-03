---
category: changed
affected:
  - src/CodeIndex/Indexer/Scanning/FileIndexer.DirectoryLinks.cs
  - src/CodeIndex/Indexer/Scanning/FileIndexer.ProjectMarkers.cs
---

## English

- **Extended scan relative-path fast paths** - project marker discovery and symlink diagnostics now reuse the scanner's project-root relative path fast path instead of calling `Path.GetRelativePath` directly.

## 日本語

- **scan relative-path fast path の適用範囲を広げました** - project marker discovery と symlink diagnostics で `Path.GetRelativePath` の直接呼び出しを避け、scanner の project-root relative path fast path を再利用します。

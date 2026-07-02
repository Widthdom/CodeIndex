---
category: changed
affected:
  - src/CodeIndex/Indexer/Scanning/FileIndexer.cs
  - src/CodeIndex/Indexer/Scanning/FileIndexer.PathComparison.cs
---

## English

- **Reduced relative-path conversion overhead** - scan diagnostics and bookkeeping now use a project-root prefix fast path for files and directories already known to be inside the workspace.

## 日本語

- **relative path 変換の overhead を削減しました** - scan diagnostics と bookkeeping で、workspace 内にあることが分かっている file / directory には project-root prefix の fast path を使います。

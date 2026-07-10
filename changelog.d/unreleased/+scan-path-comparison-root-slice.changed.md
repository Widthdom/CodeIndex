---
category: changed
affected:
  - src/CodeIndex/Indexer/Scanning/FileIndexer.PathComparison.cs
---

## English

- **Scan path comparison** - Derive root-relative path segments by slicing normalized full paths instead of calling `Path.GetRelativePath` during symlink-aware directory traversal.

## 日本語

- **scan path comparison** - symlink-aware な directory traversal 中に、正規化済み full path の slice で root-relative segment を得るようにし、`Path.GetRelativePath` 呼び出しを避けました。

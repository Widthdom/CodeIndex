---
category: changed
affected:
  - src/CodeIndex/Indexer/Scanning/FileIndexer.CaseSensitivity.cs
---

## English

- **Reduced directory case-probe path work** - scan-time case-sensitivity checks now reuse fully-qualified directory paths instead of normalizing the same path again for each directory.

## 日本語

- **directory case-probe の path 処理を削減しました** - scan 中の case-sensitivity check で fully-qualified directory path を再利用し、directory ごとの同一 path 再正規化を避けます。

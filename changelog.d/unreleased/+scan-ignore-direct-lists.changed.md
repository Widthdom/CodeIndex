---
category: changed
affected:
  - src/CodeIndex/Indexer/Scanning/FileIndexer.cs
---

## English

- **File scanning ignore-rule setup now avoids LINQ list materialization** — ignore-rule token folding and ancestor ignore-directory discovery now build their lists directly while preserving traversal order, reducing setup allocations before large workspace walks.

## 日本語

- **file scanning の ignore-rule 初期化が LINQ list materialization を避けるようになりました** — ignore-rule token folding と ancestor ignore-directory 検出は走査順を保ったままリストを直接構築し、大規模 workspace walk 前の初期化割り当てを削減します。

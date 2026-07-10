---
category: changed
affected:
  - src/CodeIndex/Indexer/Scanning/FileIndexer.FileAcceptance.cs
---

## English

- **Successful full-scan file acceptance now avoids unnecessary relative-path work** - Indexing no longer computes a project-relative path on the common accepted-file path, reducing per-file overhead in large repositories without changing scan results.

## 日本語

- **full-scan で受理された通常ファイルの不要な relative-path 計算を避けるようになりました** - 走査結果を変えずに、巨大リポジトリでファイルごとに積み上がる project-relative path 計算を成功経路から外しました。

---
category: changed
affected:
  - src/CodeIndex/Indexer/Scanning/FileIndexer.Gitmodules.cs
---

## English

- **`.gitmodules` warning paths are computed once during scan setup** — submodule discovery now reuses the project-relative `.gitmodules` path across warning branches instead of recalculating it for each warning.

## 日本語

- **scan 初期化時の `.gitmodules` warning path を 1 回だけ計算** — submodule discovery で warning 分岐ごとに project-relative な `.gitmodules` path を再計算せず、同じ値を再利用するようにしました。

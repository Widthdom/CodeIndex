---
category: changed
affected:
  - src/CodeIndex/Indexer/Scanning/FileIndexer.cs
---

## English

- **Seed scan error list capacity from known submodule warnings** — file scanning now sizes the warning list before adding preloaded `.gitmodules` diagnostics, avoiding a small reallocation during large repository discovery.

## 日本語

- **scan error list を既知の submodule warning 数で初期化します** — ファイル走査時に `.gitmodules` 診断を追加する前に warning list の容量を確保し、大規模リポジトリ探索中の小さな再確保を避けます。

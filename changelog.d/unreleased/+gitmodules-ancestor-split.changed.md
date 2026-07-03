---
category: changed
affected:
  - src/CodeIndex/Indexer/Scanning/FileIndexer.Gitmodules.cs
---

## English

- **Submodule ancestor tracking avoids split/join allocations** — `.gitmodules` scan setup now builds only the needed ancestor paths for submodule passthrough instead of splitting every submodule path into segments first.

## 日本語

- **submodule ancestor tracking で split/join 割り当てを回避** — `.gitmodules` の scan 初期化で submodule passthrough 用の ancestor path だけを作り、各 submodule path を先に segment 分割しないようにしました。

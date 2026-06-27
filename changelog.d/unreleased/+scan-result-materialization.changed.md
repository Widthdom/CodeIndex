---
category: changed
affected:
  - src/CodeIndex/Indexer/Scanning/FileIndexer.cs
---

## English

- **Scan result materialization avoids iterator chains** — full-scan directory results now build path lists and checkpointed directory sets with sized collections and ordinal `Sort`, reducing end-of-scan allocations in large repositories.

## 日本語

- **scan result の materialization で iterator chain を避けます** — full scan の directory result は path list と checkpointed directory set をサイズ既知の collection と ordinal `Sort` で構築し、巨大リポジトリの scan 終了時 allocation を減らします。

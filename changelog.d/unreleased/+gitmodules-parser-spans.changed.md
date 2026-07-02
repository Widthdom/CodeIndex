---
category: changed
affected:
  - src/CodeIndex/Indexer/Scanning/FileIndexer.Gitmodules.cs
---

## English

- **`.gitmodules` parsing reduces per-line string allocations** — submodule discovery now parses section headers, keys, comments, and rooted-path checks with spans, only materializing accepted submodule paths.

## 日本語

- **`.gitmodules` parsing の行ごとの string 割り当てを削減** — submodule discovery で section header、key、comment、rooted path 判定を span で処理し、受理した submodule path だけを string 化するようにしました。

---
category: internal
affected: Indexer
---

## English

- Restored read-time indexability checks for full-scan, MCP, freshness, and C# prepass file reads so scan-time file probes cannot be reused across symlink/reparse-point races.

## 日本語

- full-scan、MCP、freshness、C# prepass のファイル読み込みで read-time indexability check を復元し、scan 時点の file probe が symlink/reparse-point race をまたいで再利用されないようにしました。

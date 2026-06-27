---
category: changed
affected:
  - src/CodeIndex/Database/DbWriter.cs
  - tests/CodeIndex.Tests/PreparedCommandCacheTests.cs
---

## English

- **Unchanged-file legacy issue checks now reuse prepared SQLite commands** — indexing avoids rebuilding the stale issue metadata lookup for each reusable file, reducing per-file overhead while preserving legacy metadata safety checks.

## 日本語

- **unchanged file の legacy issue 判定が prepared SQLite command を再利用するようになりました** — indexing は reusable file ごとの stale issue metadata lookup 再構築を避け、legacy metadata safety check を保ったまま per-file overhead を削減します。

---
category: changed
affected:
  - src/CodeIndex/Database/DbWriter.cs
  - tests/CodeIndex.Tests/PreparedCommandCacheTests.cs
---

## English

- **Reusable-row blocking checks now reuse prepared SQLite commands** — unchanged-file indexing avoids rebuilding the cap/generated-code validation command for each file, reducing per-file overhead in large mostly unchanged runs.

## 日本語

- **再利用 row の blocking 判定が prepared SQLite command を再利用するようになりました** — unchanged-file indexing は file ごとの cap / generated-code 検証 command 再構築を避け、大規模でほぼ unchanged な実行時の per-file overhead を削減します。

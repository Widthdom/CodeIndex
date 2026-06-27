---
category: changed
affected:
  - src/CodeIndex/Database/DbWriter.cs
---

## English

- **Stale directory/stem purge scans reuse prepared commands** — rename-cleanup scans now reuse their fixed SQLite command across large indexing runs.

## 日本語

- **directory/stem の stale purge scan で prepared command を再利用します** — rename cleanup の scan が、巨大 index 実行中に固定SQLite commandを再利用します。

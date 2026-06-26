---
category: changed
affected:
  - src/CodeIndex/Database/DbWriter.cs
---

## English

- **File purge scans and deletes reuse prepared commands** — stale-file and retained-set cleanup now share cached SQLite commands for the full files scan and per-id delete loop during large index refreshes.

## 日本語

- **file purge の scan/delete で prepared command を再利用します** — 巨大 index の更新中に走る stale-file と retained-set cleanup が、files 全件 scan と id 単位 delete の SQLite command を共有して再利用します。

---
category: changed
affected:
  - src/CodeIndex/Database/DbWriter.cs
---

## English

- **Fold readiness and backfill SQL reuse prepared commands** — folded-name verification, counts, reads, and row updates now reuse fixed SQLite commands during large index maintenance.

## 日本語

- **fold readiness と backfill SQL の prepared command を再利用します** — folded-name の検証、count、read、row update が、巨大 index のメンテナンス中に固定SQLite commandを再利用します。

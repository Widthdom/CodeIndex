---
category: changed
affected:
  - src/CodeIndex/Database/DbWriter.cs
---

## English

- **Metadata helper SQL reuses prepared commands** — repeated index metadata stamps, reads, and table-existence checks now reuse fixed commands instead of recreating SQLite commands during large index runs.

## 日本語

- **metadata helper SQL の prepared command を再利用します** — index metadata の stamp、read、table existence check の反復で、巨大 index 実行中の SQLite command 再生成を減らします。

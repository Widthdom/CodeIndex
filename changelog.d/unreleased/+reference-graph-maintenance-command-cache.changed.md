---
category: changed
affected:
  - src/CodeIndex/Database/DbWriter.cs
---

## English

- **Reference graph maintenance reuses prepared commands** — mutual-recursion refresh, TypeScript augmentation rebuild, and reference graph purge now reuse fixed SQLite commands during large index finalization.

## 日本語

- **reference graph maintenance で prepared command を再利用します** — 巨大 index の finalization 中に走る相互再帰更新、TypeScript augmentation rebuild、reference graph purge が固定 SQLite command を再利用します。

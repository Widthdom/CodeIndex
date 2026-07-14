---
category: changed
affected:
  - src/CodeIndex/Database/PreparedCommandCache.cs
  - tests/CodeIndex.Tests/PreparedCommandCacheTests.cs
---

## English

- **Large full indexes retain more prepared SQLite batch shapes** — The default
  prepared-command cache now holds 64 statements, reducing repeated SQLite
  parameter binding across varied chunk, symbol, and reference batches while
  preserving the existing environment override.

## 日本語

- **巨大なフル索引でより多くの prepared SQLite batch 形状を保持します** —
  prepared-command cache の既定容量を64件へ増やし、既存の環境変数 override を
  維持したまま、多様な chunk・symbol・reference batch 間の SQLite parameter
  再束縛を削減しました。

---
category: changed
affected:
  - src/CodeIndex/Database/
---

## English

- **Index batch writes use compact sequential SQLite parameters** — Chunk,
  symbol, reference-line, and reference inserts now prepare shorter parameter
  names and SQL text across every indexed language. This reduces full-index
  parameter binding time without changing ordinal value assignment or stored rows.

## 日本語

- **索引バッチ書き込みで短い連番 SQLite parameter を使用します** — 全索引言語の
  chunk・symbol・reference-line・reference INSERT が短い parameter 名と SQL text を
  prepare するようになりました。ordinal の値設定と保存行を変えずに、フル索引時の
  parameter binding 時間を削減します。

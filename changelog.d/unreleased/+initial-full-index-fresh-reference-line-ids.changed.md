---
category: changed
affected:
  - src/CodeIndex/Database/DbWriter.AuthoritativeFreshIdentityInsert.cs
  - src/CodeIndex/Database/DbWriter.ReferenceSql.cs
---

## English

- **Cold reference-line inserts avoid correlated `RETURNING` scans** — The authoritative empty-database path now assigns checked contiguous IDs from the live table and AUTOINCREMENT history, validates each DONE statement's changed-row count, and publishes IDs only after success. Provider, rebuild, incremental, and MCP paths retain their established behavior.

## 日本語

- **初回 reference-line insert から相関 `RETURNING` scan を削減** — authoritative な空DB経路は live table と AUTOINCREMENT 履歴から検証済みの連続IDを割り当て、DONE statementごとの変更行数を確認し、成功後だけIDを公開します。provider、rebuild、incremental、MCP経路の既存挙動は維持します。

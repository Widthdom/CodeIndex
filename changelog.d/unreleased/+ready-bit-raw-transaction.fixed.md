---
category: fixed
affected:
  - src/CodeIndex/Database/DbWriter.cs
---

## English

- **Ready-bit stamping no longer depends on provider transaction state** — `SetReadyBit` now uses raw SQLite `BEGIN IMMEDIATE` / `COMMIT` for `PRAGMA user_version` updates so concurrent writer tests do not trip over stale provider-managed transaction state on pooled connections.

## 日本語

- **ready-bit stamp が provider transaction state に依存しないようになりました** — `SetReadyBit` は `PRAGMA user_version` 更新に raw SQLite の `BEGIN IMMEDIATE` / `COMMIT` を使うようになり、pooled connection 上の provider-managed transaction state により並行 writer テストが失敗しないようにしました。

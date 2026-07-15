---
category: changed
---

## English

- Delete stale file rows in 500-ID batches within the existing atomic purge transaction, reducing thousands of per-file SQLite statements while preserving cascade, FTS, callback, and rollback behavior.

## 日本語

- stale file行を既存のatomicなpurge transaction内で500 IDずつ一括削除し、cascade・FTS・callback・rollbackの挙動を維持しながら、数千回のfile単位SQLite statementを削減しました。

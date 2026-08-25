---
category: changed
affected:
  - src/CodeIndex/Database/DbWriter.Commands.cs
  - src/CodeIndex/Database/DbWriter.Transactions.cs
  - src/CodeIndex/Database/DbWriter.Meta.cs
  - src/CodeIndex/Database/DbWriter.Fts.cs
  - tests/CodeIndex.Tests/PreparedCommandCacheTests.cs
---

## English

- **Fixed SQLite savepoint controls now reuse prepared commands** — empty-state full indexing reuses the depth-one per-file SAVEPOINT / RELEASE / ROLLBACK statements, and metadata plus FTS marker savepoints share the same bounded cache path, reducing command prepare/finalize churn while deeper dynamic scopes and rollback/cancellation behavior remain unchanged.

## 日本語

- **固定 SQLite savepoint control が prepared command を再利用するようになりました** — 空状態からの full index では file 単位の depth 1 SAVEPOINT / RELEASE / ROLLBACK を再利用し、metadata と FTS marker の savepoint も同じ有界 cache 経路を共有することで、深い動的 scope と rollback / cancellation の挙動を変えずに command の prepare / finalize 負荷を減らします。

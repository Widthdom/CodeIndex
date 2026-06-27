---
category: changed
affected:
  - src/CodeIndex/Database/DbWriter.cs
  - tests/CodeIndex.Tests/PreparedCommandCacheTests.cs
---

## English

- **Index finalizer queries reuse prepared SQLite commands** — lightweight language-existence checks and JavaScript/TypeScript config discovery now reuse prepared commands across indexing runs instead of allocating fresh SQLite commands for each finalization query.

## 日本語

- **index finalizer の問い合わせが prepared SQLite command を再利用します** — 軽量な言語存在チェックと JavaScript/TypeScript 設定検出は、finalize 時の問い合わせごとに新しい SQLite command を作らず、indexing run 内で prepared command を再利用するようになりました。

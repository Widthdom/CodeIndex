---
category: changed
affected:
  - src/CodeIndex/Database/DbWriter.cs
  - tests/CodeIndex.Tests/PreparedCommandCacheTests.cs
---

## English

- **Validation issue writes reuse prepared commands** — indexing now reuses prepared SQLite commands when replacing `file_issues` rows for each file, reducing per-file command allocation across CLI and MCP indexing.

## 日本語

- **validation issue 書き込みが prepared command を再利用します** — indexing は各ファイルの `file_issues` 行を置き換える際に prepared SQLite command を再利用し、CLI / MCP indexing のファイル単位 command allocation を減らします。

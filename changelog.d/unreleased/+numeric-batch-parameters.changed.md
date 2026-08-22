---
category: changed
affected:
  - src/CodeIndex/Database/DbWriter.BatchParameters.cs
  - src/CodeIndex/Database/DbWriter.ChunkSymbolBatches.cs
  - src/CodeIndex/Database/DbWriter.Issues.cs
  - src/CodeIndex/Database/DbWriter.ReferenceSql.cs
  - tests/CodeIndex.Tests/DatabaseTests.cs
  - tests/CodeIndex.Tests/FreshReferenceResolutionTests.cs
  - DEVELOPER_GUIDE.md
---

## English

- **Index write batches now use compact SQLite numeric parameter slots across every persistence table** — chunk, symbol, issue, reference-line, and reference inserts share a one-origin `?1` through `?N` contract, reducing parameter-resolution work during full indexing while retaining the 32-parameter caller-transaction cap, exact tail statements, prepared-command reuse, NULL handling, and fresh-reference semantics.

## 日本語

- **インデックス書き込みbatchが全永続化tableでcompactなSQLite numeric parameter slotを使うようになりました** — chunk、symbol、issue、reference-line、reference insertを1-originの`?1`〜`?N`契約へ統一し、32 parameterのcaller transaction上限、正確なtail statement、prepared command再利用、NULL処理、fresh reference semanticsを保ったまま、full index中のparameter解決処理を削減します。

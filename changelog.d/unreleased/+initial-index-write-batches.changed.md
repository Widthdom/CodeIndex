---
category: changed
affected:
  - src/CodeIndex/Database/DbWriter.BatchSql.cs
  - src/CodeIndex/Database/DbWriter.ChunkSymbolBatches.cs
  - src/CodeIndex/Database/DbWriter.Issues.cs
  - src/CodeIndex/Database/DbWriter.References.cs
---

## English

- **Initial full indexing now uses binding-efficient SQLite write batches** — caller-owned file transactions cap named parameters per chunk, symbol, issue, reference-line, and reference statement, avoiding repeated dense parameter-name lookup across every supported language while preserving public writer transaction and SAVEPOINT contracts. Per-statement cancellation checkpoints remain intact. For operations above 500 rows, persistent progress logs are emitted at 500-row boundaries and completion so the smaller statements do not introduce a synchronous log flush per statement.

## 日本語

- **初回フル索引がSQLite binding効率のよいwrite batchを使うようになりました** — caller-owned file transaction内のchunk、symbol、issue、reference-line、reference statementでnamed parameter数を制限し、全対応言語に共通する密なparameter name再探索を避けつつ、public writerのtransaction / SAVEPOINT契約を維持します。statementごとのcancellation checkpointは保ちます。500 rowを超えるoperationでは、永続progress logを500 row境界と完了時だけに出力し、小さなstatementごとの同期log flushを防ぎます。

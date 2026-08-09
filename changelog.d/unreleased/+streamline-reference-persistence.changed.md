---
category: changed
affected:
  - src/CodeIndex/Cli/IndexCommandRunner.FullScan.cs
  - src/CodeIndex/Cli/IndexCommandRunner.Update.cs
  - src/CodeIndex/Database/DbWriter.ReferenceSql.cs
  - src/CodeIndex/Database/DbWriter.References.cs
---

## English

- **Large multi-language reference batches perform less index maintenance, parameter binding, and tuple hashing** — existing-database full scans that cross the established FTS bulk-load threshold now defer reference-query indexes transactionally, while scoped updates do the same recoverably only after at least 64 targets reach 60% of indexed files. Reference inserts also write the normalized legacy context column as a SQL `NULL` literal and carry materialized reference-line IDs through compact ordinal arrays, avoiding one binding and a repeated file/line/context dictionary lookup per edge across fresh and replacement paths. Small updates retain every query index and avoid rebuild overhead.

## 日本語

- **大規模な multi-language reference batch の index maintenance、parameter binding、tuple hashing を削減しました** — 既存DBの full scan が既定の FTS bulk-load 閾値を超えた場合は reference query index を transaction 内で一時退避し、scoped update では64 target以上かつ indexed fileの60%以上の場合だけ recoverable に同じ処理を行います。reference insert は正規化済み legacy context column を SQL の `NULL` literal として書き込み、materialize 済み reference-line ID を compact な ordinal array で渡します。fresh / replacement の全経路で edge ごとの binding 1件と file / line / context dictionary の再 lookup を省きます。小規模 update は全 query index を維持し、再構築 overhead を避けます。

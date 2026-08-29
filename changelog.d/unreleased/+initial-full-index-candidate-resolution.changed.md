---
category: changed
affected:
  - src/CodeIndex/Database/DbWriter.ReferenceGraphRefreshScope.cs
  - src/CodeIndex/Database/DbWriter.References.cs
  - src/CodeIndex/Database/DbWriter.cs
  - src/CodeIndex/Database/ReferenceSecondaryIndexBulkLoadGuard.cs
  - src/CodeIndex/Database/ReferenceSecondaryIndexSql.cs
---

## English

- **Cold reference resolution limits target-key work to candidates** — Graph finalization now restores the candidate reverse index immediately after candidate insertion, prepares resolution separately, materializes target-family facts only for candidate-bearing symbols, and replaces per-group DISTINCT sets with equivalent BINARY min/max singleton checks while preserving legacy NULL and ambiguity semantics.

## 日本語

- **初回 reference resolution の target-key 処理を candidate に限定** — graph finalization は candidate insert 直後に reverse index を復元して resolution を別 command で prepare し、candidate を持つ symbol だけの target-family fact を materialize します。legacy NULL と ambiguity semantics を維持したまま、group ごとの DISTINCT set を等価な BINARY min/max singleton 判定へ置き換えます。

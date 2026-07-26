---
category: fixed
affected:
  - src/CodeIndex/Indexer/References/Languages/SqlReferenceExtractor.cs
  - src/CodeIndex/Indexer/References/Languages/SqlReferenceExtractor.AlterTargets.cs
  - src/CodeIndex/Indexer/References/Languages/SqlReferenceExtractor.DropTargets.cs
  - src/CodeIndex/Indexer/References/Languages/SqlReferenceExtractor.LineMasking.cs
  - src/CodeIndex/Indexer/References/Languages/SqlReferenceExtractor.MaintenanceTargets.cs
  - src/CodeIndex/Indexer/References/Languages/SqlReferenceExtractor.QualifiedColumns.cs
  - src/CodeIndex/Indexer/References/Languages/SqlReferenceExtractor.Sources.cs
  - src/CodeIndex/Indexer/References/Languages/SqlReferenceExtractor.StatementState.cs
  - src/CodeIndex/Indexer/References/Languages/SqlReferenceExtractor.Statements.cs
  - DEVELOPER_GUIDE.md
  - TESTING_GUIDE.md
---

## English

- **Streamed SQL reference matches** — SQL statement, source, target,
  generated-column, window-clause, procedure-call, and temporary-object
  scanners now consume matches on demand and stop at bounded reference limits.

## 日本語

- **SQL の reference match を逐次走査にしました** — SQL の statement、source、
  target、generated-column、window-clause、procedure-call、一時 object scanner は
  match を demand-driven に消費し、bounded reference の上限で停止します。

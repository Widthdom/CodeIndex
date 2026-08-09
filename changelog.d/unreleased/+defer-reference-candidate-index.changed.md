---
category: changed
affected:
  - src/CodeIndex/Database/ReferenceSecondaryIndexSql.cs
  - src/CodeIndex/Database/ReferenceSecondaryIndexBulkLoadGuard.cs
  - src/CodeIndex/Database/DbContext.SchemaInitialization.cs
---

## English

- **Large reference graphs no longer maintain the reverse candidate-symbol index row by row during bulk indexing** — Fresh, rebuilt, and high-churn full-graph indexing now stages this query-only index with the existing cross-language reference index set, including through a TypeScript augmentation-owned graph pass, while retaining the candidate primary key and restoring the identical final schema on success, cancellation, and recovery.

## 日本語

- **大規模 reference graph の bulk indexing 中に candidate-symbol reverse index を行ごとに保守しなくなりました** — fresh、rebuild、および高 churn の full-graph indexing は、TypeScript augmentation が担当する graph pass も含め、この query 専用 index を既存の全言語 reference index 集合とともに遅延します。candidate primary key は維持し、成功・cancellation・recovery の全経路で同一の最終 schema を復元します。

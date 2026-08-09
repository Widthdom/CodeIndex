---
category: changed
affected:
  - src/CodeIndex/Cli/IndexCommandRunner.Update.cs
  - src/CodeIndex/Cli/IndexCommandRunner.Update.SecondaryIndexPreflight.cs
  - src/CodeIndex/Database/ReferenceSecondaryIndexSql.cs
  - src/CodeIndex/Database/ReferenceSecondaryIndexBulkLoadGuard.cs
  - src/CodeIndex/Database/DbContext.SchemaInitialization.cs
---

## English

- **Large reference graphs no longer maintain the reverse candidate-symbol index row by row during candidate materialization** — Fresh, rebuilt, and high-churn full-graph indexing keeps this query-only index during raw reference persistence and drops it only when a graph refresh will actually repopulate candidates, including a TypeScript augmentation-owned pass. A filtered stat preflight skips reference and hotspot secondary-index staging entirely when every high-cardinality scoped target is unchanged, while marker-only work also avoids rebuilding the candidate index. The candidate primary key remains available and success, cancellation, and recovery still converge on the identical final schema.

## 日本語

- **大規模 reference graph の candidate 構築中に candidate-symbol reverse index を行ごとに保守しなくなりました** — fresh、rebuild、および高 churn の full-graph indexing は、この query 専用 index を raw reference persistence 中は維持し、TypeScript augmentation が担当する pass を含め、graph refresh が candidate を実際に再構築するときだけ外します。filtered stat preflight は高 cardinality scoped target が全て unchanged の場合に reference / hotspot secondary-index staging 自体を省き、marker-only work も candidate index の再構築を避けます。candidate primary key を維持したまま、成功・cancellation・recovery の全経路で同一の最終 schema に収束します。

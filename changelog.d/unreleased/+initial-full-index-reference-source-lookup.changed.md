---
category: changed
affected:
  - src/CodeIndex/Database/DbWriter.AuthoritativeFreshBulkInsert.cs
  - src/CodeIndex/Database/DbWriter.AuthoritativeFreshReferenceSourceLookup.cs
  - src/CodeIndex/Database/DbWriter.ReferenceSql.cs
  - src/CodeIndex/Database/DbWriter.References.cs
  - src/CodeIndex/Database/DbWriter.cs
---

## English

- **Cold reference persistence probes one indexed per-file snapshot** — Empty-database raw loads now copy each relevant file's symbols once into a connection-local indexed TEMP table before resolving reference sources, preserving folded-name, display-name, legacy ASCII `NOCASE`, nesting, rollback, and query-result semantics while avoiding a persistent symbol lookup for every reference.

## 日本語

- **初回 reference 永続化は indexed file snapshot を1回だけ探索** — 空DBへのraw loadは、reference sourceを解決する前に関連fileのsymbolをconnection-localなindexed TEMP tableへ各1回copyするようになりました。referenceごとのpersistent symbol lookupを避けながら、fold済みname、display name、legacy ASCII `NOCASE`、nesting、rollback、query resultの意味を維持します。

---
category: changed
affected:
  - src/CodeIndex/Database/ReferenceSecondaryIndexSql.cs
  - src/CodeIndex/Database/ReferenceSecondaryIndexBulkLoadGuard.cs
  - src/CodeIndex/Database/DbContext.cs
  - src/CodeIndex/Database/DbContext.ReadMigrations.cs
  - src/CodeIndex/Database/DbReader.cs
  - src/CodeIndex/Database/DbWriter.ReferenceGraphRefreshScope.cs
  - src/CodeIndex/Database/DbWriter.References.cs
---

## English

- **Large reference graphs rebuild fewer redundant index pages** — canonical schema creation, read migration, and bulk-load recovery now retire six single-column name/container indexes already covered by retained composite prefixes. The former all-row folded mutual-recursion index is replaced by a partial index containing only unresolved callable edges, while folded and legacy NOCASE exact queries continue to use retained composite indexes and older databases are pruned on open.

## 日本語

- **大規模なreference graphで冗長なindex pageの再構築を削減しました** — canonical schema作成、read migration、bulk-load recoveryは、保持済みcomposite prefixで代替できるname/container単一カラムindex 6本を退役させます。全rowを保持していた旧folded mutual-recursion indexは未解決のcallable edgeだけを含むpartial indexへ置き換え、folded/legacy NOCASEのexact queryは保持済みcomposite indexを引き続き利用し、旧databaseもopen時にpruneします。

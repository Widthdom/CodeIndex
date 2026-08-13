---
category: changed
affected:
  - src/CodeIndex/Database/DbWriter.ReferenceSql.cs
  - src/CodeIndex/Database/DbWriter.References.cs
---

## English

- **Fresh full indexes now assign reference sources during insertion** — the authoritative empty-database path resolves each language-neutral reference to its narrowest containing same-file symbol while preserving the 14-parameter batch shape, so graph finalization no longer scans and rewrites every reference solely to add source identity.

## 日本語

- **fresh full indexがreference挿入時にsourceを設定するようになりました** — authoritativeなempty-database経路は14 parameterのbatch shapeを維持しつつ、言語共通の各referenceを同一file内で最も狭く包含するsymbolへ解決するため、graph finalizationはsource identity追加だけのために全referenceをscan・rewriteしません。

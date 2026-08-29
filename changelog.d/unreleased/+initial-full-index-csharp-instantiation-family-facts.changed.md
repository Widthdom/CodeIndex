---
category: changed
affected:
  - src/CodeIndex/Database/DbWriter.ReferenceGraphRefreshScope.cs
  - src/CodeIndex/Database/DbWriter.References.cs
---

## English

- **Cold C# constructor matching reuses family facts** — Reference-graph finalization now materializes constructor-family compatibility once per relevant type or constructor and reuses indexed TEMP facts across every candidate rank. This removes per-candidate correlated symbol scans while preserving partial, project/file-local, overload, optional/default/`params`, orphan, value-type, and legacy ambiguity semantics.

## 日本語

- **初回 C# constructor 照合で family fact を再利用** — reference-graph finalization は対象type / constructorごとにconstructor-family互換性を1回だけmaterializeし、全candidate rankでindexed TEMP factを再利用します。candidateごとの相関symbol scanをなくしつつ、partial、project / file-local、overload、optional / default / `params`、orphan、value type、legacy ambiguityのsemanticsを維持します。

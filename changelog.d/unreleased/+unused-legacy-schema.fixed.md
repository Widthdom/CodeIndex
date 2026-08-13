---
category: fixed
affected:
  - src/CodeIndex/Database/DbReader.cs
  - src/CodeIndex/Database/DbSymbolReader.UnusedSymbols.Candidates.cs
  - src/CodeIndex/Database/DbSymbolReader.UnusedSymbols.cs
---

## English

- **C# unused-symbol queries now remain safe on read-only legacy databases** — partial-type use filtering is applied before paging without issuing per-candidate database queries, while databases whose legacy chunk or symbol tables lack the required columns retain the existing degraded fallback instead of failing during SQL preparation.

## 日本語

- **C# の unused-symbol query が read-only legacy database でも安全に動作するようになりました** — partial type の使用判定を candidate ごとの database query なしで paging 前に適用し、必要な column が legacy chunk / symbol table にない database では SQL prepare で失敗せず、従来の degraded fallback を維持します。

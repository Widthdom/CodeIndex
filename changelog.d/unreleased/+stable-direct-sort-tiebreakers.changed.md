---
category: changed
affected:
  - src/CodeIndex/Indexer/References/ReferenceExtractor.cs
  - src/CodeIndex/Indexer/Symbols/SymbolExtractor.Assembly.cs
  - src/CodeIndex/Indexer/Symbols/SymbolExtractor.JavaScriptTypeScriptSupport.cs
---

## English

- **Preserve stable direct-sort ordering** — direct snapshot sorts for JS/TS class scans, assembly ranges, and reference prepasses now keep original discovery order as the final tie-breaker, matching the old LINQ ordering on equal keys.

## 日本語

- **direct sort の安定順序を保ちます** — JS/TS class scan、assembly range、reference prepass の direct snapshot sort で、同一key時に元の検出順を最後の tie-breaker として使い、従来の LINQ ordering と揃えます。

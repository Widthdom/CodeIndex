---
category: changed
affected:
  - src/CodeIndex/Indexer/Symbols/SymbolExtractor.cs
  - src/CodeIndex/Indexer/Symbols/SymbolExtractor.StructuredData.cs
---

## English

- **Symbol extraction now builds small filtered and bounded lists directly** — Fortran procedure-name expansion now filters comma-separated procedure names in one pass, and structured-data trimming copies the retained symbol budget directly instead of routing through LINQ.

## 日本語

- **symbol extraction が小さな filtered / bounded list を直接構築するようになりました** — Fortran procedure name 展開は comma 区切り名を1パスでフィルタし、structured data の trimming は LINQ を経由せず保持対象の symbol budget を直接コピーします。

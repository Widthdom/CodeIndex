---
category: changed
affected:
  - src/CodeIndex/Indexer/Symbols/SymbolExtractor.FortranDeclarators.cs
  - src/CodeIndex/Indexer/Symbols/SymbolExtractor.SwiftEnumDeclarators.cs
---

## English

- **Fortran and Swift declarator expansion scans segments with spans** — comma-delimited declarator lists no longer allocate a full substring for each segment before extracting names and Swift enum raw values.

## 日本語

- **Fortran と Swift の declarator 展開で segment を span 走査するようになりました** — comma 区切りの declarator list から名前や Swift enum raw value を取り出す前に、segment 全体の substring を作らないようにしました。

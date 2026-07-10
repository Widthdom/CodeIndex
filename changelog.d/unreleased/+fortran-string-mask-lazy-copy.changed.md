---
category: changed
affected:
  - src/CodeIndex/Indexer/Symbols/SymbolExtractor.Fortran.cs
  - tests/CodeIndex.Tests/SymbolExtractorTests.cs
---

## English

- **Fortran string masking now reuses quote-free lines** — Fortran symbol range detection no longer copies every scanned line before checking whether it contains a string literal.

## 日本語

- **Fortran の文字列マスキングで quote のない行を再利用するようになりました** — Fortran のシンボル範囲判定は、文字列リテラルがあるか確認する前に全 scan 行をコピーしなくなりました。

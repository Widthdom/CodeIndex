---
category: changed
affected:
  - src/CodeIndex/Indexer/Symbols/SymbolExtractor.CSharpScanner.cs
  - src/CodeIndex/Indexer/Symbols/SymbolExtractor.cs
  - tests/CodeIndex.Tests/SymbolExtractorCSharpRegexProbeTests.cs
---

## English

- **Bound declaration probes during full indexing** — C#, Razor, Blazor and CSHTML avoid rechecking static-lambda arrows and prefixes across unrelated declarations, and reject impossible method prefixes before regex matching. Static constructors, generic methods and lambda filtering retain their results, with fewer regex attempts and substantially fewer temporary allocations.

## 日本語

- **フルインデックス時の宣言候補の検査範囲を制限** — C#・Razor・Blazor・CSHTML で別の宣言に跨る静的 lambda の arrow・prefix の再検査を避け、成立しないメソッド prefix も regex 照合の前に除外します。static constructor・generic method・lambda 除外の結果を保ちながら、regex 試行回数と一時割り当てを減らします。

---
category: changed
affected:
  - src/CodeIndex/Indexer/Symbols/SymbolExtractor.CSharpScanner.cs
  - tests/CodeIndex.Tests/SymbolExtractorCSharpRegexProbeTests.cs
---

## English

- **Initial C# indexing skips impossible declaration-prefix probes** — symbol extraction rejects multiline parameter fragments with impossible leading punctuation before expensive declaration-confirmation regexes. C#, Razor, Blazor and CSHTML share the optimization, preserving the existing tuple, generic, constructor and member extraction behavior without requiring an index rebuild.

## 日本語

- **初回C#インデックスで不可能な宣言prefixの照合を省略** — 複数行引数の継続断片など、先頭部分の記号から成立しないと分かる候補を、高価な宣言確認regexの前に除外します。C#・Razor・Blazor・CSHTMLで共通の最適化で、tuple・generic・constructor・memberの既存抽出を維持し、強制rebuildは不要です。

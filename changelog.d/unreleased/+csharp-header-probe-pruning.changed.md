---
category: changed
affected:
  - src/CodeIndex/Indexer/Symbols/SymbolExtractor.CSharpScanner.cs
  - tests/CodeIndex.Tests/SymbolExtractorCSharpRegexProbeTests.cs
---

## English

- **Initial C# indexing avoids impossible multiline-header probes** — symbol extraction checks the exact prefix, tuple punctuation and incomplete-header suffix required by the existing regexes before evaluating them. C#, Razor, Blazor and CSHTML share the optimization; tuple and generic recovery, constructors, member identities and source ranges remain unchanged.

## 日本語

- **初回C#インデックスで成立しない複数行headerの照合を省略** — 既存regexが必須とするprefix・tupleの記号・未完headerの末尾を照合前に確認します。C#・Razor・Blazor・CSHTMLで共通の最適化で、tuple／genericの復旧・constructor・memberの識別情報・ソース範囲は維持します。

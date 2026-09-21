---
category: changed
affected:
  - src/CodeIndex/Indexer/Symbols/SymbolExtractor.CSharpScanner.cs
  - tests/CodeIndex.Tests/SymbolExtractorCSharpRegexProbeTests.cs
---

## English

- **Initial C# indexing avoids repeated declaration-lookahead copies and scans** — multiline symbol extraction defers combined strings until a declaration decision is possible, scans appended text once for top-level semicolons, and skips initializer scans without `=`. C#, Razor, Blazor and CSHTML share the optimization while preserving declaration text, source ranges, generic recovery and existing lookahead limits.

## 日本語

- **初回C#インデックスで宣言lookaheadの文字列コピー・再走査を削減** — 複数行のシンボル抽出は、宣言を判定できる記号が現れるまで連結文字列の生成を保留し、追加部分だけでtop-level semicolonを追跡し、`=` のない初期化子走査を省略します。C#・Razor・Blazor・CSHTMLで共通の最適化で、宣言テキスト・ソース範囲・genericの復旧・既存のlookahead上限を維持します。

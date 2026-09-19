---
category: changed
affected:
  - src/CodeIndex/Indexer/Symbols/SymbolExtractor.Patterns.cs
  - src/CodeIndex/Indexer/Symbols/SymbolExtractor.CSharpScanner.cs
  - tests/CodeIndex.Tests/SymbolExtractorRequiredLiteralGateTests.cs
  - tests/CodeIndex.Tests/SymbolExtractorCSharpRegexProbeTests.cs
---

## English

- **Initial indexing avoids impossible declaration regex probes** — C#, Razor/Blazor/CSHTML, Java, Kotlin, C/C++, JavaScript and TypeScript reject audited declaration patterns when their exact transformed input lacks mandatory punctuation. C# same-line member recovery also skips impossible property/event/delegate probes. Existing regexes, declaration ranges, indexers, generic prefixes and compact constructors retain their behavior.

## 日本語

- **初回インデックスで成立しない宣言正規表現の評価を削減しました** — C#、Razor/Blazor/CSHTML、Java、Kotlin、C/C++、JavaScript、TypeScript は、変換後の実際の入力に必須の記号がない場合、監査済みの宣言パターン評価を省略します。C# の同一行メンバー救済も、成立しない property/event/delegate 判定を省略します。既存の正規表現・宣言範囲・indexer・generic prefix・compact constructor の挙動を維持します。

---
category: changed
affected:
  - src/CodeIndex/Indexer/Symbols/SymbolExtractor.CSharpScanner.cs
  - tests/CodeIndex.Tests/SymbolExtractorTests.cs
---

## English

- **C# cross-line scanning avoids an extra string copy for delimiter-free lines** — the secondary delimiter blanking step now reuses sanitized lines that contain no string, char, or escape delimiters.

## 日本語

- **C# の cross-line scan で delimiter のない行の追加コピーを避けるようになりました** — 二段目の delimiter 空白化は、文字列・char・escape delimiter を含まないサニタイズ済み行を再利用します。

---
category: changed
affected:
  - src/CodeIndex/Indexer/Symbols/SymbolExtractor.CSharpScanner.cs
  - tests/CodeIndex.Tests/SymbolExtractorCSharpRegexProbeTests.cs
---

## English

- Speed up initial C# workspace indexing by rejecting impossible member/method confirmation suffixes before regex matching. Razor, Blazor and CSHTML share the optimization; declaration identities, ranges and Unicode handling are preserved.

## 日本語

- C# のメンバー／メソッド宣言確認で成立しない末尾を regex 照合前に除外し、初回ワークスペースインデックスを高速化しました。Razor・Blazor・CSHTML にも共通で適用し、宣言の識別情報・範囲・Unicode の扱いを維持します。

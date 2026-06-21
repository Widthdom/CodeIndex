---
category: fixed
affected:
  - src/CodeIndex/Indexer/Symbols/SymbolExtractor.cs
  - tests/CodeIndex.Tests/SymbolExtractorCSharpTests.cs
---

## English

- **C# symbol ranges no longer run away when method bodies write JSON braces** — brace-bodied C# symbols now use raw-source range scanning only when the physical line starts in normal code, so string literal braces inside methods no longer stretch later symbol ranges to the end of large files.

## 日本語

- **C# symbol range が JSON braces を書き込む method body で過大化しなくなりました** — brace body を持つ C# symbol は物理行の開始状態が通常コードのときだけ raw source で範囲走査するため、method 内の string literal braces で後続 symbol range が巨大ファイル末尾まで伸びなくなりました。

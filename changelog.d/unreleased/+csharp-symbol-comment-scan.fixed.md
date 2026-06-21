---
category: fixed
affected:
  - src/CodeIndex/Indexer/Symbols/SymbolExtractor.cs
  - src/CodeIndex/Indexer/Symbols/SymbolExtractor.CSharpScanner.cs
  - tests/CodeIndex.Tests/SymbolExtractorCSharpTests.cs
---

## English

- **Reduced C# symbol extraction allocation during full indexing** — line-comment-only filtering now avoids allocating a trimmed copy for every C# source line.

## 日本語

- **full index 時の C# symbol extraction allocation を削減しました** — 行コメントだけの行を判定するとき、C# source line ごとの trimmed copy allocation を避けるようになりました。

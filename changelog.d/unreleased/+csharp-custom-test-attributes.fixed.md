---
category: fixed
affected:
  - src/CodeIndex/Indexer/Symbols/SymbolExtractor.CSharpPatterns.cs
  - tests/CodeIndex.Tests/SymbolExtractorCSharpTests.cs
---

## English

- C# symbol extraction now classifies custom xUnit wrapper attributes ending in `Fact` or `Theory` as `test.method`, keeping self-indexing budget fixtures aligned with repository test attributes.

## 日本語

- C# symbol extraction は `Fact` / `Theory` で終わる xUnit wrapper 属性も `test.method` として分類するようになり、self-indexing の budget fixture がリポジトリ内のテスト属性と一致するようになりました。

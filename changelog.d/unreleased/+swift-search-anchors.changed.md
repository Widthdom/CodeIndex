---
category: changed
issues: null
affected:
  - src/CodeIndex/Indexer/SymbolExtractor.cs
  - tests/CodeIndex.Tests/SymbolExtractorTests.cs
---

## English

- **Improved Swift symbol extraction for initializer-style anchors** — Swift `init`, `deinit`, and `subscript` declarations are now indexed as function symbols, making Swift code searches more complete for object lifecycle and indexed-access entry points.

## 日本語

- **Swift の初期化系アンカー抽出を強化** — Swift の `init`・`deinit`・`subscript` 宣言を function シンボルとして index 化するようになり、オブジェクトライフサイクルや添字アクセスの検索網羅性が向上しました。

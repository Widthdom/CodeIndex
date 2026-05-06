---
category: added
affected:
  - src/CodeIndex/Indexer/Symbols/SymbolExtractor.cs
  - src/CodeIndex/Indexer/Symbols/SymbolExtractor.Lisp.cs
  - src/CodeIndex/Indexer/References/ReferenceExtractor.cs
  - src/CodeIndex/Indexer/References/Languages/LispReferenceExtractor.cs
  - tests/CodeIndex.Tests/SymbolExtractorTests.cs
  - tests/CodeIndex.Tests/ReferenceExtractorTests.cs
  - DEVELOPER_GUIDE.md
---

## English

- **Common Lisp and Racket now have graph-aware search support** — `cdidx` now extracts Lisp definitions with comment-aware S-expression scanning and indexes Common Lisp / Racket call references for `references`, `callers`, `callees`, and `impact`.

## 日本語

- **Common Lisp と Racket が graph 対応検索に対応しました** — `cdidx` はコメントを考慮した S 式スキャンで Lisp 定義を抽出し、Common Lisp / Racket の呼び出し参照を `references` / `callers` / `callees` / `impact` 向けにインデックス化するようになりました。

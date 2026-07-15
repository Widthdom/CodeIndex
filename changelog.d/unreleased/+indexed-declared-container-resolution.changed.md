---
category: changed
affected:
  - src/CodeIndex/Indexer/Symbols/SymbolExtractor.cs
  - tests/CodeIndex.Tests/SymbolExtractorContainerLookupTests.cs
---

## English

- **Declared-container resolution is now indexed once per file** — Symbol-dense
  C#, JavaScript, TypeScript, Solidity, markup, assembly, Lisp, and other
  supported sources avoid quadratic owner scans while preserving nested
  qualified names.

## 日本語

- **宣言コンテナ解決をファイルごとに一度索引化するようになりました** — ネストした完全修飾名を
  維持したまま、C#・JavaScript・TypeScript・Solidity・マークアップ・アセンブリ・Lisp などの
  シンボル密集ファイルで二乗的な所有者探索を回避します。

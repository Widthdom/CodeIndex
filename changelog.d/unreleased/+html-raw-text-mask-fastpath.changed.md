---
category: changed
affected:
  - src/CodeIndex/Indexer/Symbols/SymbolExtractor.Markup.cs
  - tests/CodeIndex.Tests/SymbolExtractorTests.cs
---

## English

- **HTML raw-text masking skips plain markup copies** — markup symbol extraction now reuses source text when no comments, declarations, processing instructions, or raw-text/RCDATA elements need masking.

## 日本語

- **HTML raw-text マスキングで plain markup のコピーを避けるようになりました** — markup シンボル抽出は、comment、declaration、processing instruction、raw-text/RCDATA 要素のマスクが不要な場合に元テキストを再利用します。

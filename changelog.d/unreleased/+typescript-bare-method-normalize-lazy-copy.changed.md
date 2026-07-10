---
category: changed
affected:
  - src/CodeIndex/Indexer/Symbols/SymbolExtractor.JavaScriptTypeScriptSupport.cs
  - tests/CodeIndex.Tests/SymbolExtractorJavaScriptTypeScriptTests.cs
---

## English

- **TypeScript bare-method normalization skips no-op copies** — scanner normalization now reuses plain method-header text when there is no generic span or return-type brace rewrite to apply.

## 日本語

- **TypeScript の bare method 正規化で no-op コピーを避けるようになりました** — generic span や return type の brace 置換が不要な method header は、scanner 正規化で元の文字列を再利用します。

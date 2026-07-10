---
category: changed
affected:
  - src/CodeIndex/Indexer/Symbols/SymbolExtractor.cs
  - src/CodeIndex/Indexer/Symbols/SymbolExtractor.GraphQL.cs
---

## English

- **Supplemental symbol passes reuse source text** - GraphQL input-field extraction and SQL CTE extraction now avoid rebuilding full-file text from line arrays during indexing.

## 日本語

- **補助 symbol pass が source text を再利用するようになりました** - GraphQL input field 抽出と SQL CTE 抽出は、indexing 中に line 配列からファイル全体の文字列を再構築しないようになりました。

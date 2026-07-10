---
category: changed
affected:
  - src/CodeIndex/Indexer/Symbols/SymbolExtractor.cs
  - src/CodeIndex/Indexer/Symbols/SymbolExtractor.Markup.cs
---

## English

- **Markup symbol extraction reuses normalized content** - HTML and XAML symbol extraction now avoid rebuilding full-file text from line arrays during indexing.

## 日本語

- **markup symbol extraction が正規化済み content を再利用するようになりました** - HTML と XAML の symbol extraction は、indexing 中に line 配列からファイル全体の文字列を再構築しないようになりました。

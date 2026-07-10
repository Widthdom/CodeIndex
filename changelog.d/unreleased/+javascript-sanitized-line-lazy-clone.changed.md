---
category: changed
affected:
  - src/CodeIndex/Indexer/Symbols/SymbolExtractor.JavaScriptTypeScriptSupport.cs
---

## English

- **JavaScript and TypeScript symbol extraction now reuses unmodified sanitized lines** — supplemental JS/TS scanners now avoid allocating a replacement line array when lexical sanitization leaves every line unchanged.

## 日本語

- **JavaScript / TypeScript シンボル抽出で未変更の sanitized 行を再利用するようになりました** — JS/TS の補助 scanner は、字句 sanitization 後に全行が変わらない場合、新しい行配列を割り当てなくなりました。

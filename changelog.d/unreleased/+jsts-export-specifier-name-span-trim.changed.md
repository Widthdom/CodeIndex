---
category: changed
affected:
  - src/CodeIndex/Indexer/Symbols/SymbolExtractor.JavaScriptTypeScriptSupport.cs
---

## English

- **JavaScript/TypeScript export specifier names avoid trim allocations** — quoted export specifier normalization now works over trimmed spans and only materializes when the symbol name changes.

## 日本語

- **JavaScript/TypeScript export specifier 名で trim allocation を避けるようになりました** — quoted export specifier の正規化は trim 済み span 上で行い、symbol 名が変わる場合だけ文字列化します。

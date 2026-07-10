---
category: changed
affected:
  - src/CodeIndex/Indexer/Symbols/SymbolExtractor.JavaScriptTypeScriptSupport.cs
---

## English

- **JavaScript/TypeScript export clause scanning avoids eager trim allocations** — symbol extraction now checks star and local named export prefixes with spans before building export clauses.

## 日本語

- **JavaScript/TypeScript export clause 走査で早期trim割り当てを避けるようになりました** — シンボル抽出は、export clause を構築する前に star export と local named export の接頭辞を span で確認します。

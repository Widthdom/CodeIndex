---
category: changed
affected:
  - src/CodeIndex/Indexer/Symbols/SymbolExtractor.JavaScriptTypeScriptSupport.cs
---

## English

- **JavaScript/TypeScript function assignment detection avoids trim/substr loops** — symbol extraction now routes whole-RHS checks through the existing index-based scanner.

## 日本語

- **JavaScript/TypeScript function assignment 判定で trim/substr ループを避けるようになりました** — シンボル抽出は、右辺全体の判定も既存の index ベース走査へ集約します。

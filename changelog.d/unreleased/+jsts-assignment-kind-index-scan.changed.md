---
category: changed
affected:
  - src/CodeIndex/Indexer/Symbols/SymbolExtractor.JavaScriptTypeScriptSupport.cs
---

## English

- **JavaScript/TypeScript assignment kind checks avoid trim helper allocations** — lambda, class, anonymous function, and generic-arrow probes now reuse whitespace indexes instead of rebuilding RHS slices.

## 日本語

- **JavaScript/TypeScript assignment kind 判定で trim helper の割り当てを避けるようになりました** — lambda、class、anonymous function、generic-arrow の検査は、右辺 slice を作り直さず whitespace index を再利用します。

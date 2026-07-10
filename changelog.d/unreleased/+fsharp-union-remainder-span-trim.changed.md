---
category: changed
affected:
  - src/CodeIndex/Indexer/Symbols/SymbolExtractor.FSharp.cs
---

## English

- **F# union-case scanning avoids eager remainder trim allocations** — symbol extraction now inspects type-declaration remainders as spans before parsing inline union cases.

## 日本語

- **F# union case 走査でremainderの早期trim割り当てを避けるようになりました** — シンボル抽出は、inline union case を解析する前に type declaration remainder を span として確認します。

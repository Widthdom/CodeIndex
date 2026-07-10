---
category: changed
affected:
  - src/CodeIndex/Indexer/Symbols/SymbolExtractor.Markup.cs
---

## English

- **Markdown anchor normalization avoids eager trim allocations** — symbol extraction now normalizes local link targets with spans before emitting reference symbols.

## 日本語

- **Markdown anchor 正規化で早期trim割り当てを避けるようになりました** — シンボル抽出は、reference symbol を出力する前に local link target を span で正規化します。

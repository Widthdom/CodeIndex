---
category: changed
affected:
  - src/CodeIndex/Indexer/Symbols/SymbolExtractor.Ruby.cs
---

## English

- **Ruby range scanning avoids eager trim allocations** — symbol extraction now checks masked Ruby body lines as spans before running block-token regex matching.

## 日本語

- **Ruby range 走査で早期trim割り当てを避けるようになりました** — シンボル抽出は、block token 正規表現を実行する前にマスク済みRuby本文行を span として確認します。

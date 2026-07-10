---
category: changed
affected:
  - src/CodeIndex/Indexer/Symbols/SymbolExtractor.Python.cs
---

## English

- **Python decorator scanning avoids eager trim allocations** — symbol extraction now checks decorator lines as spans before normalizing property decorators.

## 日本語

- **Python decorator 走査で早期trim割り当てを避けるようになりました** — シンボル抽出は、property decorator を正規化する前に decorator 行を span として確認します。

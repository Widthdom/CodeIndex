---
category: changed
affected:
  - src/CodeIndex/Indexer/Symbols/SymbolExtractor.Python.cs
---

## English

- **Python import spec parsing avoids eager trim allocations** — symbol extraction now keeps import-name lists as spans until individual import symbols are emitted.

## 日本語

- **Python import spec 解析で早期trim割り当てを避けるようになりました** — シンボル抽出は、個々の import symbol を出力するまで import 名リストを span のまま扱います。

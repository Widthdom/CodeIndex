---
category: changed
affected:
  - src/CodeIndex/Indexer/Symbols/SymbolExtractor.Go.cs
  - tests/CodeIndex.Tests/SymbolExtractorGoTests.cs
---

## English

- **Go import scanning avoids eager trim allocations** — symbol extraction now keeps Go import/directive candidate lines as spans until an import symbol must be parsed.

## 日本語

- **Go import 走査で早期 trim 割り当てを避けるようになりました** — シンボル抽出は、Go の import/directive 候補行を import symbol の解析が必要になるまで span のまま扱います。

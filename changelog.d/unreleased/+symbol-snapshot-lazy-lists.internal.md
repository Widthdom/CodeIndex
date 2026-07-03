---
category: internal
affected:
  - src/CodeIndex/Indexer/Symbols/SymbolExtractor.cs
---

## English

- **Allocated symbol snapshots lazily** - enum, property, and Rust associated-type snapshot helpers now avoid creating candidate lists for files that have no matching symbols.

## 日本語

- **symbol snapshot を遅延確保** - enum、property、Rust associated type の snapshot helper で、該当 symbol がないファイルでは candidate list を作らないようにしました。

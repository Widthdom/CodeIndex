---
category: changed
affected:
  - src/CodeIndex/Indexer/Symbols/SymbolExtractor.Go.cs
---

## English

- **Trim Go receiver container assignment allocation** — Go method receiver container assignment now records type kinds with a direct dictionary pass instead of grouping all type symbols during indexing.

## 日本語

- **Go receiver container 割り当ての allocation を削減します** — Go method receiver の container 割り当てで、indexing 中にすべての type symbol を group 化せず、直接 dictionary pass で type kind を記録します。

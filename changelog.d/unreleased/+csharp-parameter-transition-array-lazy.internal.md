---
category: internal
affected:
  - src/CodeIndex/Indexer/Symbols/SymbolExtractor.cs
---

## English

- **Allocated C# parameter transition rows lazily** - C# symbol extraction now creates callable-parameter transition arrays only after a parameter-list transition is observed.

## 日本語

- **C# parameter transition 行を遅延確保** - C# symbol 抽出で callable parameter-list transition が見つかった場合にだけ transition 配列を作るようにしました。

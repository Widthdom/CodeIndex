---
category: changed
affected:
  - src/CodeIndex/Indexer/Symbols/SymbolExtractor.cs
  - src/CodeIndex/Indexer/References/ReferenceExtractor.cs
---

## English

- **Plugin extractor results now copy without LINQ materialization** — Symbol and reference plugin outputs are now copied from `IReadOnlyList` results with direct indexed loops, preserving the independent list contract while reducing allocation overhead for plugins that emit large result sets.

## 日本語

- **plugin extractor result が LINQ materialization なしで copy されるようになりました** — symbol と reference の plugin output は `IReadOnlyList` result から direct indexed loop でコピーされ、独立した list contract を保ったまま、大量 result を返す plugin の割り当てオーバーヘッドを削減します。

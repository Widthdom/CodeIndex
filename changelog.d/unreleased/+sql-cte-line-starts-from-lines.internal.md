---
category: internal
affected:
  - src/CodeIndex/Indexer/Symbols/SymbolExtractor.cs
---

## English

- **Built SQL CTE line starts from existing lines** - SQL CTE symbol supplementation now derives line-start offsets from the existing line array instead of rescanning joined content.

## 日本語

- **SQL CTE の line start を既存行から構築** - SQL CTE symbol 補完で、結合済み content を再走査せず既存の行配列から line-start offset を作るようにしました。

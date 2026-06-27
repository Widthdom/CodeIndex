---
category: changed
affected:
  - src/CodeIndex/Indexer/Symbols/SymbolExtractor.cs
---

## English

- **Trim declared-container lookup allocation** — container qualified-name population now finds the best declared container with a direct scan instead of filtering and sorting candidates for every symbol.

## 日本語

- **declared container lookup の allocation を削減します** — container qualified-name 補完で、各symbolごとに候補をfilter/sortせず、直接scanで最適なdeclared containerを探します。

---
category: changed
affected:
  - src/CodeIndex/Indexer/Symbols/SymbolExtractor.Assembly.cs
---

## English

- **Trim assembly range assignment scans** — assembly symbol range assignment now sorts copied candidates in place and finds following sections with a linear cursor instead of LINQ sorting and repeated scans.

## 日本語

- **assembly range assignment の scan を削減します** — assembly symbol range 割り当てで、candidate copy を in-place に sort し、後続 section を LINQ sort と繰り返し scan ではなく線形 cursor で探します。

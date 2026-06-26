---
category: changed
affected:
  - src/CodeIndex/Indexer/Symbols/SymbolExtractor.Java.cs
  - src/CodeIndex/Indexer/Symbols/SymbolExtractor.cs
---

## English

- **Trim body-range declaration snapshot allocation** — Java module directive extraction and Rust associated-type default extraction now collect body-range declaration snapshots with direct loops before sorting, avoiding LINQ iterator chains on large symbol lists.

## 日本語

- **body-range declaration snapshot の allocation を削減します** — Java module directive 抽出と Rust associated-type default 抽出で、大きな symbol list に対する LINQ iterator chain を避け、直接ループで宣言 snapshot を収集してから sort します。

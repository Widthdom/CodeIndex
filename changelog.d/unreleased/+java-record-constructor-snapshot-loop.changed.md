---
category: changed
affected:
  - src/CodeIndex/Indexer/Symbols/SymbolExtractor.Java.cs
---

## English

- **Trim Java record compact-constructor snapshot allocation** — Java symbol extraction now collects record declarations and same-line constructor candidates with direct loops instead of LINQ iterator pipelines while indexing large Java files.

## 日本語

- **Java record compact constructor snapshot の allocation を削減します** — 大きな Java ファイルのインデックス化時、record 宣言と同一行 constructor candidate を LINQ iterator pipeline ではなく直接ループで収集します。

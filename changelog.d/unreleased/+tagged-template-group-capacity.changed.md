---
category: changed
affected:
  - src/CodeIndex/Indexer/References/ReferenceExtractor.Preparation.cs
---

## English

- **Pre-size JS/TS tagged-template grouping** — reference extraction now sizes the per-line tagged-template lookup from the known hit count to reduce reallocations in large JavaScript and TypeScript files.

## 日本語

- **JS/TS tagged-template grouping を pre-size します** — 参照抽出時の行別 tagged-template lookup を既知の hit 数で初期化し、大きな JavaScript/TypeScript ファイルでの再確保を減らします。

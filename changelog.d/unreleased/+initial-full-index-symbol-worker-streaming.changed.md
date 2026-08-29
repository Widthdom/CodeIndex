---
category: changed
affected:
  - src/CodeIndex/Indexer/Symbols/SymbolExtractionWorker.cs
---

## English

- **Cold indexing streams symbol-worker requests** — Source content is now JSON-escaped through bounded pooled buffers directly into the worker pipe, avoiding a source-sized request byte array for every language and every parallel extraction worker.

## 日本語

- **初回 index の symbol-worker request を streaming 化** — source content を bounded pooled buffer で JSON escape しながら worker pipe へ直接書き込み、全言語・各並列 extraction worker で source 規模の request byte array を作らないようにしました。

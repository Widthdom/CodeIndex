---
category: changed
affected:
  - src/CodeIndex/Indexer/Scanning/FileIndexer.FileAcceptance.cs
---

## English

- **Full-scan language counters now update through one dictionary lookup per accepted file** — large mixed-language repositories avoid the previous lookup-plus-assignment pair in the file acceptance hot path.

## 日本語

- **full-scan の言語カウンターを対象ファイルごとに1回の dictionary lookup で更新します** — 多言語の大規模リポジトリで file acceptance hot path の lookup と代入の二重探索を避けます。

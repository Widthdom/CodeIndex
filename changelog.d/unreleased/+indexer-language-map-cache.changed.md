---
category: changed
affected:
  - src/CodeIndex/Indexer/Scanning/FileIndexer.LanguageDetection.cs
  - src/CodeIndex/Indexer/Scanning/FileIndexer.cs
  - src/CodeIndex/Indexer/Scanning/LanguageMapOverrides.cs
  - tests/CodeIndex.Tests/FileIndexerTests.cs
---

## English

- **Cached language-map override lookups during indexing** - `cdidx index` now reuses per-directory language-map override snapshots within a `FileIndexer` run, avoiding repeated config-path stamp probes for each file in large directories.

## 日本語

- **indexing 中の language-map override lookup を cache しました** - `cdidx index` は `FileIndexer` の 1 実行内で directory ごとの language-map override snapshot を再利用し、大きな directory で file ごとに config path stamp を再確認する処理を減らします。

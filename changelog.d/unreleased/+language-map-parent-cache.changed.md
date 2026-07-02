---
category: changed
affected:
  - src/CodeIndex/Indexer/Scanning/FileIndexer.LanguageDetection.cs
  - tests/CodeIndex.Tests/FileIndexerTests.cs
---

## English

- **Reused parent language-map caches for child directories** - indexing now reuses a cached parent `.cdidx-langmap.yaml` result when a child directory has no local override file, reducing repeated ancestor config probes during large scans.

## 日本語

- **子 directory で親の language-map cache を再利用します** - indexing は子 directory にローカル override file が無い場合、親の `.cdidx-langmap.yaml` 解決結果を再利用し、巨大 scan 中の祖先 config probe の繰り返しを減らします。

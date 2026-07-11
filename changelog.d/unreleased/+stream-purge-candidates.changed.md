---
category: changed
affected:
  - src/CodeIndex/Database/DbWriter.cs
  - tests/CodeIndex.Tests/PreparedCommandCacheTests.cs
---

## English

- **Large-index purge scans retain only deletion candidates** - disk-stale, authoritative full-scan, and partial-authority purge paths now evaluate SQLite file rows as they are read instead of first materializing a second complete `(id, path)` list. Peak managed memory therefore scales with stale candidates rather than every indexed language file.

## 日本語

- **巨大 index の purge scan で削除候補だけを保持するようにしました** - disk-stale、authoritative full-scan、partial-authority purge は、完全な `(id, path)` 一覧を複製してから判定せず、SQLite の file 行を読みながら評価します。これにより managed memory のピークは全 indexed language file 数ではなく stale 候補数に応じて増えるようになります。

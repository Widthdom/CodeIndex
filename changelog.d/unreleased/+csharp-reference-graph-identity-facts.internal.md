---
category: internal
affected:
  - src/CodeIndex/Database/DbWriter.References.cs
  - src/CodeIndex/Database/DbWriter.ReferenceGraphRefreshScope.cs
  - tests/CodeIndex.Tests/DatabaseTests.cs
  - DEVELOPER_GUIDE.md
  - TESTING_GUIDE.md
---

## English

- **C# graph finalization reuses materialized type and constructor identities** — full, scoped, and retained reference-graph refreshes now build project/file-local type identities and ranked constructor-owner identities once per applicable symbol, replacing repeated candidate-side string construction and range scans with TEMP primary-key lookups.

## 日本語

- **C# graph finalization が materialize 済み type / constructor identity を再利用します** — full / scoped / retained の reference-graph refresh は project / file-local type identity と順位付き constructor-owner identity を対象 symbol ごとに1回だけ構築し、candidate 側で反復していた文字列生成と range scan を TEMP 主キー lookup に置き換えます。

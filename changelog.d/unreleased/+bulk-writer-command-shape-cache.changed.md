---
category: changed
affected:
  - src/CodeIndex/Database/DbWriter.cs
  - tests/CodeIndex.Tests/PreparedCommandCacheTests.cs
  - DEVELOPER_GUIDE.md
---

## English

- **Large indexes reuse prepared SQLite bulk-insert shapes** - chunk, symbol, reference, and reference-line writers now cache SQL text and typed parameter schemas by batch row count, then update only parameter values for later files. This reduces SQLite command construction, parameter allocation, and garbage collection across every indexed language while preserving bounded cache eviction and existing transaction boundaries.

## 日本語

- **大規模 index で SQLite bulk-insert の prepared shape を再利用するようにしました** - chunk、symbol、reference、reference-line writer は batch row count ごとに SQL text と型付き parameter schema を cache し、後続 file では parameter value だけを更新します。bounded cache eviction と既存 transaction boundary を維持しながら、全 indexed language で SQLite command 構築、parameter allocation、GC を削減します。

---
category: fixed
affected:
  - src/CodeIndex/Indexer/References/Languages/SqlReferenceExtractor.Patterns.cs
  - src/CodeIndex/Indexer/References/Languages/SqlReferenceExtractor.cs
  - tests/CodeIndex.Tests/ReferenceExtractorTests.cs
---

## English

- **SQL target extraction avoids a compiled-regex group crash** — `cdidx . --verbose` no longer relies on a large SQL mutation-target regex's repeated named captures when emitting target references, avoiding a possible `System.AccessViolationException` while preserving target reference extraction.

## 日本語

- **SQL target 抽出で compiled regex の group crash を回避しました** — `cdidx . --verbose` は SQL mutation target 参照の発行時に巨大な regex の繰り返し名前付き capture へ依存しなくなり、target reference 抽出を維持したまま `System.AccessViolationException` が起きうる経路を避けます。

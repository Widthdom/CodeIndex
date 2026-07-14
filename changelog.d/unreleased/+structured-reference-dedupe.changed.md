---
category: changed
affected:
  - src/CodeIndex/Indexer/References/
  - tests/CodeIndex.Tests/ReferenceExtractorTests.cs
  - tests/CodeIndex.Tests/PerformanceTests.cs
---

## English

- **Reference deduplication now keeps structured value keys across all languages** —
  Extractors no longer allocate and retain a concatenated string containing the
  file, language, location, kind, container, and name for every candidate. Dense
  long-identity coverage reduces the dedicated allocation fixture by about 89%
  while preserving file/language/container identity semantics.

## 日本語

- **全言語の reference deduplication が structured value key を保持するようになりました** —
  extractor は candidate ごとに file、language、location、kind、container、name を連結した
  string を作って保持しません。file / language / container の identity semantics を維持しつつ、
  長い identity の密な専用 fixture で allocation を約89%削減します。

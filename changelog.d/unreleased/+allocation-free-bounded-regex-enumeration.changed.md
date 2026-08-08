---
category: changed
affected:
  - DEVELOPER_GUIDE.md
  - TESTING_GUIDE.md
  - src/CodeIndex/Indexer/BoundedRegex.cs
  - src/CodeIndex/Indexer/References/ReferenceExtractor.cs
  - src/CodeIndex/Indexer/References/Languages/SqlReferenceExtractor.AlterTargets.cs
  - src/CodeIndex/Indexer/References/Languages/SqlReferenceExtractor.Sources.cs
  - src/CodeIndex/Indexer/References/Languages/SqlReferenceExtractor.StatementState.cs
  - src/CodeIndex/Indexer/References/Support/LanguageReferenceExtractionSupport.Utilities.cs
  - tests/CodeIndex.Tests/BoundedRegexTests.cs
  - tests/CodeIndex.Tests/PerformanceTests.cs
---

## English

- **Lazy regex match enumeration no longer allocates iterator wrappers on indexing hot paths** — instance-regex scans now use concrete value enumerables and enumerators through reference extraction and SQL helpers. Direct scans and already-full reference caps avoid iterator and interface-boxing allocations while preserving timeout diagnostics, zero-length and `\G` progression, explicit and right-to-left start positions, early termination, and LINQ compatibility.

## 日本語

- **indexing の hot path で lazy regex match 列挙の iterator wrapper allocation を解消しました** — instance regex の走査は、reference extraction と SQL helper を通して concrete な value enumerable / enumerator を使います。direct scan と既に満杯の reference cap は iterator および interface boxing allocation を避けながら、timeout diagnostic、zero-length と `\G` の進行、明示的および right-to-left の開始位置、早期終了、LINQ 互換性を維持します。

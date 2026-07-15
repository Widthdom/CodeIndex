---
category: changed
affected:
  - src/CodeIndex/Indexer/References/ReferenceExtractor.CoreExtraction.cs
  - src/CodeIndex/Indexer/References/Languages/GitHubActionsReferenceExtractor.cs
  - tests/CodeIndex.Tests/PerformanceTests.cs
---

## English

- **Reference container ownership now uses name-indexed candidates** — C#
  declaration/range resolution and GitHub Actions job lookup no longer rescan
  every container symbol for each reference. Dense C# and YAML fixtures keep
  the lookup allocation bounded.

## 日本語

- **reference の container ownership が name-indexed candidate を使うようになりました** —
  C# の declaration / range 解決と GitHub Actions の job lookup は、reference ごとに全
  container symbol を再走査しません。密な C# / YAML fixture で lookup allocation の
  上限を維持します。

---
category: changed
affected:
  - src/CodeIndex/Indexer/References/ReferenceExtractor.CoreExtraction.cs
  - src/CodeIndex/Indexer/References/ReferenceExtractor.cs
  - tests/CodeIndex.Tests/PerformanceTests.cs
  - tests/CodeIndex.Tests/ReferenceExtractorTests.cs
  - TESTING_GUIDE.md
---

## English

- **Large reference sets now avoid redundant post-processing allocations** — C# files without applicable using-alias rewrites skip alias dedupe entirely, rewritten aliases compact duplicates in one stable pass, BCL Regex auditing reuses container identity values directly, and repeated caller/callee names share cycle normalization results across all languages.

## 日本語

- **大量の参照に対する不要な後処理 allocation を削減しました** — 適用対象の using-alias rewrite がない C# ファイルでは alias dedupe を完全に省略し、rewrite 後の重複は stable な1回の走査で compact します。BCL Regex audit は container identity 値を直接再利用し、全言語で繰り返される caller / callee 名の cycle 正規化結果を共有します。

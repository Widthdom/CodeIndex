---
category: fixed
affected:
  - src/CodeIndex/Indexer/References/ReferenceExtractor.CoreReferenceLoop.cs
  - tests/CodeIndex.Tests/PerformanceTests.cs
  - DEVELOPER_GUIDE.md
  - TESTING_GUIDE.md
---

## English

- **Reduced reference-extraction allocations for large embedded multiline payloads** —
  Structurally masked C# raw strings, Java text blocks, and JavaScript /
  TypeScript template literals no longer materialize trimmed reference
  contexts for lines that will be skipped.

## 日本語

- **巨大な埋め込み multiline payload に対する reference extraction の allocation を削減しました** —
  構造マスク済みの C# raw string、Java text block、JavaScript / TypeScript template literal
  では、skip される行の trim 済み reference context を実体化しません。

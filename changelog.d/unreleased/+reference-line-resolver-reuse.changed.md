---
category: changed
affected:
  - src/CodeIndex/Indexer/References/ReferenceExtractor.CoreContainerResolution.cs
  - src/CodeIndex/Indexer/References/ReferenceExtractor.CoreReferenceLine.cs
  - src/CodeIndex/Indexer/References/ReferenceExtractor.CoreReferenceLoop.cs
  - src/CodeIndex/Indexer/References/ReferenceExtractor.PatternTypeReferences.cs
  - tests/CodeIndex.Tests/ReferenceExtractorTests.cs
  - tests/CodeIndex.Tests/ReferenceExtractorRustSwiftTests.cs
  - tests/CodeIndex.Tests/ReferenceExtractorPerformanceBudgetTests.cs
  - DEVELOPER_GUIDE.md
  - TESTING_GUIDE.md
---

## English

- **Initial reference extraction reuses per-file line state instead of allocating it for every declaration** — the synchronous core line loop now creates its container resolver and bound delegates once per extraction, while common non-generic C# declarations share a read-only empty generic-parameter set after a cheap `<` gate. Container attribution, generic suppression, exception and cancellation behavior, and concurrent extraction isolation remain unchanged.

## 日本語

- **初回の参照抽出で、宣言行ごとの state allocation を file 内で再利用します** — 同期的な core line loop は container resolver と bound delegate を extraction ごとに1回だけ生成し、一般的な非 generic C# declaration は安価な `<` gate の後に read-only の空 generic-parameter 集合を共有します。container 帰属、generic parameter の抑制、例外と cancellation の動作、並行 extraction の分離は維持します。

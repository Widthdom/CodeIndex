---
category: changed
affected:
  - src/CodeIndex/Indexer/References/ReferenceExtractor.CoreReferenceLoop.cs
  - src/CodeIndex/Indexer/References/ReferenceExtractor.CoreTypeReferences.cs
  - src/CodeIndex/Indexer/References/ReferenceExtractor.CoreTypeCSharpReferences.cs
  - src/CodeIndex/Indexer/References/Languages/CSharpReferenceExtractor.QualifiedPatterns.cs
  - tests/CodeIndex.Tests/ReferenceExtractorReceiverScopeTests.cs
  - tests/CodeIndex.Tests/ReferenceExtractorPerformanceBudgetTests.cs
  - tests/CodeIndex.Tests/ReferenceExtractorCSharpTests.cs
---

## English

- **C# reference extraction skips unnecessary capture and enum analysis during full indexing** — C#, Razor, Blazor, and CSHTML avoid local-declaration matching in callable bodies without a prepared lambda arrow, skip enum scans when no enum members are available, and defer qualified-name storage until a separator is found. Capture scope isolation, qualified enum reads, and name normalization remain unchanged.

## 日本語

- **フルインデックス時の C# 参照抽出で不要なキャプチャ・列挙型解析を省略** — C#、Razor、Blazor、CSHTML では、前処理済みの本体にラムダ矢印がない関数のローカル宣言照合と、列挙型メンバーが存在しない場合の列挙型走査を省略し、修飾名の保存領域も区切り文字が現れるまで確保しなくなりました。キャプチャのスコープ分離、修飾された列挙型メンバーの読み取り参照、名前の正規化は維持します。

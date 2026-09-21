---
category: changed
affected:
  - src/CodeIndex/Indexer/References/Languages/CSharpReferenceExtractor.QualifiedPatterns.cs
  - src/CodeIndex/Indexer/References/Languages/CSharpReferenceExtractor.Support.cs
  - tests/CodeIndex.Tests/ReferenceExtractorReceiverScopeTests.cs
  - tests/CodeIndex.Tests/ReferenceExtractorPerformanceBudgetTests.cs
---

## English

- **C# reference extraction avoids redundant receiver-scope work** — impossible enum-member candidates are rejected before building shadow scopes, and ordinary/recursive patterns reuse their callable's body text and known offsets. C#, Razor, Blazor and CSHTML retain their reference coordinates and shadowing behavior while reducing repeated scans and allocations during initial indexing.

## 日本語

- **C#参照抽出でreceiver scopeの重複処理を削減** — 成立しないenum member候補を隠蔽scopeの作成前に除外し、通常／recursive patternはcallableの本文と既知のoffsetを再利用します。C#・Razor・Blazor・CSHTMLの参照位置と隠蔽の挙動を保ち、初回インデックスでの反復走査と割り当てを減らします。

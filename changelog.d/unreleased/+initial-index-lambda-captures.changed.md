---
category: changed
affected:
  - src/CodeIndex/Indexer/References/ReferenceExtractor.CoreTypeCSharpReferences.cs
  - tests/CodeIndex.Tests/ReferenceExtractorCSharpTests.cs
---

## English

- Reduce repeated lambda-body scans and function-scope key allocations during C#, Razor, Blazor and CSHTML indexing while preserving capture names, order, scope and source positions.

## 日本語

- C#・Razor・Blazor・CSHTML のインデックス処理で、lambda 本体の反復走査と関数スコープ key の文字列割り当てを削減し、捕捉名・順序・スコープ・ソース位置を維持しました。

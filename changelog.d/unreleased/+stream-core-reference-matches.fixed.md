---
category: fixed
affected:
  - src/CodeIndex/Indexer/References/Languages/CSharpReferenceExtractor.cs
  - src/CodeIndex/Indexer/References/Languages/CSharpReferenceExtractor.Support.cs
  - src/CodeIndex/Indexer/References/ReferenceExtractor.CoreCallReferences.cs
  - src/CodeIndex/Indexer/References/ReferenceExtractor.CoreLanguageLines.cs
  - src/CodeIndex/Indexer/References/ReferenceExtractor.CoreSpecializedLines.cs
  - src/CodeIndex/Indexer/References/ReferenceExtractor.CoreTypeReferences.cs
  - src/CodeIndex/Indexer/References/ReferenceExtractor.CSharpPatterns.cs
  - src/CodeIndex/Indexer/References/ReferenceExtractor.CSharpTypeNames.cs
  - src/CodeIndex/Indexer/References/ReferenceExtractor.PatternTypeReferences.cs
  - src/CodeIndex/Indexer/References/ReferenceExtractor.PrimaryConstructors.cs
  - src/CodeIndex/Indexer/References/ReferenceExtractor.ReferenceRecords.cs
  - src/CodeIndex/Indexer/References/ReferenceExtractor.Solidity.cs
  - DEVELOPER_GUIDE.md
  - TESTING_GUIDE.md
---

## English

- **Streamed core reference matches** — Shared calls, C# attributes, types,
  patterns and locals, JSX elements, JVM documentation links, and Solidity
  scanners now consume single-pass match groups on demand.

## 日本語

- **core reference match を逐次走査にしました** — 共有 call、C# attribute / type /
  pattern / local、JSX element、JVM documentation link、Solidity scanner は
  single-pass の match group を demand-driven に消費します。

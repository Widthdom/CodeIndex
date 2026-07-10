---
category: changed
affected:
  - src/CodeIndex/Indexer/References/Languages/TypeScriptReferenceExtractor.cs
---

## English

- **TypeScript alias scans now pre-size hot collections** — namespace aliases, type aliases, local declaration maps, and parameter shadow ranges use small initial capacities to reduce growth work during large TypeScript/JavaScript indexing runs.

## 日本語

- **TypeScript alias scan が高頻度 collection の初期容量を指定するようになりました** — namespace alias、type alias、local declaration map、parameter shadow range に小さな初期容量を持たせ、大規模 TypeScript/JavaScript index 時の拡張処理を減らしました。

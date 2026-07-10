---
category: changed
affected:
  - src/CodeIndex/Indexer/References/ReferenceExtractor.PatternTypeReferences.cs
  - src/CodeIndex/Indexer/References/ReferenceExtractor.TypeReferences.cs
  - src/CodeIndex/Indexer/References/Languages/JavaReferenceExtractor.cs
---

## English

- **Java type-list terminator scans now accept spans** — keyword type lists avoid rescanning the full line with an absolute start offset before splitting references.

## 日本語

- **Java type-list terminator scan が span 入力を扱うようになりました** — keyword type list の参照分割前に、絶対 offset 付きで行全体を再走査しないようにしました。

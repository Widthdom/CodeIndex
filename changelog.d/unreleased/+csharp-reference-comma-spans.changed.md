---
category: changed
affected:
  - src/CodeIndex/Indexer/References/ReferenceExtractor.PatternTypeReferences.cs
---

## English

- **C# reference comma clauses scan with spans** — base lists, generic parameter clauses, where constraints, and callable parameter lists now avoid copying the whole comma-delimited clause before segment extraction.

## 日本語

- **C# reference の comma clause を span 走査するようになりました** — base list、generic parameter clause、where constraint、callable parameter list で segment 抽出前の clause 全体コピーを避けます。

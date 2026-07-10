---
category: changed
affected:
  - src/CodeIndex/Indexer/References/ReferenceExtractor.PatternTypeReferences.cs
  - src/CodeIndex/Indexer/References/Languages/JavaReferenceExtractor.cs
---

## English

- **Java generic bound extraction now scans headers with spans** — class/interface/record generic bounds avoid copying the header, parameter clause, and bound list before top-level keyword and `&` scans.

## 日本語

- **Java generic bound 抽出が header を span で走査するようになりました** — class/interface/record の generic bound 解析で、top-level keyword と `&` の走査前に header、parameter clause、bound list をコピーしないようにしました。

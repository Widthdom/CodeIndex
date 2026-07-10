---
category: changed
affected:
  - src/CodeIndex/Indexer/References/ReferenceExtractor.PatternTypeReferences.cs
  - src/CodeIndex/Indexer/References/Support/LanguageReferenceExtractionSupport.cs
  - src/CodeIndex/Indexer/References/Support/LanguageReferenceExtractionSupport.Go.cs
---

## English

- **Reference splitter buffers now start with small capacities** — shared comma/pipe/ampersand and C/Go splitters reduce list growth while preserving the single-segment fast paths.

## 日本語

- **reference splitter buffer が小さな初期容量を持つようになりました** — shared comma/pipe/ampersand と C/Go splitter で、single-segment fast path を保ちながら list 拡張を減らしました。

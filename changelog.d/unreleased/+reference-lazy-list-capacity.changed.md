---
category: changed
affected:
  - src/CodeIndex/Indexer/References/Support/LanguageReferenceExtractionSupport.cs
  - src/CodeIndex/Indexer/References/Languages/JavaReferenceExtractor.cs
---

## English

- **Java and Razor reference helpers now pre-size lazy lists** — Java constructor candidate scans and Razor `@implements` collection avoid default list growth during large reference extraction runs.

## 日本語

- **Java と Razor reference helper が lazy list の初期容量を指定するようになりました** — Java constructor candidate scan と Razor `@implements` collection で、大規模 reference extraction 時の既定 list 拡張を避けます。

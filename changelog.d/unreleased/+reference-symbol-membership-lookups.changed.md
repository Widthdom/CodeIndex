---
category: changed
affected:
  - src/CodeIndex/Indexer/References/Languages/PythonImportBindingResolver.cs
  - src/CodeIndex/Indexer/References/ReferenceExtractor.CoreExtraction.cs
  - tests/CodeIndex.Tests/PerformanceTests.cs
---

## English

- **Reference extraction now reuses file-local symbol membership lookups** — C#
  private-property receivers and Python classes/import bindings no longer cause
  a full extracted-symbol rescan for every matching call site. A bounded
  allocation guard covers dense fixtures in both languages.

## 日本語

- **reference extraction が file-local symbol membership lookup を再利用するようになりました** —
  C# の private-property receiver と Python の class / import binding は、一致する call site
  ごとの全 extracted-symbol 再走査を行いません。両言語の密な fixture を使う bounded
  allocation guard も追加しました。

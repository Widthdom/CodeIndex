---
category: changed
affected:
  - src/CodeIndex/Indexer/BoundedRegex.cs
  - tests/CodeIndex.Tests/BoundedRegexTests.cs
---

## English

- **Large multi-language indexes retain more shared static extraction regexes** - bounded extraction now keeps a cache sized for the shared pattern set, reducing repeated pattern construction, allocation pressure, and garbage collection when indexing large repositories.

## 日本語

- **大規模な複数言語 index で共有 static extraction regex をより多く保持するようにしました** - bounded extraction は共有 pattern 群に対応する容量の cache を保持することで、大規模 repository の indexing 時に繰り返される pattern 構築、allocation 負荷、GC を削減します。

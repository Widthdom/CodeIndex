---
category: changed
affected:
  - src/CodeIndex/Indexer/References
  - tests/CodeIndex.Tests/PerformanceTests.cs
  - tests/CodeIndex.Tests/ReferenceExtractorSourceContextTests.cs
  - TESTING_GUIDE.md
  - DEVELOPER_GUIDE.md
---

## English

- **Initial indexing defers trimmed reference contexts until a row is emitted** — reference-free source lines no longer allocate context strings while the core, functional-language, and Solidity extractors scan them. Emitted contexts remain trimmed and reference columns continue to use the physical source line.

## 日本語

- **初回indexでtrim済みreference contextをrow発行時まで遅延するようになりました** — core、functional language、Solidityのextractorが走査する際、referenceを出さないsource lineではcontext文字列を割り当てません。発行されたcontextは従来どおりtrim済みで、reference columnも物理source line基準を維持します。

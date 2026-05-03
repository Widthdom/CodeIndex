---
category: fixed
affected:
  - README.md
  - src/CodeIndex/Indexer/ReferenceExtractor.cs
  - tests/CodeIndex.Tests/ReferenceExtractorTests.cs
---

## English

- **COBOL `COPY` statements now surface as searchable references** - `ReferenceExtractor` now records common COBOL copybook includes as `reference` edges, so copybook names can be traced from the consuming program instead of disappearing into plain text only.

## 日本語

- **COBOL の `COPY` 文が検索可能な reference として出るようになりました** - `ReferenceExtractor` が一般的な COBOL copybook include を `reference` edge として記録するため、copybook 名を消費側プログラムから辿れるようになり、単なる全文検索頼みになりません。

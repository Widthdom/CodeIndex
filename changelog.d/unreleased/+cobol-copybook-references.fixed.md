---
category: fixed
affected:
  - README.md
  - src/CodeIndex/Indexer/ReferenceExtractor.cs
  - tests/CodeIndex.Tests/ReferenceExtractorTests.cs
---

## English

- **COBOL procedural statements now surface as searchable references** - `ReferenceExtractor` now records common COBOL copybook includes and everyday statement targets such as `GO TO`, `READ`, `WRITE`, `OPEN`, `MOVE`, `ADD`, `COMPUTE`, and `SEARCH` as `reference` edges, so the names involved in COBOL control flow and data flow stay searchable instead of disappearing into plain text only.

## 日本語

- **COBOL の手続き文が検索可能な reference として出るようになりました** - `ReferenceExtractor` が COBOL の copybook include に加えて `GO TO`、`READ`、`WRITE`、`OPEN`、`MOVE`、`ADD`、`COMPUTE`、`SEARCH` などの文の対象名も `reference` edge として記録するため、COBOL の制御フローやデータフローに出てくる名前を全文検索だけに頼らず辿れます。

---
category: fixed
affected:
  - src/CodeIndex/Indexer/References/ReferenceExtractor.TypeReferences.cs
  - tests/CodeIndex.Tests/ReferenceExtractorTests.cs
---

## English

- **Reduced reference extraction work during full indexing** — per-line string and inline block-comment regex cleanup now runs only on lines containing matching delimiters.

## 日本語

- **full index 時の reference extraction 作業を削減しました** — 行単位の文字列 literal / inline block comment regex cleanup は、該当 delimiter を含む行だけで実行するようになりました。

---
category: fixed
affected:
  - src/CodeIndex/Indexer/References/Languages/FunctionalLanguageReferenceExtractor.cs
  - src/CodeIndex/Indexer/References/Languages/FunctionalLanguageReferenceExtractor.Erlang.cs
  - src/CodeIndex/Indexer/References/Languages/FunctionalLanguageReferenceExtractor.Ocaml.cs
  - src/CodeIndex/Indexer/References/Languages/FunctionalLanguageReferenceExtractor.Raku.cs
  - tests/CodeIndex.Tests/PerformanceTests.cs
  - DEVELOPER_GUIDE.md
  - TESTING_GUIDE.md
---

## English

- **Stopped allocating empty exclusion lists on functional-language lines** —
  Erlang, OCAML, and Raku now create quoted, remote, type, qualified, and method
  span lists only after the first relevant match on a line.

## 日本語

- **functional-language line ごとの empty exclusion list allocation を解消しました** —
  Erlang、OCAML、Raku は quoted、remote、type、qualified、method span list を、行内の
  最初の関連 match が見つかった後にだけ作ります。

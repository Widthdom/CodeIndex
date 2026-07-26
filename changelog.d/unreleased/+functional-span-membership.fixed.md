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

- **Removed per-match closure allocations from functional-language references** —
  Erlang, OCAML, and Raku now use shared indexed span-membership checks when
  suppressing remote, qualified, quoted-atom, and type-reference matches.

## 日本語

- **functional-language reference の match ごとの closure allocation を解消しました** —
  Erlang、OCAML、Raku は remote、qualified、quoted-atom、type-reference match の抑制時に、
  共通の indexed span-membership check を使います。

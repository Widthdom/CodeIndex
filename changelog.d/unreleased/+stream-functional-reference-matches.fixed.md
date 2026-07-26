---
category: fixed
affected:
  - src/CodeIndex/Indexer/References/Languages/FunctionalLanguageReferenceExtractor.Clojure.cs
  - src/CodeIndex/Indexer/References/Languages/FunctionalLanguageReferenceExtractor.Erlang.cs
  - src/CodeIndex/Indexer/References/Languages/FunctionalLanguageReferenceExtractor.Ocaml.cs
  - src/CodeIndex/Indexer/References/Languages/FunctionalLanguageReferenceExtractor.Raku.cs
  - src/CodeIndex/Indexer/References/Languages/ElixirReferenceExtractor.cs
  - DEVELOPER_GUIDE.md
  - TESTING_GUIDE.md
---

## English

- **Streamed functional-language reference matches** — Clojure, Elixir,
  Erlang, OCAML, and Raku now enumerate regex results on demand and stop dense
  lines as soon as the per-file reference budget is full.

## 日本語

- **functional-language の reference match を逐次走査にしました** — Clojure、
  Elixir、Erlang、OCAML、Raku は regex result を demand-driven に列挙し、per-file
  reference budget が満杯になると dense line の走査を停止します。

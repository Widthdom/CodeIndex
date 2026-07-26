---
category: fixed
affected:
  - src/CodeIndex/Indexer/References/Languages/FunctionalLanguageReferenceExtractor.cs
  - src/CodeIndex/Indexer/References/Languages/FunctionalLanguageReferenceExtractor.Erlang.cs
  - tests/CodeIndex.Tests/PerformanceTests.cs
  - DEVELOPER_GUIDE.md
  - TESTING_GUIDE.md
---

## English

- **Removed padded-line copies from functional-language state machines** —
  Erlang specification/callable terminators and Raku heredoc terminators now
  compare trimmed spans, avoiding a new string for every padded sentinel line.

## 日本語

- **functional-language state machine の padded-line copy を解消しました** —
  Erlang specification / callable terminator と Raku heredoc terminator は trimmed span
  を比較し、padding 付き sentinel line ごとの新しい string を回避します。

---
category: internal
affected:
  - src/CodeIndex/Indexer/ReferenceExtractor.cs
  - src/CodeIndex/Indexer/RubyReferenceExtractor.cs
  - docs/DEVELOPER_GUIDE.md
---

## English

- **Extracted the Ruby reference helper** — Ruby command-style calls, block calls, and DSL target references now live in a dedicated helper while preserving existing reference output.

## 日本語

- **Ruby の reference helper を分割しました** — Ruby の command-style call、block call、DSL target 参照を専用 helper へ移し、既存の reference 出力は維持しました。

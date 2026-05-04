---
category: internal
affected:
  - src/CodeIndex/Indexer/ReferenceExtractor.cs
  - src/CodeIndex/Indexer/BatchReferenceExtractor.cs
  - src/CodeIndex/Indexer/ShellReferenceExtractor.cs
  - docs/DEVELOPER_GUIDE.md
---

## English

- **Extracted Shell and Batch reference helpers** — Shell command/source/global-alias references and Batch `goto` / `call` label-target references now live in dedicated helper classes while preserving existing reference output.

## 日本語

- **Shell と Batch の reference helper を分割しました** — Shell の command / source / global alias 参照と Batch の `goto` / `call` label target 参照を専用 helper へ移し、既存の reference 出力は維持しました。

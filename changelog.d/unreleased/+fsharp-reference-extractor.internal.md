---
category: internal
affected:
  - src/CodeIndex/Indexer/ReferenceExtractor.cs
  - src/CodeIndex/Indexer/FSharpReferenceExtractor.cs
  - docs/DEVELOPER_GUIDE.md
---

## English

- **Extracted the F# reference helper** — F# pipeline, space-application, and symbolic operator call references now live in a dedicated helper while preserving existing reference output.

## 日本語

- **F# の reference helper を分割しました** — F# の pipeline、space application、symbolic operator call 参照を専用 helper へ移し、既存の reference 出力は維持しました。

---
category: internal
affected:
  - docs/DEVELOPER_GUIDE.md
  - src/CodeIndex/Indexer/CobolReferenceExtractor.cs
  - src/CodeIndex/Indexer/ReferenceExtractor.cs
---

## English

- **COBOL reference extraction was split out of the large shared extractor** - COBOL statement regexes and `PERFORM ... THRU ...` range expansion now live in a dedicated helper, reducing repetition in the shared `ReferenceExtractor` while preserving the indexed reference behavior.

## 日本語

- **COBOL reference 抽出を大きな共通 extractor から分離しました** - COBOL 文の regex と `PERFORM ... THRU ...` の範囲展開を専用 helper に移し、indexed reference の挙動を保ったまま共通 `ReferenceExtractor` 内の重複を減らしました。

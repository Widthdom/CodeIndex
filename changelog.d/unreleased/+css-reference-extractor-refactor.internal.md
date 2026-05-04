---
category: internal
affected:
  - docs/DEVELOPER_GUIDE.md
  - src/CodeIndex/Indexer/CssReferenceExtractor.cs
  - src/CodeIndex/Indexer/ReferenceExtractor.cs
---

## English

- **CSS/SCSS reference extraction was split out of the large shared extractor** - custom property, animation, selector, variable, and extend references now use a dedicated helper with shared reference-emission loops, reducing `ReferenceExtractor` size while preserving indexed behavior.

## 日本語

- **CSS/SCSS reference 抽出を大きな共通 extractor から分離しました** - custom property、animation、selector、variable、extend の参照を専用 helper と共通 reference 出力 loop に移し、indexed behavior を維持したまま `ReferenceExtractor` の肥大化を抑えました。

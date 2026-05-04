---
category: internal
affected:
  - src/CodeIndex/Indexer/ReferenceExtractor.cs
  - src/CodeIndex/Indexer/CSharpReferenceExtractor.cs
  - docs/DEVELOPER_GUIDE.md
---

## English

- **Routed remaining C# reference scanners through the C# helper** — C# type-position references, XML-doc `cref` references, pattern-head suppression, switch-expression patterns, and qualified enum-member references now enter through `CSharpReferenceExtractor` while preserving existing extraction behavior.

## 日本語

- **残っていた C# 参照スキャナーを C# helper 経由にしました** — C# の type-position reference、XML-doc `cref` reference、pattern-head 抑制、switch expression pattern、qualified enum member reference が `CSharpReferenceExtractor` を入口にし、既存の抽出挙動は維持しました。

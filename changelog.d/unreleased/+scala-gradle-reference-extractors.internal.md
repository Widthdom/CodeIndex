---
category: internal
affected:
  - src/CodeIndex/Indexer/ReferenceExtractor.cs
  - src/CodeIndex/Indexer/ScalaReferenceExtractor.cs
  - src/CodeIndex/Indexer/GradleReferenceExtractor.cs
  - docs/DEVELOPER_GUIDE.md
---

## English

- **Extracted Scala and Gradle reference helpers** — Scala trailing block calls and Gradle/Groovy DSL call passes now live in dedicated helpers while preserving existing reference output.

## 日本語

- **Scala と Gradle の reference helper を分割しました** — Scala の trailing block call と Gradle/Groovy の DSL call pass を専用 helper へ移し、既存の reference 出力は維持しました。

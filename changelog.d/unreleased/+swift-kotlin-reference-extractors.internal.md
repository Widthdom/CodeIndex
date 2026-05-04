---
category: internal
affected:
  - src/CodeIndex/Indexer/ReferenceExtractor.cs
  - src/CodeIndex/Indexer/SwiftReferenceExtractor.cs
  - src/CodeIndex/Indexer/KotlinReferenceExtractor.cs
  - src/CodeIndex/Indexer/TrailingLambdaReferenceExtractor.cs
  - docs/DEVELOPER_GUIDE.md
---

## English

- **Extracted Swift and Kotlin reference helpers** — Swift trailing closure and Kotlin trailing lambda call references now enter through dedicated helpers backed by a shared scanner.

## 日本語

- **Swift と Kotlin の reference helper を分割しました** — Swift の trailing closure と Kotlin の trailing lambda call 参照を専用 helper 経由にし、共通 scanner で重複を避けました。

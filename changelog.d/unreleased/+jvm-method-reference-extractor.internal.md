---
category: internal
affected:
  - src/CodeIndex/Indexer/ReferenceExtractor.cs
  - src/CodeIndex/Indexer/JavaReferenceExtractor.cs
  - src/CodeIndex/Indexer/KotlinReferenceExtractor.cs
  - src/CodeIndex/Indexer/ScalaReferenceExtractor.cs
  - src/CodeIndex/Indexer/JvmMethodReferenceExtractor.cs
  - docs/DEVELOPER_GUIDE.md
---

## English

- **Extracted JVM method-reference helpers** — Java, Kotlin, and Scala `::` method-reference indexing now enters through language-specific helpers backed by a shared JVM scanner.

## 日本語

- **JVM method-reference helper を分割しました** — Java、Kotlin、Scala の `::` method-reference 索引化を言語別 helper 経由にし、共通 JVM scanner で重複を避けました。

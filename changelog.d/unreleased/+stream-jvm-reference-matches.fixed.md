---
category: fixed
affected:
  - src/CodeIndex/Indexer/References/Languages/JavaReferenceExtractor.Modules.cs
  - src/CodeIndex/Indexer/References/Languages/JavaReferenceExtractor.Types.cs
  - src/CodeIndex/Indexer/References/Languages/KotlinReferenceExtractor.cs
  - src/CodeIndex/Indexer/References/Languages/ScalaReferenceExtractor.cs
  - src/CodeIndex/Indexer/References/Languages/GradleReferenceExtractor.cs
  - src/CodeIndex/Indexer/References/Support/JvmMethodReferenceExtractor.cs
  - DEVELOPER_GUIDE.md
  - TESTING_GUIDE.md
---

## English

- **Streamed JVM-family reference matches** — Java, Kotlin, Scala, and
  Gradle/Groovy scanners now consume dense regex results on demand and stop
  bounded reference loops once the per-file cap is reached.

## 日本語

- **JVM-family の reference match を逐次走査にしました** — Java、Kotlin、Scala、
  Gradle / Groovy scanner は dense な regex result を demand-driven に消費し、bounded
  reference loop は per-file 上限に達すると停止します。

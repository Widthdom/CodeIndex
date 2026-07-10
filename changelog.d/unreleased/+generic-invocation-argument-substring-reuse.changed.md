---
category: changed
affected:
  - src/CodeIndex/Indexer/References/ReferenceExtractor.TypeReferences.cs
---

## English

- **Generic invocation argument extraction now reuses its slice** — C#, Java, and Kotlin generic invocation references avoid creating the same argument substring twice.

## 日本語

- **generic invocation argument 抽出で slice を再利用するようになりました** — C#、Java、Kotlin の generic invocation 参照抽出で、同じ argument 部分文字列を2回作らないようにしました。

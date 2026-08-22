---
category: internal
affected:
  - tests/CodeIndex.Tests/IndexCommandRunnerInitialFullIndexPerformanceTests.cs
  - TESTING_GUIDE.md
---

## English

- **Cold full-index performance work now has an end-to-end multilingual regression contract** — a fresh external database fixture exercises the real CLI pipeline for C#, TypeScript, Python, Java, Go, Rust, C++, and Kotlin, checks phase ordering, persisted counts, readiness, workspace status, FTS searchability, and database integrity under a broad runaway budget, and provides a larger opt-in manual smoke using the same assertions.

## 日本語

- **空DBの初回フルインデックス高速化に、多言語のend-to-end回帰契約を追加しました** — 外部の新規DBを使うfixtureがC#、TypeScript、Python、Java、Go、Rust、C++、Kotlinの実CLI pipelineを通り、広いrunaway budgetのもとでphase順序、永続化件数、readiness、workspace status、FTS検索可能性、DB integrityを検証します。同じassertionを使う大きめのopt-in手動smokeも追加しました。

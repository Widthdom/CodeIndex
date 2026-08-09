---
category: changed
affected:
  - DEVELOPER_GUIDE.md
  - TESTING_GUIDE.md
  - src/CodeIndex/Indexer/References/ReferenceExtractor.CoreCallReferences.cs
  - src/CodeIndex/Indexer/References/ReferenceExtractor.CoreTypeReferences.cs
  - src/CodeIndex/Indexer/References/Languages/
  - src/CodeIndex/Indexer/References/Support/
  - tests/CodeIndex.Tests/ReferenceExtractorMarkerGateTests.cs
  - tests/CodeIndex.Tests/ReferenceExtractorPerformanceBudgetTests.cs
---

## English

- **Large multi-language indexes avoid impossible per-line reference regex work** — reference extraction now checks cheap syntax markers before entering expensive call, type, framework, and markup patterns across C#, Java, Kotlin, JavaScript/TypeScript, Go, Dockerfile, GraphQL, HTML, Markdown, XAML/XML, Terraform, JSON, and GitHub Actions. Stateful multi-line parsing and syntax without the shared markers retain their existing references, while markerless source lines complete with substantially less regex work.

## 日本語

- **大規模な multi-language index で成立しない行単位 reference regex の実行を回避しました** — C#、Java、Kotlin、JavaScript/TypeScript、Go、Dockerfile、GraphQL、HTML、Markdown、XAML/XML、Terraform、JSON、GitHub Actions の call、type、framework、markup pattern を処理する前に、安価な構文 marker を確認します。stateful な複数行解析と共有 marker を持たない構文の既存 reference は維持しつつ、marker のない source 行に対する regex work を大幅に減らします。

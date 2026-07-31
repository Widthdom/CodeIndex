---
category: fixed
affected:
  - src/CodeIndex/Models/SymbolKindCatalog.cs
  - tests/CodeIndex.Tests/ReferenceExtractorTests.cs
  - DEVELOPER_GUIDE.md
---

## English

- **SQL generated-column references no longer fail during indexing** — registered the `generated_column_dependency` reference kind emitted for generated/computed-column expressions, so persisting SQL reference batches no longer raises `ArgumentException`.

## 日本語

- **SQL generated column の参照で index が失敗しなくなりました** — generated / computed column 式で抽出される `generated_column_dependency` reference kind を登録し、SQL reference batch の永続化時に `ArgumentException` が発生しないようにしました。

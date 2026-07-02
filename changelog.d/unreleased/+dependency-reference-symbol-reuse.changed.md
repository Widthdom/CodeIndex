---
category: changed
affected:
  - src/CodeIndex/Indexer/DependencyPackageExtractor.cs
  - src/CodeIndex/Indexer/References/ReferenceExtractor.Core.cs
  - src/CodeIndex/Indexer/References/ReferenceExtractor.StructuralMetadata.cs
  - tests/CodeIndex.Tests/DependencyPackageExtractorTests.cs
---

## English

- **Dependency references now reuse extracted package symbols** — large dependency lock and manifest files no longer repeat the package parser during reference extraction when symbol extraction already found packages, reducing duplicate JSON/XML/TOML work during indexing.

## 日本語

- **依存関係参照が抽出済み package symbol を再利用するようになりました** — 大きな dependency lock / manifest ファイルで、symbol 抽出済みの package がある場合は参照抽出時に package parser を再実行しないため、インデックス時の JSON/XML/TOML の重複処理を減らします。

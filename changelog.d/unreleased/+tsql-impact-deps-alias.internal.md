---
category: internal
affected:
  - src/CodeIndex/Database/DbReader.cs
  - tests/CodeIndex.Tests/DbReaderTests.cs
  - tests/CodeIndex.Tests/QueryCommandRunnerTests.cs
---

## English

- **Expanded regression coverage for the `--lang tsql` alias on impact and dependency queries** — added tests that exercise the SQL alias through `AnalyzeImpact` and `GetFileDependencies`, so the alias stays covered beyond the original `symbols` and `references` checks.

## 日本語

- **`--lang tsql` 別名の impact と dependency クエリ向け回帰テストを拡充しました** — `AnalyzeImpact` と `GetFileDependencies` で SQL 別名を通すテストを追加し、元の `symbols` / `references` 確認だけに依存しないようにしました。

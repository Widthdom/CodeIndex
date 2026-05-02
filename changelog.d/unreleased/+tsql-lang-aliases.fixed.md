---
category: fixed
affected:
  - src/CodeIndex/Cli/QueryCommandRunner.cs
  - tests/CodeIndex.Tests/QueryCommandRunnerTests.cs
---

## English

- **`--lang` now accepts common T-SQL spellings** — `cdidx search` and the other query commands now normalize `t-sql` and `transact-sql` to `sql`, so T-SQL users no longer need to remember the exact canonical filter spelling.

## 日本語

- **`--lang` が一般的な T-SQL 表記を受け付けるようになりました** — `cdidx search` を含む各種 query コマンドが `t-sql` と `transact-sql` を `sql` に正規化するため、T-SQL 利用時に canonical なフィルタ表記を覚えていなくても検索できます。

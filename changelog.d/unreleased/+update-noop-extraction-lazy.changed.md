---
category: changed
affected:
  - src/CodeIndex/Cli/IndexCommandRunner.Update.cs
  - src/CodeIndex/Cli/IndexCommandRunner.cs
  - tests/CodeIndex.Tests/IndexCommandRunnerTests.cs
---

## English

- **No-op scoped updates skip extraction setup** — `cdidx index --files` / commit-scoped update runs now avoid creating symbol extraction workers and discovering post-extraction hooks until a file actually needs re-indexing.

## 日本語

- **変更なし scoped update で抽出基盤の起動を省くようになりました** — `cdidx index --files` や commit scoped update は、実際に再インデックスが必要なファイルが出るまで symbol extraction worker と post-extraction hook discovery を作らないようになりました。

---
category: fixed
affected:
  - src/CodeIndex/Database/DbReader.cs
  - src/CodeIndex/Cli/QueryCommandRunner.cs
  - tests/CodeIndex.Tests/QueryCommandRunnerTests.cs
---

## English

- **Ruby and F# now accept their common short language aliases** — `--lang rb` and `--lang fs` now normalize to `ruby` and `fsharp`, so the query layer and completion aliases accept the short forms people usually type.

## 日本語

- **Ruby と F# でも一般的な短縮言語名を受け付けるようになりました** — `--lang rb` と `--lang fs` はそれぞれ `ruby` と `fsharp` に正規化されるため、クエリ層と補完エイリアスで普段入力される短縮形をそのまま使えます。

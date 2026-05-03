---
category: fixed
affected:
  - src/CodeIndex/Database/DbReader.cs
  - src/CodeIndex/Cli/QueryCommandRunner.cs
  - tests/CodeIndex.Tests/QueryCommandRunnerTests.cs
---

## English

- **Python queries now accept `py` as a language alias** — `--lang py` now normalizes to `python` in the query layer and completion aliases, so Python searches and symbol lookups no longer miss the common shorthand.

## 日本語

- **Python の検索で `py` を言語エイリアスとして受け付けるようになりました** — `--lang py` はクエリ層と補完用エイリアスの両方で `python` に正規化されるため、Python の検索やシンボル検索で一般的な短縮入力を取りこぼさなくなりました。

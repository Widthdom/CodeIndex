---
category: changed
affected:
  - src/CodeIndex/Cli/IndexCommandRunner.FullScan.cs
  - src/CodeIndex/Cli/IndexCommandRunner.cs
  - tests/CodeIndex.Tests/IndexCommandRunnerTests.cs
---

## English

- **No-op full scans skip extraction setup** — full-repository `cdidx index` runs now avoid starting extraction workers and post-extraction hook discovery when every file can be reused from stat metadata.

## 日本語

- **変更なし full scan で抽出基盤の起動を省くようになりました** — full-repository の `cdidx index` は、全ファイルを stat metadata から再利用できる場合に extraction worker と post-extraction hook discovery を起動しないようになりました。

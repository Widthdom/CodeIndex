---
category: fixed
affected:
  - src/CodeIndex/Database/DbReader.cs
  - src/CodeIndex/Cli/QueryCommandRunner.cs
  - tests/CodeIndex.Tests/DbReaderTests.cs
  - tests/CodeIndex.Tests/QueryCommandRunnerTests.cs
---

## English

- **XAML language aliases now resolve to XML search** — query filters now canonicalize `xaml` and `axaml` to `xml`, so indexed `.xaml` and `.axaml` files stay searchable whether users type the file format name or the canonical language name.

## 日本語

- **XAML の言語別名が XML 検索に正規化されるようになりました** — クエリフィルタが `xaml` と `axaml` を `xml` に正規化するため、`xml` で索引された `.xaml` / `.axaml` ファイルをファイル形式名でも canonical な言語名でも検索できます。

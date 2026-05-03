---
category: fixed
affected:
  - src/CodeIndex/Database/DbReader.cs
  - src/CodeIndex/Cli/QueryCommandRunner.cs
  - tests/CodeIndex.Tests/DbReaderTests.cs
  - tests/CodeIndex.Tests/QueryCommandRunnerTests.cs
---

## English

- **`--lang` now recognizes the app's supported language aliases more consistently** — `DbReader` now normalizes language filters using the full target-language set plus common alias spellings such as `c#`, `c++`, `f#`, `vb.net`, `py3`, and SQL dialect variants, so search/definition/reference queries no longer miss matches just because the user used a supported shorthand.

## 日本語

- **`--lang` がアプリの対象言語 alias をより一貫して認識するようになりました** — `DbReader` は対象言語の一覧に加えて `c#`、`c++`、`f#`、`vb.net`、`py3`、SQL 方言の表記ゆれなどの alias も正規化するため、検索 / 定義 / 参照クエリでサポート済みの短縮表記を使っても取りこぼしにくくなりました。

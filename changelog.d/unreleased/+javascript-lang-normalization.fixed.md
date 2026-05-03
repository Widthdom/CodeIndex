---
category: fixed
affected:
  - src/CodeIndex/Database/DbReader.cs
  - src/CodeIndex/Cli/QueryCommandRunner.cs
  - tests/CodeIndex.Tests/DbReaderTests.cs
  - tests/CodeIndex.Tests/QueryCommandRunnerTests.cs
---

## English

- **JavaScript search now normalizes `Javascript` spelling variants** — direct search calls now canonicalize the JavaScript language filter before applying the SQL `lang` predicate, so mixed-casing spellings like `Javascript` still return the indexed JavaScript rows users expect.
- **JavaScript search now normalizes `Javascript`, `js`, and `jsx` spelling variants** — direct search calls now canonicalize the JavaScript language filter before applying the SQL `lang` predicate, so mixed-casing spellings like `Javascript`, `JS`, and `JSX` still return the indexed JavaScript rows users expect.

## 日本語

- **JavaScript 検索で `Javascript`、`js`、`jsx` の表記ゆれを正規化するようになりました** — 直接検索呼び出しでも JavaScript の言語フィルタを SQL の `lang` 条件にかける前に canonical 化するため、`Javascript` / `JS` / `JSX` のような表記ゆれでも期待どおり indexed 済みの JavaScript 行が返ります。

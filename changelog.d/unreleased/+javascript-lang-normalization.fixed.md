---
category: fixed
affected:
  - src/CodeIndex/Database/DbReader.cs
  - tests/CodeIndex.Tests/DbReaderTests.cs
---

## English

- **JavaScript search now normalizes `Javascript` spelling variants** — direct search calls now canonicalize the JavaScript language filter before applying the SQL `lang` predicate, so mixed-casing spellings like `Javascript` still return the indexed JavaScript rows users expect.

## 日本語

- **JavaScript 検索で `Javascript` の表記ゆれを正規化するようになりました** — 直接検索呼び出しでも JavaScript の言語フィルタを SQL の `lang` 条件にかける前に canonical 化するため、`Javascript` のような表記ゆれでも期待どおり indexed 済みの JavaScript 行が返ります。

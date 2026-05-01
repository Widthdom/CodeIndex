---
category: fixed
affected:
  - src/CodeIndex/Database/DbReader.cs
  - tests/CodeIndex.Tests/QueryCommandRunnerTests.cs
---

## English

- **C# exact substring file search now normalizes verbatim qualified names** — `find --exact` now treats C# verbatim prefixes as syntax only, so `Foo.Bar` can match `using @Foo.@Bar;` and other equivalent source spellings.

## 日本語

- **C# の exact substring ファイル検索で verbatim 修飾名を正規化するようになりました** — `find --exact` は C# の verbatim 接頭辞を構文上の差分として扱うため、`Foo.Bar` で `using @Foo.@Bar;` など同値な表記にマッチできるようになりました。

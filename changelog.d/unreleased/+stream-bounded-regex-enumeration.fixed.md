---
category: fixed
affected:
  - src/CodeIndex/Indexer/BoundedRegex.cs
  - tests/CodeIndex.Tests/BoundedRegexTests.cs
  - DEVELOPER_GUIDE.md
  - TESTING_GUIDE.md
---

## English

- **Made bounded regex enumeration demand-driven** — Extractors that stop after
  reaching a result limit no longer force every remaining regex match on the
  source line to be materialized first.

## 日本語

- **bounded regex enumeration を demand-driven にしました** — 結果上限に達して
  停止する extractor は、source line に残るすべての regex match を先に実体化しなくなりました。

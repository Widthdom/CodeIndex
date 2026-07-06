---
category: changed
affected:
  - tests/CodeIndex.Tests/PublishedTrimmedCliFactAttribute.cs
  - tests/CodeIndex.Tests/PublishedTrimmedCliFactAttributeTests.cs
  - tests/CodeIndex.Tests/IndexCommandRunnerTests.cs
  - tests/CodeIndex.Tests/QueryCommandRunnerTests.cs
  - TESTING_GUIDE.md
---

## English

- Published trimmed CLI smoke tests now run only on the `net8.0` test target, avoiding duplicate expensive `dotnet publish` work in `net9.0` Build and Test matrix lanes while retaining focused cross-target in-process coverage.

## 日本語

- published trimmed CLI smoke test は `net8.0` test target でのみ実行するようになり、focused な cross-target in-process coverage は維持しつつ、Build and Test の `net9.0` matrix lane で高コストな `dotnet publish` を重複実行しないようにしました。

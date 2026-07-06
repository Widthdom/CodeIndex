---
category: changed
affected:
  - .github/workflows/dotnet.yml
  - tests/CodeIndex.Tests/CiWorkflowTests.cs
  - tests/CodeIndex.Tests/PublishedTrimmedCliFactAttribute.cs
  - tests/CodeIndex.Tests/PublishedTrimmedCliFactAttributeTests.cs
  - tests/CodeIndex.Tests/IndexCommandRunnerTests.cs
  - tests/CodeIndex.Tests/McpServerTests.cs
  - tests/CodeIndex.Tests/QueryCommandRunnerTests.cs
  - TESTING_GUIDE.md
---

## English

- Published trimmed CLI smoke tests now run only on the `net8.0` test target, avoiding duplicate expensive `dotnet publish` work in `net9.0` Build and Test matrix lanes while retaining focused cross-target in-process coverage.
- Non-primary Build and Test matrix lanes now restore only the test project and its references instead of restoring the full solution before building the test project.
- The MCP invalid UTF-8 stdio ordering test now uses transport signals instead of a fixed 200 ms serializer sleep.

## 日本語

- published trimmed CLI smoke test は `net8.0` test target でのみ実行するようになり、focused な cross-target in-process coverage は維持しつつ、Build and Test の `net9.0` matrix lane で高コストな `dotnet publish` を重複実行しないようにしました。
- Build and Test の non-primary matrix lane は、test project を build する前に solution 全体ではなく test project とその参照だけを restore するようにしました。
- MCP の invalid UTF-8 stdio ordering test は、固定 200 ms の serializer sleep ではなく transport signal を使うようにしました。

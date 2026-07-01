---
category: changed
affected:
  - .github/workflows/dotnet.yml
  - .github/scripts/run-dotnet-tests.ps1
  - src/CodeIndex/Indexer/References/Support/StructuralLineMasker.cs
  - src/CodeIndex/Database/DbWriter.cs
  - src/CodeIndex/Indexer/Scanning/IndexedFileStatReuse.cs
  - src/CodeIndex/Cli/IndexCommandRunner.FullScan.cs
  - src/CodeIndex/Cli/IndexCommandRunner.Update.cs
  - src/CodeIndex/Mcp/McpToolHandlers.cs
  - tests/CodeIndex.Tests/CiWorkflowTests.cs
  - tests/CodeIndex.Tests/ReferenceExtractorTests.cs
  - tests/CodeIndex.Tests/DatabaseTests.cs
  - TESTING_GUIDE.md
---

## English

- **Reduced CI test-runner duplication and large C# indexing allocation pressure** - the `dotnet.yml` matrix test step now delegates test argument construction, failure-log capture, timeout handling, and flaky retry classification to a dedicated PowerShell helper, while C# structural masking reuses unchanged lines instead of allocating a new string for every line during symbol/reference extraction.
- **Reduced unchanged-file indexing database roundtrips** - CLI and MCP indexing now combine stat-based reuse checks with existing cap/generated-file issue checks, avoiding a second SQLite query for every unchanged file that can be skipped before content extraction.

## 日本語

- **CI test runner の重複と巨大 C# indexing 時の allocation 負荷を減らしました** - `dotnet.yml` の matrix test step は test argument 構築、failure log capture、timeout handling、flaky retry classification を専用 PowerShell helper に委譲し、C# structural masking は symbol/reference extraction 中に全行へ新しい string を割り当てず、変更のない行を再利用するようになりました。
- **未変更ファイルの indexing DB 往復を削減しました** - CLI と MCP の indexing は stat ベースの再利用判定と既存の cap/generated-file issue 判定をまとめ、content extraction 前に skip できる未変更ファイルごとの追加 SQLite query を避けるようになりました。

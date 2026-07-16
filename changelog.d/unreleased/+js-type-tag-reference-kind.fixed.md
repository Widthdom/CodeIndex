---
category: fixed
affected:
  - src/CodeIndex/Models/SymbolKindCatalog.cs
  - src/CodeIndex/Cli/ConsoleUi.cs
  - src/CodeIndex/Cli/QueryCommandRunner.GraphOutput.cs
  - src/CodeIndex/Mcp/McpToolDefinitions.cs
  - src/CodeIndex/Mcp/McpToolHandlers.cs
  - tests/CodeIndex.Tests/ConsoleUiTests.cs
  - tests/CodeIndex.Tests/DatabaseTests.cs
  - tests/CodeIndex.Tests/McpServerToolsCallTests.cs
  - tests/CodeIndex.Tests/QueryCommandRunnerTests.cs
  - tests/CodeIndex.Tests/ReferenceExtractorTypeScriptTests.cs
  - USER_GUIDE.md
  - DEVELOPER_GUIDE.md
  - TESTING_GUIDE.md
---

## English

- **JavaScript/TypeScript discriminant guards can now be indexed without a database argument error** — `type_tag` is registered as a persisted reference kind, remains queryable through `references`, and is excluded from runtime caller/callee graphs.

## 日本語

- **JavaScript / TypeScript の discriminant guard を database argument error なしで index できるようにしました** — `type_tag` を永続化可能な reference kind として登録し、`references` から query 可能なまま runtime の caller / callee graph からは除外します。

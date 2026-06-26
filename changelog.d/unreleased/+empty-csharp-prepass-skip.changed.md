---
category: changed
affected:
  - src/CodeIndex/Cli/IndexCommandRunner.cs
  - src/CodeIndex/Cli/IndexCommandRunner.FullScan.cs
  - src/CodeIndex/Mcp/McpToolHandlers.cs
  - tests/CodeIndex.Tests/IndexCommandRunnerTests.cs
  - tests/CodeIndex.Tests/McpServerTests.cs
---

## English

- **Full index runs skip the C# static-interface prepass when the scan has no C# files** - CLI and MCP index paths now avoid the empty-candidate prepass query and use an empty C# workspace directly.

## 日本語

- **scan に C# file がない full index では C# static-interface prepass を省略します** - CLI / MCP index は候補 0 件の prepass query を避け、空の C# workspace を直接使うようになりました。

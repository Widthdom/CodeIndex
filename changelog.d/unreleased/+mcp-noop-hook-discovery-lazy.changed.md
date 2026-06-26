---
category: changed
affected:
  - src/CodeIndex/Mcp/McpToolHandlers.cs
  - src/CodeIndex/Cli/IndexCommandRunner.cs
  - tests/CodeIndex.Tests/McpServerTests.cs
---

## English

- **No-op MCP indexes skip hook discovery** — MCP `index_project` now delays post-extraction hook discovery until a file actually needs symbol/reference extraction, reducing overhead for unchanged large workspaces.

## 日本語

- **変更なし MCP index で hook discovery を省くようになりました** — MCP `index_project` は、実際に symbol/reference extraction が必要なファイルが出るまで post-extraction hook discovery を遅らせ、変更なしの巨大 workspace での余分な overhead を減らします。

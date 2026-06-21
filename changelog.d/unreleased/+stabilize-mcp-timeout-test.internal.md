---
category: internal
affected:
  - tests/CodeIndex.Tests/McpServerTests.cs
---

## English

- **Stabilized the MCP timeout diagnostics test** — the timeout test now reads the request-timeout drain diagnostics directly instead of running the heavier MCP `status` tool under a short test timeout.

## 日本語

- **MCP timeout diagnostics テストを安定化しました** — timeout テストは短いテスト用 timeout の中で重い MCP `status` ツールを実行せず、request-timeout drain diagnostics を直接読むようになりました。

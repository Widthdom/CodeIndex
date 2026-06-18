---
category: fixed
affected:
  - src/CodeIndex/Mcp/McpToolHandlers.cs
  - tests/CodeIndex.Tests/McpServerTests.cs
---

## English

- **MCP `batch_query` now keeps tight truncated responses under budget** — when result and truncated-query metadata compaction still leaves a response slightly too large, `batch_query` now shortens only the human summary before returning so the structured response remains within `maxResponseBytes`.

## 日本語

- **MCP `batch_query` の tight な truncated response が budget 内に収まるようになりました** — result と truncated-query metadata を compact しても response がわずかに大きい場合、`batch_query` は structured response を保ったまま human summary だけを短縮し、`maxResponseBytes` 内で返すようになりました。

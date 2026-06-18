---
category: fixed
affected:
  - src/CodeIndex/Mcp/McpToolHandlers.cs
  - tests/CodeIndex.Tests/McpServerTests.cs
---

## English

- **MCP `batch_query` now compacts truncated metadata before exceeding tight response budgets** — when a batch response is already reduced to truncated metadata, optional display fields are trimmed before returning so the final JSON stays within `CDIDX_MCP_BATCH_RESPONSE_MAX_BYTES` or per-call `maxResponseBytes` limits.

## 日本語

- **MCP `batch_query` が厳しいレスポンス上限でも truncated metadata を圧縮するようになりました** — batch response が truncated metadata だけになった場合は、返却前に任意の表示用フィールドを削ることで、最終 JSON が `CDIDX_MCP_BATCH_RESPONSE_MAX_BYTES` または呼び出しごとの `maxResponseBytes` 上限内に収まるようにしました。

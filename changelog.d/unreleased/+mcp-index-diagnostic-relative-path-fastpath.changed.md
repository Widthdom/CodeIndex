---
category: changed
affected:
  - src/CodeIndex/Mcp/McpToolHandlers.cs
---

## English

- **MCP index diagnostics** - Use the prefix-aware relative path helper for MCP indexing failures and byte-count diagnostics so large index runs avoid `Path.GetRelativePath` when paths are already under the project root.

## 日本語

- **MCP index 診断** - MCP indexing の失敗・バイト計測診断で prefix-aware な relative path helper を使い、project root 配下の path では `Path.GetRelativePath` を避けるようにしました。

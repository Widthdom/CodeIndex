---
category: fixed
affected:
  - src/CodeIndex/Mcp/McpToolHandlers.Instructions.cs
  - src/CodeIndex/Mcp/McpServer.Resources.cs
  - tests/CodeIndex.Tests/McpServerProtocolTests.cs
  - tests/CodeIndex.Tests/McpServerTests.cs
  - USER_GUIDE.md
  - DEVELOPER_GUIDE.md
---

## English

- **MCP file-resource controls are now discoverable in-band** — `initialize` instructions now explain exact-path templates and filtered `resources/list` usage, while each list response publishes accepted parameters, bounds, and cursor semantics under `_meta.discovery_contract`, so AI clients can use current resource discovery without guessing non-standard protocol extensions.

## 日本語

- **MCP の file-resource control を protocol 上で発見できるようになりました** — `initialize` の instructions が exact-path template と filter 付き `resources/list` の使い方を案内し、各 list response が accepted parameter、上限、cursor semantics を `_meta.discovery_contract` に公開するため、AI client は標準外の protocol extension を推測せず最新の resource discovery を利用できます。

---
category: changed
affected:
  - src/CodeIndex/Cli/IndexCommandRunner.FullScan.cs
  - src/CodeIndex/Cli/IndexCommandRunner.Update.cs
  - src/CodeIndex/Mcp/McpToolHandlers.cs
---

## English

- **Unchanged generated-code reuse checks avoid constructing issue payloads** - CLI and MCP no-op reuse paths now compare generated-code suppression state as a boolean and only build the `generated_code_extraction_skipped` issue when a file is actually reindexed.

## 日本語

- **unchanged generated code の再利用判定で issue payload を構築しないようになりました** - CLI / MCP の no-op reuse path は generated-code suppression 状態を boolean として比較し、`generated_code_extraction_skipped` issue は実際に再 index する file でのみ構築します。

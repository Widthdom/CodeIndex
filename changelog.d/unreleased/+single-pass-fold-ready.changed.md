---
category: changed
affected:
  - src/CodeIndex/Database/DbWriter.ReadyFlags.cs
  - src/CodeIndex/Cli/IndexCommandRunner.FullScan.cs
  - src/CodeIndex/Mcp/McpToolHandlers.Indexing.cs
---

## English

- **Incremental finalization now verifies folded lookup rows once** — CLI and MCP readiness stamping combines missing-row and current-fold validation inside one protected transaction, avoiding the duplicate full-table folded-value scan on successful no-op indexes while retaining precise degradation reasons.

## 日本語

- **incremental finalization の folded lookup row 検証を 1 回にまとめました** — CLI と MCP の readiness stamp は missing row と current fold の検証を 1 つの保護された transaction 内で行い、成功する no-op index の重複した full-table folded-value scan を避けつつ、正確な degradation reason を維持します。

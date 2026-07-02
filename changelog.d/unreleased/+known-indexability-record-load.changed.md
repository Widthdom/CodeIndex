---
category: changed
affected:
  - src/CodeIndex/Indexer/Scanning/FileIndexer.RecordLoading.cs
  - src/CodeIndex/Cli/IndexCommandRunner.FullScan.cs
  - src/CodeIndex/Cli/IndexCommandRunner.Update.cs
  - src/CodeIndex/Cli/IndexCommandRunner.DryRun.cs
  - src/CodeIndex/Cli/IndexFreshnessChecker.cs
  - src/CodeIndex/Mcp/McpToolHandlers.cs
---

## English

- **Record loading reuses known indexability** — full scans, incremental updates, dry runs, freshness checks, and MCP indexing avoid a second regular-file probe when the caller already proved the file is indexable.

## 日本語

- **record loading が既知の indexability を再利用** — full scan、incremental update、dry run、freshness check、MCP indexing は、呼び出し側で file が indexable と確認済みの場合に二度目の regular-file probe を避けるようにしました。

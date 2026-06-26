---
category: changed
affected:
  - src/CodeIndex/Cli/IndexCommandRunner.cs
  - src/CodeIndex/Cli/IndexCommandRunner.FullScan.cs
  - src/CodeIndex/Cli/IndexCommandRunner.Update.cs
  - src/CodeIndex/Mcp/McpToolHandlers.cs
  - tests/CodeIndex.Tests/IndexCommandRunnerTests.cs
---

## English

- **Index-run byte accounting reuses sizes already observed while indexing** - CLI and MCP index runs now feed stat-skip and loaded-record sizes into `last_index_run.bytes_read`, avoiding a second file-size probe for unchanged files during final metadata stamping.

## 日本語

- **index run の byte 集計が index 中に観測済みの size を再利用するようになりました** - CLI / MCP index は stat skip や読み込み済み record の size を `last_index_run.bytes_read` に渡し、最終 metadata stamp 時に unchanged file を再度 size probe しないようになりました。

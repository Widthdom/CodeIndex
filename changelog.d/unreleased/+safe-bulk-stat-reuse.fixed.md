---
category: fixed
affected:
  - src/CodeIndex/Database/DbWriter.FileReuse.cs
  - src/CodeIndex/Cli/IndexCommandRunner.FullScan.cs
  - src/CodeIndex/Mcp/McpToolHandlers.Indexing.cs
---

## English

- **Bulk stat reuse now handles legacy rows safely and remains cancellable** — Repository-wide CLI and MCP scans skip incomplete or malformed legacy stat rows for normal repair instead of failing the scan, and cancellation now interrupts the SQLite snapshot read.

## 日本語

- **一括 stat 再利用が旧形式 row を安全に扱い、キャンセル可能になりました** — リポジトリ全体を対象にする CLI / MCP scan は、欠損または不正な旧 stat row で失敗せず通常の修復対象として除外し、キャンセル時には SQLite snapshot 読み取りも中断します。

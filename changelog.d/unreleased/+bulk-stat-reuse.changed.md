---
category: changed
affected:
  - src/CodeIndex/Cli/IndexCommandRunner.FullScan.cs
  - src/CodeIndex/Mcp/McpToolHandlers.Indexing.cs
  - src/CodeIndex/Database/DbWriter.FileReuse.cs
  - src/CodeIndex/Indexer/Scanning/IndexedFileStatReuse.cs
---

## English

- **Repository-wide incremental scans now batch unchanged-file metadata reads** — CLI and MCP full scans load reusable stat candidates with one SQLite statement and then validate live filesystem metadata, avoiding a database query per file while preserving extractor-version, row-cap, issue-metadata, and generated-code safeguards.

## 日本語

- **リポジトリ全体の incremental scan で unchanged-file metadata を一括取得するようになりました** — CLI と MCP の full scan は再利用可能な stat 候補を 1 回の SQLite statement で読み、実 filesystem metadata を照合します。file ごとの database query を避けながら、extractor version、row cap、issue metadata、generated-code の安全条件を維持します。

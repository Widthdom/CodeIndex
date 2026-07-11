---
category: changed
affected:
  - src/CodeIndex/Indexer/Scanning/FileIndexer.ScanOrchestration.cs
  - src/CodeIndex/Database/DbWriter.cs
  - src/CodeIndex/Cli/IndexCommandRunner.FullScan.cs
  - tests/CodeIndex.Tests/FileIndexerTests.cs
---

## English

- **Incremental full scans size discovery collections from the existing index** - the CLI now passes the prior indexed-file count as a bounded capacity hint for the scan file list and language map. Large stable workspaces avoid repeatedly growing and copying those collections from the 256-file default, while rebuilds and untrusted oversized hints retain safe defaults and caps.

## 日本語

- **incremental full scan の discovery collection を既存 index 件数で初期化するようにしました** - CLI は以前の indexed-file 数を bounded capacity hint として scan file list と language map に渡します。巨大で安定した workspace は256 file の既定値から collection を繰り返し拡張・コピーせずに済み、rebuild と信頼できない過大 hint は安全な既定値・上限を維持します。

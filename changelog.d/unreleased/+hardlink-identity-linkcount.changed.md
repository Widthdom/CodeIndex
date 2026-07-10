---
category: changed
affected:
  - src/CodeIndex/Indexer/Scanning/FileIndexer.FileIdentity.cs
  - src/CodeIndex/Indexer/Scanning/FileIndexer.FileAcceptance.cs
  - src/CodeIndex/Cli/IndexCommandRunner.Update.cs
---

## English

- **Hardlink duplicate tracking now skips files with a single link** - Full scans and update mode use the filesystem link count to avoid storing identities for files that cannot have hardlink duplicates, reducing hash-set overhead in large repositories while preserving duplicate detection.

## 日本語

- **hardlink 重複 tracking が link count 1 のファイルを記録しないようになりました** - full scan と update mode で filesystem の link count を使い、hardlink 重複が起こり得ないファイルの identity 登録を避けて、巨大リポジトリでの HashSet overhead を減らします。

---
category: changed
affected:
  - src/CodeIndex/Indexer/Scanning/FileIndexer.cs
---

## English

- **Full scans now allocate hardlink identity tracking only when a multiply-linked file is encountered** — ordinary repositories avoid an otherwise unused per-scan hash set while preserving duplicate hardlink detection.

## 日本語

- **フルスキャンでは複数リンクを持つファイルを検出した場合だけ hardlink identity 追跡を確保します** — hardlink 重複検出を維持しつつ、通常のリポジトリで未使用の scan 単位 HashSet を避けます。

---
category: changed
affected:
  - src/CodeIndex/Cli/IndexCommandRunner.FullScan.cs
  - src/CodeIndex/Cli/IndexCommandRunner.cs
---

## English

- **Full-scan byte accounting** - Track already-read file sizes by scan index instead of by path string so large full scans avoid a per-file dictionary lookup while stamping read-byte metadata.

## 日本語

- **フルスキャンのバイト集計** - 読み取り済みファイルサイズをパス文字列ではなくスキャン順インデックスで記録し、大規模フルスキャンの read-byte metadata 書き込みでファイルごとの辞書 lookup を避けるようにしました。

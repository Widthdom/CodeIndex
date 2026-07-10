---
category: changed
affected:
  - src/CodeIndex/Cli/IndexCommandRunner.Update.cs
---

## English

- **Update byte accounting** - Snapshot update targets and track already-read file sizes by target index instead of by path string, avoiding per-file dictionary lookups when stamping update read-byte metadata.

## 日本語

- **更新モードのバイト集計** - 更新対象をスナップショットし、読み取り済みファイルサイズをパス文字列ではなく対象インデックスで記録して、更新時の read-byte metadata 書き込みでファイルごとの辞書 lookup を避けるようにしました。

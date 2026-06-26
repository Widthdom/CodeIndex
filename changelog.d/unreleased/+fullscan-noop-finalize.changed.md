---
category: changed
affected:
  - src/CodeIndex/Cli/IndexCommandRunner.FullScan.cs
---

## English

- **No-op full scans skip unnecessary FTS optimization** — full-repository `cdidx index` runs now avoid the expensive FTS5 optimize step when no indexed chunks were inserted, deleted, or purged.

## 日本語

- **変更なし full scan で不要な FTS optimize を省くようになりました** — full-repository の `cdidx index` は、indexed chunk の追加・削除・purge がない場合に高コストな FTS5 optimize を実行しないようになりました。

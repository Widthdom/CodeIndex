---
category: changed
affected:
  - src/CodeIndex/Indexer/Scanning/FileIndexer.ScanOrchestration.cs
---

## English

- **Fresh full scans now reuse their completed-directory set as checkpoint output** — the common no-resume path no longer copies every scanned directory into a second hash set after traversal finishes.

## 日本語

- **新規 full scan では完了済み directory set を checkpoint 出力として再利用します** — resume なしの一般的な経路で、走査完了後に全 directory を2つ目の HashSet へコピーしなくなりました。

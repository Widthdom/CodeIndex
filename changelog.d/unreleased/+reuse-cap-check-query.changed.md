---
category: changed
affected:
  - src/CodeIndex/Database/DbWriter.cs
  - src/CodeIndex/Cli/IndexCommandRunner.FullScan.cs
---

## English

- **Unchanged-file reuse checks use fewer SQLite round trips** — indexing now checks symbol/reference cap violations for reusable existing rows with one query instead of separate count and issue lookups.

## 日本語

- **未変更ファイル再利用時の SQLite 往復を削減しました** — indexing は再利用可能な既存行の symbol/reference cap 違反を、個別の count / issue lookup ではなく単一クエリで確認するようになりました。

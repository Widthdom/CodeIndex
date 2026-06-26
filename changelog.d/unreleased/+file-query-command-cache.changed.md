---
category: changed
affected:
  - src/CodeIndex/Database/DbWriter.cs
---

## English

- **File query helpers reuse prepared commands** — per-file symbol/reference counts, issue lookups, and indexed-language scans now reuse fixed SQLite commands across files.

## 日本語

- **file query helper の prepared command を再利用します** — file単位の symbol/reference count、issue lookup、indexed language scan が、ファイルを跨いで固定SQLite commandを再利用します。

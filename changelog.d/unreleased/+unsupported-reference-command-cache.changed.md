---
category: changed
affected:
  - src/CodeIndex/Database/DbWriter.cs
---

## English

- **Unsupported-language reference cleanup reuses prepared commands** — update and full-scan cleanup now cache the fixed-width SQLite statements used to count and purge graph rows for languages that are no longer supported.

## 日本語

- **unsupported language reference cleanup で prepared command を再利用します** — update/full-scan cleanup が、対応外になった言語の graph 行を count/purge する固定幅 SQLite statement を cache します。

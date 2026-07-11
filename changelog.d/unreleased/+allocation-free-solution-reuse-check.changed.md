---
category: changed
affected:
  - src/CodeIndex/Database/DbWriter.cs
---

## English

- **Unchanged-file reuse now recognizes solution paths without allocating an extension string** — large scans avoid a transient allocation for every checksum/stat reuse candidate while preserving case-insensitive `.sln` handling.

## 日本語

- **未変更ファイル再利用で extension 文字列を確保せず solution path を判定します** — `.sln` の大文字小文字を区別しない挙動を維持しつつ、大規模 scan の checksum / stat 再利用候補ごとの一時 allocation を避けます。

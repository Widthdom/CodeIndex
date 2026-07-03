---
category: internal
affected: Indexer
---

## English

- Replaced per-line CSS ancestor stack scans with a qualified-rule depth counter and lazy context stack allocation, reducing work in deeply nested CSS files.

## 日本語

- CSS ancestor 判定の行ごとの stack 走査を qualified-rule depth カウンタと遅延 context stack 確保に置き換え、深くネストした CSS ファイルでの処理量を減らしました。

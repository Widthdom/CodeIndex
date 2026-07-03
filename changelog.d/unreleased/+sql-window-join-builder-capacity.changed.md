---
title: Pre-size SQL window join builders
category: changed
---

## English

- Reuse the SQL window-clause pre-scan to estimate joined text size and initialize the join `StringBuilder` with that capacity, reducing growth churn on large SQL files that do contain window clauses.

## 日本語

- SQL window clause の事前走査で結合テキストのサイズも見積もり、その容量で join 用 `StringBuilder` を初期化して、window clause を含む大規模 SQL ファイルでの伸長コストを削減しました。

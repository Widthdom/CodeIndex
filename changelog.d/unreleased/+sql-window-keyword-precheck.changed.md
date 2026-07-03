---
title: Precheck SQL window keywords before joining lines
category: changed
---

## English

- Skip building joined SQL text for window-function suppression when no line contains `OVER`, reducing unnecessary allocation on large SQL files without window clauses.

## 日本語

- `OVER` を含む行がない場合は SQL window function suppression 用の結合テキストを作らないようにし、window clause のない大規模 SQL ファイルで不要な allocation を削減しました。

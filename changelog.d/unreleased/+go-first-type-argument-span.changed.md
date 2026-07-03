---
title: Avoid full Go builtin argument splits
category: changed
---

## English

- Read only the first top-level Go builtin type argument span instead of allocating a full comma split list during reference extraction.

## 日本語

- Go builtin type の reference 抽出で comma split list 全体を作らず、最初の top-level 型引数 span だけを読むようにしました。

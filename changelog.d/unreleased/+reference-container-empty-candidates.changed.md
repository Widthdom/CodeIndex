---
title: Avoid empty reference container candidate allocations
category: changed
---

## English

- Reduced reference extraction overhead for files without container symbols by returning shared empty candidate arrays and skipping empty resolver state.

## 日本語

- container symbol がないファイルで共有の空候補配列を返し、空の resolver 状態を作らないことで参照抽出の負荷を削減しました。

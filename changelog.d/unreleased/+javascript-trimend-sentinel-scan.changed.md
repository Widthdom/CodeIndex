---
title: Avoid JavaScript line-end trim allocations
category: changed
---

## English

- Replaced several JavaScript/TypeScript line-end `TrimEnd()` checks with direct trailing-whitespace scans during symbol extraction.

## 日本語

- JavaScript/TypeScript のシンボル抽出で複数の行末 `TrimEnd()` 判定を末尾空白の直接走査に置き換えました。

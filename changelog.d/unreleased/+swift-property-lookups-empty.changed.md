---
title: Avoid empty Swift property lookup allocations
category: changed
---

## English

- Reduced Swift reference extraction allocations by skipping property lookup dictionaries when a file has no property symbols and avoiding sort-entry lists for single-property lines.

## 日本語

- property symbol がない Swift ファイルでは property lookup dictionary を作らず、1 行に property が 1 件だけの場合の sort-entry list も避けるようにして割り当てを削減しました。

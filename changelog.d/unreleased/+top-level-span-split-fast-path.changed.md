---
title: Fast-path separator-free top-level span splits
category: changed
---

## English

- Skip depth-tracking scans in shared comma and ampersand span splitters when the separator is absent, reducing work across typed reference extraction.

## 日本語

- 共有の comma / ampersand span splitter で区切り文字がない場合は depth tracking scan を省き、型付き reference 抽出全体の処理量を減らしました。

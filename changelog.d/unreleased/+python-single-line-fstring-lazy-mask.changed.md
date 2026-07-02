---
title: Lazily mask single-line Python f-strings
category: changed
---

## English

- Avoided character array allocation for Python lines that contain `f` or `F` but do not actually contain a single-line f-string.

## 日本語

- `f` または `F` を含むものの single-line f-string ではない Python 行で、文字配列の割り当てを避けるようにしました。

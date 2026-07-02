---
title: Avoid singleton arrays for CSS scanner line masking
category: changed
---

## English
- Reuse the CSS scanner line-masking core for single-line brace scans without allocating singleton input and output arrays.

## 日本語
- CSS scanner の single-line brace scan で singleton input/output array を作らず、line-masking core を直接再利用するようにしました。

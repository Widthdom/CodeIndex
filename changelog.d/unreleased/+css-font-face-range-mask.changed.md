---
title: Avoid CSS font-face block slice arrays
category: changed
---

## English
- Mask and join CSS `@font-face` block ranges without allocating sliced line arrays during symbol extraction.

## 日本語
- CSS symbol extraction で `@font-face` block range を処理するとき、sliced line array を作らずに mask/join するようにしました。

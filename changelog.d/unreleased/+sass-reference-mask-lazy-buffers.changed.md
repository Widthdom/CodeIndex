---
title: Lazily allocate Sass reference masks
category: changed
---

## English

- Avoided copying Sass and Stylus reference-scan lines unless comments or string literals actually need masking.

## 日本語

- Sass と Stylus の reference scan で、コメントや文字列のマスクが必要な行だけをコピーするようにしました。

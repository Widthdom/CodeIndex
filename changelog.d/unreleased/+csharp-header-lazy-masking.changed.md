---
title: Lazily mask C#-style type headers
category: changed
---

## English

- Avoid per-line `char[]` allocation while rebuilding C#/Java/Kotlin type headers unless comments or literals actually need masking.

## 日本語

- C#/Java/Kotlin の型ヘッダー再構築で、コメントやリテラルのマスクが必要な行だけ `char[]` を割り当てるようにしました。

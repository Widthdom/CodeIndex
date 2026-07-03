---
title: Lazily allocate SQL line masks
category: changed
---

## English

- Avoided copying SQL reference and synthetic symbol scan lines unless comments or string literals actually need masking.

## 日本語

- SQL の reference scan と synthetic symbol scan で、コメントや文字列のマスクが必要な行だけをコピーするようにしました。

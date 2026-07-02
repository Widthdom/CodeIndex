---
title: Lazily allocate C++ friend masks
category: changed
---

## English

- Avoided copying C++ friend declaration scan lines unless comments or string literals actually need masking.

## 日本語

- C++ friend declaration scan で、コメントや文字列のマスクが必要な行だけをコピーするようにしました。

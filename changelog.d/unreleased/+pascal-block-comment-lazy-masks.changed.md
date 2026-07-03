---
title: Lazily allocate Pascal block comment masks
category: changed
---

## English

- Avoided copying every Pascal reference-scan line when only comment-bearing lines need block comment masking.

## 日本語

- Pascal の reference scan で、ブロックコメントのマスクが必要な行だけをコピーするようにしました。

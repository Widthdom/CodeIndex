---
title: Lazily allocate block comment line masks
category: changed
---

## English

- Avoided copying every line in C-style and Haskell block comment masking when only a subset of lines actually needs masking.

## 日本語

- C-style と Haskell のブロックコメントマスクで、実際にマスクが必要な一部の行だけをコピーするようにしました。

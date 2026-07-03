---
title: Lazily allocate Lisp symbol masks
category: changed
---

## English

- Avoided copying Lisp symbol-scan lines unless comments, strings, or already matched forms actually need masking.

## 日本語

- Lisp の symbol scan で、コメント、文字列、検出済みフォームのマスクが必要な行だけをコピーするようにしました。

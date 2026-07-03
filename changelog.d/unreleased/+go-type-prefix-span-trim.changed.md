---
title: Go type prefix normalization avoids slice trim chains
category: changed
---

## English

- **Go type prefix normalization avoids slice trim chains** — Go type symbol extraction now trims the text after a `type` prefix from a span slice instead of allocating a substring before trimming.

## 日本語

- **Go type prefix正規化でslice後のtrim連鎖を避けるようになりました** — Go type symbol抽出は`type` prefix後のtextをsubstring化してからtrimせず、span slice上でtrimするようになりました。

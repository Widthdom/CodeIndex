---
title: Go label comment checks avoid trim strings
category: changed
---

## English

- **Go label comment checks avoid trim strings** — Go label extraction now checks comment-like trimmed prefixes with spans instead of allocating a trimmed line before running the label regex.

## 日本語

- **Go labelのコメント判定でtrim文字列を避けるようになりました** — Go label抽出はlabel正規表現を実行する前に、trim済み行を割り当てずspanでコメント風prefixを判定するようになりました。

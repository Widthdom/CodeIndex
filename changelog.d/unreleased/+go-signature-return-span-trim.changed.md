---
title: Go signature return checks avoid trailing strings
category: changed
---

## English

- **Go signature return checks avoid trailing strings** — Go symbol extraction now checks the text after a parameter list with spans instead of allocating a trimmed suffix while deciding whether a signature has an explicit return value.

## 日本語

- **Go署名の戻り値判定で末尾文字列を避けるようになりました** — Goシンボル抽出は署名に明示的な戻り値があるかを判断する際、parameter list後のテキストをtrim済み文字列にせずspanで確認するようになりました。

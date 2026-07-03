---
title: F# type member scanning avoids trim strings
category: changed
---

## English

- **F# type member scanning avoids trim strings** — F# record and union member detection now uses trimmed spans for indentation and prefix checks before materializing candidate text.

## 日本語

- **F# type member scanでtrim文字列を避けるようになりました** — F# record/union member検出はcandidate textを文字列化する前に、indentとprefix判定をtrim済みspanで行うようになりました。

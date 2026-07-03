---
title: JavaScript and TypeScript RHS checks avoid slice trim chains
category: changed
---

## English

- **JavaScript and TypeScript RHS checks avoid slice trim chains** — arrow, lambda, class, anonymous function, and re-export parsing now trims right-hand-side slices from spans instead of allocating a substring before each trim.

## 日本語

- **JavaScript/TypeScript RHS判定でslice後のtrim連鎖を避けるようになりました** — arrow/lambda/class/anonymous function/re-export解析は各trim前にsubstringを作らず、RHS sliceをspanからtrimするようになりました。

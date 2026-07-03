---
title: Rust impl prefix checks avoid trim strings
category: changed
---

## English

- **Rust impl prefix checks avoid trim strings** — Rust `impl` statement range detection now checks the first line prefix with a single trimmed span instead of allocating trimmed strings.

## 日本語

- **Rust impl prefix判定でtrim文字列を避けるようになりました** — Rust `impl` statement range検出はtrim文字列を作らず、1つのtrim済みspanで先頭行prefixを判定するようになりました。

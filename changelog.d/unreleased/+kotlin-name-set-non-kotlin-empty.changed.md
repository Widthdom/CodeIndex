---
title: Avoid Kotlin name set allocation for other languages
category: changed
---

## English

- **Avoided Kotlin name-set allocation outside Kotlin files** — shared reference setup now reuses empty Kotlin constructor and infix-function name sets for non-Kotlin languages.

## 日本語

- **Kotlin 以外で Kotlin name set の割当を回避** — 共通参照準備処理は、Kotlin 以外の言語では空の Kotlin constructor / infix-function name set を再利用するようになりました。

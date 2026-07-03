---
title: Rust function declaration checks avoid prefix strings
category: changed
---

## English

- **Rust function declaration checks avoid prefix strings** — Rust reference extraction now tests `fn` declaration prefixes with spans instead of allocating a trimmed prefix for each call-shaped candidate.

## 日本語

- **Rustの関数宣言判定でprefix文字列を避けるようになりました** — Rust参照抽出はcall形の候補ごとにtrim済みprefix文字列を作らず、spanで`fn`宣言prefixを判定するようになりました。

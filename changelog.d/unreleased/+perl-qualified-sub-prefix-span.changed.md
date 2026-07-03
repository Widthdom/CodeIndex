---
title: Perl qualified subroutine prefix checks avoid trim strings
category: changed
---

## English

- **Perl qualified subroutine prefix checks avoid trim strings** — Perl reference extraction now checks qualified subroutine definitions with spans instead of allocating a trimmed prefix for every qualified call candidate.

## 日本語

- **Perlのqualified subroutine prefix判定でtrim文字列を避けるようになりました** — Perl参照抽出はqualified call候補ごとにtrim済みprefix文字列を作らず、spanでqualified subroutine定義を判定するようになりました。

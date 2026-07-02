---
title: Fast-path simple SQL qualified names
category: changed
---

## English

- Return simple SQL qualified-name segments without per-character scanning when the name contains no dot or quoting characters, reducing work in large symbol tables.

## 日本語

- SQL qualified name が `.` や引用文字を含まない単純名の場合は文字単位スキャンなしで segment を返し、大規模 symbol table での処理量を削減しました。

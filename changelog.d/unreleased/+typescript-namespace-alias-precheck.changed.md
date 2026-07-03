---
title: Tighten TypeScript namespace alias prechecks
category: changed
---

## English

- Avoid namespace-alias preparation for TypeScript files that only contain alias-free imports by requiring `*`, `from`, or a dynamic-import parenthesis before scanning every line.

## 日本語

- TypeScript ファイルで alias を作らない import だけの場合に namespace-alias 準備へ進まないよう、全行スキャン前の条件を `*`、`from`、または dynamic import の括弧に絞りました。

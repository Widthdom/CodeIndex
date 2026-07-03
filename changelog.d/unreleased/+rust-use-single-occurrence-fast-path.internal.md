---
category: internal
affected: Indexer
---

## English

- Skipped Rust `use` occurrence dedupe allocation when a statement expands to zero or one import symbol.

## 日本語

- Rust `use` statement が import symbol 0 件または 1 件に展開される場合は dedupe 用 allocation を省くようにしました。

---
title: Avoid Rust use-path trim strings
category: changed
---

## English

- **Rust use-path reference extraction now trims path segments with spans** — indexing large Rust modules avoids intermediate trim strings while combining nested `use` prefixes and imported names.

## 日本語

- **Rust use-path 参照抽出が path segment の trim に span を使うようになりました** — 大きな Rust module を index するとき、nested `use` prefix と import 名を結合する中間 trim 文字列生成を避けます。

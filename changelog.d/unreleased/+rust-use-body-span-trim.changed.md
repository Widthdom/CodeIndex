---
title: Rust use extraction keeps body trimming on spans
category: changed
---

## English

- **Rust use extraction keeps body trimming on spans** — Rust `use` extraction now trims the statement body and semicolon suffix on spans, preserving the body offset without re-searching the materialized string.

## 日本語

- **Rust use抽出でbody trimをspan上に保つようになりました** — Rust `use`抽出はstatement bodyとsemicolon suffixをspan上でtrimし、文字列化したbodyを再検索せずにbody offsetを保持するようになりました。

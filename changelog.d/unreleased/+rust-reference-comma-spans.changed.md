---
category: changed
affected:
  - src/CodeIndex/Indexer/References/Languages/RustReferenceExtractor.cs
---

## English

- **Rust reference comma lists scan with spans** — multiline derive attributes, grouped `use` bodies, tuple struct fields, and tuple enum variants avoid copying the full comma-delimited list before segment processing.

## 日本語

- **Rust reference の comma list を span 走査するようになりました** — multiline derive attribute、grouped `use` body、tuple struct field、tuple enum variant で segment 処理前の list 全体コピーを避けます。

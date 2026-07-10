---
category: changed
affected:
  - src/CodeIndex/Indexer/Symbols/SymbolExtractor.Css.cs
---

## English

- **CSS at-rule prefix checks avoid trim allocations** — `@font-face` and `@media` symbol extraction now checks trimmed prefixes with spans.

## 日本語

- **CSS at-rule 接頭辞判定で trim 割り当てを避けるようになりました** — `@font-face` と `@media` のシンボル抽出は、trim 済み接頭辞を span で確認します。

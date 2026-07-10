---
category: changed
affected:
  - src/CodeIndex/Indexer/Symbols/SymbolExtractor.Css.cs
---

## English

- **CSS nested selector checks avoid trim allocations** — nested qualified-rule filtering now tests the leading `@` prefix with spans.

## 日本語

- **CSS nested selector 判定で trim 割り当てを避けるようになりました** — nested qualified-rule のフィルタは、先頭の `@` 接頭辞を span で確認します。

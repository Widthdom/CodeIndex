---
title: Lazily allocate Markdown heading stacks
category: changed
affected:
  - src/CodeIndex/Indexer/Symbols/SymbolExtractor.Markup.cs
---

## English

- **Markdown symbol extraction now allocates heading stacks only after the first heading** — documents without headings skip unused heading hierarchy stacks during indexing.

## 日本語

- **Markdown symbol 抽出が最初の heading 後にだけ heading stack を割り当てるようになりました** — heading が無い文書では、indexing 中の未使用 heading hierarchy stack を避けます。

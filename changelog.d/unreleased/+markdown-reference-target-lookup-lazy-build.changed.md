---
title: Lazily build Markdown reference target lookups
category: changed
affected:
  - src/CodeIndex/Indexer/Symbols/SymbolExtractor.Markup.cs
---

## English

- **Markdown symbol extraction now builds reference target lookups only when reference-style links are present** — documents without `][` links skip full-file reference-definition scans and empty dictionaries during indexing.

## 日本語

- **Markdown symbol 抽出が reference-style link がある時だけ reference target lookup を構築するようになりました** — `][` link が無い文書では、indexing 中の全ファイル reference-definition scan と空 dictionary を避けます。

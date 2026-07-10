---
category: changed
affected:
  - src/CodeIndex/Indexer/Symbols/SymbolExtractor.Go.cs
---

## English

- **Go type kind detection avoids regex scans** — struct and interface type declarations now use a bounded keyword scanner instead of per-symbol regex checks.

## 日本語

- **Go type kind 判定で regex scan を避けるようになりました** — struct と interface の type declaration は、symbol ごとの regex 判定ではなく境界付き keyword scanner で分類します。

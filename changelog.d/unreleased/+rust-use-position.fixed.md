---
category: fixed
affected:
  - src/CodeIndex/Indexer/SymbolExtractor.cs
  - tests/CodeIndex.Tests/SymbolExtractorTests.cs
---

## English

- **Rust multiline `use` imports now preserve per-item positions** — the import symbols emitted from formatted Rust `use` trees now keep the correct line and column for each item instead of falling back to the opening `use` line, which makes search and symbol navigation land on the real import entry.

## 日本語

- **Rust の複数行 `use` import が item ごとの位置を保持するようになりました** — 整形された Rust `use` tree から出力される import symbol が、先頭の `use` 行ではなく各 item の正しい行・列を保持するため、検索や symbol ナビゲーションが実際の import 項目に着地するようになります。

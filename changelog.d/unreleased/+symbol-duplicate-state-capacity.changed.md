---
category: changed
affected:
  - src/CodeIndex/Indexer/Symbols/SymbolExtractor.cs
  - src/CodeIndex/Indexer/Symbols/SymbolExtractor.Rust.cs
  - src/CodeIndex/Indexer/Symbols/SymbolExtractor.Shell.cs
  - src/CodeIndex/Indexer/Symbols/SymbolExtractor.JavaScriptTypeScriptSupport.cs
---

## English

- **Large-file symbol extraction seeds duplicate tracking state** - Generic symbol extraction and Rust, shell, JavaScript, and TypeScript supplemental passes now give duplicate-tracking collections conservative initial capacity for long files.

## 日本語

- **大きなファイルの symbol extraction が duplicate tracking state を先取りするようになりました** - 汎用 symbol extraction と Rust、shell、JavaScript、TypeScript の補助パスは、長いファイルで duplicate tracking 用 collection に控えめな初期容量を与えます。

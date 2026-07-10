---
category: changed
affected:
  - src/CodeIndex/Indexer/Symbols/SymbolExtractor.cs
---

## English

- **Ruby require symbol normalization avoids trim allocations** — require-line detection and require path cleanup now use spans before materializing the final symbol name.

## 日本語

- **Ruby require symbol 正規化で trim allocation を避けるようになりました** — require 行判定と require path の整形は、最終 symbol 名を作る前に span で処理します。

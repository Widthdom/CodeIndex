---
category: changed
affected:
  - src/CodeIndex/Indexer/Symbols/SymbolExtractor.Go.cs
---

## English

- **Go interface body scanning avoids per-line trim allocations** — symbol extraction now keeps interface header and body candidates as spans until a symbol needs the candidate text.

## 日本語

- **Go interface body 走査で行ごとの trim 割り当てを避けるようになりました** — シンボル抽出は、symbol 追加に候補文字列が必要になるまで interface header と body 候補を span のまま扱います。

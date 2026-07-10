---
category: changed
affected:
  - src/CodeIndex/Indexer/Symbols/SymbolExtractor.Markup.cs
---

## English

- **Markdown heading parsing avoids eager trim allocations** — symbol extraction now trims ATX and Setext heading text with spans before storing the heading name.

## 日本語

- **Markdown heading 解析で早期trim割り当てを避けるようになりました** — シンボル抽出は、ATX/Setext heading text を span で整えてから heading 名を保存します。

---
category: changed
affected:
  - src/CodeIndex/Indexer/Symbols/SymbolExtractor.Markup.cs
---

## English

- **HTML attribute symbol emission now avoids temporary lists for multi-value attributes** — markup symbol extraction now streams `srcset` URLs and class names directly while keeping single-value attributes as plain strings, reducing per-attribute allocation work in large HTML, Razor, and component templates.

## 日本語

- **HTML attribute symbol emission が multi-value attribute 用の一時リストを避けるようになりました** — markup symbol extraction は `srcset` URL と class name を直接流し、単一値 attribute は文字列のまま保持することで、大きな HTML / Razor / component template での attribute ごとの割り当てを削減します。

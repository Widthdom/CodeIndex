---
category: changed
affected:
  - src/CodeIndex/Indexer/Symbols/SymbolExtractor.cs
---

## English

- **Generic symbol extraction now reuses the pattern-cache lookup** - Pattern-based language extraction avoids a second dictionary lookup after deciding that the language has built-in symbol patterns.

## 日本語

- **generic symbol extraction が pattern cache lookup を再利用するようになりました** - pattern ベースの言語抽出で、組み込み symbol pattern を持つと判定した後の二重 dictionary lookup を避けます。

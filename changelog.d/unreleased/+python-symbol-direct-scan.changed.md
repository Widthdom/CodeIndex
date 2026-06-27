---
category: changed
affected:
  - src/CodeIndex/Indexer/Symbols/SymbolExtractor.Python.cs
---

## English

- **Python symbol enrichment now avoids snapshot LINQ passes** — Python class-attribute and walrus-symbol enrichment now scans the initial symbol list directly, avoiding temporary LINQ projections while preserving the existing snapshot behavior during symbol insertion.

## 日本語

- **Python symbol enrichment が snapshot 用の LINQ pass を避けるようになりました** — Python の class attribute / walrus symbol 補完は初期 symbol list を直接走査し、symbol 追加中の既存 snapshot 挙動を保ちながら一時的な LINQ projection を避けます。

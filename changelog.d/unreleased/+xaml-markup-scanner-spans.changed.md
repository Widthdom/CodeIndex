---
category: changed
affected:
  - src/CodeIndex/Indexer/Symbols/SymbolExtractor.Markup.cs
---

## English

- **XAML markup scanners now accept spans** — binding and markup extension normalization can scan top-level payloads, equals signs, and matching braces without first creating trimmed whole-string copies.

## 日本語

- **XAML markup scanner が span 入力を扱うようになりました** — binding と markup extension の正規化で、trim 済み文字列全体を先に作らずに top-level payload、equals、対応 brace を走査できます。

---
category: changed
affected:
  - src/CodeIndex/Indexer/Symbols/SymbolExtractor.Markup.cs
---

## English

- **XAML binding normalization trims entry values with spans** — binding kind, content, key, and path suffix normalization now avoid eager trimmed-string copies while indexing markup-heavy files.

## 日本語

- **XAML binding 正規化で入力値を span trim するようになりました** — markup の多いファイルのインデックス時に、binding kind、content、key、path suffix の eager な trim 済み文字列化を避けます。

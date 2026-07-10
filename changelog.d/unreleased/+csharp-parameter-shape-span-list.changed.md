---
category: changed
affected:
  - src/CodeIndex/Indexer/References/ReferenceExtractor.PatternTypeReferences.cs
  - src/CodeIndex/Indexer/References/ReferenceExtractor.TypeReferences.cs
---

## English

- **C# callable parameter shape scans parameter lists with spans** — the shared comma splitter now accepts spans, and callable contract matching avoids copying the whole parameter list before normalizing each parameter.

## 日本語

- **C# callable parameter shape が parameter list を span 走査するようになりました** — 共有 comma splitter が span を受け取り、callable contract 照合で各 parameter 正規化前の list 全体コピーを避けます。

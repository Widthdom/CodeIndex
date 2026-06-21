---
category: fixed
affected:
  - src/CodeIndex/Indexer/References/ReferenceExtractor.Core.cs
  - src/CodeIndex/Indexer/References/ReferenceExtractor.Preparation.cs
---

## English

- **Reduced C# reference extraction work during full indexing** — files without XML doc comment markers now skip C# XML-doc line-state construction and per-line doc-comment probing.

## 日本語

- **full index 時の C# reference extraction 作業を削減しました** — XML doc comment marker を含まないファイルでは、C# XML-doc 用 line-state 構築と行ごとの doc-comment 判定を省略するようになりました。

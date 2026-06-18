---
category: fixed
affected:
  - src/CodeIndex/Indexer/References/ReferenceExtractor.Preparation.cs
  - src/CodeIndex/Indexer/References/ReferenceExtractor.TypeReferences.cs
---

## English

- **C# reference extraction now reuses one line-state scan** — Fresh full indexes avoid a duplicate pass over C# source lines when masking multiline string content and block comments, shaving CPU from first-time `cdidx .` runs.

## 日本語

- **C# reference extraction が 1 回の行状態スキャンを再利用するようになりました** — 初回 `cdidx .` で multiline string content と block comment のマスク用に C# ソース行を二重走査しないようにし、CPU 使用量を削減しました。

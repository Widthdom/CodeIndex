---
category: changed
affected:
  - src/CodeIndex/Indexer/References/ReferenceExtractor.TypeReferences.cs
---

## English

- **C# callable parameter shape normalization now builds output directly** — Static interface member matching now joins normalized parameter shapes with a single `StringBuilder` pass, reducing iterator overhead while preserving the existing top-level comma splitting semantics.

## 日本語

- **C# callable parameter shape normalization が出力を直接構築するようになりました** — static interface member matching は normalized parameter shape を単一の `StringBuilder` pass で結合し、既存の top-level comma split semantics を保ったまま iterator overhead を削減します。

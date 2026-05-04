---
category: internal
affected:
  - docs/DEVELOPER_GUIDE.md
  - src/CodeIndex/Indexer/CSharpReferenceExtractor.cs
  - src/CodeIndex/Indexer/ReferenceExtractor.cs
---

## English

- **Split C# constructor reference handling into a dedicated helper** — C# constructor-chain rewrites now live in `CSharpReferenceExtractor`, reducing `ReferenceExtractor` size while preserving existing `this(...)` and `base(...)` reference output.

## 日本語

- **C# のコンストラクタ参照処理を専用ヘルパーへ分離** — C# の constructor-chain 書き換えを `CSharpReferenceExtractor` に移し、既存の `this(...)` / `base(...)` 参照出力を維持したまま `ReferenceExtractor` を小さくしました。

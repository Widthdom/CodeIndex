---
category: internal
affected:
  - docs/DEVELOPER_GUIDE.md
  - src/CodeIndex/Indexer/JavaReferenceExtractor.cs
  - src/CodeIndex/Indexer/ReferenceExtractor.cs
---

## English

- **Split Java constructor reference handling into a dedicated helper** — Java constructor-chain rewrites and same-line constructor container recovery now live in `JavaReferenceExtractor`, reducing `ReferenceExtractor` size while preserving existing reference output.

## 日本語

- **Java のコンストラクタ参照処理を専用ヘルパーへ分離** — Java の constructor-chain 書き換えと same-line constructor container 復元を `JavaReferenceExtractor` に移し、既存の参照出力を維持したまま `ReferenceExtractor` を小さくしました。

---
category: internal
affected:
  - docs/DEVELOPER_GUIDE.md
  - src/CodeIndex/Indexer/ReferenceExtractor.cs
  - src/CodeIndex/Indexer/SqlReferenceExtractor.cs
---

## English

- **Split SQL reference extraction into a dedicated helper** — SQL source/target reference emission, temp-object tracking, and procedure-call suppression now live in `SqlReferenceExtractor`, reducing `ReferenceExtractor` size without changing indexed reference behavior.

## 日本語

- **SQL の参照抽出を専用ヘルパーへ分離** — SQL の source/target 参照発行、temp object 追跡、procedure call 抑止を `SqlReferenceExtractor` に移し、インデックスされる参照挙動を変えずに `ReferenceExtractor` を小さくしました。

---
category: changed
affected:
  - src/CodeIndex/Database/NameFold.cs
---

## English

- Reduce repeated name-folding work during indexing across all languages: vectorize ASCII processing and avoid per-character string allocations for Unicode identifiers without changing persisted lookup keys.

## 日本語

- 全言語共通のインデックス時の名前正規化で、ASCII 処理をベクトル化し、Unicode 識別子の文字ごとの文字列割り当てを省きました。保存される検索 key は変わりません。

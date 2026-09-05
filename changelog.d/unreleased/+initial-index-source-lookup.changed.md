---
category: changed
affected:
  - src/CodeIndex/Database/DbWriter.AuthoritativeFreshReferenceSourceLookup.cs
---

## English

- Speed up initial full-index reference-source lookup across languages by eliminating redundant duplicate-removal work while preserving nested symbol, alias, and legacy-name selection.

## 日本語

- 全言語共通の初回フルインデックスの参照元検索で、不要な重複除去処理を省きました。入れ子シンボル・別名・旧形式の名前の選択結果は維持します。
